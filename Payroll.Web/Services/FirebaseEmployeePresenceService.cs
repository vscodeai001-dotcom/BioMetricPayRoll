using System.Globalization;

namespace Payroll.Web.Services;

/// <summary>
/// Cross-platform employee login presence.
/// Presence is deliberately separate from GPS: an employee can be logged in
/// without a current GPS fix. Each platform/device gets its own session node.
/// </summary>
public sealed class FirebaseEmployeePresenceService
{
    private readonly FirebaseRealtimeService _firebase;
    private readonly ILogger<FirebaseEmployeePresenceService> _logger;

    public FirebaseEmployeePresenceService(
        FirebaseRealtimeService firebase,
        ILogger<FirebaseEmployeePresenceService> logger)
    {
        _firebase = firebase;
        _logger = logger;
    }

    public async Task<bool> SetWebPresenceAsync(
        int employeeId,
        string sessionId,
        string authUid,
        string email,
        string? explicitOwnerUid = null,
        CancellationToken ct = default)
    {
        if (employeeId <= 0 || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(authUid))
            return false;

        var ownerUid = !string.IsNullOrWhiteSpace(explicitOwnerUid)
            ? explicitOwnerUid.Trim()
            : _firebase.ResolveOwnerUid(authUid, "Employee");
        if (string.IsNullOrWhiteSpace(ownerUid)) return false;

        var key = SanitizeKey(sessionId);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = new Dictionary<string, object?>
        {
            ["employeeId"] = employeeId,
            ["authUid"] = authUid,
            ["email"] = email ?? string.Empty,
            ["platform"] = "WEB",
            ["deviceId"] = sessionId,
            ["active"] = true,
            ["lastSeenAt"] = nowMs,
            ["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        try
        {
            // REQUIREMENT: Enforce single-device rule across Web and Android.
            // Publish the authoritative session lock to 'employee_sessions' node
            // used by Android's lifecycle monitor. DeviceId 'WEB_BROWSER' indicates
            // that the employee is currently active on the Web Dashboard.
            var sessionRow = new Dictionary<string, object?>
            {
                ["deviceId"] = "WEB_BROWSER_" + key.Substring(0, Math.Min(8, key.Length)),
                ["employeeId"] = employeeId,
                ["ownerUid"] = ownerUid,
                ["uid"] = authUid,
                ["platform"] = "WEB",
                ["deviceName"] = "Web Browser",
                ["lastSeenAt"] = nowMs,
                ["createdAt"] = nowMs
            };

            await _firebase.SetGlobalRecordAsync($"employee_sessions/{authUid}", sessionRow, ct);

            return await _firebase.SetOwnerRecordAsync(
                ownerUid, "presence", $"{employeeId}_{key}", row, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to publish Web employee presence. EmployeeId={EmployeeId}", employeeId);
            return false;
        }
    }

    public async Task<bool> RemoveAsync(
        int employeeId,
        string sessionId,
        string authUid,
        string? explicitOwnerUid = null,
        CancellationToken ct = default)
    {
        if (employeeId <= 0 || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(authUid))
            return false;

        var ownerUid = !string.IsNullOrWhiteSpace(explicitOwnerUid)
            ? explicitOwnerUid.Trim()
            : _firebase.ResolveOwnerUid(authUid, "Employee");
        if (string.IsNullOrWhiteSpace(ownerUid)) return false;

        var key = SanitizeKey(sessionId);
        var browserDevicePrefix = "WEB_BROWSER_" + key.Substring(0, Math.Min(8, key.Length));

        try
        {
            // Clear employee_sessions node if owned by this Web session
            try
            {
                var existingSession = await _firebase.GetGlobalRecordAsync($"employee_sessions/{authUid}", ct);
                if (existingSession.HasValue &&
                    existingSession.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    existingSession.Value.TryGetProperty("deviceId", out var devProp) &&
                    devProp.GetString() == browserDevicePrefix)
                {
                    await _firebase.DeleteGlobalRecordAsync($"employee_sessions/{authUid}", ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clear employee_sessions on Web logout");
            }

            return await _firebase.DeleteOwnerRecordAsync(
                ownerUid, "presence", $"{employeeId}_{key}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to remove Web employee presence. EmployeeId={EmployeeId}", employeeId);
            return false;
        }
    }

    private static string SanitizeKey(string value) =>
        value.Trim()
            .Replace(".", "_dot_", StringComparison.Ordinal)
            .Replace("#", "_hash_", StringComparison.Ordinal)
            .Replace("$", "_dollar_", StringComparison.Ordinal)
            .Replace("[", "_open_", StringComparison.Ordinal)
            .Replace("]", "_close_", StringComparison.Ordinal)
            .Replace("/", "_slash_", StringComparison.Ordinal);
}
