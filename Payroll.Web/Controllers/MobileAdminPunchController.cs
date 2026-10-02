using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using Payroll.Web.Services;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/punches")]
[Authorize(AuthenticationSchemes = "MobileBearer", Roles = "Admin,SuperAdmin")]
public sealed class MobileAdminPunchController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AttendanceCalculatorService _calculator;
    private readonly PayrollLockService _lockService;
    private readonly AuditService _audit;
    private readonly AttendanceRefreshService _refresh;
    private readonly FirebaseEmployeeManagementService _firebaseEmployees;
    private readonly FirebaseAttendanceMutationService _firebaseAttendanceMutations;
    private readonly FirebaseAttendanceService _firebaseAttendance;
    private readonly AttendanceProcessingCoordinator _attendanceProcessingCoordinator;
    private readonly ILogger<MobileAdminPunchController> _logger;

    public MobileAdminPunchController(
    IDbContextFactory<AppDbContext> dbFactory,
    AttendanceCalculatorService calculator,
    PayrollLockService lockService,
    AuditService audit,
    AttendanceRefreshService refresh,
    FirebaseEmployeeManagementService firebaseEmployees,
    FirebaseAttendanceMutationService firebaseAttendanceMutations,
    FirebaseAttendanceService firebaseAttendance,
    AttendanceProcessingCoordinator attendanceProcessingCoordinator,
    ILogger<MobileAdminPunchController> logger)
    {
        _dbFactory = dbFactory;
        _calculator = calculator;
        _lockService = lockService;
        _audit = audit;
        _refresh = refresh;
        _firebaseEmployees = firebaseEmployees;
        _firebaseAttendanceMutations = firebaseAttendanceMutations;
        _firebaseAttendance = firebaseAttendance;
        _attendanceProcessingCoordinator = attendanceProcessingCoordinator;
        _logger = logger;
    }

    [HttpGet("issues")]
    public async Task<IActionResult> Issues([FromQuery] int employeeId = 0, [FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) start = DateTime.Today.AddDays(-7);
        if (!DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) end = DateTime.Today;
        if (end < start) return BadRequest(new { success = false, message = "Invalid date range." });
        await using var db = await _dbFactory.CreateDbContextAsync();
        var employees = (await _firebaseEmployees.GetEmployeesAsync())
            .Where(e => employeeId <= 0 || e.EmployeeID == employeeId)
            .ToList();
        var logs = await db.AttendanceLogs.AsNoTracking().Where(x => x.EmployeeID.HasValue && x.PunchTime >= start.Date && x.PunchTime < end.Date.AddDays(1)).OrderBy(x => x.PunchTime).ToListAsync();
        var result = new List<IssueDayDto>();
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Sunday) continue;
            foreach (var emp in employees)
            {
                var punches = logs.Where(x => x.EmployeeID == emp.EmployeeID && x.PunchTime.Date == day).ToList();
                if (punches.Count == 0 || punches.Count % 2 != 0)
                    result.Add(new IssueDayDto(emp.EmployeeID, emp.Name, day.ToString("yyyy-MM-dd"), punches.Select(ToDto).ToList()));
            }
        }
        return Ok(result.OrderBy(x => x.Date).ThenBy(x => x.EmployeeName));
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        List<AttendanceLog> rows;

        try
        {
            rows = (await _firebaseAttendance.GetAttendancePunchesAsync(
                    DateOnly.MinValue,
                    DateOnly.MaxValue,
                    null,
                    HttpContext.RequestAborted))
                .Where(x => !x.IsApproved &&
                            string.Equals(x.LogType, "Correction Request", StringComparison.OrdinalIgnoreCase) &&
                            x.EmployeeID.HasValue)
                .OrderBy(x => x.PunchTime)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase pending punch read failed; using SQL compatibility fallback.");
            rows = new();
        }

        if (rows.Count == 0)
        {
            rows = await db.AttendanceLogs.AsNoTracking()
                .Where(x => !x.IsApproved && x.LogType == "Correction Request" && x.EmployeeID.HasValue)
                .OrderBy(x => x.PunchTime)
                .ToListAsync(HttpContext.RequestAborted);
        }

        var names = (await _firebaseEmployees.GetEmployeesAsync())
            .ToDictionary(x => x.EmployeeID, x => x.Name);

        return Ok(rows
            .Where(x => x.EmployeeID.HasValue)
            .Select(x => new PendingPunchDto(
                x.LogID,
                x.EmployeeID!.Value,
                names.TryGetValue(x.EmployeeID.Value, out var n) ? n : $"Employee #{x.EmployeeID.Value}",
                x.PunchTime.ToString("yyyy-MM-dd HH:mm:ss"),
                x.LogType ?? "Correction Request",
                x.DeviceID ?? "")));
    }

    [HttpPost("manual")]
    public async Task<IActionResult> Add([FromBody] PunchMutationRequest request)
    {
        if (request.EmployeeId <= 0 || !DateTime.TryParseExact(request.PunchTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var punchTime))
            return BadRequest(new { success = false, message = "Invalid employee or punch time." });

        if (await _lockService.IsLockedAsync(request.EmployeeId, punchTime))
            return Conflict(new { success = false, message = "Payroll is finalized for this month. Cannot modify punches." });

        var key = $"{request.EmployeeId}:{punchTime.Date:yyyy-MM-dd}";
        using var lease = await _attendanceProcessingCoordinator.AcquireAsync(key, HttpContext.RequestAborted);

        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var log = new AttendanceLog
        {
            EmployeeID = request.EmployeeId,
            PunchTime = punchTime,
            DeviceID = "ManualCorrection",
            LogType = "Manual Correction",
            BiometricID = $"MANUAL_{request.EmployeeId}_{punchTime:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}",
            IsApproved = true
        };

        db.AttendanceLogs.Add(log);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await Recalculate(db, request.EmployeeId, punchTime.Date);

        var firebasePunchOk = await _firebaseAttendanceMutations.UpsertPunchAsync(log, "CREATED", HttpContext.RequestAborted);
        var summary = await db.DailySummaries.AsNoTracking().FirstOrDefaultAsync(
            x => x.EmployeeID == request.EmployeeId && x.ShiftDate == DateOnly.FromDateTime(punchTime.Date), HttpContext.RequestAborted);
        var firebaseSummaryOk = summary == null || await _firebaseAttendanceMutations.UpsertDailySummaryAsync(summary, "MODIFIED", HttpContext.RequestAborted);

        await _audit.LogAsync("CREATE", "AttendanceLog", log.LogID.ToString(), $"Manual punch added at {punchTime:HH:mm:ss} for EmpID: {request.EmployeeId}");
        _attendanceProcessingCoordinator.Invalidate(key);
        await _refresh.NotifyPunchChangedAsync(request.EmployeeId, DateOnly.FromDateTime(punchTime.Date), "CREATED");
        await _refresh.NotifyAttendanceChangedAsync(request.EmployeeId, DateOnly.FromDateTime(punchTime.Date));

        return Ok(new { success = true, id = log.LogID, firebaseProjected = firebasePunchOk && firebaseSummaryOk });
    }

    [HttpPost("manual/full-day")]
    public async Task<IActionResult> AddFullDay([FromBody] FullDayPunchRequest request)
    {
        if (request.EmployeeId <= 0 || !DateTime.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return BadRequest(new { success = false, message = "Invalid employee or date." });

        var start = TimeOnly.TryParseExact(request.StartTime ?? "09:00", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : new TimeOnly(9, 0);
        var end = TimeOnly.TryParseExact(request.EndTime ?? "18:00", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : new TimeOnly(18, 0);

        if (await _lockService.IsLockedAsync(request.EmployeeId, day))
            return Conflict(new { success = false, message = "Payroll is finalized for this month. Cannot modify punches." });

        var key = $"{request.EmployeeId}:{day.Date:yyyy-MM-dd}";
        using var lease = await _attendanceProcessingCoordinator.AcquireAsync(key, HttpContext.RequestAborted);

        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var a = new AttendanceLog
        {
            EmployeeID = request.EmployeeId,
            PunchTime = day.Date.Add(start.ToTimeSpan()),
            DeviceID = "ManualCorrection",
            LogType = "IN",
            BiometricID = $"MANUAL_{request.EmployeeId}_{day:yyyyMMdd}_{start:HHmmss}_IN",
            IsApproved = true
        };
        var b = new AttendanceLog
        {
            EmployeeID = request.EmployeeId,
            PunchTime = day.Date.Add(end.ToTimeSpan()),
            DeviceID = "ManualCorrection",
            LogType = "OUT",
            BiometricID = $"MANUAL_{request.EmployeeId}_{day:yyyyMMdd}_{end:HHmmss}_OUT",
            IsApproved = true
        };

        db.AttendanceLogs.AddRange(a, b);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await Recalculate(db, request.EmployeeId, day.Date);

        var aOk = await _firebaseAttendanceMutations.UpsertPunchAsync(a, "CREATED", HttpContext.RequestAborted);
        var bOk = await _firebaseAttendanceMutations.UpsertPunchAsync(b, "CREATED", HttpContext.RequestAborted);
        var summary = await db.DailySummaries.AsNoTracking().FirstOrDefaultAsync(
            x => x.EmployeeID == request.EmployeeId && x.ShiftDate == DateOnly.FromDateTime(day.Date), HttpContext.RequestAborted);
        var summaryOk = summary == null || await _firebaseAttendanceMutations.UpsertDailySummaryAsync(summary, "MODIFIED", HttpContext.RequestAborted);

        await _audit.LogAsync("CREATE", "AttendanceLog", $"{a.LogID},{b.LogID}", $"Quick Added {start:hh\\:mm tt}-{end:hh\\:mm tt} punches for EmpID: {request.EmployeeId}");
        _attendanceProcessingCoordinator.Invalidate(key);
        await _refresh.NotifyPunchChangedAsync(request.EmployeeId, DateOnly.FromDateTime(day.Date), "CREATED");
        await _refresh.NotifyAttendanceChangedAsync(request.EmployeeId, DateOnly.FromDateTime(day.Date));

        return Ok(new { success = true, firebaseProjected = aOk && bOk && summaryOk });
    }

    [HttpPut("manual/{logId:int}")]
    public async Task<IActionResult> Edit(int logId, [FromBody] EditPunchRequest request)
    {
        if (!TimeOnly.TryParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return BadRequest(new { success = false, message = "Invalid time." });

        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var log = await db.AttendanceLogs.FindAsync(new object?[] { logId }, HttpContext.RequestAborted);
        if (log == null || !log.EmployeeID.HasValue)
            return NotFound(new { success = false, message = "Punch record not found." });

        var employeeId = log.EmployeeID.Value;
        var oldDay = log.PunchTime.Date;
        if (await _lockService.IsLockedAsync(employeeId, log.PunchTime))
            return Conflict(new { success = false, message = "Payroll is finalized for this month. Cannot modify punches." });

        var key = $"{employeeId}:{oldDay:yyyy-MM-dd}";
        using var lease = await _attendanceProcessingCoordinator.AcquireAsync(key, HttpContext.RequestAborted);

        log.PunchTime = oldDay.Add(t.ToTimeSpan());
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await Recalculate(db, employeeId, oldDay);

        var firebasePunchOk = await _firebaseAttendanceMutations.UpsertPunchAsync(log, "UPDATED", HttpContext.RequestAborted);
        var summary = await db.DailySummaries.AsNoTracking().FirstOrDefaultAsync(
            x => x.EmployeeID == employeeId && x.ShiftDate == DateOnly.FromDateTime(oldDay), HttpContext.RequestAborted);
        var summaryOk = summary == null || await _firebaseAttendanceMutations.UpsertDailySummaryAsync(summary, "MODIFIED", HttpContext.RequestAborted);

        await _audit.LogAsync("UPDATE", "AttendanceLog", log.LogID.ToString(), $"Punch time updated to {t:HH:mm:ss} for EmpID: {employeeId}");
        _attendanceProcessingCoordinator.Invalidate(key);
        await _refresh.NotifyPunchChangedAsync(employeeId, DateOnly.FromDateTime(oldDay), "UPDATED");
        await _refresh.NotifyAttendanceChangedAsync(employeeId, DateOnly.FromDateTime(oldDay));

        return Ok(new { success = true, firebaseProjected = firebasePunchOk && summaryOk });
    }

    [HttpDelete("manual/{logId:int}")]
    public async Task<IActionResult> Delete(int logId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var log = await db.AttendanceLogs.FindAsync(new object?[] { logId }, HttpContext.RequestAborted);
        if (log == null || !log.EmployeeID.HasValue)
            return NotFound(new { success = false, message = "Punch record not found." });

        var employeeId = log.EmployeeID.Value;
        var day = log.PunchTime.Date;
        if (await _lockService.IsLockedAsync(employeeId, log.PunchTime))
            return Conflict(new { success = false, message = "Payroll is finalized for this month. Cannot delete punches." });

        var key = $"{employeeId}:{day:yyyy-MM-dd}";
        using var lease = await _attendanceProcessingCoordinator.AcquireAsync(key, HttpContext.RequestAborted);

        var id = log.LogID;
        var time = log.PunchTime;
        db.AttendanceLogs.Remove(log);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await Recalculate(db, employeeId, day);

        var firebaseDeleteOk = await _firebaseAttendanceMutations.DeletePunchAsync(id, employeeId, HttpContext.RequestAborted);
        var summary = await db.DailySummaries.AsNoTracking().FirstOrDefaultAsync(
            x => x.EmployeeID == employeeId && x.ShiftDate == DateOnly.FromDateTime(day), HttpContext.RequestAborted);
        var summaryOk = summary == null || await _firebaseAttendanceMutations.UpsertDailySummaryAsync(summary, "MODIFIED", HttpContext.RequestAborted);

        await _audit.LogAsync("DELETE", "AttendanceLog", id.ToString(), $"Punch deleted at {time:HH:mm:ss} for EmpID: {employeeId}");
        _attendanceProcessingCoordinator.Invalidate(key);
        await _refresh.NotifyPunchChangedAsync(employeeId, DateOnly.FromDateTime(day), "DELETED");
        await _refresh.NotifyAttendanceChangedAsync(employeeId, DateOnly.FromDateTime(day));

        return Ok(new { success = true, firebaseProjected = firebaseDeleteOk && summaryOk });
    }

    [HttpPost("pending/{logId:int}/approve")]
    public Task<IActionResult> Approve(int logId) => SetPending(logId, true);

    [HttpPost("pending/{logId:int}/reject")]
    public Task<IActionResult> Reject(int logId) => SetPending(logId, false);

    private async Task<IActionResult> SetPending(int logId, bool approve)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var log = await db.AttendanceLogs.FindAsync(new object?[] { logId }, HttpContext.RequestAborted);
        if (log == null || !log.EmployeeID.HasValue)
            return NotFound(new { success = false, message = "Request not found." });

        var employeeId = log.EmployeeID.Value;
        var day = log.PunchTime.Date;
        if (await _lockService.IsLockedAsync(employeeId, log.PunchTime))
            return Conflict(new { success = false, message = "Payroll is finalized for this month. Cannot modify punches." });

        var key = $"{employeeId}:{day:yyyy-MM-dd}";
        using var lease = await _attendanceProcessingCoordinator.AcquireAsync(key, HttpContext.RequestAborted);

        if (log.IsApproved || log.LogType != "Correction Request")
            return Conflict(new { success = false, message = "This correction request has already been processed." });

        if (approve)
        {
            log.IsApproved = true;
            await db.SaveChangesAsync(HttpContext.RequestAborted);
            await Recalculate(db, employeeId, day);

            var punchOk = await _firebaseAttendanceMutations.UpsertPunchAsync(log, "APPROVED", HttpContext.RequestAborted);
            var summary = await db.DailySummaries.AsNoTracking().FirstOrDefaultAsync(
                x => x.EmployeeID == employeeId && x.ShiftDate == DateOnly.FromDateTime(day), HttpContext.RequestAborted);
            var summaryOk = summary == null || await _firebaseAttendanceMutations.UpsertDailySummaryAsync(summary, "MODIFIED", HttpContext.RequestAborted);

            await _audit.LogAsync("APPROVE", "AttendanceLog", log.LogID.ToString(), $"Punch correction approved for EmpID: {employeeId} at {log.PunchTime:hh:mm tt}");
            _attendanceProcessingCoordinator.Invalidate(key);
            await _refresh.NotifyPunchChangedAsync(employeeId, DateOnly.FromDateTime(day), "APPROVED");
            await _refresh.NotifyAttendanceChangedAsync(employeeId, DateOnly.FromDateTime(day));
            return Ok(new { success = true, firebaseProjected = punchOk && summaryOk });
        }

        var id = log.LogID;
        var time = log.PunchTime;
        db.AttendanceLogs.Remove(log);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        var deleteOk = await _firebaseAttendanceMutations.DeletePunchAsync(id, employeeId, HttpContext.RequestAborted);

        await _audit.LogAsync("REJECT/DELETE", "AttendanceLog", id.ToString(), $"Punch correction rejected and deleted for EmpID: {employeeId} at {time:hh:mm tt}");
        _attendanceProcessingCoordinator.Invalidate(key);
        await _refresh.NotifyPunchChangedAsync(employeeId, DateOnly.FromDateTime(day), "REJECTED");
        await _refresh.NotifyAttendanceChangedAsync(employeeId, DateOnly.FromDateTime(day));
        return Ok(new { success = true, firebaseProjected = deleteOk });
    }

    private async Task Recalculate(AppDbContext db, int employeeId, DateTime day, bool includePreviousOvernight = true)
    {
        var emp = await _firebaseEmployees.GetEmployeeAsync(employeeId);
        if (emp == null) return;

        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.SettingID == 1) ?? new CompanySetting();
        var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1) ?? new FeatureSettings();
        var holidays = await db.CompanyHolidays.Where(x => x.HolidayDate == DateOnly.FromDateTime(day)).ToListAsync();
        var punches = await db.AttendanceLogs.Where(x => x.EmployeeID == employeeId && x.PunchTime.Date == day.Date).ToListAsync();
        var schedule = await db.ShiftSchedules.FirstOrDefaultAsync(x => x.EmployeeID == employeeId && x.ShiftDate == DateOnly.FromDateTime(day));
        var leave = await db.LeaveRequests.FirstOrDefaultAsync(x => x.EmployeeID == employeeId && x.LeaveDate.HasValue && x.LeaveDate.Value.Date == day.Date);
        var r = _calculator.CalculateDailyResult(emp, day, punches, leave, schedule, settings, holidays, features);
        var s = await db.DailySummaries.FirstOrDefaultAsync(x => x.EmployeeID == employeeId && x.ShiftDate == DateOnly.FromDateTime(day));
        if (s == null)
        {
            s = new DailySummary { EmployeeID = employeeId, ShiftDate = DateOnly.FromDateTime(day) };
            db.DailySummaries.Add(s);
        }
        s.Status = r.Status;
        s.EarnedStandardHours = (decimal)r.EarnedStandardDuration.TotalHours;
        s.TotalOvertimeDuration = r.TotalOvertimeDuration;
        s.TotalPenaltyDuration = r.TotalPenalty;
        s.TotalLateness = r.TotalLateness;
        s.TotalBreakPenalty = r.TotalBreakPenalty;
        s.ScheduledShiftDuration = r.ScheduledShiftDuration;
        s.ShiftAllowanceEarned = r.ShiftAllowanceEarned;
        s.IsManualOverride = false;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        // REQUIREMENT: Synchronize the recalculated DailySummary to Firebase SSOT.
        await _firebaseAttendanceMutations.UpsertDailySummaryAsync(s, "MODIFIED", HttpContext.RequestAborted);

        // 1200-K: a punch just after midnight can close the previous
        // calendar day's overnight shift. Recalculate that ShiftDate too,
        // but only when its effective shift actually crosses midnight.
        if (includePreviousOvernight)
        {
            var previousDate = day.Date.AddDays(-1);
            var previousSchedule = await db.ShiftSchedules
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.EmployeeID == employeeId &&
                         x.ShiftDate == DateOnly.FromDateTime(previousDate));

            var overnight = string.Equals(emp.ShiftMode, "CONTINUOUS", StringComparison.OrdinalIgnoreCase) ||
                (previousSchedule != null
                    ? previousSchedule.EndTime <= previousSchedule.StartTime
                    : emp.ShiftStartTime.HasValue &&
                      emp.ShiftEndTime.HasValue &&
                      emp.ShiftEndTime.Value <= emp.ShiftStartTime.Value);

            if (overnight)
            {
                await Recalculate(
                    db,
                    employeeId,
                    previousDate,
                    includePreviousOvernight: false);
            }
        }
    }

    private static PunchDto ToDto(AttendanceLog x)=>new(x.LogID,x.EmployeeID??0,x.PunchTime.ToString("yyyy-MM-dd HH:mm:ss"),x.LogType??"",x.DeviceID??"",x.IsApproved);
    public sealed record IssueDayDto(int EmployeeId,string EmployeeName,string Date,List<PunchDto> Punches);
    public sealed record PunchDto(int Id,int EmployeeId,string PunchTime,string LogType,string DeviceId,bool IsApproved);
    public sealed record PendingPunchDto(int Id,int EmployeeId,string EmployeeName,string PunchTime,string LogType,string DeviceId);
    public sealed record PunchMutationRequest(int EmployeeId,string PunchTime);
    public sealed record FullDayPunchRequest(int EmployeeId,string Date,string? StartTime,string? EndTime);
    public sealed record EditPunchRequest(string Time);
}
