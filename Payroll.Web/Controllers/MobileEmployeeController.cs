using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Hubs;
using Payroll.Web.Security;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/employee")]
public sealed class MobileEmployeeController : ControllerBase
{
    private const string MobileDevicePrefix = "ANDROID:";

    private static string NormalizeMobileDeviceId(string deviceId)
    {
        var value = deviceId.Trim();
        return value.StartsWith(MobileDevicePrefix, StringComparison.OrdinalIgnoreCase)
            ? value
            : MobileDevicePrefix + value;
    }

    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MobileEmployeeTokenService _tokens;
    private readonly GeoLocationService _geo;
    private readonly IHubContext<AttendanceRefreshHub> _hub;
    private readonly ILogger<MobileEmployeeController> _logger;
    private readonly RegularizationService _regularizationService;
    private readonly AttendanceEventMonitorService _attendanceMonitor;

    public MobileEmployeeController(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        IDbContextFactory<AppDbContext> dbFactory,
        MobileEmployeeTokenService tokens,
        GeoLocationService geo,
        IHubContext<AttendanceRefreshHub> hub,
        ILogger<MobileEmployeeController> logger,
        RegularizationService regularizationService,
        AttendanceEventMonitorService attendanceMonitor)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _dbFactory = dbFactory;
        _tokens = tokens;
        _geo = geo;
        _hub = hub;
        _logger = logger;
        _regularizationService = regularizationService;
        _attendanceMonitor = attendanceMonitor;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] MobileLoginRequest request)
    {
        // SSOT: Use Email/Password pattern similar to web application.
        var emailIdentifier = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : request.EmployeeId;

        if (string.IsNullOrWhiteSpace(emailIdentifier) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return BadRequest(new { success = false, message = "Email, password and device ID are required." });
        }

        // 1. Find Identity User: Try Email first, then UserName (SSOT handles both)
        var user = await _userManager.FindByEmailAsync(emailIdentifier.Trim())
                   ?? await _userManager.FindByNameAsync(emailIdentifier.Trim());

        if (user == null)
            return Unauthorized(new { success = false, code = "INVALID_CREDENTIALS", message = "Invalid email or password." });

        // 2. Verify confirmation if required by SSOT policy
        if (!await _userManager.IsEmailConfirmedAsync(user) && _userManager.Options.SignIn.RequireConfirmedEmail)
        {
            return Unauthorized(new { success = false, code = "EMAIL_NOT_CONFIRMED", message = "Please confirm your email address before signing in." });
        }

        // 3. Check Password using SignInManager to ensure consistency with Web App (lockout, etc.)
        var passwordResult = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);

        if (!passwordResult.Succeeded)
        {
            await _attendanceMonitor.RecordAsync(
                "LOGIN_FAILED", user.Id, user.Email ?? emailIdentifier, request.DeviceId, "Android", "FAILED",
                passwordResult.IsLockedOut ? "LOCKED" : (passwordResult.IsNotAllowed ? "NOT_ALLOWED" : "INVALID_CREDENTIALS"));
            if (passwordResult.IsLockedOut)
                return Unauthorized(new { success = false, code = "LOCKED", message = "This account is temporarily locked. Please try again later." });

            if (passwordResult.IsNotAllowed)
                return Unauthorized(new { success = false, code = "NOT_ALLOWED", message = "This account is currently not allowed to sign in." });

            return Unauthorized(new { success = false, code = "INVALID_CREDENTIALS", message = "Invalid email or password." });
        }

        // 4. Find the linked Employee record in SSOT Payroll database
        await using var db = await _dbFactory.CreateDbContextAsync();
        var employee = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => (x.AspNetUserId == user.Id || x.Email == user.Email || x.Email == emailIdentifier.Trim()) && !x.IsDeleted);

        // Check for roles
        var roles = await _userManager.GetRolesAsync(user);
        var primaryRole = roles.FirstOrDefault() ?? "Employee";
        var isAdmin = primaryRole.Contains("Admin", StringComparison.OrdinalIgnoreCase);

        if (employee == null && !isAdmin)
            return Unauthorized(new { success = false, code = "NOT_LINKED", message = "Identity account verified, but no active payroll link found." });

        var suppliedDeviceId = request.DeviceId.Trim();
        var mobileDeviceId = NormalizeMobileDeviceId(suppliedDeviceId);

        var existing = await db.EmployeeDeviceLocks.FirstOrDefaultAsync(x => x.UserId == user.Id);
        var sameDevice = existing != null &&
            (string.Equals(existing.DeviceId, mobileDeviceId, StringComparison.Ordinal) ||
             string.Equals(existing.DeviceId, suppliedDeviceId, StringComparison.Ordinal));

        if (existing != null && !sameDevice && !request.ForceReplace)
        {
            await _attendanceMonitor.RecordAsync(
                "SECOND_DEVICE_ATTEMPT", user.Id, user.Email ?? emailIdentifier, mobileDeviceId, "Android",
                "EXISTING_SESSION_FOUND", "SINGLE_DEVICE_POLICY",
                new { ExistingSessionSinceUtc = existing.CreatedAtUtc }, existing.DeviceId);

            return Conflict(new
            {
                success = false,
                code = "EXISTING_SESSION",
                message = "This employee is already logged in on another device.",
                activeSinceUtc = existing.CreatedAtUtc
            });
        }

        if (existing != null && sameDevice &&
            !string.Equals(existing.DeviceId, mobileDeviceId, StringComparison.Ordinal))
        {
            // Migrate a legacy mobile lock to the explicit mobile namespace.
            existing.DeviceId = mobileDeviceId;
            existing.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        if (existing != null && !sameDevice)
        {
            await _attendanceMonitor.RecordAsync(
                "FORCE_LOGOUT_REQUESTED", user.Id, user.Email ?? emailIdentifier, mobileDeviceId, "Android",
                "REQUESTED", "NEW_DEVICE_REPLACE",
                new { ForceReplace = request.ForceReplace }, existing.DeviceId);

            // Replacing a mobile device is an explicit force logout of the
            // previous device. End every active GPS session before releasing
            // the old device lock so no live session survives replacement.
            if (employee != null)
            {
                var endedCount = await _geo.EndAllGpsSessionsAsync(
                    employee.EmployeeID,
                    "FORCE_LOGGED_OUT");

                _logger.LogInformation(
                    "Mobile employee session replaced. EmployeeId={EmployeeId}, SessionsEnded={SessionsEnded}, Reason=FORCE_LOGGED_OUT",
                    employee.EmployeeID,
                    endedCount);
            }

            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
                return StatusCode(500, new { success = false, message = "Unable to replace the existing employee session." });

            var oldDeviceId = existing.DeviceId;
            db.EmployeeDeviceLocks.Remove(existing);
            await db.SaveChangesAsync();

            await _attendanceMonitor.RecordAsync(
                "FORCED_SESSION_LOGOUT", user.Id, user.Email ?? emailIdentifier, oldDeviceId, "Android",
                "TERMINATED", "REPLACED_BY_NEW_DEVICE",
                new { NewDeviceId = mobileDeviceId }, oldDeviceId);

            existing = null;
        }

        if (existing == null)
        {
            db.EmployeeDeviceLocks.Add(new EmployeeDeviceLock
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                DeviceId = mobileDeviceId,
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        else
        {
            existing.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await _attendanceMonitor.RecordAsync(
            "LOGIN_SUCCESS", user.Id, user.Email ?? emailIdentifier, mobileDeviceId, "Android", "SUCCESS",
            request.ForceReplace ? "NEW_DEVICE_AFTER_FORCE_REPLACE" : "SESSION_STARTED",
            new { EmployeeId = employee?.EmployeeID ?? 0, Role = primaryRole });

        var token = _tokens.Create(user.Id, employee?.EmployeeID ?? 0, mobileDeviceId, primaryRole);

        return Ok(new MobileLoginResponse
        {
            Success = true,
            Token = token,
            EmployeeId = employee?.EmployeeID ?? 0,
            Name = employee?.Name ?? user.UserName ?? "Admin",
            Email = employee?.Email ?? user.Email ?? string.Empty,
            Role = primaryRole,
            Message = "Login successful",
            MonthlySalary = employee?.MonthlySalary ?? 0,
            PaidLeaveBalance = employee?.PaidLeaveBalance ?? 0,
            SickLeaveBalance = employee?.SickLeaveBalance ?? 0
        });
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Me()
    {
        var employeeId = GetEmployeeId();
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (employeeId == 0)
        {
             return Ok(new MobileLoginResponse
             {
                 Success = true,
                 EmployeeId = 0,
                 Name = User.Identity?.Name ?? "Administrator",
                 Email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
                 Role = User.FindFirstValue(ClaimTypes.Role) ?? "Admin"
             });
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeID == employeeId && !x.IsDeleted);
        if (employee == null) return NotFound(new { success = false, message = "Employee not found." });

        return Ok(new MobileLoginResponse
        {
            Success = true,
            EmployeeId = employee.EmployeeID,
            Name = employee.Name,
            Email = employee.Email ?? string.Empty,
            MonthlySalary = employee.MonthlySalary,
            PaidLeaveBalance = employee.PaidLeaveBalance,
            SickLeaveBalance = employee.SickLeaveBalance
        });
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var employeeId = GetEmployeeId();
        var deviceId = User.FindFirstValue("BioMetric-Employee-Device") ?? "ANDROID";

        await _attendanceMonitor.RecordAsync(
            "LOGOUT_REQUESTED", userId, User.FindFirstValue(ClaimTypes.Email), deviceId, "Android", "REQUESTED", "MANUAL_LOGOUT");

        // Logout is authoritative: end every active GPS session before the
        // device lock is released. This also cleans up legacy duplicate
        // sessions without changing the database design.
        var endedCount = await _geo.EndAllGpsSessionsAsync(
            employeeId,
            "MANUAL_LOGOUT");

        _logger.LogInformation(
            "Mobile employee logout completed. EmployeeId={EmployeeId}, SessionsEnded={SessionsEnded}, Reason=MANUAL_LOGOUT",
            employeeId,
            endedCount);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var lockRecord = await db.EmployeeDeviceLocks.FirstOrDefaultAsync(x => x.UserId == userId);
        if (lockRecord != null)
        {
            db.EmployeeDeviceLocks.Remove(lockRecord);
            await db.SaveChangesAsync();
        }

        await _attendanceMonitor.RecordAsync(
            "DEVICE_LOCK_RELEASED", userId, User.FindFirstValue(ClaimTypes.Email), deviceId, "Android", "SUCCESS", "MANUAL_LOGOUT");
        await _attendanceMonitor.RecordAsync(
            "LOGOUT_COMPLETED", userId, User.FindFirstValue(ClaimTypes.Email), deviceId, "Android", "SUCCESS",
            "MANUAL_LOGOUT_COMPLETED", new { EmployeeId = employeeId, SessionsEnded = endedCount });

        return Ok(new { success = true });
    }

    [HttpPost("gps/start")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> StartGps([FromBody] GpsSessionRequest request)
    {
        var employeeId = GetEmployeeId();
        if (!Guid.TryParse(request.SessionId, out var sessionId) || sessionId == Guid.Empty)
            return BadRequest(new { success = false, message = "A valid GPS session ID is required." });

        var ok = await _geo.StartGpsSessionAsync(employeeId, sessionId);
        return ok
            ? Ok(new { success = true, sessionId })
            : Conflict(new { success = false, message = "Unable to start GPS session." });
    }

    [HttpPost("gps/update")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> UpdateGps([FromBody] GpsUpdateRequest request)
    {
        var employeeId = GetEmployeeId();
        if (!Guid.TryParse(request.SessionId, out var sessionId) || sessionId == Guid.Empty)
            return BadRequest(new { success = false, message = "A valid GPS session ID is required." });
        if (!double.IsFinite(request.Latitude) || !double.IsFinite(request.Longitude) ||
            request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { success = false, message = "Invalid GPS coordinates." });

        var distance = await _geo.GetDistanceFromOfficeAsync(request.Latitude, request.Longitude);
        if (!distance.Success)
            return StatusCode(500, new { success = false, message = distance.Message });

        var sessionUpdated = await _geo.UpdateGpsSessionAsync(
            employeeId,
            sessionId,
            request.Latitude,
            request.Longitude,
            request.Accuracy,
            distance.DistanceMeters,
            distance.AllowedRadiusMeters,
            distance.IsWithinAllowedRadius);

        if (!sessionUpdated)
            return Conflict(new { success = false, message = "GPS session is no longer active." });

        await _geo.SaveLocationHistoryAsync(employeeId, sessionId, request.Latitude, request.Longitude,
            distance.DistanceMeters, distance.AllowedRadiusMeters, distance.IsWithinAllowedRadius, request.Accuracy);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var lockRecord = await db.EmployeeDeviceLocks.FirstOrDefaultAsync(x => x.UserId == userId);
            if (lockRecord != null) { lockRecord.LastSeenAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
        }

        return Ok(new
        {
            success = true,
            employeeId,
            distanceMeters = distance.DistanceMeters,
            allowedRadiusMeters = distance.AllowedRadiusMeters,
            isWithinAllowedRadius = distance.IsWithinAllowedRadius,
            timestamp = DateTime.UtcNow
        });
    }

    [HttpPost("gps/end")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> EndGps([FromBody] GpsSessionRequest request)
    {
        var employeeId = GetEmployeeId();
        if (!Guid.TryParse(request.SessionId, out var sessionId) || sessionId == Guid.Empty)
            return BadRequest(new { success = false, message = "A valid GPS session ID is required." });

        await _geo.EndGpsSessionAsync(employeeId, sessionId, "LOGGED_OUT");
        return Ok(new { success = true });
    }

    [HttpGet("punch-status")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> PunchStatus()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var last = await db.AttendanceLogs.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.PunchTime).FirstOrDefaultAsync();
        var lastType = last?.LogType?.ToUpperInvariant();
        var next = lastType == "IN" ? "OUT" : "IN";
        return Ok(new { success = true, lastType, nextType = next, lastPunchTime = last?.PunchTime.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    [HttpPost("punch")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Punch([FromBody] EmployeePunchRequest request)
    {
        var type = request.Type?.Trim().ToUpperInvariant();
        if (type is not ("IN" or "OUT")) return BadRequest(new { success = false, message = "Punch type must be IN or OUT." });
        if (!double.IsFinite(request.Latitude) || !double.IsFinite(request.Longitude) || request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180) return BadRequest(new { success = false, message = "Invalid GPS coordinates." });
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var last = await db.AttendanceLogs
            .Where(x => x.EmployeeID == employeeId)
            .OrderByDescending(x => x.PunchTime)
            .ThenByDescending(x => x.LogID)
            .FirstOrDefaultAsync();

        var lastIsAutomaticFallback =
            string.Equals(last?.DeviceID, "GeofenceAuto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(last?.BiometricID, "GEOFENCE_AUTO", StringComparison.OrdinalIgnoreCase);

        // If the most recent event is a temporary geofence fallback, allow
        // an explicit Android punch of the same direction to replace it.
        // This preserves the existing expected-punch rule for normal punches.
        var expected =
            lastIsAutomaticFallback
                ? last?.LogType?.ToUpperInvariant()
                : last?.LogType?.ToUpperInvariant() == "IN"
                    ? "OUT"
                    : "IN";

        if (type != expected)
            return Conflict(new { success = false, message = $"Next valid punch is {expected}." });
        await using var attendanceTransaction =
            await db.Database.BeginTransactionAsync();

        var log = new Payroll.Shared.AttendanceLog
        {
            EmployeeID = employeeId,
            BiometricID = $"ANDROID-{employeeId}",
            PunchTime = DateTime.Now,
            DeviceID = "Android",
            LogType = type,
            IsApproved = true,
            Latitude = request.Latitude,
            Longitude = request.Longitude
        };

        db.AttendanceLogs.Add(log);
        await db.SaveChangesAsync();

        // In Dual Attendance mode, an explicit Android punch is
        // authoritative. If a GPS fallback was created moments earlier,
        // remove only that temporary fallback. The geo audit remains intact.
        await _geo.ReconcileAutomaticFallbackAsync(
            db,
            employeeId,
            log.PunchTime);

        await attendanceTransaction.CommitAsync();

        return Ok(new
        {
            success = true,
            lastType = type,
            nextType = type == "IN" ? "OUT" : "IN",
            lastPunchTime = log.PunchTime.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    [HttpGet("dashboard")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Dashboard()
    {
        var employeeId = GetEmployeeId();
        await using var db = await _dbFactory.CreateDbContextAsync();

        var company = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.SettingID == 1);
        var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);

        if (employeeId == 0)
        {
            // Admin/SuperAdmin Dashboard (Minimal Info)
            return Ok(new
            {
                success = true,
                employeeId = 0,
                name = "Administrator",
                officeLatitude = company?.OfficeLatitude ?? 0,
                officeLongitude = company?.OfficeLongitude ?? 0,
                geoRadiusMeters = company?.GeoRadiusMeters ?? 100,
                enableGeoFencing = features?.EnableGeoFencing ?? true,
                enableDualAttendance = features?.EnableDualAttendance ?? false,
                enableAutomaticGeofencePunching = features?.EnableAutomaticGeofencePunching ?? false
            });
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeID == employeeId && !x.IsDeleted);
        if (employee == null) return NotFound(new { success = false, message = "Employee not found." });

        var latest = await db.PayrollHistories.AsNoTracking().Where(x => x.EmployeeID == employeeId)
            .OrderByDescending(x => x.PayYear).ThenByDescending(x => x.PayMonth).FirstOrDefaultAsync();

        return Ok(new {
            success = true,
            employeeId,
            name = employee.Name,
            email = employee.Email ?? "",
            monthlySalary = employee.MonthlySalary,
            paidLeaveBalance = employee.PaidLeaveBalance,
            sickLeaveBalance = employee.SickLeaveBalance,
            latestPayslip = latest == null ? null : ToPayslip(latest),
            officeLatitude = company?.OfficeLatitude ?? 0,
            officeLongitude = company?.OfficeLongitude ?? 0,
            geoRadiusMeters = company?.GeoRadiusMeters ?? 100,
            enableGeoFencing = features?.EnableGeoFencing ?? true,
            enableDualAttendance = features?.EnableDualAttendance ?? false,
            enableAutomaticGeofencePunching = features?.EnableAutomaticGeofencePunching ?? false,

            // Profile Info
            role = employee.Role,
            dob = employee.DOB?.ToString("yyyy-MM-dd"),
            hireDate = employee.HireDate?.ToString("yyyy-MM-dd"),
            shiftStartTime = employee.ShiftStartTime?.ToString("HH:mm"),
            shiftEndTime = employee.ShiftEndTime?.ToString("HH:mm"),
            uan = employee.UAN,
            esiNumber = employee.ESINumber,
            bankName = employee.BankName,
            bankAccountNumber = employee.BankAccountNumber,
            bankIfscCode = employee.BankIfscCode
        });
    }

    [HttpGet("company-settings")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> GetCompanySettings()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var company = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.SettingID == 1);

        return Ok(new
        {
            success = true,
            officeLatitude = company?.OfficeLatitude ?? 0,
            officeLongitude = company?.OfficeLongitude ?? 0,
            geoRadiusMeters = company?.GeoRadiusMeters ?? 100
        });
    }

    [HttpGet("attendance")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Attendance([FromQuery] DateOnly from, [FromQuery] DateOnly to)
    {
        var employeeId = GetEmployeeId();
        if (to < from) return BadRequest(new { success = false, message = "Invalid date range." });
        await using var db = await _dbFactory.CreateDbContextAsync();
        var summaries = await db.DailySummaries.AsNoTracking().Where(x => x.EmployeeID == employeeId && x.ShiftDate >= from && x.ShiftDate <= to)
            .OrderByDescending(x => x.ShiftDate).ToListAsync();
        var logs = await db.AttendanceLogs.AsNoTracking().Where(x => x.EmployeeID == employeeId && x.PunchTime.Date >= from.ToDateTime(TimeOnly.MinValue).Date && x.PunchTime.Date <= to.ToDateTime(TimeOnly.MaxValue).Date)
            .OrderBy(x => x.PunchTime).ToListAsync();
        var result = summaries.Select(x => new {
            date = x.ShiftDate.ToString("yyyy-MM-dd"), status = x.Status,
            scheduledHours = x.ScheduledShiftDuration.TotalHours, workedHours = (double)x.EarnedStandardHours,
            overtime = x.TotalOvertimeDuration.ToString(), penalty = x.TotalPenaltyDuration.ToString(),
            punches = logs.Where(l => DateOnly.FromDateTime(l.PunchTime) == x.ShiftDate).Select(l => new { time = l.PunchTime.ToString("HH:mm:ss"), type = l.LogType ?? "Punch", source = l.DeviceID ?? "", approved = l.IsApproved }).ToList()
        });
        return Ok(result);
    }

    [HttpGet("payslips")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Payslips()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.PayrollHistories.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.PayYear).ThenByDescending(x => x.PayMonth).ToListAsync();
        return Ok(rows.Select(ToPayslip));
    }

    [HttpGet("leaves")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Leaves()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.LeaveRequests.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.LeaveDate).ToListAsync();
        return Ok(rows.Select(x => new { id = x.LeaveRequestID, date = x.LeaveDate.HasValue ? x.LeaveDate.Value.ToString("yyyy-MM-dd") : "", leaveType = x.LeaveType, halfDay = x.IsHalfDay, approved = x.IsApproved, notes = x.Notes }));
    }

    [HttpPost("leaves")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> CreateLeave([FromBody] LeaveCreateRequest request)
    {
        if (!DateTime.TryParse(request.LeaveDate, out var date) || string.IsNullOrWhiteSpace(request.LeaveType)) return BadRequest(new { success = false, message = "Valid leave date and type are required." });
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = new Payroll.Shared.LeaveRequest { EmployeeID = employeeId, LeaveDate = date.Date, LeaveType = request.LeaveType.Trim(), IsHalfDay = request.IsHalfDay, IsApproved = false, Notes = request.Notes };
        db.LeaveRequests.Add(entry); await db.SaveChangesAsync();
        return Ok(new { id = entry.LeaveRequestID, date = entry.LeaveDate!.Value.ToString("yyyy-MM-dd"), leaveType = entry.LeaveType, halfDay = entry.IsHalfDay, approved = entry.IsApproved, notes = entry.Notes });
    }

    [HttpGet("advances")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Advances()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.SalaryAdvances.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.AdvanceDate).ToListAsync();
        return Ok(rows.Select(x => new { id = x.AdvanceID, date = x.AdvanceDate.HasValue ? x.AdvanceDate.Value.ToString("yyyy-MM-dd") : "", amount = x.Amount, type = x.AdvanceType ?? "Advance", description = "", paid = x.PayrollID_Paid.HasValue }));
    }

    [HttpGet("bonuses")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Bonuses()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.BonusRecords.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.BonusDate).ToListAsync();
        return Ok(rows.Select(x => new { id = x.BonusID, date = x.BonusDate.ToString("yyyy-MM-dd"), amount = x.Amount, type = "Bonus", description = x.Description ?? "", paid = x.PayrollID_Paid.HasValue }));
    }

    [HttpGet("regularizations")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Regularizations()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.AttendanceRegularizations.AsNoTracking().Where(x => x.EmployeeId == employeeId).OrderByDescending(x => x.DateOfPunch).ToListAsync();
        return Ok(rows.Select(x => new
        {
            id = x.RegularizationId,
            date = x.DateOfPunch.ToString("yyyy-MM-dd"),
            inPunch = x.IsInPunch,
            punchTime = x.PunchTimeNew.ToString("HH:mm"),
            reason = x.Reason,
            status = x.Status,
            remarks = RegularizationService.GetDisplayAdminRemarks(x.AdminRemarks),
            canResubmit = RegularizationService.CanResubmit(x)
        }));
    }

    [HttpPost("regularizations")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> CreateRegularization([FromBody] RegularizationCreateRequest request)
    {
        if (!DateOnly.TryParse(request.DateOfPunch, out var date) || !TimeOnly.TryParse(request.PunchTimeNew, out var time) || string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { success = false, message = "Date, time and reason are required." });
        var employeeId = GetEmployeeId();
        long createdId;
        try
        {
            createdId = await _regularizationService.SubmitRequestAsync(
                employeeId, date, time, request.Reason.Trim(), request.IsInPunch);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { success = false, message = ex.Message });
        }

        return Ok(new
        {
            id = createdId,
            date = date.ToString("yyyy-MM-dd"),
            inPunch = request.IsInPunch,
            punchTime = time.ToString("HH:mm"),
            reason = request.Reason.Trim(),
            status = "Pending",
            remarks = (string?)null
        });
    }

    [HttpGet("resignation")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Resignation()
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var x = await db.ResignationRequests.AsNoTracking().Where(r => r.EmployeeId == employeeId).OrderByDescending(r => r.SubmissionDate).FirstOrDefaultAsync();
        return Ok(x == null ? null : new { id = x.RequestId, submissionDate = x.SubmissionDate.ToString("yyyy-MM-dd HH:mm"), desiredLastWorkingDay = x.DesiredLastWorkingDay.ToString("yyyy-MM-dd"), reason = x.Reason, status = x.Status, approvedLastWorkingDay = x.ApprovedLastWorkingDay.HasValue ? x.ApprovedLastWorkingDay.Value.ToString("yyyy-MM-dd") : null, adminRemarks = x.AdminRemarks, settled = x.IsSettled });
    }

    [HttpPost("resignation")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> CreateResignation([FromBody] ResignationCreateRequest request)
    {
        if (!DateOnly.TryParse(request.DesiredLastWorkingDay, out var lastDay) || string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { success = false, message = "Last working day and reason are required." });
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var active = await db.ResignationRequests.AnyAsync(x => x.EmployeeId == employeeId && (x.Status == "Pending" || x.Status == "Approved") && !x.IsSettled);
        if (active) return Conflict(new { success = false, message = "An active resignation request already exists." });
        var x = new ResignationRequest { EmployeeId = employeeId, DesiredLastWorkingDay = lastDay, Reason = request.Reason.Trim(), Status = "Pending", SubmissionDate = DateTime.Now };
        db.ResignationRequests.Add(x); await db.SaveChangesAsync();
        return Ok(new { id = x.RequestId, submissionDate = x.SubmissionDate.ToString("yyyy-MM-dd HH:mm"), desiredLastWorkingDay = lastDay.ToString("yyyy-MM-dd"), reason = x.Reason, status = x.Status, approvedLastWorkingDay = (string?)null, adminRemarks = (string?)null, settled = false });
    }

    [HttpGet("tax")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Tax([FromQuery] int financialYear)
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync(); var x = await db.TaxDeclarations.AsNoTracking().FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.FinancialYear == financialYear);
        return Ok(x == null ? null : ToTax(x));
    }

    [HttpPost("tax")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> SaveTax([FromBody] TaxDeclarationRequest request)
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var x = await db.TaxDeclarations.FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.FinancialYear == request.FinancialYear);
        if (x == null) { x = new TaxDeclaration { EmployeeId = employeeId, FinancialYear = request.FinancialYear }; db.TaxDeclarations.Add(x); }
        if (string.Equals(x.Status, "Approved", StringComparison.OrdinalIgnoreCase)) return Conflict(new { success = false, message = "This tax declaration is already approved." });
        x.Regime = request.Regime == "Old" ? "Old" : "New"; x.Section80C = Math.Max(0, request.Section80C); x.Section80D = Math.Max(0, request.Section80D); x.HraRentPaid = Math.Max(0, request.HraRentPaid); x.OtherExemptions = Math.Max(0, request.OtherExemptions); x.Status = "Pending"; x.SubmissionDate = DateTime.Now;
        await db.SaveChangesAsync(); return Ok(ToTax(x));
    }

    [HttpGet("fbp")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Fbp([FromQuery] int financialYear)
    {
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync(); var rows = await db.FlexibleBenefitDeclarations.AsNoTracking().Where(x => x.EmployeeId == employeeId && x.FinancialYear == financialYear && x.IsActive).OrderBy(x => x.ComponentName).ToListAsync();
        return Ok(rows.Select(ToFbp));
    }

    [HttpPost("fbp")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> SaveFbp([FromBody] FbpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ComponentName) || request.AnnualAllocatedAmount < 0) return BadRequest(new { success = false, message = "Component and amount are required." });
        var employeeId = GetEmployeeId(); await using var db = await _dbFactory.CreateDbContextAsync();
        var comp = await db.FBPComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Name == request.ComponentName && x.IsActive); if (comp == null) return BadRequest(new { success = false, message = "Invalid FBP component." });
        if (request.AnnualAllocatedAmount > comp.MaxAnnualLimit) return BadRequest(new { success = false, message = "Amount exceeds the component annual limit." });
        var x = await db.FlexibleBenefitDeclarations.FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.FinancialYear == request.FinancialYear && r.ComponentName == request.ComponentName);
        if (x == null) { x = new FlexibleBenefitDeclaration { EmployeeId = employeeId, FinancialYear = request.FinancialYear, ComponentName = request.ComponentName.Trim() }; db.FlexibleBenefitDeclarations.Add(x); }
        if (x.Status is "Approved" or "Locked") return Conflict(new { success = false, message = "This FBP component is locked." });
        x.AnnualAllocatedAmount = request.AnnualAllocatedAmount; x.MonthlyAllocatedAmount = request.AnnualAllocatedAmount / 12m; x.Status = "Submitted"; x.SubmissionDate = DateTime.Now; x.IsActive = true;
        await db.SaveChangesAsync(); return Ok(ToFbp(x));
    }

    [HttpGet("shifts")]
    [Authorize(AuthenticationSchemes = "MobileBearer")]
    public async Task<IActionResult> Shifts([FromQuery] string month)
    {
        if (!DateTime.TryParse($"{month}-01", out var parsed)) return BadRequest(new { success = false, message = "Month must be YYYY-MM." });
        var start = DateOnly.FromDateTime(parsed); var end = start.AddMonths(1).AddDays(-1); var employeeId = GetEmployeeId();
        await using var db = await _dbFactory.CreateDbContextAsync(); var rows = await db.ShiftSchedules.AsNoTracking().Where(x => x.EmployeeID == employeeId && x.ShiftDate >= start && x.ShiftDate <= end).OrderBy(x => x.ShiftDate).ToListAsync();
        return Ok(rows.Select(x => new { date = x.ShiftDate.ToString("yyyy-MM-dd"), day = x.ShiftDate.DayOfWeek.ToString(), startTime = x.StartTime.ToString("HH:mm"), endTime = x.EndTime.ToString("HH:mm"), status = "Scheduled" }));
    }

    private static object ToPayslip(Payroll.Shared.PayrollHistory x) => new { payrollId = x.PayrollID, month = x.PayMonth, year = x.PayYear, baseSalary = x.BaseSalary ?? 0m, overtimePay = x.OvertimePay ?? 0m, bonus = x.Bonus ?? 0m, advanceDeduction = x.Deductions_Advance ?? 0m, pfDeduction = x.PfDeduction, esiDeduction = x.EsiDeduction, ptDeduction = x.PtDeduction, tdsDeduction = x.TdsDeduction, netSalary = x.NetSalary, hourlyRate = x.HourlyRate, totalHoursWorked = x.TotalHoursWorked ?? 0m };
    private static object ToTax(TaxDeclaration x) => new { declarationId = x.DeclarationId, financialYear = x.FinancialYear, regime = x.Regime, section80C = x.Section80C, section80D = x.Section80D, hraRentPaid = x.HraRentPaid, otherExemptions = x.OtherExemptions, status = x.Status, adminRemarks = x.AdminRemarks };
    private static object ToFbp(FlexibleBenefitDeclaration x) => new { declarationId = x.DeclarationId, financialYear = x.FinancialYear, componentName = x.ComponentName, annualAllocatedAmount = x.AnnualAllocatedAmount, monthlyAllocatedAmount = x.MonthlyAllocatedAmount, status = x.Status, adminRemarks = x.AdminRemarks };

    private int GetEmployeeId()
    {
        return int.TryParse(User.FindFirstValue("employee_id"), out var id) ? id : 0;
    }

    public sealed class MobileLoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string EmployeeId { get; set; } = string.Empty; // Added back for transition compatibility
        public string Password { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public bool ForceReplace { get; set; }
    }

    public sealed class MobileLoginResponse
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public string? Message { get; set; }
        public int EmployeeId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Role { get; set; }
        public decimal MonthlySalary { get; set; }
        public decimal PaidLeaveBalance { get; set; }
        public decimal SickLeaveBalance { get; set; }
    }

    public class GpsSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }

    public sealed class GpsUpdateRequest : GpsSessionRequest
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Accuracy { get; set; }
        public double Speed { get; set; }
        public long Timestamp { get; set; }
        public int BatteryLevel { get; set; }
    }

    public sealed class EmployeePunchRequest { public string Type { get; set; } = "IN"; public double Latitude { get; set; } public double Longitude { get; set; } public double Accuracy { get; set; } }

    public sealed class LeaveCreateRequest { public string LeaveDate { get; set; } = string.Empty; public string LeaveType { get; set; } = string.Empty; public bool IsHalfDay { get; set; } public string? Notes { get; set; } }
    public sealed class RegularizationCreateRequest { public string DateOfPunch { get; set; } = string.Empty; public bool IsInPunch { get; set; } public string PunchTimeNew { get; set; } = string.Empty; public string Reason { get; set; } = string.Empty; }
    public sealed class ResignationCreateRequest { public string DesiredLastWorkingDay { get; set; } = string.Empty; public string Reason { get; set; } = string.Empty; }
    public sealed class TaxDeclarationRequest { public int FinancialYear { get; set; } public string Regime { get; set; } = "New"; public decimal Section80C { get; set; } public decimal Section80D { get; set; } public decimal HraRentPaid { get; set; } public decimal OtherExemptions { get; set; } }
    public sealed class FbpRequest { public int FinancialYear { get; set; } public string ComponentName { get; set; } = string.Empty; public decimal AnnualAllocatedAmount { get; set; } }
}
