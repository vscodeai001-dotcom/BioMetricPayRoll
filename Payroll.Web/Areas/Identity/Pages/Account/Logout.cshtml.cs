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
    public class LogoutModel : PageModel
    {
        private const string DeviceCookieName =
            "BioMetric-Employee-Device";

        private const string DeviceClaimType =
            "BioMetric-Employee-Device";


        private readonly SignInManager<IdentityUser>
            _signInManager;

        private readonly UserManager<IdentityUser>
            _userManager;

        private readonly IDbContextFactory<AppDbContext>
            _dbFactory;

        private readonly GeoLocationService
            _geoLocationService;

        private readonly ILogger<LogoutModel>
            _logger;

        private readonly AttendanceEventMonitorService
            _attendanceEventMonitor;


        public LogoutModel(
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager,
            IDbContextFactory<AppDbContext> dbFactory,
            GeoLocationService geoLocationService,
            ILogger<LogoutModel> logger,
            AttendanceEventMonitorService attendanceEventMonitor)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _dbFactory = dbFactory;
            _geoLocationService = geoLocationService;
            _logger = logger;

            _attendanceEventMonitor = attendanceEventMonitor;
        }


        // ============================================================
        // GET
        // ============================================================

        public IActionResult OnGet()
        {
            return RedirectToPage(
                "/Account/Login",
                new
                {
                    area = "Identity"
                });
        }


        // ============================================================
        // POST LOGOUT
        // ============================================================

        public async Task<IActionResult> OnPostAsync(
            string? returnUrl = null)
        {
            var user =
                await _userManager.GetUserAsync(User);


            try
            {
                if (user != null)
                {
                    await _attendanceEventMonitor.RecordEmployeeStateAsync(
                        "LOGOUT_REQUESTED",
                        user.Id,
                        details: new { action = "MANUAL_LOGOUT", attendanceDecisionChanged = false });

                    await EndEmployeeGpsSessionAsync(user);

                    await ReleaseEmployeeDeviceLockAsync(
                        user);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "EMPLOYEE DEVICE LOCK CLEANUP FAILED DURING LOGOUT. UserId={UserId}",
                    user?.Id);
            }


            try
            {
                await _signInManager.SignOutAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "IDENTITY SIGN-OUT FAILED. UserId={UserId}",
                    user?.Id);
            }


            DeleteDeviceCookie();


            _logger.LogInformation(
                "LOGOUT COMPLETED. UserId={UserId}",
                user?.Id);

            if (user != null)
            {
                await _attendanceEventMonitor.RecordEmployeeStateAsync(
                    "LOGOUT_COMPLETED",
                    user.Id,
                    details: new
                    {
                        action = "MANUAL_LOGOUT",
                        attendancePunchCreatedByLogout = false,
                        note = "Monitoring only. Existing attendance priority/fallback rules were not changed."
                    });
            }


            if (
                !string.IsNullOrWhiteSpace(returnUrl) &&
                Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }


            return RedirectToPage(
                "/Account/Login",
                new
                {
                    area = "Identity"
                });
        }




        // ============================================================
        // END EMPLOYEE GPS SESSION ON REAL LOGOUT
        // ============================================================

        private async Task EndEmployeeGpsSessionAsync(
            IdentityUser user)
        {
            try
            {
                var isEmployee =
                    await _userManager.IsInRoleAsync(
                        user,
                        "Employee");

                if (!isEmployee)
                {
                    return;
                }

                await using var db =
                    await _dbFactory.CreateDbContextAsync();

                var employee =
                    await db.Employees
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            e => e.AspNetUserId == user.Id);

                if (employee == null)
                {
                    return;
                }

                // Logout is authoritative: close every active GPS session
                // under one employee-level lifecycle lock. This prevents a
                // second active/legacy session from surviving the logout.
                var endedCount = await _geoLocationService.EndAllGpsSessionsAsync(
                    employee.EmployeeID,
                    "MANUAL_LOGOUT");

                _logger.LogInformation(
                    "GPS SESSION CLEANUP DURING MANUAL LOGOUT. EmployeeId={EmployeeId}, SessionsEnded={SessionsEnded}, Reason=MANUAL_LOGOUT",
                    employee.EmployeeID,
                    endedCount);
            }
            catch (Exception ex)
            {
                // GPS cleanup must never block the actual Identity logout.
                _logger.LogWarning(
                    ex,
                    "GPS SESSION CLEANUP FAILED DURING MANUAL LOGOUT. UserId={UserId}",
                    user.Id);
            }
        }

        // ============================================================
        // RELEASE EMPLOYEE LOCK
        // ============================================================

        private async Task
            ReleaseEmployeeDeviceLockAsync(
                IdentityUser user)
        {
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


            // Admin / SuperAdmin are unrestricted.

            if (
                !isEmployee ||
                isAdmin ||
                isSuperAdmin)
            {
                return;
            }


            // ========================================================
            // CLAIM FIRST
            // ========================================================

            var deviceId =
                User.FindFirstValue(
                    DeviceClaimType);


            // ========================================================
            // COOKIE FALLBACK
            // ========================================================

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                Request.Cookies.TryGetValue(
                    DeviceCookieName,
                    out deviceId);
            }


            if (string.IsNullOrWhiteSpace(deviceId))
            {
                _logger.LogWarning(
                    "LOGOUT: Device identity unavailable. UserId={UserId}",
                    user.Id);

                return;
            }


            deviceId =
                deviceId.Trim();


            // ========================================================
            // REMOVE ONLY THIS DEVICE'S LOCK
            // ========================================================

            await using var db =
                await _dbFactory.CreateDbContextAsync();


            var lockRecord =
                await db.EmployeeDeviceLocks
                    .FirstOrDefaultAsync(
                        x =>
                            x.UserId == user.Id &&
                            x.DeviceId == deviceId);


            if (lockRecord == null)
            {
                _logger.LogInformation(
                    "LOGOUT: No matching device lock. UserId={UserId}, DeviceId={DeviceId}",
                    user.Id,
                    deviceId);

                return;
            }


            db.EmployeeDeviceLocks.Remove(
                lockRecord);


            await db.SaveChangesAsync();


            _logger.LogInformation(
                "EMPLOYEE DEVICE LOCK RELEASED. UserId={UserId}, DeviceId={DeviceId}",
                user.Id,
                deviceId);
        }


        // ============================================================
        // DELETE DEVICE COOKIE
        // ============================================================

        private void DeleteDeviceCookie()
        {
            Response.Cookies.Delete(
                DeviceCookieName,
                new CookieOptions
                {
                    HttpOnly = true,

                    Secure = true,

                    SameSite =
                        SameSiteMode.Lax,

                    IsEssential = true,

                    Path = "/"
                });
        }
    }
}