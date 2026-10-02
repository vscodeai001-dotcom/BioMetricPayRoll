using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;


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

        private readonly FirebaseEmployeeManagementService
            _firebaseEmployees;

        private readonly FirebaseEmployeePresenceService
            _firebasePresence;

        private readonly FirebaseRealtimeService
            _firebaseRealtime;
        private readonly IAppModeService
            _appMode;


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
            AttendanceEventMonitorService attendanceMonitor,
            FirebaseEmployeeManagementService firebaseEmployees,
            FirebaseEmployeePresenceService firebasePresence,
            FirebaseRealtimeService firebaseRealtime,
            IAppModeService appMode)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _dbFactory = dbFactory;
            _logger = logger;
            _notificationService = notificationService;
            _geoLocationService = geoLocationService;
            _attendanceMonitor = attendanceMonitor;
            _firebaseEmployees = firebaseEmployees;
            _firebasePresence = firebasePresence;
            _firebaseRealtime = firebaseRealtime;
            _appMode = appMode;
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
            // FIND AND VERIFY USER (Identity + Firebase Auth)
            // ========================================================

            var user =
                await _userManager.FindByEmailAsync(email);

            var passwordVerified = false;

            if (user != null)
            {
                var localPasswordResult =
                    await _signInManager.CheckPasswordSignInAsync(
                        user,
                        Input.Password,
                        lockoutOnFailure: false);

                if (localPasswordResult.Succeeded)
                {
                    passwordVerified = true;
                }
                else if (localPasswordResult.IsLockedOut)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "This account is temporarily locked. Please try again later.");
                    return Page();
                }
                else if (localPasswordResult.IsNotAllowed)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "This account is currently not allowed to sign in.");
                    return Page();
                }
            }

            // If user does not exist in local Identity or local password failed,
            // authenticate against Firebase Authentication directly.
            if (!passwordVerified)
            {
                var fbResult = await _firebaseRealtime.VerifyEmailPasswordAsync(
                    email,
                    Input.Password,
                    HttpContext.RequestAborted);

                if (fbResult.Success)
                {
                    passwordVerified = true;

                    if (user == null)
                    {
                        user = new IdentityUser
                        {
                            Id = fbResult.LocalId ?? Guid.NewGuid().ToString(),
                            Email = email,
                            UserName = email,
                            EmailConfirmed = true
                        };

                        var createResult = await _userManager.CreateAsync(user, Input.Password);
                        if (createResult.Succeeded)
                        {
                            var fbEmp = await _firebaseEmployees.GetEmployeeByEmailAsync(email);
                            var role = string.Equals(email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase)
                                ? "SuperAdmin" : (fbEmp != null ? "Employee" : "Admin");
                            await _userManager.AddToRoleAsync(user, role);

                            if (fbEmp != null)
                            {
                                await using var db = await _dbFactory.CreateDbContextAsync();
                                var dbEmp = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeID == fbEmp.EmployeeID);
                                if (dbEmp != null)
                                {
                                    dbEmp.AspNetUserId = user.Id;
                                    dbEmp.Email = email;
                                    await db.SaveChangesAsync();
                                }
                            }
                        }
                    }
                    else
                    {
                        // Sync updated Firebase password into local Identity
                        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                        await _userManager.ResetPasswordAsync(user, resetToken, Input.Password);
                    }
                }
            }

            if (user == null || !passwordVerified)
            {
                if (user != null)
                {
                    await _attendanceMonitor.RecordAsync(
                        "LOGIN_FAILED", user.Id, user.Email ?? email, null, "Web", "FAILED", "INVALID_CREDENTIALS");
                }

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
                    "SuperAdmin") ||
                string.Equals(email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase);

            var hasKnownRole =
                isEmployee ||
                isAdmin ||
                isSuperAdmin;


            _logger.LogInformation(
                "LOGIN PASSWORD VERIFIED. UserId={UserId}, KnownRole={KnownRole}",
                user.Id,
                hasKnownRole);

            // Resolve the canonical employee from Firebase when the legacy Web
            // Identity row has not yet been linked. A unique email match is safe
            // because Firebase employees are the application SSOT.
            var firebaseEmployee = isEmployee
                ? await _firebaseEmployees.GetEmployeeByEmailAsync(email)
                : null;

            if (isEmployee && firebaseEmployee == null)
            {
                AddInvalidLoginError();
                _logger.LogWarning(
                    "Employee login rejected because no saved Firebase Employee record is linked to {Email}.",
                    email);
                return Page();
            }

            // Successful Web authentication is the bridge that keeps Web and
            // Android credentials/claims identical. The password is used only
            // for this Firebase Auth synchronization and is never stored.
            string? firebaseUid = null;
            if (isEmployee && firebaseEmployee != null)
            {
                firebaseUid = await _firebaseEmployeesReconcileLoginAsync(
                    firebaseEmployee,
                    email,
                    Input.Password,
                    ct: HttpContext.RequestAborted);

                if (string.IsNullOrWhiteSpace(firebaseUid))
                {
                    firebaseUid = await _firebaseEmployees.GetFirebaseAuthUidByEmailAsync(
                        email,
                        HttpContext.RequestAborted);
                }

                if (string.IsNullOrWhiteSpace(firebaseUid))
                {
                    var fbResult = await _firebaseRealtime.VerifyEmailPasswordAsync(
                        email,
                        Input.Password,
                        HttpContext.RequestAborted);
                    if (fbResult.Success && !string.IsNullOrWhiteSpace(fbResult.LocalId))
                    {
                        firebaseUid = fbResult.LocalId;
                    }
                }

                if (string.IsNullOrWhiteSpace(firebaseUid) && !string.IsNullOrWhiteSpace(firebaseEmployee.AspNetUserId))
                {
                    firebaseUid = firebaseEmployee.AspNetUserId;
                }

                if (string.IsNullOrWhiteSpace(firebaseUid) && user != null && !string.IsNullOrWhiteSpace(user.Id))
                {
                    firebaseUid = user.Id;
                }
            }

            // ========================================================
            // SUPERADMIN & ADMIN: UNRESTRICTED INSTANT LOGIN
            // ========================================================
            // SuperAdmin and Company Admins are governance/management users
            // and must NEVER be blocked by single-device employee locks or
            // employee session replacement constraints.
            // ========================================================

            if (isSuperAdmin || isAdmin || !isEmployee)
            {
                var customClaims = new List<Claim>();

                string? tenantId = null;
                if (!isSuperAdmin)
                {
                    var currentUserId = user!.Id;
                    await using var db = await _dbFactory.CreateDbContextAsync();
                    var tenant = await db.CompanyTenants
                        .FirstOrDefaultAsync(t => (currentUserId != null && t.AdminUserId == currentUserId) ||
                                                  (t.AdminEmail.ToLower() == email.ToLower()));
                    if (tenant != null)
                    {
                        if (string.IsNullOrWhiteSpace(tenant.AdminUserId) || tenant.AdminUserId != currentUserId)
                        {
                            tenant.AdminUserId = currentUserId;
                            await db.SaveChangesAsync();
                        }
                        tenantId = tenant.TenantId;
                        customClaims.Add(new Claim("TenantId", tenantId));
                        customClaims.Add(new Claim("OwnerUid", tenantId));
                        customClaims.Add(new Claim("CompanyName", tenant.CompanyName));
                    }
                }
                else
                {
                    customClaims.Add(new Claim("IsSuperAdmin", "true"));
                }

                await _signInManager.SignInWithClaimsAsync(user!, Input.RememberMe, customClaims);
                _logger.LogInformation("ADMIN / SUPERADMIN LOGIN SUCCESS WITHOUT RESTRICTION. UserId={UserId}, Email={Email}, TenantId={TenantId}", user!.Id, email, tenantId);

                await _attendanceMonitor.RecordAsync(
                    "LOGIN_SUCCESS", user.Id, user.Email ?? email, "WEB_ADMIN", "Web", "SUCCESS",
                    "ADMIN_LOGIN_UNRESTRICTED", null, null);

                if (isSuperAdmin)
                {
                    if (!await _appMode.IsOfflineModeAsync())
                    {
                        return LocalRedirect("/superadmin/tenants");
                    }
                    return LocalRedirect("/");
                }

                if (!string.IsNullOrWhiteSpace(ReturnUrl) && ReturnUrl != "/" && Url.IsLocalUrl(ReturnUrl))
                {
                    return LocalRedirect(ReturnUrl);
                }

                return LocalRedirect("/");
            }

            // ========================================================
            // SINGLE SESSION FOR EMPLOYEES ONLY
            // ========================================================

            var deviceId =
                GetCurrentDeviceId();

            var currentDeviceOwnsSession = false;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                currentDeviceOwnsSession =
                    await ReconcileCurrentDeviceLockAsync(
                        user!.Id,
                        deviceId);
            }

            if (!currentDeviceOwnsSession)
            {
                deviceId = Guid.NewGuid().ToString("N");
            }

            deviceId ??= Guid.NewGuid().ToString("N");


            var activeDeviceBeforeLogin = await _attendanceMonitor.GetActiveDeviceIdAsync(user!.Id);

            await _attendanceMonitor.RecordAsync(
                "LOGIN_ATTEMPT", user.Id, user.Email ?? email, deviceId, "Web", "PASSWORD_VERIFIED",
                "AUTOMATIC_SINGLE_SESSION_PROVISIONING",
                new { CurrentDeviceOwnsSession = currentDeviceOwnsSession, ExistingSession = activeDeviceBeforeLogin != null },
                activeDeviceBeforeLogin);

            _logger.LogInformation(
                "LOGIN ATTEMPT. UserId={UserId}, DeviceId={DeviceId}, Owns={Owns}",
                user.Id, deviceId, currentDeviceOwnsSession);


            // ========================================================
            // EXPLICIT CROSS-DEVICE SESSION ENFORCEMENT
            // ========================================================
            // Only an explicit user confirmation may replace an existing
            // employee login on another device/platform. Network loss, GPS
            // loss and airplane mode never end a login session.
            // ========================================================

            var firebaseActiveDevice = string.Empty;
            var firebaseActivePlatform = string.Empty;
            var firebaseActiveDeviceName = string.Empty;
            long firebaseActiveLastSeen = 0;
            string? foundSessionUid = null;

            if (isEmployee)
            {
                try
                {
                    // 1. Direct path check if firebaseUid is known
                    if (!string.IsNullOrWhiteSpace(firebaseUid))
                    {
                        var firebaseSession = await _firebaseRealtime.GetGlobalRecordAsync(
                            $"employee_sessions/{firebaseUid}",
                            HttpContext.RequestAborted);

                        if (firebaseSession.HasValue && firebaseSession.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foundSessionUid = firebaseUid;
                            if (firebaseSession.Value.TryGetProperty("deviceId", out var deviceProp))
                                firebaseActiveDevice = deviceProp.GetString() ?? string.Empty;
                            if (firebaseSession.Value.TryGetProperty("platform", out var platProp))
                                firebaseActivePlatform = platProp.GetString() ?? string.Empty;
                            if (firebaseSession.Value.TryGetProperty("deviceName", out var devNameProp))
                                firebaseActiveDeviceName = devNameProp.GetString() ?? string.Empty;
                            if (firebaseSession.Value.TryGetProperty("lastSeenAt", out var lastSeenProp))
                            {
                                if (lastSeenProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    firebaseActiveLastSeen = lastSeenProp.GetInt64();
                            }
                        }
                    }

                    // 2. Fallback scan of employee_sessions if direct lookup had no active device
                    if (string.IsNullOrWhiteSpace(firebaseActiveDevice) && firebaseEmployee != null)
                    {
                        var allSessions = await _firebaseRealtime.GetGlobalRecordAsync("employee_sessions", HttpContext.RequestAborted);
                        if (allSessions.HasValue && allSessions.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var sProp in allSessions.Value.EnumerateObject())
                            {
                                if (sProp.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                                var empIdVal = 0;
                                if (sProp.Value.TryGetProperty("employeeId", out var empIdProp))
                                {
                                    if (empIdProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        empIdVal = empIdProp.GetInt32();
                                    else if (empIdProp.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(empIdProp.GetString(), out var parsed))
                                        empIdVal = parsed;
                                }

                                if (empIdVal == firebaseEmployee.EmployeeID)
                                {
                                    foundSessionUid = sProp.Name;
                                    if (string.IsNullOrWhiteSpace(firebaseUid)) firebaseUid = foundSessionUid;
                                    if (sProp.Value.TryGetProperty("deviceId", out var deviceProp))
                                        firebaseActiveDevice = deviceProp.GetString() ?? string.Empty;
                                    if (sProp.Value.TryGetProperty("platform", out var platProp))
                                        firebaseActivePlatform = platProp.GetString() ?? string.Empty;
                                    if (sProp.Value.TryGetProperty("deviceName", out var devNameProp))
                                        firebaseActiveDeviceName = devNameProp.GetString() ?? string.Empty;
                                    if (sProp.Value.TryGetProperty("lastSeenAt", out var lastSeenProp))
                                    {
                                        if (lastSeenProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                            firebaseActiveLastSeen = lastSeenProp.GetInt64();
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not inspect Firebase employee session for {EmployeeId}", firebaseEmployee?.EmployeeID);
                }
            }

            // Retrieve last known GPS location & workplace distance for the displaced/existing device
            double existingLat = 0;
            double existingLng = 0;
            double existingDistanceMeters = 0;
            bool existingWithinRadius = false;
            DateTime? existingLocationTime = null;

            if (firebaseEmployee != null)
            {
                var liveLoc = LiveLocationStore.Get(firebaseEmployee.EmployeeID);
                if (liveLoc != null)
                {
                    existingLat = liveLoc.Latitude;
                    existingLng = liveLoc.Longitude;
                    existingDistanceMeters = liveLoc.DistanceMeters;
                    existingWithinRadius = liveLoc.IsWithinAllowedRadius;
                    existingLocationTime = liveLoc.LastUpdatedUtc;
                }
                else
                {
                    try
                    {
                        await using var gpsDb = await _dbFactory.CreateDbContextAsync();
                        var activeGpsSession = await gpsDb.EmployeeGpsSessions.AsNoTracking()
                            .Where(s => s.EmployeeId == firebaseEmployee.EmployeeID && s.EndedAtUtc == null)
                            .OrderByDescending(s => s.LastUpdateAtUtc)
                            .FirstOrDefaultAsync();

                        if (activeGpsSession != null)
                        {
                            existingLat = activeGpsSession.LastLatitude ?? 0;
                            existingLng = activeGpsSession.LastLongitude ?? 0;
                            existingDistanceMeters = activeGpsSession.LastDistanceFromOfficeMeters ?? 0;
                            existingWithinRadius = activeGpsSession.LastIsWithinAllowedRadius ?? false;
                            existingLocationTime = activeGpsSession.LastUpdateAtUtc;
                        }
                    }
                    catch { }
                }
            }

            var isCurrentWebBrowser = !string.IsNullOrWhiteSpace(firebaseActiveDevice) &&
                firebaseActiveDevice.StartsWith("WEB_BROWSER_", StringComparison.OrdinalIgnoreCase) &&
                firebaseActiveDevice.EndsWith(deviceId[..Math.Min(8, deviceId.Length)], StringComparison.OrdinalIgnoreCase);

            var firebaseOtherDeviceActive = !string.IsNullOrWhiteSpace(firebaseActiveDevice) && !isCurrentWebBrowser;

            var existingPlatformName = !string.IsNullOrWhiteSpace(firebaseActivePlatform)
                ? firebaseActivePlatform
                : (firebaseActiveDevice.StartsWith("WEB_BROWSER_", StringComparison.OrdinalIgnoreCase) ? "Web" : "Android");

            var existingDeviceLabel = !string.IsNullOrWhiteSpace(firebaseActiveDeviceName)
                ? firebaseActiveDeviceName
                : (existingPlatformName.Equals("Android", StringComparison.OrdinalIgnoreCase) ? $"Android Device ({firebaseActiveDevice})" : $"Web Browser ({firebaseActiveDevice})");

            var existingLocationDesc = (existingLat != 0 || existingLng != 0)
                ? $"Lat {existingLat:F5}, Lng {existingLng:F5} ({existingDistanceMeters:F0}m from office {(existingWithinRadius ? "[Inside Geofence]" : "[Outside Geofence]")})"
                : "No active GPS fix";

            var indiaZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            var existingTimeDesc = existingLocationTime.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(existingLocationTime.Value, DateTimeKind.Utc), indiaZone).ToString("dd-MMM HH:mm:ss")
                : (firebaseActiveLastSeen > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(firebaseActiveLastSeen).ToOffset(TimeSpan.FromHours(5.5)).ToString("dd-MMM HH:mm:ss") : "Recent");

            var httpContext = HttpContext;
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unavailable";
            var forwardedIp = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedIp))
                ipAddress = forwardedIp.Split(',')[0].Trim();

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(userAgent))
                userAgent = "Unavailable";

            if (isEmployee &&
                (activeDeviceBeforeLogin != null || firebaseOtherDeviceActive) &&
                !currentDeviceOwnsSession &&
                !ForceLogoutExisting)
            {
                ShowForceLogout = true;
                var warningMessage = $"This employee account is already active on {existingPlatformName} ({existingDeviceLabel}). " +
                    $"Last active: {existingTimeDesc}, Location: {existingLocationDesc}. " +
                    $"Check 'Log out previous device and continue' to switch to this device.";

                ModelState.AddModelError(string.Empty, warningMessage);

                await _attendanceMonitor.RecordAsync(
                    "SECOND_DEVICE_ATTEMPT",
                    user.Id,
                    user.Email ?? email,
                    deviceId,
                    "Web",
                    "BLOCKED_REQUIRE_CONFIRMATION",
                    "EXISTING_DEVICE_ACTIVE",
                    new
                    {
                        EmployeeId = firebaseEmployee?.EmployeeID,
                        Summary = $"Login attempt blocked: Account is already active on {existingPlatformName} ({existingDeviceLabel}) at {existingLocationDesc}. Explicit confirmation required to displace.",
                        DisplacedDevice = new
                        {
                            DeviceId = firebaseActiveDevice,
                            Platform = existingPlatformName,
                            DeviceName = existingDeviceLabel,
                            LastSeen = existingTimeDesc,
                            LastLatitude = existingLat,
                            LastLongitude = existingLng,
                            LastDistanceMeters = existingDistanceMeters,
                            WithinAllowedRadius = existingWithinRadius
                        },
                        NewDevice = new
                        {
                            DeviceId = deviceId,
                            Platform = "Web",
                            IpAddress = ipAddress,
                            UserAgent = userAgent,
                            AttemptedAtUtc = DateTime.UtcNow
                        }
                    },
                    firebaseActiveDevice);

                return Page();
            }

            if (!currentDeviceOwnsSession)
            {
                _logger.LogWarning("FORCED SESSION REPLACEMENT. UserId={UserId}, NewDevice={DeviceId}", user.Id, deviceId);

                if (!await ReplaceAndInvalidateEmployeeSessionAsync(user, deviceId))
                {
                    ModelState.AddModelError(string.Empty, "Unable to establish employee session.");
                    return Page();
                }

                // Authoritatively write Web's session lock into employee_sessions/{firebaseUid}
                // so Android's observeSessionActive() immediately sees that activeDevice has changed
                // to WEB_BROWSER_... and stops tracking / logs out.
                if (isEmployee && !string.IsNullOrWhiteSpace(firebaseUid))
                {
                    try
                    {
                        var sessionRow = new Dictionary<string, object?>
                        {
                            ["deviceId"] = "WEB_BROWSER_" + deviceId[..Math.Min(8, deviceId.Length)],
                            ["employeeId"] = firebaseEmployee?.EmployeeID,
                            ["ownerUid"] = firebaseEmployee?.TenantId,
                            ["uid"] = firebaseUid,
                            ["platform"] = "WEB",
                            ["deviceName"] = "Web Browser",
                            ["lastSeenAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        await _firebaseRealtime.SetGlobalRecordAsync(
                            $"employee_sessions/{firebaseUid}",
                            sessionRow,
                            HttpContext.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update Firebase employee session for forced replacement. EmployeeId={EmployeeId}", firebaseEmployee?.EmployeeID);
                    }
                }

                if (isEmployee && (firebaseOtherDeviceActive || activeDeviceBeforeLogin != null))
                {
                    await _attendanceMonitor.RecordAsync(
                        "FORCED_SESSION_LOGOUT",
                        user.Id,
                        user.Email ?? email,
                        firebaseActiveDevice,
                        "Web",
                        "TERMINATED",
                        "REPLACED_BY_NEW_DEVICE",
                        new
                        {
                            EmployeeId = firebaseEmployee?.EmployeeID,
                            Summary = $"Single-Device Rule Enforced: Displaced {existingPlatformName} session ({existingDeviceLabel} at {existingLocationDesc}). New session established on Web Browser (IP: {ipAddress}).",
                            DisplacedDevice = new
                            {
                                DeviceId = firebaseActiveDevice,
                                Platform = existingPlatformName,
                                DeviceName = existingDeviceLabel,
                                LastSeen = existingTimeDesc,
                                LastLatitude = existingLat,
                                LastLongitude = existingLng,
                                LastDistanceMeters = existingDistanceMeters,
                                WithinAllowedRadius = existingWithinRadius
                            },
                            NewDevice = new
                            {
                                DeviceId = deviceId,
                                Platform = "Web",
                                IpAddress = ipAddress,
                                UserAgent = userAgent,
                                EstablishedAtUtc = DateTime.UtcNow
                            }
                        },
                        deviceId);
                }
            }


            // ========================================================
            // CREATE NEW AUTHENTICATION COOKIE
            // ========================================================

            try
            {
                var customClaims = new List<Claim>
                {
                    new Claim(DeviceClaimType, deviceId)
                };

                if (!string.IsNullOrWhiteSpace(firebaseUid))
                {
                    customClaims.Add(new Claim("FirebaseUid", firebaseUid));
                }

                if (firebaseEmployee != null)
                {
                    customClaims.Add(new Claim("employee_id", firebaseEmployee.EmployeeID.ToString()));
                    customClaims.Add(new Claim("EmployeeID", firebaseEmployee.EmployeeID.ToString()));

                    if (!string.IsNullOrWhiteSpace(firebaseEmployee.TenantId))
                    {
                        customClaims.Add(new Claim("OwnerUid", firebaseEmployee.TenantId));
                        customClaims.Add(new Claim("TenantId", firebaseEmployee.TenantId));
                    }
                }

                await _signInManager.SignInWithClaimsAsync(
                    user,
                    Input.RememberMe,
                    customClaims);
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

            if (isEmployee && firebaseEmployee != null && !string.IsNullOrWhiteSpace(firebaseUid))
            {
                await _firebasePresence.SetWebPresenceAsync(
                    firebaseEmployee.EmployeeID,
                    deviceId,
                    firebaseUid,
                    email,
                    firebaseEmployee.TenantId,
                    HttpContext.RequestAborted);
            }

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
                new
                {
                    ForceLogoutExisting,
                    Platform = "Web",
                    DeviceName = "Web Browser",
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    Summary = ForceLogoutExisting
                        ? $"Login successful on Web after force displacing previous {existingPlatformName} ({existingDeviceLabel})."
                        : "Login successful on Web.",
                    DisplacedDevice = ForceLogoutExisting ? new
                    {
                        DeviceId = firebaseActiveDevice,
                        Platform = existingPlatformName,
                        DeviceName = existingDeviceLabel,
                        LastSeen = existingTimeDesc,
                        LastLatitude = existingLat,
                        LastLongitude = existingLng,
                        LastDistanceMeters = existingDistanceMeters,
                        WithinAllowedRadius = existingWithinRadius
                    } : null,
                    NewDevice = new
                    {
                        DeviceId = deviceId,
                        Platform = "Web",
                        DeviceName = "Web Browser",
                        IpAddress = ipAddress,
                        UserAgent = userAgent,
                        EstablishedAtUtc = DateTime.UtcNow
                    }
                },
                ForceLogoutExisting ? firebaseActiveDevice : null);

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

                if (isSuperAdmin)
                {
                    if (!await _appMode.IsOfflineModeAsync())
                    {
                        return LocalRedirect("/superadmin/tenants");
                    }
                    return LocalRedirect("/");
                }

                return LocalRedirect("/");
            }

            return LocalRedirect("/employee-home");
        }


        private async Task<string?> _firebaseEmployeesReconcileLoginAsync(
            Employee employee,
            string email,
            string password,
            CancellationToken ct)
        {
            try
            {
                return await _firebaseEmployees.EnsureLoginAuthBindingAsync(
                    employee,
                    email,
                    password,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase employee identity reconciliation failed during Web login. EmployeeId={EmployeeId}",
                    employee.EmployeeID);
                return null;
            }
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
                return false;

            lockRecord.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

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
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                if (await db.EmployeeDeviceLocks.AsNoTracking().AnyAsync(x => x.UserId == userId))
                    return EmployeeLockResult.AlreadyActive;

                var now = DateTime.UtcNow;
                db.EmployeeDeviceLocks.Add(new EmployeeDeviceLock
                {
                    Id = Guid.NewGuid(), UserId = userId, DeviceId = deviceId,
                    CreatedAtUtc = now, LastSeenAtUtc = now
                });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return EmployeeLockResult.Acquired;
            }
            catch (DbUpdateException)
            {
                try { await transaction.RollbackAsync(); } catch { }
                return EmployeeLockResult.AlreadyActive;
            }
            catch
            {
                try { await transaction.RollbackAsync(); } catch { }
                throw;
            }
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
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var existing = await db.EmployeeDeviceLocks.FirstOrDefaultAsync(x => x.UserId == userId);
                if (existing != null)
                {
                    db.EmployeeDeviceLocks.Remove(existing);
                    await db.SaveChangesAsync();
                }

                var now = DateTime.UtcNow;
                db.EmployeeDeviceLocks.Add(new EmployeeDeviceLock
                {
                    Id = Guid.NewGuid(), UserId = userId, DeviceId = newDeviceId,
                    CreatedAtUtc = now, LastSeenAtUtc = now
                });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogWarning("EMPLOYEE ACTIVE SESSION REPLACED. UserId={UserId}, NewDeviceId={DeviceId}", userId, newDeviceId);
                return true;
            }
            catch (Exception ex)
            {
                try { await transaction.RollbackAsync(); } catch { }
                _logger.LogError(ex, "EMPLOYEE FORCE SESSION REPLACEMENT FAILED. UserId={UserId}", userId);
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