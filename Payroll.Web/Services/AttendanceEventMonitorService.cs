using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Observability-only layer for employee authentication/session and attendance state.
/// It never changes AttendanceLog rows or attendance priority/fallback rules.
/// </summary>
public sealed class AttendanceEventMonitorService
{
    private readonly AuditService _audit;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AttendanceEventMonitorService> _logger;

    public AttendanceEventMonitorService(
        AuditService audit,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AttendanceEventMonitorService> logger)
    {
        _audit = audit;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // Compatibility signature used by the Web/Android monitoring call sites.
    public Task RecordAsync(
        string eventType,
        string userId,
        string? email,
        string? deviceId,
        string platform,
        string? result,
        string? reason,
        object? context = null,
        string? relatedDeviceId = null)
    {
        var details = JsonSerializer.Serialize(new
        {
            EventType = eventType,
            Platform = platform,
            DeviceId = deviceId,
            RelatedDeviceId = relatedDeviceId,
            Result = result,
            Reason = reason,
            Context = context,
            RecordedAtUtc = DateTime.UtcNow
        });

        return SafeWriteAsync(userId, email, details, eventType);
    }

    // Compatibility overload for EmployeeSingleSessionSignInManager calls such as:
    // RecordAsync("LOGIN_PASSWORD_FAILED", user.Id, details: new { ... })
    public Task RecordAsync(string eventType, string userId, object? details = null)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            EventType = eventType,
            Context = details,
            RecordedAtUtc = DateTime.UtcNow
        });

        return SafeWriteAsync(userId, null, serialized, eventType);
    }

    public async Task<string?> GetActiveDeviceIdAsync(string userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.EmployeeDeviceLocks
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.DeviceId)
            .FirstOrDefaultAsync();
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
                .FirstOrDefaultAsync(x => x.AspNetUserId == userId && !x.IsDeleted);

            if (employee == null)
            {
                await RecordAsync(eventType, userId, null, deviceId, "System", null, null, details);
                return;
            }

            var today = DateTime.Now.Date;
            var todayPunches = await db.AttendanceLogs
                .AsNoTracking()
                .Where(x => x.EmployeeID == employee.EmployeeID && x.PunchTime.Date == today)
                .OrderBy(x => x.PunchTime)
                .ToListAsync();

            var latest = todayPunches.LastOrDefault();

            var state = new
            {
                EmployeeId = employee.EmployeeID,
                AttendanceStateBeforeEvent = todayPunches.Count % 2 == 0 ? "CLOSED" : "OPEN",
                TodayPunchCount = todayPunches.Count,
                ExistingParityExpectedNext = todayPunches.Count % 2 == 0 ? "IN" : "OUT",
                LastPunchId = latest?.LogID,
                LastPunchTime = latest?.PunchTime,
                LastPunchDeviceId = latest?.DeviceID,
                LastPunchLogType = latest?.LogType,
                LastPunchBiometricId = latest?.BiometricID,
                LastPunchApproved = latest?.IsApproved,
                Context = details
            };

            await RecordAsync(
                eventType,
                userId,
                employee.EmployeeID.ToString(),
                deviceId,
                "Attendance",
                "OBSERVED",
                null,
                state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Attendance employee state monitoring failed. EventType={EventType}, UserId={UserId}",
                eventType,
                userId);
        }
    }

    private async Task SafeWriteAsync(
        string userId,
        string? email,
        string details,
        string eventType)
    {
        try
        {
            await _audit.LogAsync(
                "AUTH_SESSION",
                "EmployeeAttendanceSession",
                userId,
                details,
                userId,
                email);
        }
        catch (Exception ex)
        {
            // Monitoring must never break authentication/session/attendance flow.
            _logger.LogWarning(
                ex,
                "Attendance event monitoring failed. UserId={UserId}, EventType={EventType}",
                userId,
                eventType);
        }
    }
}
