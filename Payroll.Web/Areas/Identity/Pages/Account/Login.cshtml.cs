using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginModel : PageModel
    {
        // ============================================================
        // CONSTANTS
        // ============================================================

        public const string DeviceCookieName =
            "BioMetric-Employee-Device";

        public const string DeviceClaimType =
            "BioMetric-Employee-Device";

        private const string InvalidLoginMessage =
            "Invalid email or password.";

        private const string AlreadyLoggedInMessage =
            "This account is already logged in on another device or browser.";

        private const string ForceLogoutInstruction =
            "Log out from the existing session before continuing on this device.";


        // ============================================================
        // SERVICES
        // ============================================================

        private readonly SignInManager<IdentityUser>
            _signInManager;

        private readonly UserManager<IdentityUser>
            _userManager;

        private readonly IDbContextFactory<AppDbContext>
            _dbFactory;

        private readonly ILogger<LoginModel>
            _logger;

        private readonly NotificationService
            _notificationService;

        private readonly GeoLocationService
            _geoLocationService;

        private readonly AttendanceEventMonitorService
            _attendanceMonitor;


        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public LoginModel(
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager,
            IDbContextFactory<AppDbContext> dbFactory,
            ILogger<LoginModel> logger,
            NotificationService notificationService,
            GeoLocationService geoLocationService,
            AttendanceEventMonitorService attendanceMonitor)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _dbFactory = dbFactory;
            _logger = logger;
            _notificationService = notificationService;
            _geoLocationService = geoLocationService;
            _attendanceMonitor = attendanceMonitor;
        }


        // ============================================================
        // INPUT
        // ============================================================

        [BindProperty]
        public InputModel Input { get; set; } = new();


        // ============================================================
        // FORCE EXISTING SESSION LOGOUT
        // ============================================================

        [BindProperty]
        public bool ForceLogoutExisting { get; set; }


        // ============================================================
        // SHOW FORCE LOGOUT OPTION
        // ============================================================

        public bool ShowForceLogout { get; private set; }


        // ============================================================
        // RETURN URL
        // ============================================================

        public string ReturnUrl { get; set; } = "/";


        // ============================================================
        // INPUT MODEL
        // ============================================================

        public class InputModel
        {
            [Required(
                ErrorMessage = "Email is required.")]
            [EmailAddress(
                ErrorMessage = "Enter a valid email address.")]
            public string Email { get; set; } = string.Empty;


            [Required(
                ErrorMessage = "Password is required.")]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;


            [Display(Name = "Remember me")]
            public bool RememberMe { get; set; } = true;
        }


        // ============================================================
        // GET
        // ============================================================

        public void OnGet(string? returnUrl = null)
        {
            ReturnUrl =
                !string.IsNullOrWhiteSpace(returnUrl)
                    ? returnUrl
                    : "/";

            Input.RememberMe = true;
        }


        // ============================================================
        // POST LOGIN
        // ============================================================

        public async Task<IActionResult> OnPostAsync(
            string? returnUrl = null)
        {
            ReturnUrl =
                !string.IsNullOrWhiteSpace(returnUrl)
                    ? returnUrl
                    : "/";


            // ========================================================
            // VALIDATE FORM
            // ========================================================

            if (!ModelState.IsValid)
            {
                return Page();
            }


            // ========================================================
            // NORMALIZE EMAIL
            // ========================================================

            var email =
                Input.Email.Trim();


            // ========================================================
            // FIND USER
            // ========================================================

            var user =
                await _userManager.FindByEmailAsync(email);


            // --------------------------------------------------------
            // Do not reveal whether an account exists.
            // --------------------------------------------------------

            if (user == null)
            {
                AddInvalidLoginError();

                return Page();
            }


            // ========================================================
            // CONFIRMED EMAIL CHECK
            // ========================================================

            if (
                !await _userManager.IsEmailConfirmedAsync(user) &&
                _userManager.Options.SignIn.RequireConfirmedEmail)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Please confirm your email address before signing in.");

                return Page();
            }


            // ========================================================
            // ROLE CHECK
            // ========================================================

            var isEmployee =
                await _userManager.IsInRoleAsync(
                    user,
                    "Employee");

            var isAdmin =
                await _userManager.IsInRoleAsync(
                    user,
                    "Admin");

            var isSuperAdmin =
                await _userManager.IsInRoleAsync(
                    user,
                    "SuperAdmin");

            var hasKnownRole =
                isEmployee ||
                isAdmin ||
                isSuperAdmin;


            // ========================================================
            // VERIFY PASSWORD FIRST
            // ========================================================
            //
            // We NEVER allow a person to force logout an existing
            // session without first proving the correct password.
            //
            // This is important.
            // ========================================================

            var passwordResult =
                await _signInManager.CheckPasswordSignInAsync(
                    user,
                    Input.Password,
                    lockoutOnFailure: false);


            if (passwordResult.IsLockedOut)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "This account is temporarily locked. Please try again later.");

                return Page();
            }


            if (passwordResult.IsNotAllowed)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "This account is currently not allowed to sign in.");

                return Page();
            }


            if (!passwordResult.Succeeded)
            {
                await _attendanceMonitor.RecordAsync(
                    "LOGIN_FAILED", user.Id, user.Email ?? email, null, "Web", "FAILED",
                    passwordResult.IsLockedOut ? "LOCKED" : (passwordResult.IsNotAllowed ? "NOT_ALLOWED" : "INVALID_CREDENTIALS"));

                AddInvalidLoginError();

                return Page();
            }


            _logger.LogInformation(
                "LOGIN PASSWORD VERIFIED. UserId={UserId}, KnownRole={KnownRole}",
                user.Id,
                hasKnownRole);


            // ========================================================
            // SINGLE SESSION FOR ALL ROLES
            // ========================================================

            var deviceId =
                GetCurrentDeviceId();

            var currentDeviceOwnsSession = false;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                currentDeviceOwnsSession =
                    await ReconcileCurrentDeviceLockAsync(
                        user.Id,
                        deviceId);
            }

            if (!currentDeviceOwnsSession)
            {
                deviceId = Guid.NewGuid().ToString("N");
            }

            deviceId ??= Guid.NewGuid().ToString("N");


            var activeDeviceBeforeLogin = await _attendanceMonitor.GetActiveDeviceIdAsync(user.Id);

            await _attendanceMonitor.RecordAsync(
                "LOGIN_ATTEMPT", user.Id, user.Email ?? email, deviceId, "Web", "PASSWORD_VERIFIED",
                ForceLogoutExisting ? "FORCE_REPLACE_REQUEST" : "NORMAL_LOGIN_ATTEMPT",
                new { CurrentDeviceOwnsSession = currentDeviceOwnsSession, ExistingSession = activeDeviceBeforeLogin != null },
                activeDeviceBeforeLogin);

            _logger.LogInformation(
                "LOGIN ATTEMPT. UserId={UserId}, ForceLogout={ForceLogout}",
                user.Id,
                ForceLogoutExisting);


            // ========================================================
            // NORMAL LOGIN
            // ========================================================

            if (!ForceLogoutExisting &&
                !currentDeviceOwnsSession)
            {
                var lockResult =
                    await TryAcquireEmployeeLockAsync(
                        user.Id,
                        deviceId);


                if (lockResult ==
                    EmployeeLockResult.AlreadyActive)
                {
                    ShowForceLogout = true;
                    ModelState.AddModelError(
                        string.Empty,
                        $"{AlreadyLoggedInMessage} {ForceLogoutInstruction}");

                    await _attendanceMonitor.RecordAsync(
                        "SECOND_DEVICE_ATTEMPT", user.Id, user.Email ?? email, deviceId, "Web",
                        "EXISTING_SESSION_FOUND", "SINGLE_DEVICE_POLICY",
                        new { ExistingSession = true }, activeDeviceBeforeLogin);

                    await NotifyBlockedLoginAsync(
                        user,
                        email);

                    return Page();
                }


                if (lockResult !=
                    EmployeeLockResult.Acquired)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "We could not start your employee session. Please try again.");

                    return Page();
                }
            }


            // ========================================================
            // FORCE LOGOUT EXISTING SESSION
            // ========================================================

            else
            {
                await _attendanceMonitor.RecordAsync(
                    "FORCE_LOGOUT_REQUESTED", user.Id, user.Email ?? email, deviceId, "Web",
                    "REQUESTED", "EMPLOYEE_CONFIRMED_EXISTING_SESSION_REPLACEMENT",
                    new { ForceLogoutExisting }, activeDeviceBeforeLogin);

                _logger.LogWarning(
                    "FORCE LOGIN REQUEST. UserId={UserId}",
                    user.Id);


                if (!await ReplaceAndInvalidateEmployeeSessionAsync(
                        user,
                        deviceId))
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "The existing session could not be replaced. Please try again.");

                    return Page();
                }


                await _attendanceMonitor.RecordAsync(
                    "FORCED_SESSION_LOGOUT", user.Id, user.Email ?? email, activeDeviceBeforeLogin, "Web",
                    "TERMINATED", "REPLACED_BY_NEW_DEVICE",
                    new { NewDeviceId = deviceId }, activeDeviceBeforeLogin);

                _logger.LogWarning(
                    "EXISTING EMPLOYEE SESSION INVALIDATED. UserId={UserId}",
                    user.Id);
            }


            // ========================================================
            // CREATE NEW AUTHENTICATION COOKIE
            // ========================================================

            try
            {
                var claims =
                    new[]
                    {
                        new Claim(
                            DeviceClaimType,
                            deviceId)
                    };


                await _signInManager.SignInWithClaimsAsync(
                    user,
                    Input.RememberMe,
                    claims);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "EMPLOYEE IDENTITY SIGN-IN FAILED. UserId={UserId}",
                    user.Id);


                await RemoveEmployeeLockAsync(
                    user.Id,
                    deviceId);


                ModelState.AddModelError(
                    string.Empty,
                    "Unable to sign in. Please try again.");

                return Page();
            }


            // ========================================================
            // DEVICE COOKIE
            // ========================================================

            Response.Cookies.Append(
                DeviceCookieName,
                deviceId,
                new CookieOptions
                {
                    HttpOnly = true,

                    Secure = true,

                    SameSite =
                        SameSiteMode.Lax,

                    IsEssential = true,

                    MaxAge =
                        TimeSpan.FromDays(365),

                    Path = "/"
                });

            var httpContext = HttpContext;
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unavailable";
            var forwardedIp = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedIp))
                ipAddress = forwardedIp.Split(',')[0].Trim();

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(userAgent))
                userAgent = "Unavailable";

            try
            {
                await _notificationService.NotifyAdminsEmployeeLoginAsync(
                    user.UserName ?? user.Email ?? user.Id,
                    user.Email ?? email,
                    ipAddress,
                    userAgent,
                    DateTime.UtcNow,
                    ForceLogoutExisting,
                    "GPS coordinates will be available after device tracking starts");
            }
            catch (Exception notificationEx)
            {
                _logger.LogWarning(
                    notificationEx,
                    "Employee login succeeded but admin notification failed. UserId={UserId}",
                    user.Id);
            }


            await _attendanceMonitor.RecordAsync(
                "LOGIN_SUCCESS", user.Id, user.Email ?? email, deviceId, "Web", "SUCCESS",
                ForceLogoutExisting ? "NEW_DEVICE_AFTER_FORCE_REPLACE" : "SESSION_STARTED",
                new { ForceLogoutExisting }, ForceLogoutExisting ? activeDeviceBeforeLogin : null);

            // ========================================================
            // SUCCESS
            // ========================================================

            _logger.LogInformation(
                "EMPLOYEE LOGIN SUCCESS. UserId={UserId}, DeviceId={DeviceId}, Forced={Forced}",
                user.Id,
                deviceId,
                ForceLogoutExisting);


            if (isAdmin || isSuperAdmin)
            {
                if (!string.IsNullOrWhiteSpace(ReturnUrl) &&
                    ReturnUrl != "/" &&
                    Url.IsLocalUrl(ReturnUrl))
                {
                    return LocalRedirect(ReturnUrl);
                }

                return LocalRedirect("/");
            }

            return LocalRedirect("/employee-home");
        }


        private string? GetCurrentDeviceId()
        {
            if (Request.Cookies.TryGetValue(
                    DeviceCookieName,
                    out var deviceId) &&
                !string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId.Trim();
            }

            return null;
        }

        private async Task<bool> ReconcileCurrentDeviceLockAsync(
            string userId,
            string deviceId)
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            var lockRecord =
                await db.EmployeeDeviceLocks
                    .FirstOrDefaultAsync(lockItem =>
                        lockItem.UserId == userId);

            if (lockRecord == null)
                return false;

            if (lockRecord.DeviceId != deviceId)
            {
                lockRecord.DeviceId = deviceId;
                lockRecord.LastSeenAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            return true;
        }

        private async Task NotifyBlockedLoginAsync(
            IdentityUser user,
            string email)
        {
            try
            {
                var ipAddress =
                    HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unavailable";
                var forwardedIp =
                    Request.Headers["X-Forwarded-For"].FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(forwardedIp))
                    ipAddress = forwardedIp.Split(',')[0].Trim();

                await _notificationService.NotifyAdminsEmployeeLoginAsync(
                    user.UserName ?? user.Email ?? user.Id,
                    user.Email ?? email,
                    ipAddress,
                    Request.Headers.UserAgent.ToString(),
                    DateTime.UtcNow,
                    replacedExistingSession: false,
                    gpsDetails: "BLOCKED: another active device session exists",
                    blockedExistingSession: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Blocked login notification failed. UserId={UserId}",
                    user.Id);
            }
        }

        // ============================================================
        // EMPLOYEE LOCK RESULT
        // ============================================================

        private async Task<bool> ReplaceAndInvalidateEmployeeSessionAsync(
            IdentityUser user,
            string deviceId)
        {
            var replaced = await ForceReplaceEmployeeLockAsync(
                user.Id,
                deviceId);

            if (!replaced)
                return false;

            // ============================================================
            // END GPS SESSION FOR FORCE LOGOUT
            // ============================================================
            //
            // When forcing logout, we need to:
            // 1. End any active GPS session in the database
            // 2. Remove it from the in-memory live location store
            // 3. This ensures the admin dashboard immediately shows offline
            // ============================================================

            try
            {
                // Identity User.Id is a string and is NOT the payroll EmployeeID.
                // Resolve the real employee key from the existing SSOT link before
                // ending GPS sessions. This fixes force-login cleanup without any
                // database/schema change.
                await using var gpsDb = await _dbFactory.CreateDbContextAsync();
                var employee = await gpsDb.Employees
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.AspNetUserId == user.Id && !x.IsDeleted);

                if (employee != null)
                {
                    var endedCount = await _geoLocationService.EndAllGpsSessionsAsync(
                        employee.EmployeeID,
                        "FORCE_LOGGED_OUT");

                    _logger.LogInformation(
                        "GPS sessions ended for force logout. UserId={UserId}, EmployeeId={EmployeeId}, SessionsEnded={SessionsEnded}",
                        user.Id,
                        employee.EmployeeID,
                        endedCount);
                }
                else
                {
                    _logger.LogWarning(
                        "No payroll employee link found while force-replacing session. UserId={UserId}",
                        user.Id);
                }
            }
            catch (Exception gpsEx)
            {
                _logger.LogWarning(
                    gpsEx,
                    "Failed to end GPS sessions during force logout. UserId={UserId}",
                    user.Id);

                // GPS cleanup failure should not block session replacement.
            }

            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (stampResult.Succeeded)
                return true;

            _logger.LogError(
                "SECURITY STAMP UPDATE FAILED DURING EMPLOYEE SESSION REPLACEMENT. UserId={UserId}",
                user.Id);

            await RemoveEmployeeLockAsync(user.Id, deviceId);
            return false;
        }

        private enum EmployeeLockResult
        {
            Acquired,
            AlreadyActive
        }


        // ============================================================
        // NORMAL LOCK ACQUISITION
        // ============================================================

        private async Task<EmployeeLockResult>
            TryAcquireEmployeeLockAsync(
                string userId,
                string deviceId)
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();


            var connection =
                db.Database.GetDbConnection();


            if (connection is not NpgsqlConnection postgres)
            {
                throw new InvalidOperationException(
                    "Employee device locking requires PostgreSQL.");
            }


            if (postgres.State !=
                ConnectionState.Open)
            {
                await postgres.OpenAsync();
            }


            const string sql =
                """
                INSERT INTO public."employee_device_locks"
                (
                    "Id",
                    "UserId",
                    "DeviceId",
                    "CreatedAtUtc",
                    "LastSeenAtUtc"
                )
                VALUES
                (
                    @id,
                    @userId,
                    @deviceId,
                    @createdAtUtc,
                    @lastSeenAtUtc
                )
                ON CONFLICT ("UserId")
                DO NOTHING
                RETURNING "Id";
                """;


            await using var command =
                new NpgsqlCommand(
                    sql,
                    postgres);


            var now =
                DateTime.UtcNow;


            command.Parameters.AddWithValue(
                "id",
                Guid.NewGuid());

            command.Parameters.AddWithValue(
                "userId",
                userId);

            command.Parameters.AddWithValue(
                "deviceId",
                deviceId);

            command.Parameters.AddWithValue(
                "createdAtUtc",
                now);

            command.Parameters.AddWithValue(
                "lastSeenAtUtc",
                now);


            var result =
                await command.ExecuteScalarAsync();


            if (
                result == null ||
                result == DBNull.Value)
            {
                return EmployeeLockResult.AlreadyActive;
            }


            return EmployeeLockResult.Acquired;
        }


        // ============================================================
        // FORCE REPLACE LOCK
        // ============================================================
        //
        // This is ONLY called after:
        //
        // 1. Correct password was supplied.
        // 2. User explicitly clicked Force Logout.
        //
        // It removes the old lock and creates the new one.
        // ============================================================

        private async Task<bool>
            ForceReplaceEmployeeLockAsync(
                string userId,
                string newDeviceId)
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();


            var connection =
                db.Database.GetDbConnection();


            if (connection is not NpgsqlConnection postgres)
            {
                throw new InvalidOperationException(
                    "Employee device locking requires PostgreSQL.");
            }


            if (postgres.State !=
                ConnectionState.Open)
            {
                await postgres.OpenAsync();
            }


            await using var transaction =
                await postgres.BeginTransactionAsync(
                    IsolationLevel.Serializable);


            try
            {
                // ----------------------------------------------------
                // Remove current active session.
                // ----------------------------------------------------

                const string deleteSql =
                    """
                    DELETE FROM public."employee_device_locks"
                    WHERE "UserId" = @userId;
                    """;


                await using (
                    var deleteCommand =
                        new NpgsqlCommand(
                            deleteSql,
                            postgres,
                            transaction))
                {
                    deleteCommand.Parameters.AddWithValue(
                        "userId",
                        userId);

                    await deleteCommand.ExecuteNonQueryAsync();
                }


                // ----------------------------------------------------
                // Create new active session.
                // ----------------------------------------------------

                const string insertSql =
                    """
                    INSERT INTO public."employee_device_locks"
                    (
                        "Id",
                        "UserId",
                        "DeviceId",
                        "CreatedAtUtc",
                        "LastSeenAtUtc"
                    )
                    VALUES
                    (
                        @id,
                        @userId,
                        @deviceId,
                        @createdAtUtc,
                        @lastSeenAtUtc
                    )
                    ON CONFLICT ("UserId")
                    DO NOTHING
                    RETURNING "Id";
                    """;


                await using (
                    var insertCommand =
                        new NpgsqlCommand(
                            insertSql,
                            postgres,
                            transaction))
                {
                    var now =
                        DateTime.UtcNow;


                    insertCommand.Parameters.AddWithValue(
                        "id",
                        Guid.NewGuid());

                    insertCommand.Parameters.AddWithValue(
                        "userId",
                        userId);

                    insertCommand.Parameters.AddWithValue(
                        "deviceId",
                        newDeviceId);

                    insertCommand.Parameters.AddWithValue(
                        "createdAtUtc",
                        now);

                    insertCommand.Parameters.AddWithValue(
                        "lastSeenAtUtc",
                        now);


                    var result =
                        await insertCommand.ExecuteScalarAsync();


                    if (
                        result == null ||
                        result == DBNull.Value)
                    {
                        await transaction.RollbackAsync();

                        return false;
                    }
                }


                await transaction.CommitAsync();


                _logger.LogWarning(
                    "EMPLOYEE ACTIVE SESSION REPLACED. UserId={UserId}, NewDeviceId={DeviceId}",
                    userId,
                    newDeviceId);


                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch
                {
                    // Ignore rollback failure.
                }


                _logger.LogError(
                    ex,
                    "EMPLOYEE FORCE SESSION REPLACEMENT FAILED. UserId={UserId}",
                    userId);


                return false;
            }
        }


        // ============================================================
        // REMOVE LOCK
        // ============================================================

        private async Task RemoveEmployeeLockAsync(
            string userId,
            string deviceId)
        {
            if (
                string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }


            try
            {
                await using var db =
                    await _dbFactory.CreateDbContextAsync();


                var lockRecord =
                    await db.EmployeeDeviceLocks
                        .FirstOrDefaultAsync(
                            x =>
                                x.UserId == userId &&
                                x.DeviceId == deviceId);


                if (lockRecord == null)
                {
                    return;
                }


                db.EmployeeDeviceLocks.Remove(
                    lockRecord);


                await db.SaveChangesAsync();


                _logger.LogInformation(
                    "EMPLOYEE LOCK REMOVED AFTER LOGIN FAILURE. UserId={UserId}, DeviceId={DeviceId}",
                    userId,
                    deviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "EMPLOYEE LOCK CLEANUP FAILED. UserId={UserId}, DeviceId={DeviceId}",
                    userId,
                    deviceId);
            }
        }


        // ============================================================
        // INVALID LOGIN
        // ============================================================

        private void AddInvalidLoginError()
        {
            ModelState.AddModelError(
                string.Empty,
                InvalidLoginMessage);
        }
    }
}