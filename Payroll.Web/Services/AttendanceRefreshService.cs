using Microsoft.AspNetCore.SignalR;
using Payroll.Web.Hubs;

namespace Payroll.Web.Services
{
    public sealed class AttendanceRefreshService
    {
        private readonly IHubContext<AttendanceRefreshHub> _hub;

        public AttendanceRefreshService(
            IHubContext<AttendanceRefreshHub> hub)
        {
            _hub = hub;
        }


        /*
         * ==========================================================
         * GENERIC ATTENDANCE DATA CHANGE
         * ==========================================================
         *
         * Use this after ANY successful attendance-related CRUD.
         *
         * Examples:
         *
         * - Mobile punch
         * - Manual punch
         * - Edit punch
         * - Delete punch
         * - Leave approval
         * - Leave cancellation
         * - Attendance correction
         * - Regularization
         * - Shift changes
         * - Recalculation
         *
         */

        public async Task NotifyDataChangedAsync(
            int? employeeId = null,
            DateOnly? date = null,
            string? source = null)
        {
            await _hub.Clients.All.SendAsync(
                "DataChanged",
                new
                {
                    EmployeeId = employeeId,

                    Date =
                        date?.ToString("yyyy-MM-dd"),

                    Source = source ?? "ATTENDANCE",

                    Timestamp =
                        DateTime.UtcNow
                });
        }


        public async Task NotifyAttendanceChangedAsync(
            int? employeeId = null,
            DateOnly? date = null)
        {
            await _hub.Clients.All.SendAsync(
                "AttendanceChanged",
                new
                {
                    EmployeeId = employeeId,

                    Date =
                        date?.ToString("yyyy-MM-dd"),

                    Timestamp =
                        DateTime.UtcNow
                });
        }

        public async Task NotifyPunchCreatedAsync(
            int employeeId,
            DateOnly date)
        {
            await _hub.Clients.All.SendAsync(
                "PunchChanged",
                new
                {
                    EmployeeId = employeeId,
                    Date = date.ToString("yyyy-MM-dd"),
                    Action = "CREATED",
                    Timestamp = DateTime.UtcNow
                });
        }


        public async Task NotifyLocationChangedAsync(
            int? employeeId = null)
        {
            await _hub.Clients.All.SendAsync(
                "LocationChanged",
                new
                {
                    EmployeeId = employeeId,

                    Timestamp =
                        DateTime.UtcNow
                });
        }


        public async Task NotifyRegularizationChangedAsync(
            int? employeeId = null)
        {
            await _hub.Clients.All.SendAsync(
                "RegularizationChanged",
                new
                {
                    EmployeeId = employeeId,

                    Timestamp =
                        DateTime.UtcNow
                });
        }


        public async Task NotifyAllDataChangedAsync()
        {
            await NotifyDataChangedAsync(
                null,
                null,
                "GLOBAL");
        }


        /*
         * ==========================================================
         * APPLICATION-WIDE DATABASE CHANGE
         * ==========================================================
         *
         * This event is emitted centrally after a successful EF Core
         * SaveChanges operation. It is intentionally separate from
         * the existing attendance/location events so existing
         * listeners keep their current behaviour.
         *
         * The payload describes which entity types changed. The
         * client uses this as a realtime invalidation signal and
         * reloads the currently visible route from the database.
         */

        public async Task NotifyApplicationDataChangedAsync(
            IReadOnlyCollection<string> changedEntities)
        {
            var changes = changedEntities
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => new ApplicationDataChange
                {
                    Entity = x,
                    Action = "MODIFIED"
                })
                .ToArray();

            await NotifyApplicationDataChangedAsync(changes);
        }

        public async Task NotifyApplicationDataChangedAsync(
            IReadOnlyCollection<ApplicationDataChange> changes)
        {
            var normalized = changes
                .Where(x => !string.IsNullOrWhiteSpace(x.Entity))
                .GroupBy(x => x.Entity, StringComparer.Ordinal)
                .Select(g => new ApplicationDataChange
                {
                    Entity = g.Key,
                    Action = g.Select(x => x.Action)
                        .FirstOrDefault(a => string.Equals(a, "ADDED", StringComparison.OrdinalIgnoreCase))
                        ?? g.Select(x => x.Action)
                            .FirstOrDefault(a => string.Equals(a, "DELETED", StringComparison.OrdinalIgnoreCase))
                        ?? "MODIFIED"
                })
                .OrderBy(x => x.Entity, StringComparer.Ordinal)
                .ToArray();

            await _hub.Clients.All.SendAsync(
                "ApplicationDataChanged",
                new
                {
                    Changes = normalized,
                    Entities = normalized.Select(x => x.Entity).ToArray(),
                    Timestamp = DateTime.UtcNow
                });
        }

