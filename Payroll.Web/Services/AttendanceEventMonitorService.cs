using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>Observes authentication/session decisions without changing attendance rules.</summary>
public sealed class AttendanceEventMonitorService
{
    private readonly AuditService _audit;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AttendanceEventMonitorService> _logger;

    public AttendanceEventMonitorService(AuditService audit, IDbContextFactory<AppDbContext> dbFactory, ILogger<AttendanceEventMonitorService> logger)
    { _audit = audit; _dbFactory = dbFactory; _logger = logger; }

    public Task RecordAsync(string eventType, string userId, string? email, string? deviceId, string platform, string? result, string? reason, object? context = null, string? relatedDeviceId = null)
    {
        var details = JsonSerializer.Serialize(new { EventType=eventType, Platform=platform, DeviceId=deviceId, RelatedDeviceId=relatedDeviceId, Result=result, Reason=reason, Context=context, RecordedAtUtc=DateTime.UtcNow });
        return SafeWriteAsync(userId, email, details, eventType);
    }

    public async Task<string?> GetActiveDeviceIdAsync(string userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.EmployeeDeviceLocks.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.DeviceId).FirstOrDefaultAsync();
    }

    private async Task SafeWriteAsync(string userId, string? email, string details, string eventType)
    {
        try { await _audit.LogAsync("AUTH_SESSION", "EmployeeAttendanceSession", userId, details, userId, email); }
        catch (Exception ex) { _logger.LogWarning(ex, "Attendance event monitoring failed. UserId={UserId}, EventType={EventType}", userId, eventType); }
    }
}
