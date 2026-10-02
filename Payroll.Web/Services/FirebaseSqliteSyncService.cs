using Payroll.Shared;
using Payroll.Shared.Firebase;
using Payroll.Shared.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Net.Sockets;

namespace Payroll.Web.Services;

public sealed class FirebaseSqliteSyncService : BackgroundService
{
    private static IReadOnlyDictionary<string, string> Tables => FirebaseSsotSchema.Tables;
    private static readonly TimeZoneInfo IndiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseSqliteSyncService> _logger;
    private readonly FirebaseSyncWriteScope _firebaseSyncWriteScope;
    private readonly AttendanceRefreshService _refreshService;
    private static readonly SemaphoreSlim _attendancePunchLock = new(1, 1);
    private readonly long _serviceStartTimeEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _lastProcessedWipeEpochMs = 0;

    public FirebaseSqliteSyncService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseSqliteSyncService> logger,
        FirebaseSyncWriteScope firebaseSyncWriteScope,
        AttendanceRefreshService refreshService)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
        _firebaseSyncWriteScope = firebaseSyncWriteScope;
        _refreshService = refreshService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var activeTenants = new System.Collections.Concurrent.ConcurrentDictionary<string, (Task OwnerTask, Task TrackingTask, Task WipeTask)>(StringComparer.OrdinalIgnoreCase);

        async Task StartTenantSyncAsync(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || activeTenants.ContainsKey(tenantId))
                return;

            try
            {
                using (var checkScope = _scopeFactory.CreateScope())
                {
                    var appMode = checkScope.ServiceProvider.GetService<IAppModeService>();
                    if (appMode != null && await appMode.IsOfflineModeAsync())
                    {
                        _logger.LogInformation("Skipping Firebase sync for tenant {TenantId}: Application is running in Offline Standalone Mode.", tenantId);
                        return;
                    }

                    var dbFactory = checkScope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                    if (dbFactory != null)
                    {
                        using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                        var tenant = await db.CompanyTenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, stoppingToken);
                        if (tenant != null && (tenant.IsOfflineMode || string.Equals(tenant.DeploymentMode, "Offline", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogInformation("Skipping Firebase sync for tenant {TenantId}: Tenant is configured for Offline Standalone Mode.", tenantId);
                            return;
                        }
                    }
                }

                _logger.LogInformation("Initializing SQLite sync and tracking stream for tenant {TenantId}", tenantId);
                await SyncAllTablesAsync(tenantId, stoppingToken);

                var ownerTask = RunOwnerStreamLoopAsync(tenantId, stoppingToken);
                var trackingTask = RunGlobalStreamLoopAsync($"owners/{tenantId}/tracking/live", async (path, data, ct) =>
                    await ProcessFirebaseTrackingEventAsync(path, data, ct), stoppingToken);
                var wipeTask = RunGlobalStreamLoopAsync($"owners/{tenantId}/system_events/wipe", async (path, data, ct) =>
                    await ProcessFirebaseWipeEventAsync(tenantId, data, ct), stoppingToken);

                activeTenants[tenantId] = (ownerTask, trackingTask, wipeTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize sync and tracking streams for tenant {TenantId}", tenantId);
            }
        }

        // 1. Initial tenant discovery
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
            if (dbFactory != null)
            {
                using var db = dbFactory.CreateDbContext();
                var tenantIds = await db.CompanyTenants.AsNoTracking().Where(t => t.IsActive).Select(t => t.TenantId).ToListAsync(stoppingToken);
                foreach (var tid in tenantIds)
                {
                    await StartTenantSyncAsync(tid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed initial tenant discovery for FirebaseSqliteSyncService.");
        }

        // Always ensure default primary tenant is streaming (if not offline)
        await StartTenantSyncAsync(Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid);

        // Global mobile auth events stream (Online mode only)
        var authTask = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                _logger.LogInformation("Offline mode active: mobile_auth_events Firebase streaming paused.");
                return;
            }
            await RunGlobalStreamLoopAsync("mobile_auth_events", async (path, data, ct) =>
                await ProcessFirebaseMobileAuthEventAsync(path, data, ct), stoppingToken);
        }, stoppingToken);

        // Continuous watcher loop to dynamically discover any newly provisioned tenants
        var watcherTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                    using var scope = _scopeFactory.CreateScope();
                    var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                    if (dbFactory != null)
                    {
                        using var db = dbFactory.CreateDbContext();
                        var currentTenantIds = await db.CompanyTenants.AsNoTracking().Where(t => t.IsActive).Select(t => t.TenantId).ToListAsync(stoppingToken);
                        foreach (var tid in currentTenantIds)
                        {
                            if (!activeTenants.ContainsKey(tid))
                            {
                                await StartTenantSyncAsync(tid);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Periodic tenant sync check encountered an issue.");
                }
            }
        }, stoppingToken);

        await Task.WhenAll(authTask, watcherTask);
    }

    private static readonly (string EntityName, string FirebaseTable)[] ActiveStreamTables = new[]
    {
        ("AttendancePunch", "attendance_punches"),
        ("AttendanceLog", "attendance"),
        ("GeoPunchAudit", "geo_punch_audits"),
        ("LeaveRequest", "leave_requests"),
        ("SalaryAdvance", "advance_payments"),
        ("AttendanceRegularization", "regularizations"),
        ("ResignationRequest", "resignation_requests"),
        ("Employee", "employees"),
        ("CompanySetting", "company_settings"),
        ("FeatureSettings", "feature_settings")
    };

    private async Task RunOwnerStreamLoopAsync(string ownerUid, CancellationToken stoppingToken)
    {
        // Stream only mobile-submitted operational tables individually instead of
        // streaming the entire root owners/{ownerUid}.
        var tasks = ActiveStreamTables.Select(t =>
            RunTableStreamLoopAsync(ownerUid, t.EntityName, t.FirebaseTable, stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task RunTableStreamLoopAsync(
        string ownerUid,
        string entityName,
        string firebaseTable,
        CancellationToken stoppingToken)
    {
        var nodePath = $"owners/{ownerUid}/{firebaseTable}";
        await RunGlobalStreamLoopAsync(nodePath, async (relativePath, eventData, ct) =>
        {
            var rawKey = (relativePath ?? "/").Trim('/');
            bool changed = false;

            if (string.IsNullOrEmpty(rawKey))
            {
                if (eventData.HasValue && eventData.Value.ValueKind == JsonValueKind.Object)
                {
                    changed = await UpsertTableAsync(entityName, eventData.Value, ownerUid, ct);
                }
                else if (eventData.HasValue && eventData.Value.ValueKind == JsonValueKind.Null)
                {
                    changed = await DeleteLocalFirebaseRecordAsync(entityName, null, ct);
                }
            }
            else
            {
                var recordKey = Uri.UnescapeDataString(rawKey)
                    .Replace("%2E", ".", StringComparison.OrdinalIgnoreCase)
                    .Replace("%23", "#", StringComparison.OrdinalIgnoreCase)
                    .Replace("%24", "$", StringComparison.OrdinalIgnoreCase)
                    .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
                    .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
                    .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

                if (eventData.HasValue && eventData.Value.ValueKind == JsonValueKind.Null)
                {
                    changed = await DeleteLocalFirebaseRecordAsync(entityName, recordKey, ct);
                }
                else if (eventData.HasValue && eventData.Value.ValueKind == JsonValueKind.Object)
                {
                    // BANDWIDTH OPTIMIZATION: Apply the single updated record from eventData directly.
                    // Never call SyncTableAsync (which issues an HTTP GET to download the entire table).
                    changed = await UpsertSingleFirebaseRecordAsync(entityName, recordKey, eventData.Value, ownerUid, ct);
                }
                else
                {
                    changed = await SyncTableAsync(entityName, firebaseTable, ownerUid, ct);
                }
            }

            if (changed)
            {
                await _refreshService.NotifyApplicationDataChangedAsync(new[] { entityName });

                if (entityName.Equals("CompanySetting", StringComparison.Ordinal))
                {
                    var settings = await _firebase.GetOwnerRecordAsync(ownerUid, firebaseTable, "1", ct);
                    if (settings.HasValue && settings.Value.ValueKind == JsonValueKind.Object)
                    {
                        var lat = GetDouble(settings.Value, "officeLatitude", "OfficeLatitude", "latitude", "Latitude");
                        var lon = GetDouble(settings.Value, "officeLongitude", "OfficeLongitude", "longitude", "Longitude");
                        var radius = GetInt(settings.Value, "geoRadiusMeters", "GeoRadiusMeters", "radius", "Radius");
                        var speed = GetBool(settings.Value, "useSpeedBasedMarkers", "use_speed_based_markers", "UseSpeedBasedMarkers");
                        await _refreshService.NotifyGeoSettingsChangedAsync(lat, lon, radius, speed);

                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<GeoLocationService>().RebaselineAllActiveSessionsAsync();
                    }
                }
                else if (entityName.Equals("FeatureSettings", StringComparison.Ordinal))
                {
                    await _refreshService.NotifyGlobalRefreshAsync("FEATURE_TOGGLES_UPDATED");
                }
                else if (entityName.Equals("AttendanceLog", StringComparison.Ordinal) ||
                         entityName.Equals("AttendancePunch", StringComparison.Ordinal))
                {
                    await _refreshService.NotifyGlobalRefreshAsync("PUNCH_SYNCED");
                }
            }
        }, stoppingToken);
    }

    private async Task RunGlobalStreamLoopAsync(
        string rootNode,
        Func<string, JsonElement?, CancellationToken, Task> handler,
        CancellationToken stoppingToken)
    {
        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _firebase.StreamGlobalChangesAsync(rootNode, handler, stoppingToken);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                var delaySeconds = Math.Min(30, 3 * consecutiveErrors);
                if (IsConnectionReset(ex))
                {
                    _logger.LogInformation("Firebase stream {RootNode} reconnected after timeout.", rootNode);
                }
                else
                {
                    _logger.LogWarning(ex, "Firebase {RootNode} realtime stream disconnected. Reconnecting in {Delay}s.", rootNode, delaySeconds);
                }
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
    }

    private static bool IsConnectionReset(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is SocketException sockEx &&
                (sockEx.SocketErrorCode == SocketError.ConnectionReset || sockEx.ErrorCode == 10054))
            {
                return true;
            }
            if (ex is System.IO.IOException ioEx &&
                (ioEx.Message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) ||
                 ioEx.Message.Contains("0 bytes", StringComparison.OrdinalIgnoreCase) ||
                 ioEx.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (ex.Message.Contains("10054") ||
                ex.Message.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ConnectionReset", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    private async Task ProcessFirebaseTrackingEventAsync(
        string relativePath,
        JsonElement? eventData,
        CancellationToken ct,
        bool evaluateAttendance = true)
    {
        // GeoLocationService is scoped. FirebaseSqliteSyncService is a singleton
        // hosted service, so resolve the scoped service inside a short-lived scope
        // for each Firebase event instead of injecting it into the hosted service.
        using var geoScope = _scopeFactory.CreateScope();
        var geoLocationService = geoScope.ServiceProvider.GetRequiredService<GeoLocationService>();

        if (!eventData.HasValue || eventData.Value.ValueKind != JsonValueKind.Object) return;

        var path = (relativePath ?? "/").Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // History is an immutable GPS ledger. It must NEVER execute the live
        // attendance/geofence state machine, including when Firebase sends an
        // incremental history event rather than the initial snapshot.
        if (parts.Any(p => p.Equals("history", StringComparison.OrdinalIgnoreCase)))
            evaluateAttendance = false;

        // Firebase sends the initial tracking snapshot at /. Hydrate both the
        // current live branch and immutable history branch. History replay is
        // deliberately attendance-neutral; live data only updates the Web
        // in-memory live store and never creates a second punch/history row.
        if (parts.Length == 0)
        {
            if (eventData.Value.TryGetProperty("live", out var live) &&
                (live.ValueKind == JsonValueKind.Object || live.ValueKind == JsonValueKind.Array))
            {
                if (live.ValueKind == JsonValueKind.Object)
                {
                    foreach (var employeeNode in live.EnumerateObject())
                    {
                        if (employeeNode.Value.ValueKind == JsonValueKind.Object)
                            await ProcessFirebaseLiveLocationAsync(
                                $"/live/{employeeNode.Name}",
                                employeeNode.Value.Clone(), ct);
                    }
                }
                else
                {
                    var index = 0;
                    foreach (var node in live.EnumerateArray())
                    {
                        if (node.ValueKind == JsonValueKind.Object)
                            await ProcessFirebaseLiveLocationAsync(
                                $"/live/{index}",
                                node.Clone(), ct);
                        index++;
                    }
                }
            }
            else if (eventData.Value.ValueKind == JsonValueKind.Object &&
                     !eventData.Value.TryGetProperty("sessions", out _) &&
                     !eventData.Value.TryGetProperty("history", out _))
            {
                // Scoped tracking/live root payload: root keys are Employee IDs
                foreach (var employeeNode in eventData.Value.EnumerateObject())
                {
                    if (employeeNode.Value.ValueKind == JsonValueKind.Object)
                        await ProcessFirebaseLiveLocationAsync(
                            $"/live/{employeeNode.Name}",
                            employeeNode.Value.Clone(), ct);
                }
            }

            if (eventData.Value.TryGetProperty("sessions", out var sessions) &&
                (sessions.ValueKind == JsonValueKind.Object || sessions.ValueKind == JsonValueKind.Array))
            {
                if (sessions.ValueKind == JsonValueKind.Object)
                {
                    foreach (var employeeNode in sessions.EnumerateObject())
                    {
                        if (employeeNode.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var sessionNode in employeeNode.Value.EnumerateObject())
                        {
                            if (sessionNode.Value.ValueKind == JsonValueKind.Object)
                                await ProcessFirebaseTrackingSessionEventAsync(
                                    $"/sessions/{employeeNode.Name}/{sessionNode.Name}",
                                    sessionNode.Value.Clone(), ct);
                        }
                    }
                }
                else
                {
                    var index = 0;
                    foreach (var employeeNode in sessions.EnumerateArray())
                    {
                        if (employeeNode.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var sessionNode in employeeNode.EnumerateObject())
                            {
                                if (sessionNode.Value.ValueKind == JsonValueKind.Object)
                                    await ProcessFirebaseTrackingSessionEventAsync(
                                        $"/sessions/{index}/{sessionNode.Name}",
                                        sessionNode.Value.Clone(), ct);
                            }
                        }
                        index++;
                    }
                }
            }

            if (eventData.Value.TryGetProperty("history", out var history) &&
                (history.ValueKind == JsonValueKind.Object || history.ValueKind == JsonValueKind.Array))
            {
                if (history.ValueKind == JsonValueKind.Object)
                {
                    foreach (var employeeNode in history.EnumerateObject())
                    {
                        if (employeeNode.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var eventNode in employeeNode.Value.EnumerateObject())
                        {
                            if (eventNode.Value.ValueKind == JsonValueKind.Object)
                                await ProcessFirebaseTrackingEventAsync(
                                    $"/history/{employeeNode.Name}/{eventNode.Name}",
                                    eventNode.Value.Clone(), ct, evaluateAttendance: false);
                        }
                    }
                }
                else
                {
                    var index = 0;
                    foreach (var employeeNode in history.EnumerateArray())
                    {
                        if (employeeNode.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var eventNode in employeeNode.EnumerateObject())
                            {
                                if (eventNode.Value.ValueKind == JsonValueKind.Object)
                                    await ProcessFirebaseTrackingEventAsync(
                                        $"/history/{index}/{eventNode.Name}",
                                        eventNode.Value.Clone(), ct, evaluateAttendance: false);
                            }
                        }
                        index++;
                    }
                }
            }
            return;
        }

        // 1200-M: Background tracking lifecycle events are durably queued on
        // Android when Firebase is temporarily unavailable. Replaying these
        // events restores the exact GPS session boundary before/after queued
        // location points are processed, without changing attendance formulas.
        if (parts.Any(p => p.Equals("events", StringComparison.OrdinalIgnoreCase)))
        {
            await ProcessFirebaseTrackingLifecycleEventAsync(eventData.Value, ct);
            return;
        }

        if (parts.Any(p => p.Equals("live", StringComparison.OrdinalIgnoreCase)) ||
            (parts.Length == 1 && int.TryParse(parts[0], out _)))
        {
            await ProcessFirebaseLiveLocationAsync(
                relativePath ?? "/",
                eventData.Value,
                ct);
            return;
        }

        if (parts.Any(p => p.Equals("sessions", StringComparison.OrdinalIgnoreCase)))
        {
            await ProcessFirebaseTrackingSessionEventAsync(
                relativePath ?? "/",
                eventData.Value,
                ct);
            return;
        }

        if (!parts.Any(p => p.Equals("history", StringComparison.OrdinalIgnoreCase)))
            return;

        var employeeId = GetInt(eventData.Value, "EmployeeId", "employeeId");
        if (employeeId <= 0) return;

        var sessionText = GetString(eventData.Value, "SessionId", "sessionId");
        if (!Guid.TryParse(sessionText, out var sessionId) || sessionId == Guid.Empty) return;

        var latitude = GetDouble(eventData.Value, "Latitude", "latitude");
        var longitude = GetDouble(eventData.Value, "Longitude", "longitude");
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            (latitude == 0.0 && longitude == 0.0))
            return;
        var accuracy = GetDouble(eventData.Value, "AccuracyMeters", "accuracyMeters");
        var distance = GetDouble(eventData.Value, "DistanceMeters", "distanceMeters");
        var radius = GetInt(eventData.Value, "AllowedRadiusMeters", "allowedRadiusMeters");
        var within = GetBool(eventData.Value, "IsWithinAllowedRadius", "isWithinAllowedRadius");
        var captured = GetDateTime(eventData.Value, "Timestamp", "timestamp");
        var source = GetString(eventData.Value, "CaptureSource", "captureSource", "Source", "source") ?? "Online";

        if (radius <= 0 || distance < 0)
        {
            var calculated = await geoLocationService.GetDistanceFromOfficeAsync(latitude, longitude);
            if (calculated.Success)
            {
                distance = calculated.DistanceMeters;
                radius = calculated.AllowedRadiusMeters;
                within = radius > 0 && distance <= radius + 2;
            }
        }

        // REQUIREMENT: Correctly identify Android capture source for offline auditing.
        var captureSource = source;
        if (source.Equals("ANDROID_FIREBASE", StringComparison.OrdinalIgnoreCase))
            captureSource = "Online";

        if (evaluateAttendance)
        {
            // Feed the existing, already-tested geofence/session/attendance
            // engine. Firebase is the transport/SSOT; the existing attendance
            // rules remain unchanged. Historical replay never evaluates
            // attendance, preventing an old GPS snapshot from creating a new
            // punch or resurrecting an old session.
            await geoLocationService.UpdateGpsSessionAsync(
                employeeId, sessionId, latitude, longitude, accuracy, distance, radius, within, captured);
        }

        await geoLocationService.SaveLocationHistoryAsync(
            employeeId, sessionId, latitude, longitude, distance, radius, within,
            accuracy, captured, captureSource);

        try
        {
            await _refreshService.NotifyLocationChangedAsync(employeeId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send SignalR location notification for employee {EmployeeId}", employeeId);
        }

        // OfflineSync history is durable recovery evidence. If the live branch
        // could not complete attendance while the phone was offline, replay the
        // captured point through the existing server attendance engine. This
        // never resurrects the GPS session and is idempotent through attendance
        // parity/authoritative-punch protection.
        if (!evaluateAttendance &&
            parts.Any(p => p.Equals("history", StringComparison.OrdinalIgnoreCase)) &&
            captureSource.Equals("OfflineSync", StringComparison.OrdinalIgnoreCase) &&
            captured.HasValue)
        {
            await geoLocationService.ReconcileHistoricalGeofencePointAsync(
                employeeId,
                sessionId,
                latitude,
                longitude,
                accuracy,
                distance,
                radius,
                within,
                captured.Value);
        }

        _logger.LogDebug(
            "Processed Firebase GPS event. EmployeeId={EmployeeId}, SessionId={SessionId}, Distance={Distance}m, Radius={Radius}m, Within={Within}",
            employeeId, sessionId, Math.Round(distance, 1), radius, within);
    }

    private async Task ProcessFirebaseTrackingLifecycleEventAsync(
        JsonElement eventData,
        CancellationToken ct)
    {
        if (eventData.ValueKind != JsonValueKind.Object)
            return;

        var nested = eventData.TryGetProperty("event", out var eventNode) &&
                     eventNode.ValueKind == JsonValueKind.Object
            ? eventNode
            : eventData;

        var eventType = GetString(nested, "eventType", "EventType") ?? string.Empty;
        if (!eventType.Equals("SESSION_STARTED", StringComparison.OrdinalIgnoreCase) &&
            !eventType.Equals("SESSION_ENDED", StringComparison.OrdinalIgnoreCase))
            return;

        var employeeId = GetInt(eventData, "employeeId", "EmployeeId");
        if (employeeId <= 0)
            employeeId = GetInt(nested, "employeeId", "EmployeeId");

        var sessionText = GetString(nested, "sessionId", "SessionId");
        if (employeeId <= 0 || !Guid.TryParse(sessionText, out var sessionId) || sessionId == Guid.Empty)
            return;

        using var scope = _scopeFactory.CreateScope();
        var geo = scope.ServiceProvider.GetRequiredService<GeoLocationService>();

        if (eventType.Equals("SESSION_STARTED", StringComparison.OrdinalIgnoreCase))
        {
            var started = await geo.StartGpsSessionAsync(employeeId, sessionId);
            if (started)
                await _refreshService.NotifyLocationChangedAsync(employeeId);

            _logger.LogDebug(
                "Processed durable Android tracking session start. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId, sessionId);
            return;
        }

        var message = GetString(nested, "message", "Message") ?? string.Empty;
        var reason = "OFFLINE_SYNC";
        const string prefix = "Tracking session end deferred. Reason:";
        if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var parsed = message[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(parsed))
                reason = parsed;
        }

        await geo.EndGpsSessionAsync(employeeId, sessionId, reason);
        await _refreshService.NotifyLocationChangedAsync(employeeId);

        _logger.LogDebug(
            "Processed durable Android tracking session end. EmployeeId={EmployeeId}, SessionId={SessionId}, Reason={Reason}",
            employeeId, sessionId, reason);
    }

    private async Task ProcessFirebaseTrackingSessionEventAsync(
        string relativePath,
        JsonElement? eventData,
        CancellationToken ct)
    {
        // GeoLocationService is scoped. FirebaseSqliteSyncService is a singleton
        // hosted service, so resolve the scoped service inside a short-lived scope
        // for each Firebase event instead of injecting it into the hosted service.
        using var geoScope = _scopeFactory.CreateScope();
        var geoLocationService = geoScope.ServiceProvider.GetRequiredService<GeoLocationService>();

        if (!eventData.HasValue || eventData.Value.ValueKind != JsonValueKind.Object)
            return;

        var employeeId = GetInt(eventData.Value, "EmployeeId", "employeeId");
        if (employeeId <= 0)
        {
            var parts = (relativePath ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                int.TryParse(parts[1], out employeeId);
        }

        var sessionText = GetString(eventData.Value, "SessionId", "sessionId");
        if (employeeId <= 0 || !Guid.TryParse(sessionText, out var sessionId) || sessionId == Guid.Empty)
            return;

        var endedText = GetString(eventData.Value, "EndedAtUtc", "endedAtUtc");
        var endReason = GetString(eventData.Value, "EndReason", "endReason") ?? "LOGGED_OUT";

        if (string.IsNullOrWhiteSpace(endedText))
        {
            var started = await geoLocationService.StartGpsSessionAsync(employeeId, sessionId);
            if (started)
            {
                await _refreshService.NotifyLocationChangedAsync(employeeId);

                _logger.LogInformation(
                    "Firebase GPS session started/provisioned in Web compatibility layer. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    employeeId, sessionId);
            }
            else
            {
                _logger.LogWarning(
                    "Firebase GPS session start could not be provisioned in the Web compatibility layer. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    employeeId, sessionId);
            }
            return;
        }

        await geoLocationService.EndGpsSessionAsync(employeeId, sessionId, endReason);
        await _refreshService.NotifyLocationChangedAsync(employeeId);

        _logger.LogInformation(
            "Firebase GPS session ended in Web compatibility layer. EmployeeId={EmployeeId}, SessionId={SessionId}, Reason={Reason}",
            employeeId, sessionId, endReason);
    }

    private async Task ProcessFirebaseLiveLocationAsync(
        string relativePath,
        JsonElement? eventData,
        CancellationToken ct)
    {
        // Firebase is the realtime transport/SSOT for Android GPS. Every live
        // fix must pass through the same authoritative geofence + attendance
        // engine used by browser/HTTP GPS updates. The live branch must not
        // merely paint the admin map, otherwise Android GPS would never drive
        // automatic geofence attendance reconciliation.
        using var geoScope = _scopeFactory.CreateScope();
        var geoLocationService = geoScope.ServiceProvider.GetRequiredService<GeoLocationService>();

        if (!eventData.HasValue)
        {
            var key = (relativePath ?? "/")
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            if (int.TryParse(key, out var removedEmployeeId))
            {
                var currentSession = LiveLocationStore.GetSessionId(removedEmployeeId);
                if (currentSession.HasValue)
                    LiveLocationStore.Remove(removedEmployeeId, currentSession.Value);

                await _refreshService.NotifyLocationChangedAsync(removedEmployeeId);
            }

            return;
        }

        if (eventData.Value.ValueKind != JsonValueKind.Object)
            return;

        var employeeId = GetInt(eventData.Value, "EmployeeId", "employeeId");
        if (employeeId <= 0)
        {
            var key = (relativePath ?? "/")
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            employeeId = int.TryParse(key, out var parsed) ? parsed : 0;
        }

        if (employeeId <= 0)
            return;

        var sessionText = GetString(eventData.Value, "SessionId", "sessionId");
        if (!Guid.TryParse(sessionText, out var sessionId) || sessionId == Guid.Empty)
            return;

        var latitude = GetDouble(eventData.Value, "Latitude", "latitude");
        var longitude = GetDouble(eventData.Value, "Longitude", "longitude");
        var accuracy = Math.Max(0, GetDouble(eventData.Value, "AccuracyMeters", "accuracyMeters"));
        var captured = GetDateTime(eventData.Value, "Timestamp", "timestamp") ?? DateTime.UtcNow;

        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            (latitude == 0.0 && longitude == 0.0))
        {
            _logger.LogWarning(
                "Rejected Firebase GPS live event with invalid coordinates. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);
            return;
        }

        // Never trust client-supplied distance/radius/within values. Recompute
        // against the current Admin-configured office location and radius.
        // This also makes a live Admin configuration change effective for the
        // next Firebase GPS fix without requiring Android to restart tracking.
        var distanceResult = await geoLocationService.GetDistanceFromOfficeAsync(
            latitude,
            longitude);

        if (!distanceResult.Success)
        {
            _logger.LogWarning(
                "Firebase GPS live event could not be geofence-validated. EmployeeId={EmployeeId}, SessionId={SessionId}, Message={Message}",
                employeeId,
                sessionId,
                distanceResult.Message);
            return;
        }

        var distance = distanceResult.DistanceMeters;
        var radius = distanceResult.AllowedRadiusMeters;
        var within = distanceResult.IsWithinAllowedRadius;

        // 1200-L integration point: use the existing GeoLocationService as
        // the sole attendance authority. This preserves payroll locks, manual
        // overrides, automatic geofence reconciliation, GPS session lifecycle,
        // cross-day attendance rules, and existing refresh behavior.
        var accepted = await geoLocationService.UpdateGpsSessionAsync(
            employeeId,
            sessionId,
            latitude,
            longitude,
            accuracy,
            distance,
            radius,
            within,
            captured);

        if (accepted)
        {
            await geoLocationService.SaveLocationHistoryAsync(
                employeeId,
                sessionId,
                latitude,
                longitude,
                distance,
                radius,
                within,
                accuracy,
                captured,
                "Online");
        }

        if (!accepted)
        {
            // SQL may already have ended this session while Firebase still
            // carries an ACTIVE marker. Close only this stale session in
            // Firebase. The next current Android GPS fix will then rotate to
            // a NEW session instead of continuing an ended timeline.
            var activeSession = await geoLocationService.GetActiveGpsSessionAsync(employeeId);

            if (activeSession == null || activeSession.SessionId != sessionId)
            {
                try
                {
                    await _firebase.MarkTrackingSessionEndedAsync(
                        employeeId,
                        sessionId,
                        _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee"),
                        "WEB_SESSION_RECOVERY");
                }
                catch (Exception recoveryEx)
                {
                    _logger.LogWarning(
                        recoveryEx,
                        "Failed to close stale Firebase GPS session during recovery. EmployeeId={EmployeeId}, SessionId={SessionId}",
                        employeeId,
                        sessionId);
                }
            }

            _logger.LogDebug(
                "Firebase GPS live event was not accepted for incoming session; recovery state reconciled. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);
            return;
        }

        // UpdateGpsSessionAsync already updates LiveLocationStore only after
        // validating session ownership/order. Avoid a second competing writer.
        await _refreshService.NotifyLocationChangedAsync(employeeId);

        _logger.LogDebug(
            "Processed Firebase live GPS event through authoritative geofence engine. EmployeeId={EmployeeId}, SessionId={SessionId}, Distance={Distance}m, Radius={Radius}, Within={Within}",
            employeeId,
            sessionId,
            Math.Round(distance, 1),
            radius,
            within);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out i)) return i;
        }
        return 0;
    }

    private static double GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
        }
        return 0;
    }

    private static bool GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
        }
        return false;
    }

    private static DateTime? GetDateTime(JsonElement element, params string[] names)
    {
        var text = GetString(element, names);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    private static long GetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l)) return l;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out l)) return l;
        }
        return 0L;
    }

    private async Task ProcessFirebaseWipeEventAsync(
        string tenantId,
        JsonElement? data,
        CancellationToken ct)
    {
        if (!data.HasValue || data.Value.ValueKind != JsonValueKind.Object)
            return;

        var wipeType = GetString(data.Value, "wipeType", "wipe_type", "type");
        var source = GetString(data.Value, "source") ?? "Remote";
        var timestamp = GetLong(data.Value, "timestamp");

        if (timestamp > 0 && timestamp < _serviceStartTimeEpochMs)
            return;

        if (timestamp > 0 && timestamp <= _lastProcessedWipeEpochMs)
            return;

        _lastProcessedWipeEpochMs = timestamp;

        _logger.LogInformation(
            "Processing real-time wipe event from {Source} for tenant {TenantId}: Type={WipeType}",
            source, tenantId, wipeType);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupRestoreService>();

            if (string.Equals(wipeType, "FULL", StringComparison.OrdinalIgnoreCase))
            {
                await backupService.WipeLocalAllDataForTenantAsync(tenantId, ct);
            }
            else
            {
                await backupService.WipeLocalOperationalDataForTenantAsync(tenantId, ct);
            }

            await _refreshService.NotifyGlobalRefreshAsync($"TENANT_DATA_WIPED:{tenantId}");
            await _refreshService.NotifyApplicationDataChangedAsync(new[]
            {
                "AttendancePunch", "AttendanceLog", "LeaveRequest", "SalaryAdvance", "Employee"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process remote wipe event for tenant {TenantId}", tenantId);
        }
    }

    private async Task ProcessFirebaseMobileAuthEventAsync(
        string relativePath,
        JsonElement? eventData,
        CancellationToken ct)
    {
        if (!eventData.HasValue || eventData.Value.ValueKind != JsonValueKind.Object) return;

        // The first Firebase SSE event for mobile_auth_events is the complete
        // current snapshot at /. Walk that snapshot so existing Android login
        // events are projected into the Web audit screen after startup/reconnect.
        var normalizedPath = (relativePath ?? "/").Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            foreach (var employeeNode in eventData.Value.EnumerateObject())
            {
                if (employeeNode.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var eventNode in employeeNode.Value.EnumerateObject())
                {
                    if (eventNode.Value.ValueKind != JsonValueKind.Object) continue;
                    await ProcessFirebaseMobileAuthEventAsync(
                        $"/{employeeNode.Name}/{eventNode.Name}",
                        eventNode.Value.Clone(),
                        ct);
                }
            }
            return;
        }

        var employeeId = GetInt(eventData.Value, "employeeId", "EmployeeId");
        var eventType = GetString(eventData.Value, "eventType", "EventType") ?? "MOBILE_AUTH_EVENT";
        var uid = GetString(eventData.Value, "firebaseUid", "FirebaseUid") ?? string.Empty;
        var email = GetString(eventData.Value, "email", "Email") ?? string.Empty;
        var deviceId = GetString(eventData.Value, "deviceId", "DeviceId") ?? string.Empty;
        var platform = GetString(eventData.Value, "platform", "Platform") ?? "Android";
        var timestamp = GetDateTime(eventData.Value, "timestamp", "Timestamp") ?? DateTime.UtcNow;
        var normalizedRelativePath =
    (relativePath ?? string.Empty).Trim('/');

        var eventId = GetString(
            eventData.Value,
            "eventId",
            "EventId")
            ?? normalizedRelativePath
                .Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

        if (string.IsNullOrWhiteSpace(eventId)) eventId = Guid.NewGuid().ToString("N");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        // Avoid duplicating an Android event when the Firebase stream reconnects.
        var marker = $"FirebaseMobileAuth:{eventId}";
        var exists = await db.AuditLogs.AsNoTracking()
            .AnyAsync(x => x.EntityID == marker, ct);
        if (exists) return;

        var details = new Dictionary<string, object?>
        {
            ["EventType"] = eventType,
            ["Platform"] = platform,
            ["DeviceId"] = deviceId,
            ["FirebaseUid"] = uid,
            ["RecordedAtUtc"] = timestamp.ToString("O"),
            ["EventId"] = eventId
        };

        foreach (var prop in eventData.Value.EnumerateObject())
        {
            if (!details.ContainsKey(prop.Name))
            {
                details[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
            }
        }

        var log = new Payroll.Shared.Data.AuditLog
        {
            Timestamp = timestamp,
            UserID = string.IsNullOrWhiteSpace(uid) ? (employeeId > 0 ? $"ANDROID_EMPLOYEE_{employeeId}" : (string.IsNullOrWhiteSpace(email) ? "Android" : email)) : uid,
            UserEmail = string.IsNullOrWhiteSpace(email) ? "Android" : email,
            ActionType = "AUTH_SESSION",
            EntityType = employeeId > 0 ? "EmployeeSession" : "UserSession",
            EntityID = marker,
            Details = JsonSerializer.Serialize(details)
        };

        using var syncScope = _firebaseSyncWriteScope.Enter();
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync(ct);

        await _refreshService.NotifyApplicationDataChangedAsync(new[] { "AuditLog" });
        _logger.LogInformation(
            "Android Firebase auth event projected to Web audit monitor. EmployeeId={EmployeeId}, Event={EventType}, Email={Email}, DeviceId={DeviceId}",
            employeeId, eventType, email, deviceId);
    }

    private async Task SyncAllTablesAsync(string ownerUid, CancellationToken ct)
    {
        // Exclude strictly background tracking breadcrumbs and historical logs from bootstrap.
        // Operational tables (CompanySetting, FeatureSettings, Shop, Employee, AttendancePunch,
        // AttendanceLog, GeoPunchAudit, LeaveRequest, SalaryAdvance, AttendanceRegularization)
        // are always synced so that offline edits from mobile or web immediately catch up.
        var skipOnBootstrap = new HashSet<string>(StringComparer.Ordinal)
        {
            "AuditLog",                 // audit_logs         — append-only, large
            "EmployeeLocationHistory",  // tracking/history   — append-only, large
            "EmployeeGpsSession",       // tracking/sessions  — append-only, large
            "PayrollHistory",           // payroll_history    — historical, large
            "SalarySnapshot",           // salary_snapshots   — historical, large
        };

        var priorityOrder = new[]
        {
            "CompanySetting",
            "FeatureSettings",
            "Shop",
            "Employee",
            "AttendancePunch",
            "AttendanceLog",
            "GeoPunchAudit",
            "LeaveRequest",
            "SalaryAdvance",
            "AttendanceRegularization"
        };

        var orderedTables = priorityOrder
            .Where(name => Tables.ContainsKey(name))
            .Select(name => new KeyValuePair<string, string>(name, Tables[name]))
            .Concat(Tables.Where(kvp => !priorityOrder.Contains(kvp.Key, StringComparer.Ordinal)))
            .ToList();

        foreach (var table in orderedTables)
        {
            ct.ThrowIfCancellationRequested();
            if (skipOnBootstrap.Contains(table.Key))
            {
                _logger.LogInformation(
                    "Skipping bootstrap sync for high-volume table {Table} ({FirebaseTable}). " +
                    "SSE stream will deliver all deltas.", table.Key, table.Value);
                continue;
            }
            try
            {
                var changed = await SyncTableAsync(table.Key, table.Value, ownerUid, ct);
                if (table.Key.Equals("CompanySetting", StringComparison.Ordinal))
                {
                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        var cs = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingID == 1, ct);
                        if (cs != null)
                        {
                            await _refreshService.NotifyGeoSettingsChangedAsync(
                                cs.OfficeLatitude, cs.OfficeLongitude, cs.GeoRadiusMeters, cs.UseSpeedBasedMarkers);
                        }
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogWarning(notifyEx, "Failed to broadcast bootstrap geo settings for {OwnerUid}", ownerUid);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to bootstrap sync table {Table} ({FirebaseTable}) for owner {OwnerUid}", table.Key, table.Value, ownerUid);
            }
        }
    }

    private async Task<bool> SyncTableAsync(
        string entityName,
        string firebaseTable,
        string ownerUid,
        CancellationToken ct)
    {
        if (entityName == "EmployeeLocationHistory")
            return await SyncTrackingHistoryAsync(ownerUid, ct);

        var json = await _firebase.GetOwnerTableAsync(ownerUid, firebaseTable, ct);
        if (json is null)
            return false;

        // Firebase REST can serialize numeric-keyed collections as arrays.
        // Normalize that transport representation to the existing object-shaped
        // upsert pipeline without changing the database schema.
        if (json.Value.ValueKind == JsonValueKind.Array)
        {
            var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in json.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Null)
                    normalized[index.ToString(CultureInfo.InvariantCulture)] = item.Clone();
                index++;
            }

            var normalizedJson = JsonSerializer.SerializeToElement(normalized);
            return await UpsertTableAsync(entityName, normalizedJson, ownerUid, ct);
        }

        if (json.Value.ValueKind != JsonValueKind.Object)
            return false;

        return await UpsertTableAsync(entityName, json.Value, ownerUid, ct);
    }

    private async Task<bool> SyncTrackingHistoryAsync(
    string ownerUid,
    CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            ownerUid.Equals("tenant_nocompany", StringComparison.OrdinalIgnoreCase) ||
            ownerUid.StartsWith("tenant_no", StringComparison.OrdinalIgnoreCase))
            return false;

        const int bootstrapHistoryLimitPerEmployee = 2000;

        var changed = false;

        /*
         * IMPORTANT:
         *
         * Do NOT read:
         *
         * owners/{ownerUid}/tracking/history
         *
         * as one giant Firebase REST response.
         *
         * GPS history grows continuously and can become very large.
         *
         * Instead:
         *   1. Read the owner employee directory.
         *   2. Read each employee's history independently.
         *   3. Limit each employee to the latest 2000 points.
         *
         * New history continues through the realtime tracking stream,
         * so the Web application does not lose future GPS events.
         */

        var employeesJson =
            await _firebase.GetOwnerTableAsync(
                ownerUid,
                "employees",
                ct);

        if (!employeesJson.HasValue)
        {
            _logger.LogWarning(
                "Firebase employee directory unavailable during GPS history bootstrap for owner {OwnerUid}.",
                ownerUid);

            return false;
        }

        var employeeIds = new HashSet<int>();

        if (employeesJson.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var employeeNode in employeesJson.Value.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();

                if (employeeNode.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (int.TryParse(
                        employeeNode.Name,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var keyId) &&
                    keyId > 0)
                {
                    employeeIds.Add(keyId);
                    continue;
                }

                var payloadId =
                    GetInt(
                        employeeNode.Value,
                        "EmployeeId",
                        "employeeId");

                if (payloadId > 0)
                    employeeIds.Add(payloadId);
            }
        }
        else if (employeesJson.Value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var employeeNode in employeesJson.Value.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                if (employeeNode.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                var payloadId =
                    GetInt(
                        employeeNode,
                        "EmployeeId",
                        "employeeId");

                if (payloadId > 0)
                    employeeIds.Add(payloadId);
                else if (index > 0)
                    employeeIds.Add(index);

                index++;
            }
        }

        if (employeeIds.Count == 0)
        {
            _logger.LogInformation(
                "No employees found for GPS history bootstrap for owner {OwnerUid}.",
                ownerUid);

            return false;
        }

        foreach (var employeeId in employeeIds.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json =
                    await _firebase.GetOwnerTrackingHistoryAsync(
                        ownerUid,
                        employeeId,
                        bootstrapHistoryLimitPerEmployee,
                        ct);

                if (!json.HasValue)
                    continue;

                async Task ProcessHistoryEvent(
                    JsonElement eventNode)
                {
                    ct.ThrowIfCancellationRequested();

                    if (eventNode.ValueKind != JsonValueKind.Object)
                        return;

                    await ProcessFirebaseTrackingEventAsync(
                        $"/history/{employeeId}",
                        eventNode.Clone(),
                        ct,
                        evaluateAttendance: false);

                    changed = true;
                }

                if (json.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var eventNode in json.Value.EnumerateObject())
                    {
                        await ProcessHistoryEvent(eventNode.Value);
                    }
                }
                else if (json.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var eventNode in json.Value.EnumerateArray())
                    {
                        await ProcessHistoryEvent(eventNode);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                /*
                 * One employee's oversized/malformed history must not prevent
                 * all other employees from synchronizing.
                 */
                _logger.LogWarning(
                    ex,
                    "GPS history bootstrap skipped for EmployeeId={EmployeeId}, Owner={OwnerUid}.",
                    employeeId,
                    ownerUid);
            }
        }

        return changed;
    }

    private readonly record struct FirebasePathTarget(string EntityName, string? RecordKey);

    private static FirebasePathTarget? ParseFirebasePath(string relativePath)
    {
        var normalized = (relativePath ?? "/").Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var firebaseTable = parts[0];

        if (firebaseTable.Equals("tracking", StringComparison.OrdinalIgnoreCase) &&
            parts.Length >= 4 &&
            parts[1].Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            return new FirebasePathTarget("TrackingHistory", parts[3]);
        }

        if (firebaseTable.Equals("tracking", StringComparison.OrdinalIgnoreCase))
            return new FirebasePathTarget("TrackingHistory", null);

        if (firebaseTable.Equals("mobile_auth_events", StringComparison.OrdinalIgnoreCase))
            return new FirebasePathTarget("MobileAuthEvent", parts.Length >= 3 ? parts[2] : null);

        var entity = Tables.FirstOrDefault(x =>
            string.Equals(x.Value, firebaseTable, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(entity.Key))
            return null;

        if (parts.Length == 1)
            return new FirebasePathTarget(entity.Key, null);

        var key = Uri.UnescapeDataString(parts[1]);
        key = key.Replace("%2E", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("%23", "#", StringComparison.OrdinalIgnoreCase)
            .Replace("%24", "$", StringComparison.OrdinalIgnoreCase)
            .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
            .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
            .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        return new FirebasePathTarget(entity.Key, key);
    }

    private async Task<bool> DeleteLocalFirebaseRecordAsync(
        string entityName,
        string? firebaseKey,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(x => x.ClrType.Name == entityName);
        if (entityType == null)
            return false;

        if (string.IsNullOrWhiteSpace(firebaseKey))
        {
            var rows = await GetEntitySet(db, entityType.ClrType).Cast<object>().ToListAsync(ct);
            if (rows.Count == 0)
                return false;

            using var tableScope = _firebaseSyncWriteScope.Enter();
            db.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return true;
        }

        var keys = entityType.FindPrimaryKey()?.Properties;
        if (keys == null || keys.Count == 0)
            return false;

        var keyParts = firebaseKey.Split('|');
        if (entityType.ClrType == typeof(SalaryAdvance))
        {
            SalaryAdvance? advToDelete = null;
            if (int.TryParse(keyParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAdvId) && parsedAdvId > 0)
            {
                advToDelete = await db.SalaryAdvances.FirstOrDefaultAsync(s => s.AdvanceID == parsedAdvId, ct);
            }

            if (advToDelete != null)
            {
                using var advScope = _firebaseSyncWriteScope.Enter();
                db.SalaryAdvances.Remove(advToDelete);
                await db.SaveChangesAsync(ct);
                return true;
            }
        }

        if (keyParts.Length < keys.Count)
            return false;

        var keyValues = new object?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var keyProperty = keys[i];
            keyValues[i] = ConvertStringValue(keyParts[i], keyProperty.ClrType);
            if (keyValues[i] is null)
                return false;
        }

        var existing = await db.FindAsync(entityType.ClrType, keyValues, ct);
        if (existing == null)
            return false;

        using var recordScope = _firebaseSyncWriteScope.Enter();
        db.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UpsertSingleFirebaseRecordAsync(
        string entityName,
        string firebaseKey,
        JsonElement recordJson,
        string ownerUid,
        CancellationToken ct)
    {
        try
        {
            if (entityName.Equals("CompanySetting", StringComparison.Ordinal) ||
                entityName.Equals("FeatureSettings", StringComparison.Ordinal))
            {
                var wrapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [firebaseKey] = recordJson
                };
                var wrappedJson = JsonSerializer.SerializeToElement(wrapped);
                return await UpsertTableAsync(entityName, wrappedJson, ownerUid, ct);
            }

            if (entityName.Equals("AttendancePunch", StringComparison.Ordinal))
            {
                entityName = "AttendanceLog";
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);

            var entityType = db.Model.GetEntityTypes()
                .FirstOrDefault(x => x.ClrType.Name == entityName);
            if (entityType == null)
                return false;

            var keys = entityType.FindPrimaryKey()?.Properties;
            if (keys == null || keys.Count == 0)
                return false;

            var rowChanged = await UpsertRecordAsync(
                db,
                entityType,
                keys,
                firebaseKey,
                recordJson,
                ct);

            if (!rowChanged)
                return false;

            using var syncScope = _firebaseSyncWriteScope.Enter();
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert single Firebase record for {Entity} key {Key}", entityName, firebaseKey);
            return false;
        }
    }

    private async Task<bool> UpsertTableAsync(
     string entityName,
     JsonElement table,
     string ownerUid,
     CancellationToken ct)
    {
        if (entityName.Equals("AttendancePunch", StringComparison.Ordinal))
        {
            entityName = "AttendanceLog";
        }
        // LeaveRequest requires special handling because its shared EF model
        // uses an identity/ValueGeneratedOnAdd key while Firebase owns the
        // actual record ID.
        //
        // Each LeaveRequest is processed with its own DbContext so that one
        // problematic/generated-key record cannot collide with another tracked
        // LeaveRequest instance.

        if (entityName.Equals("LeaveRequest", StringComparison.Ordinal) ||
            entityName.Equals("AuditLog", StringComparison.Ordinal))
        {
            var isolatedEntityChanged = false;
            var isolatedEntityName = entityName;


            foreach (var isolatedChild in table.EnumerateObject())
            {
                if (isolatedChild.Value.ValueKind != JsonValueKind.Object)
                    continue;

                try
                {
                    // IMPORTANT:
                    // LeaveRequest and AuditLog both use generated/identity
                    // primary-key metadata in the shared EF model while
                    // Firebase supplies the authoritative record key.
                    //
                    // A separate DbContext per Firebase record prevents EF's
                    // identity map/store-generated-value propagation from
                    // making one record collide with another tracked instance.
                    await using var isolatedScope =
                        _scopeFactory.CreateAsyncScope();

                    var isolatedFactory =
                        isolatedScope.ServiceProvider
                            .GetRequiredService<IDbContextFactory<AppDbContext>>();

                    await using var isolatedDb =
                        await isolatedFactory.CreateDbContextAsync(ct);

                    var isolatedEntityType =
                        isolatedDb.Model.GetEntityTypes()
                            .FirstOrDefault(x => x.ClrType.Name == isolatedEntityName);

                    var isolatedKeys =
                        isolatedEntityType?.FindPrimaryKey()?.Properties;

                    if (isolatedEntityType == null ||
                        isolatedKeys == null ||
                        isolatedKeys.Count == 0)
                    {
                        _logger.LogWarning(
                            "Firebase {Entity} sync skipped because EF entity/key metadata was not found.",
                            isolatedEntityName);
                        continue;
                    }

                    var rowChanged = await UpsertRecordAsync(
                        isolatedDb,
                        isolatedEntityType,
                        isolatedKeys,
                        isolatedChild.Name,
                        isolatedChild.Value,
                        ct);

                    if (!rowChanged)
                        continue;

                    using var isolatedSyncScope =
                        _firebaseSyncWriteScope.Enter();

                    await isolatedDb.SaveChangesAsync(ct);

                    isolatedEntityChanged = true;
                }
                catch (OperationCanceledException)
                    when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A single invalid/duplicate identity-key record must
                    // never terminate the Firebase synchronization service.
                    _logger.LogError(
                        ex,
                        "Firebase {Entity} projection failed for row {Key}; continuing with remaining records.",
                        isolatedEntityName,
                        isolatedChild.Name);
                }
            }

            return isolatedEntityChanged;
        }

        // ------------------------------------------------------------
        // Normal Firebase table synchronization
        // ------------------------------------------------------------

        await using var tableScope =
            _scopeFactory.CreateAsyncScope();

        var tableFactory =
            tableScope.ServiceProvider
                .GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var tableDb =
            await tableFactory.CreateDbContextAsync(ct);

        // Strict Multi-Tenant Isolation for CompanySettings:
        // Prevent tenant_2 or any other company from overwriting SettingID = 1 in SQLite!
        if (entityName.Equals("CompanySetting", StringComparison.Ordinal))
        {
            var tenant = await tableDb.CompanyTenants.FirstOrDefaultAsync(t => t.TenantId == ownerUid, ct)
                ?? await tableDb.CompanyTenants.FirstOrDefaultAsync(t => t.IsActive, ct)
                ?? await tableDb.CompanyTenants.FirstOrDefaultAsync(ct);
            var targetSettingId = tenant?.CompanySettingId > 0 ? tenant.CompanySettingId : 1;

            var existingSetting = await tableDb.CompanySettings.FirstOrDefaultAsync(s => s.SettingID == targetSettingId, ct);
            if (existingSetting == null)
            {
                existingSetting = new CompanySetting { SettingID = targetSettingId };
                tableDb.CompanySettings.Add(existingSetting);
            }

            foreach (var child in table.EnumerateObject())
            {
                if (child.Value.ValueKind != JsonValueKind.Object) continue;

                var compName = GetString(child.Value, "companyName", "CompanyName");
                if (!string.IsNullOrWhiteSpace(compName))
                {
                    existingSetting.CompanyName = compName.Trim();
                    if (tenant != null) tenant.CompanyName = compName.Trim();
                }
                var addr = GetString(child.Value, "addressLine1", "AddressLine1");
                if (addr != null) existingSetting.AddressLine1 = addr;
                var city = GetString(child.Value, "cityStatePincode", "CityStatePincode");
                if (city != null) existingSetting.CityStatePincode = city;

                var lat = GetDouble(child.Value, "officeLatitude", "OfficeLatitude", "latitude", "Latitude");
                if (lat != 0) existingSetting.OfficeLatitude = lat;
                var lon = GetDouble(child.Value, "officeLongitude", "OfficeLongitude", "longitude", "Longitude");
                if (lon != 0) existingSetting.OfficeLongitude = lon;
                var rad = GetInt(child.Value, "geoRadiusMeters", "GeoRadiusMeters", "radius", "Radius");
                if (rad > 0) existingSetting.GeoRadiusMeters = rad;
                var cutoff = GetInt(child.Value, "workDayCutoffHour", "WorkDayCutoffHour");
                if (cutoff > 0) existingSetting.WorkDayCutoffHour = cutoff;
                var late = GetInt(child.Value, "lateGraceMinutes", "LateGraceMinutes");
                if (late >= 0) existingSetting.LateGraceMinutes = late;
                var endGrace = GetInt(child.Value, "endTimeGraceMinutes", "EndTimeGraceMinutes");
                if (endGrace >= 0) existingSetting.EndTimeGraceMinutes = endGrace;
                var backup = GetInt(child.Value, "autoBackupIntervalHours", "AutoBackupIntervalHours");
                if (child.Value.TryGetProperty("autoBackupIntervalHours", out _) || child.Value.TryGetProperty("AutoBackupIntervalHours", out _))
                {
                    existingSetting.AutoBackupIntervalHours = backup;
                }
                var pageTrans = GetString(child.Value, "pageTransitionEffect", "page_transition_effect", "PageTransitionEffect");
                if (!string.IsNullOrWhiteSpace(pageTrans)) existingSetting.PageTransitionEffect = pageTrans;
                var dwell = GetInt(child.Value, "stayDwellMinutes", "StayDwellMinutes");
                if (dwell > 0) existingSetting.StayDwellMinutes = dwell;
                var cluster = GetInt(child.Value, "stayClusterRadiusMeters", "StayClusterRadiusMeters");
                if (cluster > 0) existingSetting.StayClusterRadiusMeters = cluster;
                var speed = GetBool(child.Value, "useSpeedBasedMarkers", "use_speed_based_markers", "UseSpeedBasedMarkers");
                existingSetting.UseSpeedBasedMarkers = speed;
                var emailNotif = GetBool(child.Value, "enableEmailNotifications", "EnableEmailNotifications");
                existingSetting.EnableEmailNotifications = emailNotif;
                var host = GetString(child.Value, "smtpHost", "SmtpHost");
                if (host != null) existingSetting.SmtpHost = host;
                var port = GetInt(child.Value, "smtpPort", "SmtpPort");
                if (port > 0) existingSetting.SmtpPort = port;
                var fromEmail = GetString(child.Value, "smtpFromEmail", "SmtpFromEmail");
                if (fromEmail != null) existingSetting.SmtpFromEmail = fromEmail;
                var user = GetString(child.Value, "smtpUser", "SmtpUser");
                if (user != null) existingSetting.SmtpUser = user;
                var pass = GetString(child.Value, "smtpPass", "SmtpPass");
                if (pass != null) existingSetting.SmtpPass = pass;
                break;
            }

            using var syncScope = _firebaseSyncWriteScope.Enter();
            await tableDb.SaveChangesAsync(ct);
            return true;
        }

        // Strict Multi-Tenant Isolation for FeatureSettings:
        if (entityName.Equals("FeatureSettings", StringComparison.Ordinal))
        {
            var tenant = await tableDb.CompanyTenants.FirstOrDefaultAsync(t => t.TenantId == ownerUid, ct)
                ?? await tableDb.CompanyTenants.FirstOrDefaultAsync(t => t.IsActive, ct)
                ?? await tableDb.CompanyTenants.FirstOrDefaultAsync(ct);
            var targetFeatureId = tenant?.FeatureSettingsId > 0 ? tenant.FeatureSettingsId : 1;

            var existingFeature = await tableDb.FeatureSettings.FirstOrDefaultAsync(f => f.Id == targetFeatureId, ct);
            if (existingFeature == null)
            {
                existingFeature = new FeatureSettings { Id = targetFeatureId };
                tableDb.FeatureSettings.Add(existingFeature);
            }

            foreach (var child in table.EnumerateObject())
            {
                if (child.Value.ValueKind != JsonValueKind.Object) continue;

                existingFeature.EnablePayroll = GetBool(child.Value, "enablePayroll", "EnablePayroll");
                existingFeature.EnableGeoFencing = GetBool(child.Value, "enableGeoFencing", "EnableGeoFencing");
                existingFeature.EnableDualAttendance = GetBool(child.Value, "enableDualAttendance", "EnableDualAttendance");
                existingFeature.EnableAutomaticGeofencePunching = GetBool(child.Value, "enableAutomaticGeofencePunching", "enable_automatic_geofence_punching", "EnableAutomaticGeofencePunching");
                existingFeature.EnableSalaryAdvance = GetBool(child.Value, "enableSalaryAdvance", "EnableSalaryAdvance");
                existingFeature.EnableBonusManagement = GetBool(child.Value, "enableBonusManagement", "EnableBonusManagement");
                existingFeature.EnableLeaveManagement = GetBool(child.Value, "enableLeaveManagement", "EnableLeaveManagement");
                existingFeature.EnableShiftScheduling = GetBool(child.Value, "enableShiftScheduling", "EnableShiftScheduling");
                existingFeature.EnablePunchCorrection = GetBool(child.Value, "enablePunchCorrection", "EnablePunchCorrection");
                existingFeature.EnableTaxDeclarations = GetBool(child.Value, "enableTaxDeclarations", "EnableTaxDeclarations");
                existingFeature.EnableResignationModule = GetBool(child.Value, "enableResignationModule", "EnableResignationModule");
                existingFeature.EnableFlexibleBenefits = GetBool(child.Value, "enableFlexibleBenefits", "EnableFlexibleBenefits");
                existingFeature.EnableCompanyReports = GetBool(child.Value, "enableCompanyReports", "EnableCompanyReports");
                existingFeature.EnableEmailNotifications = GetBool(child.Value, "enableEmailNotifications", "EnableEmailNotifications");
                existingFeature.AdminCanViewDashboard = GetBool(child.Value, "adminCanViewDashboard", "AdminCanViewDashboard");
                existingFeature.AdminCanManageEmployees = GetBool(child.Value, "adminCanManageEmployees", "AdminCanManageEmployees");
                existingFeature.AdminCanViewAttendance = GetBool(child.Value, "adminCanViewAttendance", "AdminCanViewAttendance");
                existingFeature.AdminCanRunPayroll = GetBool(child.Value, "adminCanRunPayroll", "AdminCanRunPayroll");
                existingFeature.AdminCanEditSettings = GetBool(child.Value, "adminCanEditSettings", "AdminCanEditSettings");
                existingFeature.AdminCanManageShifts = GetBool(child.Value, "adminCanManageShifts", "AdminCanManageShifts");
                existingFeature.AdminCanManagePunchApprovals = GetBool(child.Value, "adminCanManagePunchApprovals", "AdminCanManagePunchApprovals");
                existingFeature.AdminCanViewReports = GetBool(child.Value, "adminCanViewReports", "AdminCanViewReports");
                existingFeature.AdminCanManageEmployeePermissions = GetBool(child.Value, "adminCanManageEmployeePermissions", "AdminCanManageEmployeePermissions");
                existingFeature.AdminCanManageFeatureToggles = GetBool(child.Value, "adminCanManageFeatureToggles", "AdminCanManageFeatureToggles");
                break;
            }

            using var syncScope = _firebaseSyncWriteScope.Enter();
            await tableDb.SaveChangesAsync(ct);
            return true;
        }

        var tableEntityType =
            tableDb.Model.GetEntityTypes()
                .FirstOrDefault(x => x.ClrType.Name == entityName);

        if (tableEntityType == null)
            return false;

        var tableKeys =
            tableEntityType.FindPrimaryKey()?.Properties;

        if (tableKeys == null || tableKeys.Count == 0)
            return false;

        var changedAny = false;
        var processedCount = 0;

        foreach (var child in table.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object)
                continue;

            // Handle 2-level hierarchy in tracking/sessions: tracking/sessions/{employeeId}/{sessionId}
            if (entityName == "EmployeeGpsSession" &&
                !child.Value.TryGetProperty("SessionId", out _) &&
                !child.Value.TryGetProperty("sessionId", out _))
            {
                foreach (var sessionChild in child.Value.EnumerateObject())
                {
                    if (sessionChild.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    try
                    {
                        changedAny |= await UpsertRecordAsync(
                            tableDb,
                            tableEntityType,
                            tableKeys,
                            sessionChild.Name,
                            sessionChild.Value,
                            ct);

                        processedCount++;
                        if (processedCount % 100 == 0)
                        {
                            if (changedAny)
                            {
                                using var batchSyncScope = _firebaseSyncWriteScope.Enter();
                                await tableDb.SaveChangesAsync(ct);
                                changedAny = false;
                            }
                            tableDb.ChangeTracker.Clear();
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Skipping Firebase nested session {Key}", sessionChild.Name);
                    }
                }
                continue;
            }

            try
            {
                changedAny |= await UpsertRecordAsync(
                    tableDb,
                    tableEntityType,
                    tableKeys,
                    child.Name,
                    child.Value,
                    ct);

                processedCount++;

                // Save in batches to avoid keeping a very large Firebase
                // snapshot tracked inside one EF context.
                if (processedCount % 100 == 0)
                {
                    if (changedAny)
                    {
                        using var batchSyncScope =
                            _firebaseSyncWriteScope.Enter();

                        await tableDb.SaveChangesAsync(ct);

                        changedAny = false;
                    }

                    tableDb.ChangeTracker.Clear();
                }
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping Firebase row {Entity}/{Key}.",
                    entityName,
                    child.Name);
            }
        }

        if (changedAny)
        {
            try
            {
                using var finalSyncScope =
                    _firebaseSyncWriteScope.Enter();

                await tableDb.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Do not allow one Firebase table's database projection
                // failure to terminate the Web host. The affected batch is
                // discarded from this context and the realtime stream remains
                // available for the next change/reconnect.
                _logger.LogError(
                    ex,
                    "Firebase table final SaveChanges failed for {Entity}; clearing the failed EF batch and continuing.",
                    entityName);

                tableDb.ChangeTracker.Clear();
            }
        }

        return true;
    }

    private static async Task<bool> UpsertRecordAsync(
        AppDbContext db,
        IEntityType entityType,
        IReadOnlyList<IProperty> keys,
        string firebaseKey,
        JsonElement json,
        CancellationToken ct)
    {
        // Specialized handler for EmployeeGpsSession:
        // In SQLite, session_id is a unique key, and id is an auto-increment integer.
        // Firebase keys records by SessionId (Guid), so lookups and updates must match on SessionId
        // rather than the auto-generated id to prevent 'UNIQUE constraint failed: employee_gps_sessions.session_id'.
        if (entityType.ClrType == typeof(EmployeeGpsSession))
        {
            var sidElement = FindJsonValue(json, nameof(EmployeeGpsSession.SessionId));
            var sidStr = sidElement?.GetString() ?? sidElement?.ToString() ?? (firebaseKey.Contains('/') ? firebaseKey.Split('/')[^1] : firebaseKey);
            if (!Guid.TryParse(sidStr, out var sid) || sid == Guid.Empty)
                return false;

            var existingSession = await db.EmployeeGpsSessions.FirstOrDefaultAsync(s => s.SessionId == sid, ct)
                ?? db.ChangeTracker.Entries<EmployeeGpsSession>()
                    .Select(e => e.Entity)
                    .FirstOrDefault(s => s.SessionId == sid);

            var isNew = existingSession == null;
            var targetSession = existingSession ?? new EmployeeGpsSession { SessionId = sid };

            var changedSession = false;
            foreach (var property in entityType.GetProperties())
            {
                if (property.IsShadowProperty() || property.PropertyInfo == null) continue;
                if (property.Name == nameof(EmployeeGpsSession.Id)) continue; // Preserve SQLite autoincrement ID

                var value = FindJsonValue(json, property.Name);
                if (value is null) continue;

                var converted = ConvertValue(value, property.ClrType);
                if (converted is null && Nullable.GetUnderlyingType(property.ClrType) == null && property.ClrType.IsValueType)
                    continue;

                var current = property.PropertyInfo.GetValue(targetSession);
                if (!Equals(current, converted))
                {
                    property.PropertyInfo.SetValue(targetSession, converted);
                    changedSession = true;
                }
            }

            if (isNew)
            {
                db.EmployeeGpsSessions.Add(targetSession);
                return true;
            }
            else if (changedSession && db.Entry(targetSession).State == EntityState.Unchanged)
            {
                db.Entry(targetSession).State = EntityState.Modified;
            }

            return changedSession;
        }

        // Specialized handler for AttendanceLog (and AttendancePunch):
        // In SQLite, logid is an auto-increment integer, while Firebase Android records use string keys (e.g. "AUTO_3_1790696351000" or UUIDs).
        // The generic handler fails to convert string keys to int, causing all punches from Android to be dropped.
        // This handler parses timestamps, normalizes punch types, matches existing punches by LogID or logical window (EmployeeID + LogType + PunchTime +/- 60s),
        // and inserts new punches or updates existing ones.
        if (entityType.ClrType == typeof(AttendanceLog))
        {
            await _attendancePunchLock.WaitAsync(ct);
            try
            {
                var empId = GetInt(json, "employeeId", "EmployeeId", "staffId", "StaffId");
                if (empId <= 0) return false;

                DateTime punchTime = DateTime.MinValue;
                var timeEl = FindJsonValue(json, "punchTime")
                    ?? FindJsonValue(json, "timestamp")
                    ?? FindJsonValue(json, "checkInTime")
                    ?? FindJsonValue(json, "createdAt")
                    ?? FindJsonValue(json, "PunchTime");

                if (timeEl is { } te)
                {
                    if (te.ValueKind == JsonValueKind.Number && te.TryGetInt64(out var epochMs) && epochMs > 0)
                    {
                        punchTime = TimeZoneInfo.ConvertTimeFromUtc(
                            DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime,
                            IndiaTimeZone);
                    }
                    else if (ConvertValue(te, typeof(DateTime)) is DateTime dt && dt > DateTime.MinValue)
                    {
                        punchTime = dt;
                    }
                }

                if (punchTime == DateTime.MinValue) return false;

                var rawType = GetString(json, "type", "logType", "LogType", "note") ?? "IN";
                var logType = "IN";
                if (rawType.Equals("OUT", StringComparison.OrdinalIgnoreCase) ||
                    rawType.Equals("CheckOut", StringComparison.OrdinalIgnoreCase))
                {
                    logType = "OUT";
                }

                var deviceId = GetString(json, "deviceId", "DeviceID", "source") ?? "Mobile";
                var biometricId = GetString(json, "biometricId", "BiometricID") ?? string.Empty;
                var isApproved = GetBool(json, "isApproved", "IsApproved") ||
                    string.Equals(GetString(json, "status", "Status"), "APPROVED", StringComparison.OrdinalIgnoreCase);
                var lat = GetDouble(json, "latitude", "Latitude");
                var lon = GetDouble(json, "longitude", "Longitude");

                AttendanceLog? existingLog = null;

                // Extract the Firebase punch key (e.g. "AUTO_3_1790696351000" or a UUID)
                // from the json punchId/attendanceId field, falling back to the firebaseKey.
                var firebasePunchId = GetString(json, "punchId", "attendanceId") ?? firebaseKey;
                var effectiveBiometricId = !string.IsNullOrEmpty(biometricId) ? biometricId
                    : !string.IsNullOrEmpty(firebasePunchId) ? firebasePunchId
                    : string.Empty;

                // 0. Match by BiometricID == Firebase punch key or effectiveBiometricId
                if (!string.IsNullOrEmpty(firebasePunchId))
                {
                    existingLog = await db.AttendanceLogs.FirstOrDefaultAsync(l =>
                        (l.BiometricID == firebasePunchId || (!string.IsNullOrEmpty(effectiveBiometricId) && l.BiometricID == effectiveBiometricId)) && l.EmployeeID == empId, ct)
                        ?? db.ChangeTracker.Entries<AttendanceLog>()
                            .Select(e => e.Entity)
                            .FirstOrDefault(l => (l.BiometricID == firebasePunchId || (!string.IsNullOrEmpty(effectiveBiometricId) && l.BiometricID == effectiveBiometricId)) && l.EmployeeID == empId);
                }

                // 1. Try matching by integer LogID if key or json contains an integer id
                var idEl = FindJsonValue(json, "logId") ?? FindJsonValue(json, "LogID") ?? FindJsonValue(json, "punchId") ?? FindJsonValue(json, "attendanceId");
                var idStr = idEl?.GetString() ?? idEl?.ToString() ?? (firebaseKey.Contains('/') ? firebaseKey.Split('/')[^1] : firebaseKey);
                int parsedLogId = 0;
                if (existingLog == null && int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLogId) && parsedLogId > 0)
                {
                    existingLog = await db.AttendanceLogs.FirstOrDefaultAsync(l => l.LogID == parsedLogId, ct)
                        ?? db.ChangeTracker.Entries<AttendanceLog>()
                            .Select(e => e.Entity)
                            .FirstOrDefault(l => l.LogID == parsedLogId);
                }

                // 2. If not found by ID, match by logical window (same employee, same day, within +/- 60 seconds)
                if (existingLog == null)
                {
                    var punchDay = punchTime.Date;
                    var dayPunches = await db.AttendanceLogs
                        .Where(l => l.EmployeeID == empId && l.PunchTime.Date == punchDay)
                        .ToListAsync(ct);

                    existingLog = dayPunches
                        .FirstOrDefault(l =>
                            Math.Abs((l.PunchTime - punchTime).TotalSeconds) <= 60 &&
                            (string.IsNullOrEmpty(logType) || (l.LogType ?? "").Contains("OUT") == logType.Contains("OUT")))
                        ?? db.ChangeTracker.Entries<AttendanceLog>()
                            .Select(e => e.Entity)
                            .FirstOrDefault(l =>
                                l.EmployeeID == empId &&
                                l.PunchTime.Date == punchDay &&
                                Math.Abs((l.PunchTime - punchTime).TotalSeconds) <= 60 &&
                                (string.IsNullOrEmpty(logType) || (l.LogType ?? "").Contains("OUT") == logType.Contains("OUT")));
                }

                var isNew = existingLog == null;
                var targetLog = existingLog ?? new AttendanceLog
                {
                    EmployeeID = empId,
                    PunchTime = punchTime,
                    LogType = logType,
                    BiometricID = effectiveBiometricId,
                    DeviceID = deviceId,
                    IsApproved = isApproved,
                    Latitude = lat != 0 ? lat : null,
                    Longitude = lon != 0 ? lon : null
                };

                var changedLog = false;
                if (targetLog.EmployeeID != empId) { targetLog.EmployeeID = empId; changedLog = true; }
                if (targetLog.LogType != logType) { targetLog.LogType = logType; changedLog = true; }
                if (targetLog.IsApproved != isApproved) { targetLog.IsApproved = isApproved; changedLog = true; }
                if (lat != 0 && targetLog.Latitude != lat) { targetLog.Latitude = lat; changedLog = true; }
                if (lon != 0 && targetLog.Longitude != lon) { targetLog.Longitude = lon; changedLog = true; }

                if (isNew)
                {
                    if (parsedLogId > 0) targetLog.LogID = parsedLogId;
                    db.AttendanceLogs.Add(targetLog);
                    return true;
                }
                else if (changedLog && db.Entry(targetLog).State == EntityState.Unchanged)
                {
                    db.Entry(targetLog).State = EntityState.Modified;
                }

                return changedLog;
            }
            finally
            {
                _attendancePunchLock.Release();
            }
        }

        // Specialized handler for LeaveRequest:
        // Firebase Android leave records use a UUID string as the record key (the "id" field).
        // SQLite's LeaveRequestID is an EF auto-increment int — the generic handler converts the
        // UUID to int (which yields 0), then always does a db.FindAsync(0) → not found → inserts a
        // brand new row on every SSE event, producing 10+ duplicate rows for a single leave request.
        //
        // This handler deduplicates by:
        //   1. FirebaseLeaveId (column added for this purpose) — fast exact match.
        //   2. Logical key (EmployeeID, LeaveDate) — fallback for records created before this column.
        //
        // It also correctly maps Android's string employeeId/staffId fields to the int EmployeeID column,
        // which fixes the "Unknown" employee name shown in the Leave Management table.
        if (entityType.ClrType == typeof(LeaveRequest))
        {
            // The Firebase record key (UUID) is authoritative — it is also stored in the "id" field.
            var firebaseLeaveId = GetString(json, "id", "firebaseLeaveId") ?? firebaseKey;
            if (string.IsNullOrWhiteSpace(firebaseLeaveId))
                return false;

            // employeeId can arrive as a number or a string (Android sends a string).
            var empId = GetInt(json, "employeeId", "EmployeeId", "staffId", "StaffId");
            if (empId <= 0) return false;

            // Prefer the explicit calendar date string (e.g. "2026-10-15") if present to avoid UTC/IST boundary truncation.
            DateTime? leaveDate = null;
            var leaveDateEl = FindJsonValue(json, "leaveDate") ?? FindJsonValue(json, "LeaveDate");
            if (leaveDateEl is { } lde && DateTime.TryParse(lde.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                leaveDate = parsedDate.Date;
            }
            else
            {
                var startDateEl = FindJsonValue(json, "startDate") ?? leaveDateEl;
                if (startDateEl is { } sde)
                {
                    if (sde.ValueKind == JsonValueKind.Number && sde.TryGetInt64(out var ms))
                    {
                        // Convert UTC milliseconds to India business date to prevent timezone day-shift
                        leaveDate = TimeZoneInfo.ConvertTimeFromUtc(
                            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                            IndiaTimeZone).Date;
                    }
                    else
                    {
                        var converted = ConvertValue(sde, typeof(DateTime));
                        if (converted is DateTime dt) leaveDate = dt.Date;
                    }
                }
            }

            if (!leaveDate.HasValue) return false;

            // 1. Fast path: look up by FirebaseLeaveId.
            LeaveRequest? existingLeave = await db.LeaveRequests
                .FirstOrDefaultAsync(lr => lr.FirebaseLeaveId == firebaseLeaveId, ct);

            // 2. Fallback: match by (EmployeeID, LeaveDate) for rows that predate this column.
            if (existingLeave == null)
            {
                existingLeave = await db.LeaveRequests.FirstOrDefaultAsync(
                    lr => lr.EmployeeID == empId &&
                          lr.LeaveDate.HasValue &&
                          lr.LeaveDate.Value.Date == leaveDate.Value, ct);
            }

            var isNewLeave = existingLeave == null;
            var targetLeave = existingLeave ?? new LeaveRequest();

            var changedLeave = false;

            // Stamp the Firebase UUID so we can find this row instantly next time.
            if (targetLeave.FirebaseLeaveId != firebaseLeaveId) { targetLeave.FirebaseLeaveId = firebaseLeaveId; changedLeave = true; }

            if (targetLeave.EmployeeID != empId) { targetLeave.EmployeeID = empId; changedLeave = true; }
            if (!targetLeave.LeaveDate.HasValue || targetLeave.LeaveDate.Value.Date != leaveDate.Value)
            { targetLeave.LeaveDate = leaveDate.Value; changedLeave = true; }

            var leaveType = GetString(json, "leaveType", "LeaveType") ?? "Casual Leave";
            if (targetLeave.LeaveType != leaveType) { targetLeave.LeaveType = leaveType; changedLeave = true; }

            var isHalfDay = GetBool(json, "isHalfDay", "IsHalfDay");
            if (targetLeave.IsHalfDay != isHalfDay) { targetLeave.IsHalfDay = isHalfDay; changedLeave = true; }

            var statusStr = GetString(json, "status", "Status") ?? "Pending";
            var isApproved = statusStr.Equals("Approved", StringComparison.OrdinalIgnoreCase);
            if (targetLeave.IsApproved != isApproved) { targetLeave.IsApproved = isApproved; changedLeave = true; }
            if (targetLeave.Status != statusStr) { targetLeave.Status = statusStr; changedLeave = true; }

            var notes = GetString(json, "reason", "notes", "Notes");
            if (targetLeave.Notes != notes) { targetLeave.Notes = notes; changedLeave = true; }

            var adminNotes = GetString(json, "adminNotes", "AdminNotes", "adminRemarks");
            if (targetLeave.AdminNotes != adminNotes) { targetLeave.AdminNotes = adminNotes; changedLeave = true; }

            if (!changedLeave) return false;

            if (isNewLeave)
                db.LeaveRequests.Add(targetLeave);
            else if (db.Entry(targetLeave).State == EntityState.Unchanged)
                db.Entry(targetLeave).State = EntityState.Modified;

            return true;
        }

        // Specialized handler for SalaryAdvance:
        // In SQLite, advanceid is an auto-increment integer, while Firebase records can use UUIDs or string keys.
        // Lookups must match either by integer AdvanceID or by logical uniqueness (EmployeeID, Amount, AdvanceDate)
        // to prevent duplicate insertions on every sync reconnect or polling cycle.
        if (entityType.ClrType == typeof(SalaryAdvance))
        {
            var empId = GetInt(json, "employeeId", "EmployeeId", "staffId");
            if (empId <= 0) return false;


            var amount = (decimal)GetDouble(json, "amount", "Amount");
            if (amount <= 0m) return false;

            var dateElement = FindJsonValue(json, "date") ?? FindJsonValue(json, "advanceDate") ?? FindJsonValue(json, "AdvanceDate");
            var advDate = (DateTime?)(ConvertValue(dateElement, typeof(DateTime))) ?? DateTime.UtcNow;
            var advType = GetString(json, "advanceType", "AdvanceType", "type") ?? "General";
            var payrollIdVal = GetInt(json, "payrollIdPaid", "PayrollID_Paid", "recoveryPaymentId");
            int? payrollIdPaid = payrollIdVal > 0 ? payrollIdVal : null;
            var isRecovered = GetBool(json, "isRecovered", "IsRecovered") || payrollIdPaid.HasValue;

            SalaryAdvance? existingAdvance = null;

            // 1. Try matching by integer AdvanceID if key or json contains an integer id
            var idElement = FindJsonValue(json, "advanceId") ?? FindJsonValue(json, "AdvanceID");
            var idStr = idElement?.GetString() ?? idElement?.ToString() ?? (firebaseKey.Contains('/') ? firebaseKey.Split('/')[^1] : firebaseKey);
            if (int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var advId) && advId > 0)
            {
                existingAdvance = await db.SalaryAdvances.FirstOrDefaultAsync(s => s.AdvanceID == advId, ct)
                    ?? db.ChangeTracker.Entries<SalaryAdvance>()
                        .Select(e => e.Entity)
                        .FirstOrDefault(s => s.AdvanceID == advId);
            }

            // 2. If not found by ID (e.g. key is a UUID from mobile or web), match by logical content:
            // same employee, same amount, and same advance date (within same day/seconds)
            if (existingAdvance == null)
            {
                var candidates = await db.SalaryAdvances
                    .Where(s => s.EmployeeID == empId && s.Amount == amount)
                    .ToListAsync(ct);

                existingAdvance = candidates.FirstOrDefault(s =>
                    s.AdvanceDate.HasValue &&
                    Math.Abs((s.AdvanceDate.Value - advDate).TotalSeconds) < 60)
                    ?? candidates.FirstOrDefault(s =>
                        s.AdvanceDate.HasValue &&
                        s.AdvanceDate.Value.Date == advDate.Date);
            }

            var status = GetString(json, "status", "Status");
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    if (existingAdvance != null)
                    {
                        db.SalaryAdvances.Remove(existingAdvance);
                        return true;
                    }
                    return false;
                }
            }

            var isNew = existingAdvance == null;
            var targetAdvance = existingAdvance ?? new SalaryAdvance
            {
                EmployeeID = empId,
                AdvanceDate = advDate,
                Amount = amount,
                AdvanceType = advType
            };

            var changedAdvance = false;

            if (targetAdvance.EmployeeID != empId) { targetAdvance.EmployeeID = empId; changedAdvance = true; }
            if (targetAdvance.Amount != amount) { targetAdvance.Amount = amount; changedAdvance = true; }
            if (targetAdvance.AdvanceType != advType) { targetAdvance.AdvanceType = advType; changedAdvance = true; }
            if (targetAdvance.AdvanceDate != advDate && isNew) { targetAdvance.AdvanceDate = advDate; changedAdvance = true; }
            if (isRecovered && targetAdvance.PayrollID_Paid == null && payrollIdPaid.HasValue)
            {
                targetAdvance.PayrollID_Paid = payrollIdPaid;
                changedAdvance = true;
            }

            if (isNew)
            {
                if (int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
                {
                    targetAdvance.AdvanceID = parsedId;
                }
                db.SalaryAdvances.Add(targetAdvance);
                return true;
            }
            else if (changedAdvance && db.Entry(targetAdvance).State == EntityState.Unchanged)
            {
                db.Entry(targetAdvance).State = EntityState.Modified;
            }

            return changedAdvance;
        }

        var keyParts = firebaseKey.Split('|');
        var keyValues = new object?[keys.Count];

        for (var i = 0; i < keys.Count; i++)
        {
            var keyProperty = keys[i];
            var value = FindJsonValue(json, keyProperty.Name);
            if (value is not null)
            {
                keyValues[i] = ConvertValue(value, keyProperty.ClrType);
            }
            else if (keyParts.Length > i)
            {
                keyValues[i] = ConvertStringValue(keyParts[i], keyProperty.ClrType);
            }
            else
            {
                keyValues[i] = null;
            }
        }

        if (keyValues.Any(x => x is null)) return false;

        // REQUIREMENT: Prevent identity map conflicts ("already being tracked").
        // First check the database/tracker via FindAsync.
        var existing = await db.FindAsync(entityType.ClrType, keyValues, ct);

        // If FindAsync fails to find a tracked entity (e.g. due to state or type nuances),
        // perform a manual lookup in the tracker to ensure we don't attempt to track a duplicate.
        if (existing == null)
        {
            existing = db.ChangeTracker.Entries()
                .Where(e => e.Metadata.Name == entityType.Name)
                .Select(e => e.Entity)
                .FirstOrDefault(e =>
                {
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var keyProperty = keys[i];
                        var val = keyProperty.PropertyInfo?.GetValue(e);
                        if (!Equals(val, keyValues[i])) return false;
                    }
                    return true;
                });
        }

        var target = existing ?? Activator.CreateInstance(entityType.ClrType);
        if (target == null) return false;

        if (existing == null)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                var keyProperty = keys[i];
                if (keyProperty.PropertyInfo != null)
                {
                    keyProperty.PropertyInfo.SetValue(target, keyValues[i]);
                }
            }
        }

        var changed = false;
        foreach (var property in entityType.GetProperties())
        {
            if (property.IsShadowProperty() || property.PropertyInfo == null) continue;
            if (keys.Any(k => k.Name == property.Name)) continue;

            var value = FindJsonValue(json, property.Name);
            if (value is null) continue;

            var converted = ConvertValue(value, property.ClrType);

            // Special mapping for Employee break hours (Firebase) to minutes (SQL)
            if (entityType.ClrType.Name == "Employee" && property.Name == "StandardBreakMinutes")
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var hours))
                {
                    converted = (int)Math.Round(hours * 60d);
                }
            }

            // Special mapping for Employee isActive (Firebase) to IsDeleted (SQL) - REVERSED
            if (entityType.ClrType.Name == "Employee" && property.Name == "IsDeleted")
            {
                if (converted is bool isActuallyActive)
                {
                    converted = !isActuallyActive;
                }
            }

            // Special mapping for BonusRecord payrollIdPaid <= 0 (Firebase) to null (SQL)
            if (entityType.ClrType.Name == "BonusRecord" && property.Name == "PayrollID_Paid")
            {
                if (converted is int pid && pid <= 0)
                {
                    converted = null;
                }
            }

            if (converted is null && Nullable.GetUnderlyingType(property.ClrType) == null && property.ClrType.IsValueType)
                continue;

            var current = property.PropertyInfo.GetValue(target);
            if (!Equals(current, converted))
            {
                property.PropertyInfo.SetValue(target, converted);
                changed = true;
            }
        }

        if (existing == null && changed)
        {
            // Firebase record keys are authoritative for the compatibility
            // projection. Several entities, including LeaveRequest, use an
            // identity/ValueGeneratedOnAdd key in the SQL model. Calling
            // DbSet.Add() directly can make EF/SQLite treat an explicitly
            // supplied Firebase key as a store-generated/temporary key. When
            // SaveChanges reads the generated value back, EF can then collide
            // with another tracked instance and throw:
            // "cannot be tracked because another instance with the same key".
            //
            // Attach first so EF records the supplied key as the real key, then
            // switch the entity to Added and explicitly mark generated key
            // properties as non-temporary. This preserves the Firebase key
            // without changing the database schema or business logic.
            var entry = db.Entry(target);
            entry.State = EntityState.Unchanged;

            for (int i = 0; i < keys.Count; i++)
            {
                var keyMetadata = keys[i];
                var keyProperty = entry.Property(keyMetadata.Name);
                keyProperty.CurrentValue = keyValues[i];
                if (keyMetadata.ValueGenerated != ValueGenerated.Never)
                    keyProperty.IsTemporary = false;
            }

            entry.State = EntityState.Added;
        }
        else if (existing != null && changed && db.Entry(target).State == EntityState.Unchanged)
        {
            db.Entry(target).State = EntityState.Modified;
        }

        return changed;
    }


    private static JsonElement? FindJsonValue(
        JsonElement json,
        string propertyName)
    {
        var wanted = Normalize(propertyName);

        foreach (var property in json.EnumerateObject())
        {
            if (Normalize(property.Name) == wanted)
                return property.Value;

            if (AliasMatches(propertyName, property.Name))
                return property.Value;
        }

        return null;
    }

    private static bool AliasMatches(
        string clrName,
        string firebaseName)
        => (Normalize(clrName), Normalize(firebaseName)) switch
        {
            ("logid", "attendanceid") => true,
            ("leaverequestid", "id") => true,
            ("regularizationid", "id") => true,
            ("employeeid", "staffid") => true,
            ("monthlysalary", "salaryrate") => true,
            ("standardbreakminutes", "breakhours") => true,
            ("payrolltypeoverride", "salarytype") => true,
            ("terminationdate", "terminatedate") => true,
            ("isdeleted", "isactive") => true,
            ("shiftstarttime", "shiftstart") => true,
            ("shiftendtime", "shiftend") => true,
            ("hiredate", "hiredate") => true,
            ("dob", "dob") => true,
            ("standardhours", "standardhours") => true,
            ("otrule", "otrule") => true,
            ("otflatrate", "otflatrate") => true,
            ("compoffdayofweek", "compoffdayofweek") => true,
            ("leavedate", "startdate") => true,
            ("leavedate", "leavedate") => true,
            ("notes", "reason") => true,
            ("adminnotes", "adminremarks") => true,
            ("isapproved", "status") => true,
            ("advanceid", "id") => true,
            ("advancedate", "date") => true,
            ("dateofpunch", "date") => true,
            ("isinpunch", "punchtype") => true,
            ("punchtimenew", "requestedtime") => true,
            ("submissiondate", "submittedat") => true,
            ("desiredlastworkingday", "desiredlastworkingday") => true,
            ("approvedlastworkingday", "approvedlastworkingday") => true,
            _ => false
        };

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static IQueryable GetEntitySet(
        DbContext db,
        Type entityType)
    {
        var setMethod = typeof(DbContext)
            .GetMethods()
            .First(method =>
                method.Name == nameof(DbContext.Set) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 &&
                method.GetParameters().Length == 0);

        return (IQueryable)setMethod
            .MakeGenericMethod(entityType)
            .Invoke(db, null)!;
    }

    private static object? ConvertStringValue(
        string value,
        Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            if (type == typeof(string)) return value;
            if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(Guid)) return Guid.Parse(value);
            if (type == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateOnly)) return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(TimeOnly)) return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type.IsEnum) return Enum.Parse(type, value, true);
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static object? ConvertValue(
        JsonElement? value,
        Type targetType)
    {
        if (value is null ||
            value.Value.ValueKind == JsonValueKind.Null)
            return null;

        var type =
            Nullable.GetUnderlyingType(targetType)
            ?? targetType;

        try
        {
            if (type == typeof(string))
            {
                if (value.Value.ValueKind == JsonValueKind.String)
                    return value.Value.GetString();
                return value.Value.ToString();
            }

            if (type == typeof(int))
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var i))
                    return i;
                var text = value.Value.ToString();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
                return 0;
            }

            if (type == typeof(long))
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var l))
                    return l;
                var text = value.Value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return (long)Math.Round(d);
                return 0L;
            }

            if (type == typeof(decimal))
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDecimal(out var dec))
                    return dec;
                var text = value.Value.ToString();
                if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out dec)) return dec;
                return 0m;
            }

            if (type == typeof(double))
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var dbl))
                    return dbl;
                var text = value.Value.ToString();
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out dbl)) return dbl;
                return 0.0;
            }

            if (type == typeof(float))
            {
                if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetSingle(out var flt))
                    return flt;
                var text = value.Value.ToString();
                if (float.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out flt)) return flt;
                return 0.0f;
            }

            if (type == typeof(bool))
            {
                if (value.Value.ValueKind == JsonValueKind.True) return true;
                if (value.Value.ValueKind == JsonValueKind.False) return false;
                var text = value.Value.ToString();
                if (bool.TryParse(text, out var b)) return b;
                if (string.Equals(text, "Approved", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Rejected", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(text, "IN", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "OUT", StringComparison.OrdinalIgnoreCase)) return false;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n != 0;
                return false;
            }

            if (type == typeof(Guid))
            {
                if (value.Value.ValueKind == JsonValueKind.String)
                    return Guid.Parse(value.Value.GetString()!);
                return Guid.Parse(value.Value.ToString());
            }

            if (type == typeof(DateTime))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMs))
                    return DateTimeOffset.FromUnixTimeMilliseconds(pMs).UtcDateTime;

                return DateTime.Parse(
                    text!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateTimeOffset))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);

                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMs))
                    return DateTimeOffset.FromUnixTimeMilliseconds(pMs);

                return DateTimeOffset.Parse(
                    text!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateOnly))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateOnly.FromDateTime(
                        DateTimeOffset.FromUnixTimeMilliseconds(ms).DateTime);

                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMs))
                    return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(pMs).DateTime);

                return DateOnly.Parse(
                    text!,
                    CultureInfo.InvariantCulture);
            }

            if (type == typeof(TimeOnly))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var epochMs) && epochMs > 86400000L)
                {
                    return TimeOnly.FromTimeSpan(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
                }
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetDouble(out var ms))
                    return TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(ms));

                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMs) && pMs > 86400000L)
                {
                    return TimeOnly.FromTimeSpan(DateTimeOffset.FromUnixTimeMilliseconds(pMs).ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
                }
                return TimeOnly.Parse(
                    text!,
                    CultureInfo.InvariantCulture);
            }

            if (type == typeof(TimeSpan))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetDouble(out var ms))
                    return TimeSpan.FromMilliseconds(ms);

                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var pMs))
                    return TimeSpan.FromMilliseconds(pMs);

                return TimeSpan.Parse(text!, CultureInfo.InvariantCulture);
            }

            if (type.IsEnum)
            {
                var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                return Enum.Parse(type, text!, true);
            }

            return JsonSerializer.Deserialize(
                value.Value.GetRawText(),
                type);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
