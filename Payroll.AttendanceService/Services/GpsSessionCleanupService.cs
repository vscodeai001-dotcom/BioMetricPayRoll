using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Payroll.Shared.Data;

namespace Payroll.AttendanceService.Services;

/// <summary>
/// Background service that monitors and cleans up stale GPS sessions.
///
/// RESPONSIBILITIES:
/// ================================================================
/// 1. End GPS sessions for employees with no active device lock
/// 2. Mark GPS sessions as timed-out if no updates for 30 minutes
/// 3. Ensure database state stays synchronized with device locks
///
/// RUN INTERVAL: Every 60 seconds
///
/// SCENARIOS HANDLED:
/// ================================================================
/// 1. Browser Circuit Disposed / Page Reload
///    - Employee still logged in (device lock exists)
///    - GPS session still active in database
///    - No updates received for 30+ minutes
///    ? Mark as TIMED_OUT
///
/// 2. Force Logout from Another Device
///    - Device lock removed by force logout code
///    - GPS session still active in database
///    ? End session immediately with FORCE_LOGGED_OUT reason
///
/// 3. Manual Logout
///    - Device lock removed by logout
///    - GPS session already ended by logout code
///    ? Verify consistency (should already be ended)
/// ================================================================
/// </summary>
public class GpsSessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GpsSessionCleanupService> _logger;
    private readonly int _checkIntervalSeconds;
    private readonly int _mobileSessionLeaseSeconds;
    private readonly IConfiguration _configuration;

    // Timeout policy constants
    private const int NoUpdateTimeoutSeconds = 1800; // 30 minutes for normal timeout policy

    public GpsSessionCleanupService(
        IServiceProvider serviceProvider,
        ILogger<GpsSessionCleanupService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;

        _checkIntervalSeconds = configuration.GetValue<int>(
            "GpsSessionCleanup:CheckIntervalSeconds",
            60);

        _mobileSessionLeaseSeconds = configuration.GetValue<int>(
            "GpsSessionCleanup:MobileSessionLeaseSeconds",
            600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GPS Session Cleanup Service starting. Check interval: {IntervalSeconds} seconds",
            _checkIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupStaleSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "GPS Session Cleanup Service encountered an error");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_checkIntervalSeconds),
                stoppingToken);
        }

        _logger.LogInformation("GPS Session Cleanup Service stopped");
    }

    private async Task CleanupStaleSessionsAsync(
        CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // ================================================================
            // PHASE 1: END SESSIONS FOR EMPLOYEES WITH NO DEVICE LOCK
            // ================================================================
            //
            // If an employee has NO device lock but an active GPS session,
            // it means they were forcefully logged out or manually logged out.
            //
            // The GPS session should be ended immediately.
            // ================================================================

            await EndSessionsWithoutDeviceLockAsync(db, stoppingToken);

            // Mobile applications can disappear without sending Logout
            // (for example uninstall, OS force-stop, or lost local token).
            // Their explicit ANDROID device lease is therefore reconciled
            // separately. Browser sessions keep the existing indefinite
            // device-lock semantics.
            //
            // UPDATE: To ensure 24/7 connectivity and prevent "401 Disconnected"
            // errors, we no longer automatically remove ANDROID locks.
            // Mobile sessions are now authoritative and permanent until
            // an explicit logout or device replacement occurs.
            // await ReconcileAbandonedMobileSessionsAsync(db, stoppingToken);

            // ================================================================
            // PHASE 2: MARK SESSIONS AS TIMED OUT (30+ minutes no updates)
            // ================================================================
            //
            // If a GPS session has been inactive for 30+ minutes AND the
            // employee still has a device lock (meaning they're still logged in),
            // mark it as timed out.
            //
            // This handles the case where:
            // - GPS watcher paused by browser power management
            // - Network issues causing GPS failure
            // - Browser minimized for long time
            // ================================================================

            await MarkTimedOutSessionsAsync(db, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to cleanup GPS sessions");
        }
    }

    private async Task EndSessionsWithoutDeviceLockAsync(
        AppDbContext db,
        CancellationToken stoppingToken)
    {
        try
        {
            // Find all active GPS sessions
            var activeSessions = await db.EmployeeGpsSessions
                .Where(s => s.EndedAtUtc == null)
                .ToListAsync(stoppingToken);

            if (activeSessions.Count == 0)
                return;

            // Get all employee IDs from active sessions
            var employeeIds = activeSessions
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToList();

            // Get all employees and their AspNetUserIds
            var employees = await db.Employees
                .AsNoTracking()
                .Where(e => employeeIds.Contains(e.EmployeeID))
                .Select(e => new { e.EmployeeID, e.AspNetUserId })
                .ToListAsync(stoppingToken);

            // Get all UserIds with active device locks
            var userIdsWithLocks = await db.EmployeeDeviceLocks
                .AsNoTracking()
                .Select(d => d.UserId)
                .Distinct()
                .ToListAsync(stoppingToken);

            var sessionsToEnd = new List<EmployeeGpsSession>();
            var now = DateTime.UtcNow;

            // Check each session
            foreach (var session in activeSessions)
            {
                // Find the employee's AspNetUserId
                var employee = employees.FirstOrDefault(e => e.EmployeeID == session.EmployeeId);

                if (employee?.AspNetUserId == null)
                    continue;

                // Check if this employee has a device lock
                var hasDeviceLock = userIdsWithLocks.Contains(employee.AspNetUserId);

                if (!hasDeviceLock)
                {
                    // No device lock = employee is NOT logged in
                    // End the GPS session
                    session.EndedAtUtc = now;
                    session.EndReason = "NO_DEVICE_LOCK";

                    sessionsToEnd.Add(session);

                    _logger.LogInformation(
                        "Ending GPS session - no device lock. EmployeeId={EmployeeId}, SessionId={SessionId}",
                        session.EmployeeId,
                        session.SessionId);
                }
            }

            if (sessionsToEnd.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);

                await NotifyWebSessionEndedAsync(
                    sessionsToEnd
                        .Select(x => new GpsSessionEndNotification(
                            x.EmployeeId, x.SessionId, x.EndedAtUtc!.Value, x.EndReason ?? "NO_DEVICE_LOCK"))
                        .ToList(),
                    stoppingToken);

                _logger.LogInformation(
                    "Ended {Count} GPS sessions due to missing device locks",
                    sessionsToEnd.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to end sessions without device locks");
        }
    }

    private async Task ReconcileAbandonedMobileSessionsAsync(
        AppDbContext db,
        CancellationToken stoppingToken)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_mobileSessionLeaseSeconds);

            var staleMobileLocks = await db.EmployeeDeviceLocks
                .Where(x =>
                    x.DeviceId.StartsWith("ANDROID:") &&
                    x.LastSeenAtUtc <= cutoff)
                .ToListAsync(stoppingToken);

            if (staleMobileLocks.Count == 0)
                return;

            var userIds = staleMobileLocks
                .Select(x => x.UserId)
                .Distinct()
                .ToList();

            var employees = await db.Employees
                .AsNoTracking()
                .Where(x => userIds.Contains(x.AspNetUserId!) && !x.IsDeleted)
                .Select(x => new { x.EmployeeID, x.AspNetUserId })
                .ToListAsync(stoppingToken);

            var employeeByUser = employees
                .Where(x => !string.IsNullOrWhiteSpace(x.AspNetUserId))
                .ToDictionary(x => x.AspNetUserId!, x => x.EmployeeID, StringComparer.Ordinal);

            var userByEmployee = employeeByUser
                .ToDictionary(x => x.Value, x => x.Key);

            var employeeIds = employees
                .Select(x => x.EmployeeID)
                .ToList();

            var activeSessions = await db.EmployeeGpsSessions
                .Where(x => employeeIds.Contains(x.EmployeeId) && x.EndedAtUtc == null)
                .ToListAsync(stoppingToken);

            var staleUserIds = staleMobileLocks
                .Select(x => x.UserId)
                .ToHashSet(StringComparer.Ordinal);

            var now = DateTime.UtcNow;
            var ended = 0;

            foreach (var session in activeSessions)
            {
                if (!userByEmployee.TryGetValue(session.EmployeeId, out var userId) ||
                    !staleUserIds.Contains(userId))
                    continue;

                session.EndedAtUtc = now;
                session.EndReason = "MOBILE_SESSION_EXPIRED";
                ended++;

                _logger.LogWarning(
                    "Ending abandoned mobile GPS session. EmployeeId={EmployeeId}, SessionId={SessionId}, Reason=MOBILE_SESSION_EXPIRED",
                    session.EmployeeId,
                    session.SessionId);
            }

            foreach (var lockRecord in staleMobileLocks)
            {
                // The ANDROID prefix is authoritative for mobile locks.
                // Release the stale lock even if its employee record was
                // deleted, preventing orphaned mobile locks from blocking
                // future login attempts.
                db.EmployeeDeviceLocks.Remove(lockRecord);
            }

            await db.SaveChangesAsync(stoppingToken);

            await NotifyWebSessionEndedAsync(
                activeSessions.Where(x => x.EndedAtUtc.HasValue)
                    .Select(x => new GpsSessionEndNotification(
                        x.EmployeeId, x.SessionId, x.EndedAtUtc!.Value, x.EndReason ?? "MOBILE_SESSION_EXPIRED"))
                    .ToList(),
                stoppingToken);

            if (ended > 0 || staleMobileLocks.Count > 0)
            {
                _logger.LogInformation(
                    "Mobile session lease reconciliation completed. LocksReleased={LocksReleased}, GpsSessionsEnded={GpsSessionsEnded}",
                    staleMobileLocks.Count,
                    ended);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile abandoned mobile sessions");
        }
    }

    private async Task MarkTimedOutSessionsAsync(
        AppDbContext db,
        CancellationToken stoppingToken)
    {
        try
        {
            var timeoutBefore =
                DateTime.UtcNow.AddSeconds(-NoUpdateTimeoutSeconds);

            // ================================================================
            // IMPORTANT FIX: Only timeout sessions if employee is NOT logged in
            // ================================================================
            // If employee is still logged in (has device lock), the session
            // should remain ACTIVE even if no GPS updates received.
            // This prevents false OFFLINE status for actively logged-in employees.
            //
            // Only mark as TIMED_OUT if:
            // 1. No GPS updates for 30+ minutes AND
            // 2. Employee is NOT logged in (device lock removed)
            // ================================================================

            var inactiveSessions = await db.EmployeeGpsSessions
                .Where(s =>
                    s.EndedAtUtc == null &&
                    s.LastUpdateAtUtc <= timeoutBefore)
                .ToListAsync(stoppingToken);

            if (inactiveSessions.Count == 0)
                return;

            // Get employee IDs from inactive sessions
            var employeeIds = inactiveSessions
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToList();

            // Get all employees and their AspNetUserIds
            var employees = await db.Employees
                .AsNoTracking()
                .Where(e => employeeIds.Contains(e.EmployeeID))
                .Select(e => new { e.EmployeeID, e.AspNetUserId })
                .ToListAsync(stoppingToken);

            // Get all UserIds with active device locks
            var userIdsWithLocks = await db.EmployeeDeviceLocks
                .AsNoTracking()
                .Select(d => d.UserId)
                .Distinct()
                .ToListAsync(stoppingToken);

            var sessionsToTimeout = new List<EmployeeGpsSession>();
            var now = DateTime.UtcNow;

            // Check each inactive session
            foreach (var session in inactiveSessions)
            {
                // Find the employee's AspNetUserId
                var employee = employees.FirstOrDefault(e => e.EmployeeID == session.EmployeeId);

                if (employee?.AspNetUserId == null)
                    continue;

                // Check if this employee has a device lock
                var hasDeviceLock = userIdsWithLocks.Contains(employee.AspNetUserId);

                // ONLY timeout if employee is NOT logged in
                if (!hasDeviceLock)
                {
                    session.EndedAtUtc = now;
                    session.EndReason = "TIMED_OUT";

                    sessionsToTimeout.Add(session);

                    _logger.LogInformation(
                        "Marking GPS session as timed out (not logged in). " +
                        "EmployeeId={EmployeeId}, SessionId={SessionId}, " +
                        "LastUpdate={LastUpdate}, Age={Age} minutes",
                        session.EmployeeId,
                        session.SessionId,
                        session.LastUpdateAtUtc,
                        (int)(now - session.LastUpdateAtUtc).TotalMinutes);
                }
                else
                {
                    // Employee is still logged in - KEEP session active
                    _logger.LogInformation(
                        "Keeping GPS session ACTIVE (employee still logged in). " +
                        "EmployeeId={EmployeeId}, SessionId={SessionId}, " +
                        "LastUpdate={LastUpdate}, Age={Age} minutes",
                        session.EmployeeId,
                        session.SessionId,
                        session.LastUpdateAtUtc,
                        (int)(now - session.LastUpdateAtUtc).TotalMinutes);
                }
            }

            if (sessionsToTimeout.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);

                await NotifyWebSessionEndedAsync(
                    sessionsToTimeout
                        .Select(x => new GpsSessionEndNotification(
                            x.EmployeeId, x.SessionId, x.EndedAtUtc!.Value, x.EndReason ?? "TIMED_OUT"))
                        .ToList(),
                    stoppingToken);

                _logger.LogInformation(
                    "Marked {Count} GPS sessions as timed out",
                    sessionsToTimeout.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to mark timed-out GPS sessions");
        }
    }

    private async Task NotifyWebSessionEndedAsync(
        IReadOnlyCollection<GpsSessionEndNotification> sessions,
        CancellationToken stoppingToken)
    {
        if (sessions.Count == 0)
            return;

        var webBaseUrl = _configuration["AttendanceRefresh:WebBaseUrl"];
        var secret = _configuration["AttendanceRefresh:Secret"];

        if (string.IsNullOrWhiteSpace(webBaseUrl) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{webBaseUrl.TrimEnd('/')}/api/internal/gps-session-ended");

            request.Headers.Add("X-Attendance-Refresh-Secret", secret);
            request.Content = JsonContent.Create(sessions);

            using var response = await client.SendAsync(request, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GPS session-end realtime notification failed. HTTP {StatusCode}.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to notify Web application about GPS session endings.");
        }
    }

    private sealed record GpsSessionEndNotification(
        int EmployeeId,
        Guid SessionId,
        DateTime EndedAtUtc,
        string EndReason);
}

