using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Observability-only layer for employee authentication/logout and
/// attendance-state decisions. This service NEVER creates, deletes,
/// changes, or reorders AttendanceLog rows.
/// Existing attendance priority/fallback/business rules remain the
/// sole authority for the final attendance result.
/// </summary>
public sealed class AttendanceEventMonitorService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<AttendanceEventMonitorService> _logger;

    public AttendanceEventMonitorService(
        IDbContextFactory<AppDbContext> dbFactory,
        AuditService auditService,
        ILogger<AttendanceEventMonitorService> logger)
    {
        _dbFactory = dbFactory;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task RecordAsync(
        string eventType,
        string userId,
        string? employeeId = null,
        string? deviceId = null,
        object? details = null)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["monitorVersion"] = 1,
                ["eventType"] = eventType,
                ["eventUtc"] = DateTime.UtcNow,
                ["userId"] = userId,
                ["employeeId"] = employeeId,
                ["deviceId"] = deviceId,
                ["details"] = details
            };

            await _auditService.LogAsync(
                "ATTENDANCE_EVENT_MONITOR",
                "EmployeeAttendanceDecision",
                employeeId ?? userId,
                JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            // Monitoring must never change authentication or attendance flow.
            _logger.LogWarning(
                ex,
                "ATTENDANCE EVENT MONITORING FAILED. EventType={EventType}, UserId={UserId}, EmployeeId={EmployeeId}",
                eventType,
                userId,
                employeeId);
        }
    }

    public async Task RecordEmployeeStateAsync(
        string eventType,
        string userId,
        string? deviceId = null,
        object? details = null)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var employee = await db.Employees
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AspNetUserId == userId);

            if (employee == null)
            {
                await RecordAsync(eventType, userId, null, deviceId, details);
                return;
            }

            var punches = await db.AttendanceLogs
                .AsNoTracking()
                .Where(x => x.EmployeeID == employee.EmployeeID)
                .OrderByDescending(x => x.PunchTime)
                .Take(20)
                .ToListAsync();

            var latest = punches.FirstOrDefault();
            var todayLocal = DateTime.Now.Date;
            var todayPunches = punches
                .Where(x => x.PunchTime.Date == todayLocal)
                .OrderBy(x => x.PunchTime)
                .ToList();

            var state = new Dictionary<string, object?>
            {
                ["attendanceStateBeforeEvent"] = todayPunches.Count % 2 == 0 ? "CLOSED" : "OPEN",
                ["todayPunchCount"] = todayPunches.Count,
                ["todayExpectedNextByExistingParity"] = todayPunches.Count % 2 == 0 ? "IN" : "OUT",
                ["lastPunchId"] = latest?.LogID,
                ["lastPunchTime"] = latest?.PunchTime,
                ["lastPunchDeviceId"] = latest?.DeviceID,
                ["lastPunchLogType"] = latest?.LogType,
                ["lastPunchBiometricId"] = latest?.BiometricID,
                ["lastPunchApproved"] = latest?.IsApproved,
                ["lastPunchLatitude"] = latest?.Latitude,
                ["lastPunchLongitude"] = latest?.Longitude,
                ["details"] = details
            };

            await RecordAsync(
                eventType,
                userId,
                employee.EmployeeID.ToString(),
                deviceId,
                state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ATTENDANCE EMPLOYEE STATE MONITORING FAILED. EventType={EventType}, UserId={UserId}",
                eventType,
                userId);
        }
    }
}
