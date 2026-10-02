using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Hubs;
using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class NotificationService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly IHubContext<AttendanceRefreshHub> _hub;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly ILogger<NotificationService> _logger;
        private readonly FirebaseEmployeeManagementService _firebaseEmployees;
       
        public NotificationService(
            IDbContextFactory<AppDbContext> dbFactory,
            IHubContext<AttendanceRefreshHub> hub,
            UserManager<IdentityUser> userManager,
            ILogger<NotificationService> logger,
            FirebaseEmployeeManagementService firebaseEmployees)
        {
            _dbFactory = dbFactory;
            _hub = hub;
            _userManager = userManager;
            _logger = logger;
            _firebaseEmployees = firebaseEmployees;
        }

        public async Task SendNotificationAsync(string userId, string title, string message, string? url = null)
        {
            // 1. Save to Database (Persistence)
            var notification = new Notification
            {
                UserId = userId,
                Title = title,
                Message = message,
                Url = url,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };

            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();

            // The database write is authoritative. A transient SignalR failure
            // must never make the business operation appear to have failed.
            try
            {
                await _hub.Clients.User(userId).SendAsync("NotificationChanged");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Realtime notification delivery failed for UserId={UserId}; notification {NotificationId} remains persisted.",
                    userId, notification.NotificationId);
            }
        }

        /// <summary>
        /// Sends one centralized in-app notification to every Admin and SuperAdmin.
        /// This is the standard destination for employee-submitted approval requests.
        /// </summary>
        public async Task NotifyAdminsAsync(
            string title,
            string message,
            string? url = null)
        {
            var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
            var superAdminUsers = await _userManager.GetUsersInRoleAsync("SuperAdmin");

            var recipients = adminUsers
                .Concat(superAdminUsers)
                .Select(u => u.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var userId in recipients)
            {
                try
                {
                    await SendNotificationAsync(userId, title, message, url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to persist admin notification for UserId={UserId}, Title={Title}",
                        userId, title);
                }
            }
        }

        /// <summary>
        /// Resolves the Identity account linked to an employee and sends the
        /// notification directly to that employee's active bell.
        /// </summary>
        public async Task NotifyEmployeeAsync(
            int employeeId,
            string title,
            string message,
            string? url = null)
        {
            var employee = await _firebaseEmployees.GetEmployeeAsync(employeeId);
            if (employee == null || string.IsNullOrWhiteSpace(employee.Email))
                return;

            // Identity account resolution remains on the server-side Identity
            // boundary. Only the employee projection/name/email lookup moved to Firebase.
            var user = await _userManager.FindByEmailAsync(employee.Email);
            if (user == null)
                return;

            try
            {
                await SendNotificationAsync(user.Id, title, message, url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist employee notification for EmployeeId={EmployeeId}, UserId={UserId}, Title={Title}",
                    employeeId, user.Id, title);
            }
        }

        public async Task NotifyAdminsEmployeeLoginAsync(
            string employeeName,
            string email,
            string ipAddress,
            string userAgent,
            DateTime loginTimeUtc,
            bool replacedExistingSession,
            string? gpsDetails = null,
            bool blockedExistingSession = false)
        {
            var title = blockedExistingSession
                ? "Blocked login attempt"
                : replacedExistingSession
                    ? "Employee session replaced"
                    : "Employee login detected";

            var message =
                $"Employee: {employeeName} ({email}); " +
                $"Time UTC: {loginTimeUtc:yyyy-MM-dd HH:mm:ss}; " +
                $"IP: {ipAddress}; " +
                $"GPS: {gpsDetails ?? "Pending from device"}; " +
                $"Device: {userAgent}";

            if (message.Length > 500)
                message = message[..500];

            var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
            var superAdminUsers = await _userManager.GetUsersInRoleAsync("SuperAdmin");
            var adminIds = adminUsers
                .Concat(superAdminUsers)
                .Select(u => u.Id)
                .Distinct()
                .ToList();

            foreach (var adminId in adminIds)
            {
                await SendNotificationAsync(
                    adminId,
                    title,
                    message,
                    "/");
            }
        }

        public async Task MarkAsReadAsync(int notificationId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var note = await db.Notifications.FindAsync(notificationId);
            if (note != null)
            {
                note.IsRead = true;
                await db.SaveChangesAsync();
            }
        }

        public async Task<List<Notification>> GetRecentNotificationsAsync(string userId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // Load last 10 notifications (unread first)
            return await db.Notifications
                .Where(n => n.UserId == userId)
                .OrderBy(n => n.IsRead)
                .ThenByDescending(n => n.CreatedAt)
                .Take(10)
                .ToListAsync();
        }
    }
}