        public sealed class ApplicationDataChange
        {
            public string Entity { get; set; } = string.Empty;
            public string Action { get; set; } = "MODIFIED";
        }


        /*
         * ==========================================================
         * LEAVE REQUEST CHANGES
         * ==========================================================
         *
         * Notify when leave requests are created, updated, or deleted.
         */

        public async Task NotifyLeaveChangedAsync(
            int? employeeId = null,
            DateTime? leaveDate = null,
            string? action = null)
        {
            await _hub.Clients.All.SendAsync(
                "LeaveChanged",
                new
                {
                    EmployeeId = employeeId,
                    LeaveDate = leaveDate?.ToString("yyyy-MM-dd"),
                    Action = action ?? "MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }


        /*
         * ==========================================================
         * SALARY ADVANCE CHANGES
         * ==========================================================
         *
         * Notify when salary advances are created or deleted.
         */

        public async Task NotifyAdvanceChangedAsync(
            int? employeeId = null,
            string? action = null)
        {
            await _hub.Clients.All.SendAsync(
                "AdvanceChanged",
                new
                {
                    EmployeeId = employeeId,
                    Action = action ?? "MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }


        /*
         * ==========================================================
         * PUNCH CHANGES (Manual/Correction)
         * ==========================================================
         *
         * Notify when punches are added, edited, or deleted.
         */

        public async Task NotifyPunchChangedAsync(
            int? employeeId = null,
            DateOnly? date = null,
            string? action = null)
        {
            await _hub.Clients.All.SendAsync(
                "PunchChanged",
                new
                {
                    EmployeeId = employeeId,
                    Date = date?.ToString("yyyy-MM-dd"),
                    Action = action ?? "MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }


        /*
         * ==========================================================
         * EMPLOYEE DATA CHANGES
         * ==========================================================
         *
         * Notify when employee records are updated.
         */

        public async Task NotifyEmployeeChangedAsync(
            int? employeeId = null,
            string? action = null)
        {
            await _hub.Clients.All.SendAsync(
                "EmployeeChanged",
                new
                {
                    EmployeeId = employeeId,
                    Action = action ?? "MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }


        /*
         * ==========================================================
         * EXIT/RESIGNATION CHANGES
         * ==========================================================
         *
         * Notify when exit or resignation data is modified.
         */

        public async Task NotifyExitChangedAsync(
            int? employeeId = null,
            string? action = null)
        {
            await _hub.Clients.All.SendAsync(
                "ExitChanged",
                new
                {
                    EmployeeId = employeeId,
                    Action = action ?? "MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }


        /*
         * ==========================================================
         * BULK REFRESH FOR GLOBAL CRUD
         * ==========================================================
         *
         * Notify all clients to refresh when major operations occur.
         */

        public async Task NotifyGeoSettingsChangedAsync(
            double officeLatitude,
            double officeLongitude,
            int geoRadiusMeters)
        {
            await _hub.Clients.All.SendAsync(
                "GeoSettingsChanged",
                new
                {
                    OfficeLatitude = officeLatitude,
                    OfficeLongitude = officeLongitude,
                    GeoRadiusMeters = geoRadiusMeters,
                    Timestamp = DateTime.UtcNow
                });
        }


        public async Task NotifyGlobalRefreshAsync(string? reason = null)
        {
            await _hub.Clients.All.SendAsync(
                "GlobalRefresh",
                new
                {
                    Reason = reason ?? "DATA_MODIFIED",
                    Timestamp = DateTime.UtcNow
                });
        }

        public async Task NotifyGpsSessionsEndedAsync(
            IEnumerable<GpsSessionEndNotification> sessions)
        {
            foreach (var session in sessions)
            {
                await _hub.Clients.All.SendAsync(
                    "SessionEnded",
                    new
                    {
                        session.EmployeeId,
                        session.SessionId,
                        session.EndedAtUtc,
                        session.EndReason
                    });
            }
        }

        public sealed record GpsSessionEndNotification(
            int EmployeeId,
            Guid SessionId,
            DateTime EndedAtUtc,
            string EndReason);

    }
}