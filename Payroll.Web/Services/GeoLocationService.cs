using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Web.Hubs;

namespace Payroll.Web.Services;

public class GeoLocationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<GeoLocationService> _logger;
    private readonly AttendanceRefreshService _refreshService;
    private readonly IHubContext<AttendanceRefreshHub> _hubContext;
    private readonly FirebaseRealtimeService _firebase;
    private readonly FirebaseAttendanceMutationService _firebaseAttendanceMutations;
    private readonly IConfiguration _configuration;

    // Dual Attendance treats the configured geofence as a reconciliation
    // signal. GPS state is checked against the authoritative attendance
    // parity on every valid fix, so missed transitions can self-heal after
    // reconnects, app suspension, browser sleep, or a session starting
    // outside the office. Physical biometric/mobile punches remain
    // authoritative and are protected by a short conflict window.
    private const int AuthoritativePunchProtectionSeconds = 120;
    private const int FallbackReconciliationWindowSeconds = 300;
    private const long AttendanceAdvisoryLockNamespace = 0x504159524F4C4CL;
    private const long GpsSessionAdvisoryLockNamespace = 0x4750534C4F434BL;
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> LocalAdvisoryLocks = new();

    public GeoLocationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<GeoLocationService> logger,
        AttendanceRefreshService refreshService,
        IHubContext<AttendanceRefreshHub> hubContext,
        FirebaseRealtimeService firebase,
        FirebaseAttendanceMutationService firebaseAttendanceMutations,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _refreshService = refreshService;
        _hubContext = hubContext;
        _firebase = firebase;
        _firebaseAttendanceMutations = firebaseAttendanceMutations;
        _configuration = configuration;
    }

    // ================================================================
    // GET DISTANCE FROM OFFICE
    // ================================================================

    public async Task<GeoDistanceResult> GetDistanceFromOfficeAsync(
        double latitude,
        double longitude)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var features = await db.FeatureSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == 1);

        if (features?.EnableGeoFencing != true)
        {
            return new GeoDistanceResult
            {
                Success = false,
                Message = "Geo-fencing module is disabled."
            };
        }

        var company = await db.CompanySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingID == 1);

        if (company == null ||
            company.OfficeLatitude == 0 ||
            company.OfficeLongitude == 0)
        {
            return new GeoDistanceResult
            {
                Success = false,
                Message = "Office location not configured by Admin."
            };
        }

        if (!IsValidCoordinate(latitude, longitude))
        {
            return new GeoDistanceResult
            {
                Success = false,
                Message = "Invalid GPS coordinates received."
            };
        }

        var distance = CalculateDistance(
            latitude,
            longitude,
            company.OfficeLatitude,
            company.OfficeLongitude);

        return new GeoDistanceResult
        {
            Success = true,
            DistanceMeters = distance,
            AllowedRadiusMeters = company.GeoRadiusMeters
        };
    }

    // ================================================================
    // REBASELINE ALL ACTIVE SESSIONS (Admin Config Change)
    // ================================================================

    public async Task RebaselineAllActiveSessionsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var company = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingID == 1);
        if (company == null) return;

        var activeSessions = await db.EmployeeGpsSessions
            .Where(x => x.EndedAtUtc == null && x.LastLatitude.HasValue)
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            var distance = CalculateDistance(
                session.LastLatitude!.Value,
                session.LastLongitude!.Value,
                company.OfficeLatitude,
                company.OfficeLongitude);

            var previousWithin = session.LastIsWithinAllowedRadius;
            var currentWithin = distance <= (company.GeoRadiusMeters + 1);

            // REQUIREMENT: Always evaluate against the new radius immediately.
            // Rebaseline even if previous state was the same, to ensure the
            // authoritative LastAllowedRadiusMeters is updated and any missed
            // transitions are reconciled.
            await ProcessAutomaticGeofencePunchAsync(
                db,
                session.EmployeeId,
                session.SessionId,
                session.LastLatitude.Value,
                session.LastLongitude.Value,
                session.LastAccuracyMeters ?? 0.0,
                distance,
                company.GeoRadiusMeters,
                previousWithin,
                currentWithin);

            session.LastAllowedRadiusMeters = company.GeoRadiusMeters;
            session.LastIsWithinAllowedRadius = currentWithin;
        }

        await SaveChangesWithSqliteRetryAsync(db);

        // Notify all dashboards to refresh their authoritative membership
        await _refreshService.NotifyGlobalRefreshAsync("RADIUS_REBASELINED");
        await _refreshService.NotifyGeoSettingsChangedAsync(
            company.OfficeLatitude,
            company.OfficeLongitude,
            company.GeoRadiusMeters);
    }

    // ================================================================
    // START GPS SESSION
    // ================================================================

    public async Task<bool> StartGpsSessionAsync(
        int employeeId,
        Guid sessionId)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return false;

        try
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            var lockKey = GpsSessionAdvisoryLockNamespace + (uint)employeeId;
            var lockHeld = false;

            try
            {
                // Serialize session start/end/update lifecycle operations for
                // this employee. This prevents an old GPS request from racing
                // a new login and leaving two active sessions behind.
                await AcquireLocalLockAsync(lockKey);
                lockHeld = true;

                var existing = await db.EmployeeGpsSessions
                    .FirstOrDefaultAsync(x =>
                        x.SessionId == sessionId);

                var previousSessions = await db.EmployeeGpsSessions
                    .Where(x =>
                        x.EmployeeId == employeeId &&
                        x.EndedAtUtc == null &&
                        x.SessionId != sessionId)
                    .ToListAsync();

                var now = DateTime.UtcNow;

                if (previousSessions.Count > 0)
                {
                    foreach (var previous in previousSessions)
                    {
                        previous.EndedAtUtc = now;
                        previous.EndReason = "NEW_SESSION";

                        LiveLocationStore.Remove(
                            previous.EmployeeId,
                            previous.SessionId);

                        // Ensure the Firebase live marker is un-bound from
                        // this now-ended session.
                        await _firebase.TerminateLiveLocationAsync(
                            previous.EmployeeId,
                            _firebase.ResolveOwnerUid($"employee-{previous.EmployeeId}", "Employee"),
                            expectedSessionId: previous.SessionId);
                    }
                    await SaveChangesWithSqliteRetryAsync(db);
                }

                if (existing != null)
                {
                    if (existing.EmployeeId != employeeId)
                        return false;

                    if (existing.EndedAtUtc.HasValue)
                    {
                        existing.EndedAtUtc = null;
                        existing.EndReason = null;
                        existing.LastUpdateAtUtc = now;
                        await SaveChangesWithSqliteRetryAsync(db);
                    }

                    await _firebase.BindLiveLocationAsync(
                        employeeId,
                        sessionId,
                        _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee"));

                    return true;
                }

                var session = new EmployeeGpsSession
                {
                    EmployeeId = employeeId,
                    SessionId = sessionId,
                    StartedAtUtc = now,
                    LastUpdateAtUtc = now,
                    EndedAtUtc = null,
                    EndReason = null,
                    TotalPoints = 0,
                    TotalDistanceMeters = 0,
                    AverageAccuracyMeters = null
                };

                db.EmployeeGpsSessions.Add(session);
                await SaveChangesWithSqliteRetryAsync(db);

                // REQUIREMENT: Bind the live marker to the new session ID in Firebase.
                // This ensures that late location fixes from a previous session
                // (e.g. from another device) are ignored across both platforms.
                await _firebase.BindLiveLocationAsync(
                    employeeId,
                    sessionId,
                    _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee"));

                // Broadcast only after the database commit so every admin
                // refresh triggered by SessionEnded observes EndedAtUtc.
                foreach (var previous in previousSessions)
                {
                    try
                    {
                        await _hubContext.Clients.All.SendAsync(
                            "SessionEnded",
                            new
                            {
                                EmployeeId = previous.EmployeeId,
                                SessionId = previous.SessionId,
                                EndedAtUtc = previous.EndedAtUtc,
                                EndReason = previous.EndReason
                            });
                    }
                    catch (Exception signalREx)
                    {
                        _logger.LogWarning(
                            signalREx,
                            "Failed to broadcast previous GPS session end for employee {EmployeeId}",
                            previous.EmployeeId);
                    }
                }

                _logger.LogInformation(
                    "GPS session started. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    employeeId,
                    sessionId);

                try
                {
                    await _hubContext.Clients.All.SendAsync(
                        "SessionStarted",
                        new
                        {
                            EmployeeId = employeeId,
                            SessionId = sessionId,
                            StartedAtUtc = now
                        });
                }
                catch (Exception signalREx)
                {
                    _logger.LogWarning(
                        signalREx,
                        "Failed to broadcast session start via SignalR for employee {EmployeeId}",
                        employeeId);
                }

                return true;
            }
            finally
            {
                if (lockHeld)
                {
                    try
                    {
                        ReleaseLocalLock(lockKey);
                    }
                    catch (Exception unlockEx)
                    {
                        _logger.LogWarning(
                            unlockEx,
                            "Failed to release GPS session advisory lock after start. EmployeeId={EmployeeId}",
                            employeeId);
                    }
                }

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start GPS session. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);

            return false;
        }
    }


    // ================================================================
    // UPDATE GPS SESSION
    // ================================================================

    public async Task<bool> UpdateGpsSessionAsync(
        int employeeId,
        Guid sessionId,
        double latitude,
        double longitude,
        double accuracyMeters,
        double distanceMeters,
        int allowedRadiusMeters,
        bool isWithinAllowedRadius,
        DateTime? capturedAtUtc = null,
        bool allowSessionRecovery = true)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty ||
            !IsValidCoordinate(latitude, longitude))
        {
            return false;
        }

        try
        {
            // Session lookup is repeated under the advisory lock below. Do
            // not create a session before the lock is held: a concurrent newer
            // session could otherwise be ended by a stale GPS request.
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            EmployeeGpsSession? session = null;

            // legacy database advisory lock coordinates GPS updates
            // and logout/session-end operations across Web/Worker instances.
            // This closes the race where an old GPS request could repopulate
            // LiveLocationStore immediately after logout.
            var lockKey = GpsSessionAdvisoryLockNamespace + (uint)employeeId;
            var lockHeld = false;

            try
            {
                await AcquireLocalLockAsync(lockKey);
                lockHeld = true;

                session = await db.EmployeeGpsSessions
                    .FirstOrDefaultAsync(x =>
                        x.EmployeeId == employeeId &&
                        x.SessionId == sessionId);

                // A GPS fix can arrive after another platform has ended the
                // incoming session. Never resurrect that session. For a current
                // event, first attach to the newest already-active session. Only
                // when there is no active session may we create a new one.
                var effectiveSessionId = sessionId;

                if (session == null || session.EndedAtUtc.HasValue)
                {
                    if (!allowSessionRecovery)
                    {
                        if (session != null && session.EndedAtUtc.HasValue)
                        {
                            LiveLocationStore.Remove(employeeId, sessionId);
                        }
                        return false;
                    }

                    var recoveryStart = DateTime.UtcNow;

                    // Check if another active session exists for this employee
                    var existingActive = await db.EmployeeGpsSessions
                        .Where(x =>
                            x.EmployeeId == employeeId &&
                            x.EndedAtUtc == null &&
                            x.SessionId != sessionId)
                        .OrderByDescending(x => x.StartedAtUtc)
                        .FirstOrDefaultAsync();

                    if (existingActive != null)
                    {
                        session = existingActive;
                        effectiveSessionId = existingActive.SessionId;
                    }
                    else if (session != null && session.EndedAtUtc.HasValue)
                    {
                        session.EndedAtUtc = null;
                        session.EndReason = null;
                        session.LastUpdateAtUtc = recoveryStart;
                        await SaveChangesWithSqliteRetryAsync(db);
                        effectiveSessionId = sessionId;

                        await _firebase.BindLiveLocationAsync(
                            employeeId,
                            sessionId,
                            _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee"));

                        _logger.LogInformation(
                            "Reactivated ended GPS session for EmployeeId={EmployeeId}, SessionId={SessionId}",
                            employeeId,
                            sessionId);
                    }
                    else
                    {
                        // Ensure only ONE session can be active for an employee. Close any older dangling sessions.
                        var previousSessions = await db.EmployeeGpsSessions
                            .Where(x =>
                                x.EmployeeId == employeeId &&
                                x.EndedAtUtc == null &&
                                x.SessionId != sessionId)
                            .ToListAsync();

                        if (previousSessions.Count > 0)
                        {
                            foreach (var previous in previousSessions)
                            {
                                previous.EndedAtUtc = previous.LastUpdateAtUtc > DateTime.MinValue ? previous.LastUpdateAtUtc : recoveryStart;
                                previous.EndReason = "SUPERSEDED";
                                LiveLocationStore.Remove(previous.EmployeeId, previous.SessionId);
                                await _firebase.TerminateLiveLocationAsync(
                                    previous.EmployeeId,
                                    _firebase.ResolveOwnerUid($"employee-{previous.EmployeeId}", "Employee"),
                                    expectedSessionId: previous.SessionId);
                            }
                        }

                        effectiveSessionId = sessionId;

                        session = new EmployeeGpsSession
                        {
                            EmployeeId = employeeId,
                            SessionId = sessionId,
                            StartedAtUtc = recoveryStart,
                            LastUpdateAtUtc = DateTime.MinValue,
                            EndedAtUtc = null,
                            EndReason = null,
                            TotalPoints = 0,
                            TotalDistanceMeters = 0,
                            AverageAccuracyMeters = null
                        };

                        db.EmployeeGpsSessions.Add(session);
                        await SaveChangesWithSqliteRetryAsync(db);

                        await _firebase.BindLiveLocationAsync(
                            employeeId,
                            sessionId,
                            _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee"));

                        try
                        {
                            await _hubContext.Clients.All.SendAsync(
                                "SessionStarted",
                                new
                                {
                                    EmployeeId = employeeId,
                                    SessionId = sessionId,
                                    StartedAtUtc = recoveryStart
                                });
                        }
                        catch (Exception signalREx)
                        {
                            _logger.LogWarning(
                                signalREx,
                                "Failed to broadcast recovered GPS session start for employee {EmployeeId}",
                                employeeId);
                        }

                        _logger.LogInformation(
                            "Created/Recovered GPS session for EmployeeId={EmployeeId}, SessionId={SessionId}",
                            employeeId,
                            sessionId);
                    }
                }

                // Ensure only ONE session can be active for an employee. Close any older dangling sessions.
                var supersededSessions = await db.EmployeeGpsSessions
                    .Where(x =>
                        x.EmployeeId == employeeId &&
                        x.EndedAtUtc == null &&
                        x.SessionId != effectiveSessionId)
                    .ToListAsync();

                if (supersededSessions.Count > 0)
                {
                    var nowUtc = DateTime.UtcNow;
                    foreach (var superseded in supersededSessions)
                    {
                        superseded.EndedAtUtc = superseded.LastUpdateAtUtc > DateTime.MinValue ? superseded.LastUpdateAtUtc : nowUtc;
                        superseded.EndReason = "SUPERSEDED";
                        LiveLocationStore.Remove(superseded.EmployeeId, superseded.SessionId);
                    }
                    await SaveChangesWithSqliteRetryAsync(db);
                }

                var captureTime = capturedAtUtc.HasValue && capturedAtUtc.Value != default
                    ? capturedAtUtc.Value.ToUniversalTime()
                    : DateTime.UtcNow;

                // Adjust session start time if first fix has minor clock skew before session creation
                if (session.LastUpdateAtUtc == DateTime.MinValue && captureTime < session.StartedAtUtc)
                {
                    session.StartedAtUtc = captureTime;
                }

                // Do not let delayed/retried GPS packets overwrite the newer
                // session position. Allow reasonable 60-second clock skew for live fixes.
                if (captureTime < session.StartedAtUtc.AddSeconds(-60))
                {
                    _logger.LogDebug(
                        "Ignoring GPS fix captured before effective session start. EmployeeId={EmployeeId}, IncomingSessionId={IncomingSessionId}, EffectiveSessionId={EffectiveSessionId}",
                        employeeId, sessionId, effectiveSessionId);
                    return false;
                }

                if (captureTime < session.LastUpdateAtUtc)
                {
                    _logger.LogDebug(
                        "Ignoring out-of-order GPS fix. EmployeeId={EmployeeId}, SessionId={SessionId}, Capture={CaptureTime}, Current={CurrentTime}",
                        employeeId, effectiveSessionId, captureTime, session.LastUpdateAtUtc);
                    return true;
                }

                var safeAccuracy = NormalizeAccuracy(accuracyMeters);
                var safeDistance = NormalizeDistance(distanceMeters);
                var now = DateTime.UtcNow;

                var captureTimeIndia = TimeZoneInfo.ConvertTimeFromUtc(captureTime, IndiaTimeZone);
                var sessionLastIndia = session.LastUpdateAtUtc != DateTime.MinValue
                    ? TimeZoneInfo.ConvertTimeFromUtc(session.LastUpdateAtUtc, IndiaTimeZone)
                    : (DateTime?)null;

                // Day boundary check: if the previous GPS update was on an earlier calendar date (in India Time),
                // treat previousLocationState as null so that the new day evaluates initial entrance fresh.
                var isNewCalendarDay = sessionLastIndia == null || captureTimeIndia.Date > sessionLastIndia.Value.Date;
                var effectivePreviousLocationState = isNewCalendarDay ? (bool?)null : session.LastIsWithinAllowedRadius;

                var stableLocationState = ResolveStableGeofenceState(
                    effectivePreviousLocationState,
                    safeDistance,
                    allowedRadiusMeters + 1); // UX: 1m buffer for map stability

                // A radius change is configuration, not employee movement.
                // Re-baseline the session against the new radius instead of
                // manufacturing an automatic IN/OUT merely because Admin
                // changed the allowed distance while the employee was
                // stationary. The next genuine boundary transition is then
                // evaluated normally.
                var radiusChanged =
                    session.LastAllowedRadiusMeters.HasValue &&
                    session.LastAllowedRadiusMeters.Value != allowedRadiusMeters;

                // Automatic attendance is evaluated while the session lock is
                // held, so logout cannot interleave with the decision.
                if (stableLocationState.HasValue)
                {
                    // REQUIREMENT: Process automatic punching even if radius changed.
                    // This ensures Admin geofence adjustments take immediate effect.
                    var attendanceEvaluationCompleted = await ProcessAutomaticGeofencePunchAsync(
                            db,
                            employeeId,
                            effectiveSessionId,
                            latitude,
                            longitude,
                            safeAccuracy,
                            safeDistance,
                            allowedRadiusMeters,
                            effectivePreviousLocationState,
                            stableLocationState.Value,
                            captureTime); // MIRROR: Use original capture time for offline sync reconciliation

                    // Persist the SAME stable state that drove the attendance
                    // decision only when the evaluation completed safely.
                    if (attendanceEvaluationCompleted)
                    {
                        session.LastIsWithinAllowedRadius =
                            stableLocationState.Value;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Dual Attendance evaluation did not complete. Geofence state was not advanced. EmployeeId={EmployeeId}, SessionId={SessionId}",
                            employeeId,
                            effectiveSessionId);
                    }

                    if (radiusChanged)
                    {
                        _logger.LogInformation(
                            "Geofence radius changed during active GPS session; state re-baselined with immediate attendance evaluation. EmployeeId={EmployeeId}, SessionId={SessionId}, PreviousRadius={PreviousRadius}, NewRadius={NewRadius}, State={State}",
                            employeeId,
                            effectiveSessionId,
                            session.LastAllowedRadiusMeters,
                            allowedRadiusMeters,
                            stableLocationState.Value ? "INSIDE" : "OUTSIDE");
                    }
                }

                session.LastUpdateAtUtc = now;
                session.LastLatitude = latitude;
                session.LastLongitude = longitude;
                session.LastAccuracyMeters = safeAccuracy;
                session.LastDistanceFromOfficeMeters = safeDistance;
                session.LastAllowedRadiusMeters =
                    allowedRadiusMeters < 0 ? 0 : allowedRadiusMeters;

                session.TotalPoints++;
                session.TotalDistanceMeters += safeDistance;

                if (session.TotalPoints == 1)
                {
                    session.AverageAccuracyMeters = safeAccuracy;
                }
                else
                {
                    var previousAverage =
                        session.AverageAccuracyMeters ?? 0;

                    session.AverageAccuracyMeters =
                        ((previousAverage * (session.TotalPoints - 1)) +
                         safeAccuracy) /
                        session.TotalPoints;
                }

                // Only after the active-session check and state update do we
                // publish the location into the process-local live store.
                var liveUpdated = LiveLocationStore.Update(
                    employeeId,
                    latitude,
                    longitude,
                    safeAccuracy,
                    safeDistance,
                    allowedRadiusMeters,
                    session.LastIsWithinAllowedRadius ?? isWithinAllowedRadius,
                    effectiveSessionId,
                    captureTime);

                if (!liveUpdated)
                {
                    _logger.LogWarning(
                        "GPS live-store update rejected. EmployeeId={EmployeeId}, SessionId={SessionId}",
                        employeeId,
                        effectiveSessionId);
                    return false;
                }

                await SaveChangesWithSqliteRetryAsync(db);

                /*
                 * Firebase is the shared realtime wire for Web + Android.
                 * Publish only after the authoritative GPS session update has
                 * committed. A Firebase outage must never break the existing
                 * Web attendance/GPS business flow.
                 */
                try
                {
                    var live = LiveLocationStore.Get(employeeId);
                    var speed = live?.SpeedMps ?? 0;

                    var clientEventId =
                        $"web-{effectiveSessionId:N}-{session.TotalPoints}";

                    await _firebase.PublishLiveLocationAsync(
                        employeeId,
                        effectiveSessionId,
                        clientEventId,
                        session.TotalPoints,
                        latitude,
                        longitude,
                        safeAccuracy,
                        speed,
                        new DateTimeOffset(captureTime).ToUnixTimeMilliseconds(),
                        allowedRadiusMeters,
                        session.LastIsWithinAllowedRadius ?? isWithinAllowedRadius,
                        session.StartedAtUtc,
                        safeDistance);
                }
                catch (Exception firebaseEx)
                {
                    _logger.LogWarning(
                        firebaseEx,
                        "Firebase live-location publish failed after committed GPS update. EmployeeId={EmployeeId}, SessionId={SessionId}",
                        employeeId,
                        effectiveSessionId);
                }

                try
                {
                    // Broadcast the same authoritative live-store snapshot that
                    // the admin map uses. This makes a GPS update self-contained:
                    // Android does not have to wait for a manual refresh or a
                    // second HTTP request just to obtain speed/movement details.
                    var live = LiveLocationStore.Get(employeeId);

                    await _hubContext.Clients.All.SendAsync(
                        "LocationChanged",
                        new
                        {
                            EmployeeId = employeeId,
                            SessionId = effectiveSessionId,
                            Latitude = latitude,
                            Longitude = longitude,
                            Timestamp = live?.LastUpdatedUtc ?? captureTime,
                            DistanceMeters = live?.DistanceMeters ?? safeDistance,
                            AccuracyMeters = live?.AccuracyMeters ?? safeAccuracy,
                            AllowedRadiusMeters = live?.AllowedRadiusMeters ?? allowedRadiusMeters,
                            IsWithinAllowedRadius = live?.IsWithinAllowedRadius ?? isWithinAllowedRadius,
                            SpeedMps = live?.SpeedMps ?? 0,
                            MovementState = live?.MovementState ?? "Stopped"
                        });
                }
                catch (Exception signalREx)
                {
                    _logger.LogWarning(
                        signalREx,
                        "Failed to broadcast location update via SignalR for employee {EmployeeId}",
                        employeeId);
                }

                return true;
            }
            finally
            {
                if (lockHeld)
                {
                    try
                    {
                        ReleaseLocalLock(lockKey);
                    }
                    catch (Exception unlockEx)
                    {
                        _logger.LogWarning(
                            unlockEx,
                            "Failed to release GPS session advisory lock. EmployeeId={EmployeeId}",
                            employeeId);
                    }
                }

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to update GPS session. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);

            return false;
        }
    }


    // ================================================================
    // AUTOMATIC GEOFENCE ATTENDANCE FALLBACK
    // ================================================================
    //
    // Geofence attendance is a FALLBACK/RECONCILIATION source.
    //
    // INSIDE  + attendance OUT -> automatic IN
    // OUTSIDE + attendance IN  -> automatic OUT
    //
    // The decision is evaluated on every valid GPS fix rather than only on
    // a previous-state transition. This makes the system self-healing when
    // the first fix is outside, a transition was missed, or the browser/app
    // was suspended.
    //
    // Physical biometric punches remain the priority source. If a
    // biometric punch arrives shortly after an automatic geofence punch,
    // the Attendance Worker reconciles the temporary geofence punch and
    // keeps the biometric punch.
    // ================================================================

    private async Task<bool> ProcessAutomaticGeofencePunchAsync(
        AppDbContext db,
        int employeeId,
        Guid sessionId,
        double latitude,
        double longitude,
        double accuracyMeters,
        double distanceMeters,
        int allowedRadiusMeters,
        bool? previousLocationState,
        bool currentLocationState,
        DateTime? overridePunchTime = null)
    {
        var features = await db.FeatureSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == 1);

        if (features?.EnableGeoFencing != true ||
            features.EnableAutomaticGeofencePunching != true)
            return true;

        if (allowedRadiusMeters <= 0)
            return true;

        try
        {
            /*
             * IMPORTANT:
             * Automatic geofence attendance is a RADIUS STATE TRANSITION,
             * not a reconciliation on every GPS fix.
             *
             * OUTSIDE -> INSIDE = one IN
             * INSIDE  -> OUTSIDE = one OUT
             * INSIDE  -> INSIDE  = no punch
             * OUTSIDE -> OUTSIDE = no punch
             *
             * A null previous state is the first known state for this GPS
             * session. Establishing OUTSIDE never creates an OUT punch.
             * Establishing INSIDE may create the initial IN, but only when
             * the current attendance parity is still OUT.
             *
             * If a genuine transition fails because SQLite is temporarily
             * locked, return false and do NOT advance the session state. The
             * next GPS fix can retry the same transition without creating
             * duplicate punches after the first successful save.
             */
            var isTransition =
                previousLocationState.HasValue &&
                previousLocationState.Value != currentLocationState;

            var isInitialInside =
                !previousLocationState.HasValue && currentLocationState;

            // If neither a state transition nor initial inside fix:
            // Only proceed if the employee is currently inside (to check for new day opening punch).
            // OUTSIDE -> OUTSIDE is an immediate no-op.
            if (!isTransition && !isInitialInside && !currentLocationState)
                return true;

            await using var transaction =
                await db.Database.BeginTransactionAsync();

            await AcquireAttendanceAdvisoryLockAsync(db, employeeId);

            var punchTime = overridePunchTime.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(
                    overridePunchTime.Value,
                    IndiaTimeZone)
                : GetIndiaNow();

            var businessDayStart = DateTime.SpecifyKind(
                punchTime.Date,
                DateTimeKind.Unspecified);

            var businessDayEnd = DateTime.SpecifyKind(
                punchTime.Date.AddDays(1),
                DateTimeKind.Unspecified);

            var todaysPunches = await db.AttendanceLogs
                .Where(x =>
                    x.EmployeeID == employeeId &&
                    x.PunchTime >= businessDayStart &&
                    x.PunchTime < businessDayEnd)
                .OrderBy(x => x.PunchTime)
                .ThenBy(x => x.LogID)
                .ToListAsync();

            var emp = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.EmployeeID == employeeId);
            var isContinuous = string.Equals(emp?.ShiftMode, "CONTINUOUS", StringComparison.OrdinalIgnoreCase) ||
                               (emp?.ShiftStartTime.HasValue == true && emp?.ShiftEndTime.HasValue == true && emp.ShiftEndTime.Value <= emp.ShiftStartTime.Value);

            bool attendanceCurrentlyOpen = false;

            if (isContinuous)
            {
                // In continuous shift mode (or overnight shift), check the most recent punch within the last 24 hours.
                // Use IsInType/IsOutType so aliases like "Punch", "CheckIn", "AUTO_IN" are recognised.
                var windowStart = punchTime.AddHours(-24);
                var recentPunches = await db.AttendanceLogs
                    .AsNoTracking()
                    .Where(x => x.EmployeeID == employeeId &&
                                x.PunchTime >= windowStart &&
                                x.PunchTime <= punchTime)
                    .OrderByDescending(x => x.PunchTime)
                    .ThenByDescending(x => x.LogID)
                    .ToListAsync();

                // Filter to canonical IN/OUT after EF materialises results (IsInType is not translatable to SQL)
                var recentValid = recentPunches
                    .Where(x => IsInType(x.LogType) || IsOutType(x.LogType))
                    .Take(1)
                    .ToList();

                if (recentValid.Count > 0)
                {
                    attendanceCurrentlyOpen = IsInType(recentValid[0].LogType);
                }
            }
            else
            {
                // Standard Single Day Shift mode: check punches belonging to today's business day.
                // Use IsInType/IsOutType so aliases like "Punch", "CheckIn", "AUTO_IN" are recognised.
                var validPunches = todaysPunches
                    .Where(p => IsInType(p.LogType) || IsOutType(p.LogType))
                    .ToList();

                if (validPunches.Count > 0)
                {
                    attendanceCurrentlyOpen = IsInType(validPunches.Last().LogType);
                }
            }
            // If this is an INSIDE -> INSIDE fix (neither transition nor initial inside):
            // For single-day shift, only allow it if today's business day has ZERO punches yet
            // (e.g. employee remained inside across midnight 12:00 AM).
            // If punches already exist today, or if continuous mode, INSIDE -> INSIDE is a no-op.
            if (!isTransition && !isInitialInside)
            {
                if (isContinuous || todaysPunches.Count > 0)
                {
                    await transaction.CommitAsync();
                    return true;
                }
            }

            var punchType = currentLocationState ? "IN" : "OUT";

            // Initial OUT is only a state initialization, never an OUT punch.
            if (!currentLocationState && !previousLocationState.HasValue)
            {
                await transaction.CommitAsync();
                return true;
            }

            // For a real transition, the geofence direction must agree with
            // the attendance direction before creating a fallback punch.
            // This prevents a manual/biometric punch from being duplicated.
            if (currentLocationState == attendanceCurrentlyOpen)
            {
                await transaction.CommitAsync();
                return true;
            }

            var recentAuthoritative = todaysPunches
                .Where(IsAuthoritativeAttendancePunch)
                .Where(x =>
                    Math.Abs((x.PunchTime - punchTime).TotalSeconds) <=
                    AuthoritativePunchProtectionSeconds)
                .OrderByDescending(x => x.PunchTime)
                .FirstOrDefault();

            if (recentAuthoritative != null)
            {
                _logger.LogInformation(
    "Automatic geofence {PunchType} skipped because an authoritative attendance punch already exists. " +
    "EmployeeId={EmployeeId}, LogId={LogId}, Device={Device}, Time={PunchTime}",
    punchType,
    employeeId,
    recentAuthoritative.LogID,
    recentAuthoritative.DeviceID,
    recentAuthoritative.PunchTime);

                await transaction.CommitAsync();
                return true;
            }

            // 5-minute IN guard: cross-device safety net.
            // If an IN punch (any LogType alias) was created within the last 5 minutes,
            // skip creating another IN even if the parity check above passed
            // (can happen when Android auto-punch fires concurrently and its "IN" type
            // was not yet normalised at parity-check time).
            if (currentLocationState) // i.e. generating an IN punch
            {
                var recentInPunch = todaysPunches
                    .Where(p => IsInType(p.LogType))
                    .Where(p => Math.Abs((p.PunchTime - punchTime).TotalSeconds) <= 300) // 5 minutes
                    .OrderByDescending(p => p.PunchTime)
                    .FirstOrDefault();

                if (recentInPunch != null)
                {
                    _logger.LogInformation(
                        "Automatic geofence IN skipped — an IN punch already exists within 5 minutes. " +
                        "EmployeeId={EmployeeId}, ExistingLogId={LogId}, ExistingDevice={Device}, ExistingTime={Time}",
                        employeeId,
                        recentInPunch.LogID,
                        recentInPunch.DeviceID,
                        recentInPunch.PunchTime);

                    await transaction.CommitAsync();
                    return true;
                }
            }
            else // i.e. generating an OUT punch
            {
                // 10-minute OUT guard: cross-device safety net.
                // Re-query the DB fresh (not the stale todaysPunches snapshot) so that
                // any AndroidGeofenceAuto OUT written by Firebase sync AFTER the initial
                // todaysPunches load is also caught, closing the async-write race window.
                // Fetch the bounded 10-min window, then apply IsOutType in memory
                // (IsOutType is a C# static method — not EF-translatable to SQL).
                var guardWindowStart = punchTime.AddMinutes(-10);
                var guardWindowEnd   = punchTime;
                var windowPunches = await db.AttendanceLogs
                    .Where(p =>
                        p.EmployeeID == employeeId &&
                        p.PunchTime >= businessDayStart &&
                        p.PunchTime < businessDayEnd &&
                        p.PunchTime >= guardWindowStart &&
                        p.PunchTime <= guardWindowEnd)
                    .OrderByDescending(p => p.PunchTime)
                    .ToListAsync();
                var freshOutPunch = windowPunches.FirstOrDefault(p => IsOutType(p.LogType) &&
                    (p.DeviceID == "GeofenceAuto" || p.DeviceID == "AndroidGeofenceAuto"));

                if (freshOutPunch != null)
                {
                    _logger.LogInformation(
                        "Automatic geofence OUT skipped — an OUT punch already exists within 10 minutes (fresh re-query). " +
                        "EmployeeId={EmployeeId}, ExistingLogId={LogId}, ExistingDevice={Device}, ExistingTime={Time}",
                        employeeId,
                        freshOutPunch.LogID,
                        freshOutPunch.DeviceID,
                        freshOutPunch.PunchTime);

                    await transaction.CommitAsync();
                    return true;
                }
            }

            var log = new AttendanceLog
            {
                EmployeeID = employeeId,
                BiometricID = "GEOFENCE_AUTO",
                PunchTime = punchTime,
                DeviceID = "GeofenceAuto",
                LogType = punchType,
                Latitude = latitude,
                Longitude = longitude,
                IsApproved = true
            };

            db.AttendanceLogs.Add(log);
            await SaveChangesWithSqliteRetryAsync(db);

            // Firebase synchronization remains secondary to the committed
            // local attendance transaction and must never create another punch.
            _ = _firebaseAttendanceMutations.UpsertPunchAsync(log, "CREATED");

            try
            {
                var punchDate = DateOnly.FromDateTime(punchTime);
                _ = _refreshService.NotifyPunchCreatedAsync(employeeId, punchDate);
                _ = _refreshService.NotifyAttendanceChangedAsync(employeeId, punchDate);
            }
            catch (Exception refreshEx)
            {
                _logger.LogWarning(refreshEx, "Failed to broadcast attendance change for auto-punch: {EmployeeId}", employeeId);
            }

            var result = new GeoPunchResult
            {
                Success = true,
                Message =
    $"Automatic geofence {punchType} recorded. " +
    $"(Dist: {distanceMeters:F0}m)"
            };

            await SavePunchAuditAsync(
                db,
                employeeId,
                sessionId,
                overridePunchTime?.ToUniversalTime() ?? DateTime.UtcNow,
                latitude,
                longitude,
                accuracyMeters,
                distanceMeters,
                allowedRadiusMeters,
                currentLocationState,
                result,
                log.LogID,
                "GEOFENCE_AUTO");

            await transaction.CommitAsync();

            _logger.LogInformation(
                "Automatic geofence {PunchType} recorded. EmployeeId={EmployeeId}, LogId={LogId}, Distance={Distance}m, PreviousState={PreviousState}, CurrentState={CurrentState}",
                punchType,
                employeeId,
                log.LogID,
                Math.Round(distanceMeters, 1),
                previousLocationState?.ToString() ?? "NULL",
                currentLocationState);

            try
            {
                await _refreshService.NotifyDataChangedAsync(
                    employeeId,
                    DateOnly.FromDateTime(log.PunchTime.Date),
                    "GEOFENCE_AUTO");

                await _refreshService.NotifyAttendanceChangedAsync(
                    employeeId,
                    DateOnly.FromDateTime(log.PunchTime.Date));

                await _refreshService.NotifyPunchCreatedAsync(
                    employeeId,
                    DateOnly.FromDateTime(log.PunchTime.Date));
            }
            catch (Exception refreshEx)
            {
                _logger.LogWarning(
                    refreshEx,
                    "Automatic geofence punch saved but attendance refresh notification failed. EmployeeId={EmployeeId}, LogId={LogId}",
                    employeeId,
                    log.LogID);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Automatic fallback must NEVER stop normal GPS tracking. A
            // transient SQLite lock leaves the previous geofence state intact,
            // allowing the next GPS fix to retry the same transition.
            _logger.LogError(
                ex,
                "Automatic geofence attendance processing failed. EmployeeId={EmployeeId}, SessionId={SessionId}, PreviousState={PreviousState}, CurrentState={CurrentState}",
                employeeId,
                sessionId,
                previousLocationState?.ToString() ?? "NULL",
                currentLocationState);

            try
            {
                await using var auditDb =
                    await _dbFactory.CreateDbContextAsync();

                await SavePunchAuditAsync(
                    auditDb,
                    employeeId,
                    sessionId,
                    overridePunchTime?.ToUniversalTime() ?? DateTime.UtcNow,
                    latitude,
                    longitude,
                    accuracyMeters,
                    distanceMeters,
                    allowedRadiusMeters,
                    currentLocationState,
                    new GeoPunchResult
                    {
                        Success = false,
                        Message =
                            $"Automatic geofence {requiredPunchType(currentLocationState)} failed: {ex.Message}"
                    },
                    null,
                    "GEOFENCE_AUTO");
            }
            catch (Exception auditEx)
            {
                _logger.LogWarning(
                    auditEx,
                    "Failed to record automatic geofence failure audit. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    employeeId,
                    sessionId);
            }

            return false;
        }

        static string requiredPunchType(bool inside) => inside ? "IN" : "OUT";
    }

    private static bool IsAuthoritativeAttendancePunch(AttendanceLog log)
    {
        if (log == null || !log.IsApproved)
            return false;

        var device = log.DeviceID?.Trim() ?? string.Empty;
        var biometricId = log.BiometricID?.Trim() ?? string.Empty;

        if (device.Equals("GeofenceAuto", StringComparison.OrdinalIgnoreCase) ||
            biometricId.Equals("GEOFENCE_AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return
            device.StartsWith("ZKTeco_", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("MobileWeb", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("Android", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="logType"/> represents any form of check-IN punch.
    /// Covers all aliases used by Web, Android, biometric machine, and Firebase sync:
    /// "IN", "CHECKIN", "CHECK_IN", "Punch" (Web manual default), and "AUTO_IN" prefixed.
    /// </summary>
    private static bool IsInType(string? logType)
    {
        if (string.IsNullOrWhiteSpace(logType)) return false;
        var t = logType.Trim();
        return string.Equals(t, "IN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "CHECKIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "CHECK_IN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Punch", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("AUTO_IN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="logType"/> represents any form of check-OUT punch.
    /// </summary>
    private static bool IsOutType(string? logType)
    {
        if (string.IsNullOrWhiteSpace(logType)) return false;
        var t = logType.Trim();
        return string.Equals(t, "OUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "CHECKOUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "CHECK_OUT", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("AUTO_OUT", StringComparison.OrdinalIgnoreCase);
    }

    private static Task AcquireAttendanceAdvisoryLockAsync(
        AppDbContext db,
        int employeeId)
    {
        // The local SQLite compatibility database serializes writes at the
        // transaction boundary, so no PostgreSQL-specific advisory SQL is used.
        return Task.CompletedTask;
    }

    private static async Task AcquireLocalLockAsync(long key)
    {
        var gate = LocalAdvisoryLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
    }

    private static void ReleaseLocalLock(long key)
    {
        if (!LocalAdvisoryLocks.TryGetValue(key, out var gate)) return;
        try { gate.Release(); } catch (SemaphoreFullException) { }
    }

    private static async Task SaveChangesWithSqliteRetryAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (SqliteException ex)
                when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) &&
                      attempt < maxAttempts)
            {
                // SQLITE_BUSY (5) / SQLITE_LOCKED (6). Another Web/Worker
                // compatibility-database operation is briefly holding the
                // table. Wait with bounded exponential backoff instead of
                // immediately failing the GPS/geofence operation.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    cancellationToken);
            }
        }

        // Final attempt propagates the original SQLite exception.
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool? ResolveStableGeofenceState(
        bool? previousState,
        double distanceMeters,
        int allowedRadiusMeters)
    {
        if (allowedRadiusMeters <= 0 ||
            !double.IsFinite(distanceMeters))
        {
            return null;
        }

        // Attendance transitions use the ADMIN-CONFIGURED radius exactly.
        // The previous implementation applied a hidden 10% hysteresis band
        // (minimum 15m), which meant an employee inside a 100m radius at 90m
        // could still be treated as outside. That prevented the expected
        // Dual Attendance IN/OUT transition.
        //
        // Idempotency is still guaranteed by attendance parity checks in
        // ProcessAutomaticGeofencePunchAsync, so repeated GPS fixes do not
        // create duplicate same-direction punches.
        return distanceMeters <= allowedRadiusMeters;
    }

    private static bool IsBiometricPunch(
        AttendanceLog log)
    {
        if (log == null)
            return false;

        var device = log.DeviceID?.Trim() ?? string.Empty;
        var biometricId = log.BiometricID?.Trim() ?? string.Empty;

        return device.StartsWith(
                   "ZKTeco_",
                   StringComparison.OrdinalIgnoreCase)
               ||
               (
                   !string.IsNullOrWhiteSpace(biometricId) &&
                   !biometricId.Equals(
                       "MOBILE_APP",
                       StringComparison.OrdinalIgnoreCase) &&
                   !biometricId.Equals(
                       "GEOFENCE_AUTO",
                       StringComparison.OrdinalIgnoreCase) &&
                   !biometricId.StartsWith(
                       "ANDROID-",
                       StringComparison.OrdinalIgnoreCase) &&
                   !device.Equals(
                       "MobileWeb",
                       StringComparison.OrdinalIgnoreCase) &&
                   !device.Equals(
                       "Android",
                       StringComparison.OrdinalIgnoreCase) &&
                   !device.Equals(
                       "GeofenceAuto",
                       StringComparison.OrdinalIgnoreCase)
               );
    }

    // ================================================================
    // END GPS SESSION
    // ================================================================

    public async Task EndGpsSessionAsync(
        int employeeId,
        Guid sessionId,
        string endReason = "LOGGED_OUT")
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return;

        try
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            var lockKey = GpsSessionAdvisoryLockNamespace + (uint)employeeId;
            var lockHeld = false;

            try
            {
                await AcquireLocalLockAsync(lockKey);
                lockHeld = true;

                var session = await db.EmployeeGpsSessions
                    .FirstOrDefaultAsync(x =>
                        x.EmployeeId == employeeId &&
                        x.SessionId == sessionId);

                if (session == null)
                {
                    LiveLocationStore.Remove(employeeId, sessionId);
                    return;
                }

                if (session.EndedAtUtc.HasValue)
                {
                    LiveLocationStore.Remove(employeeId, sessionId);
                    return;
                }

                session.EndedAtUtc = DateTime.UtcNow;
                session.EndReason =
                    string.IsNullOrWhiteSpace(endReason)
                        ? "ENDED"
                        : endReason.Length > 40
                            ? endReason[..40]
                            : endReason;

                await SaveChangesWithSqliteRetryAsync(db);

                // Keep the cross-platform Firebase session lifecycle aligned with
                // the authoritative Web session. This prevents Android from
                // continuing to publish against a session that Web has ended.
                try
                {
                    var ownerUidForSession = _firebase.ResolveOwnerUid(
                        $"employee-{employeeId}",
                        "Employee");

                    await _firebase.TerminateTrackingSessionAsync(
                        employeeId,
                        sessionId,
                        ownerUidForSession,
                        session.EndReason ?? "ENDED");
                }
                catch (Exception firebaseSessionEx)
                {
                    _logger.LogWarning(
                        firebaseSessionEx,
                        "Failed to publish GPS session end to Firebase. EmployeeId={EmployeeId}, SessionId={SessionId}",
                        employeeId,
                        sessionId);
                }

                // Remove only the ended session from the process-local store.
                // Before deleting the Firebase live marker, verify that no newer
                // active session exists for the same employee.
                LiveLocationStore.Remove(employeeId, sessionId);

                var newerActiveSessionExists = await db.EmployeeGpsSessions
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.EmployeeId == employeeId &&
                        x.EndedAtUtc == null &&
                        x.SessionId != sessionId);

                if (!newerActiveSessionExists)
                {
                    try
                    {
                        var ownerUid = _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee");
                        await _firebase.DeleteGlobalRecordAsync($"tracking/live/{employeeId}");
                        if (!string.IsNullOrWhiteSpace(ownerUid))
                        {
                            await _firebase.DeleteGlobalRecordAsync(
                                $"owners/{ownerUid}/tracking/live/{employeeId}");
                        }
                    }
                    catch (Exception firebaseEx)
                    {
                        _logger.LogWarning(
                            firebaseEx,
                            "Failed to remove ended GPS live marker for employee {EmployeeId}",
                            employeeId);
                    }
                }

                _logger.LogInformation(
                    "GPS session ended. EmployeeId={EmployeeId}, SessionId={SessionId}, Reason={Reason}",
                    employeeId,
                    sessionId,
                    session.EndReason);

                try
                {
                    await _hubContext.Clients.All.SendAsync(
                        "SessionEnded",
                        new
                        {
                            EmployeeId = employeeId,
                            SessionId = sessionId,
                            EndedAtUtc = session.EndedAtUtc,
                            EndReason = session.EndReason
                        });
                }
                catch (Exception signalREx)
                {
                    _logger.LogWarning(
                        signalREx,
                        "Failed to broadcast session end via SignalR for employee {EmployeeId}",
                        employeeId);
                }
            }
            finally
            {
                if (lockHeld)
                {
                    try
                    {
                        ReleaseLocalLock(lockKey);
                    }
                    catch (Exception unlockEx)
                    {
                        _logger.LogWarning(
                            unlockEx,
                            "Failed to release GPS session advisory lock after logout. EmployeeId={EmployeeId}",
                            employeeId);
                    }
                }

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to end GPS session. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);
        }
    }


    // ================================================================
    // END ALL ACTIVE GPS SESSIONS FOR AN EMPLOYEE
    // ================================================================

    public async Task<int> EndAllGpsSessionsAsync(
        int employeeId,
        string endReason)
    {
        if (employeeId <= 0)
            return 0;

        var safeReason = string.IsNullOrWhiteSpace(endReason)
            ? "ENDED"
            : endReason.Length > 40
                ? endReason[..40]
                : endReason;

        try
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            var lockKey = GpsSessionAdvisoryLockNamespace + (uint)employeeId;
            var lockHeld = false;

            try
            {
                await AcquireLocalLockAsync(lockKey);
                lockHeld = true;

                var sessions = await db.EmployeeGpsSessions
                    .Where(x =>
                        x.EmployeeId == employeeId &&
                        x.EndedAtUtc == null)
                    .ToListAsync();

                if (sessions.Count == 0)
                {
                    // Even if the database is already clean, remove only the
                    // currently stored location for this employee. This clears
                    // stale process-local state without touching another
                    // employee/session.
                    var liveSessionId = LiveLocationStore.GetSessionId(employeeId);
                    if (liveSessionId.HasValue)
                        LiveLocationStore.Remove(employeeId, liveSessionId.Value);

                    return 0;
                }

                var now = DateTime.UtcNow;
                foreach (var session in sessions)
                {
                    session.EndedAtUtc = now;
                    session.EndReason = safeReason;
                }

                await SaveChangesWithSqliteRetryAsync(db);

                // REQUIREMENT: Synchronize session termination to Firebase.
                // This ensures map markers go offline immediately without
                // waiting for a SignalR broadcast or browser refresh.
                var ownerUidForSessions = _firebase.ResolveOwnerUid(
                    $"employee-{employeeId}",
                    "Employee");

                foreach (var session in sessions)
                {
                    try
                    {
                        await _firebase.TerminateTrackingSessionAsync(
                            session.EmployeeId,
                            session.SessionId,
                            ownerUidForSessions,
                            session.EndReason ?? safeReason);
                    }
                    catch (Exception firebaseSessionEx)
                    {
                        _logger.LogWarning(
                            firebaseSessionEx,
                            "Failed to publish GPS session termination to Firebase. EmployeeId={EmployeeId}, SessionId={SessionId}",
                            session.EmployeeId,
                            session.SessionId);
                    }
                }

                await _firebase.TerminateLiveLocationAsync(
                    employeeId,
                    ownerUidForSessions);

                foreach (var session in sessions)
                {
                    LiveLocationStore.Remove(
                        session.EmployeeId,
                        session.SessionId);

                    try
                    {
                        await _hubContext.Clients.All.SendAsync(
                            "SessionEnded",
                            new
                            {
                                EmployeeId = session.EmployeeId,
                                SessionId = session.SessionId,
                                EndedAtUtc = session.EndedAtUtc,
                                EndReason = session.EndReason
                            });
                    }
                    catch (Exception signalREx)
                    {
                        _logger.LogWarning(
                            signalREx,
                            "Failed to broadcast GPS session end for employee {EmployeeId}, SessionId={SessionId}",
                            session.EmployeeId,
                            session.SessionId);
                    }
                }

                _logger.LogInformation(
                    "Ended {Count} active GPS sessions. EmployeeId={EmployeeId}, Reason={Reason}",
                    sessions.Count,
                    employeeId,
                    safeReason);

                return sessions.Count;
            }
            finally
            {
                if (lockHeld)
                {
                    try
                    {
                        ReleaseLocalLock(lockKey);
                    }
                    catch (Exception unlockEx)
                    {
                        _logger.LogWarning(
                            unlockEx,
                            "Failed to release GPS session advisory lock after ending all sessions. EmployeeId={EmployeeId}",
                            employeeId);
                    }
                }

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to end all GPS sessions. EmployeeId={EmployeeId}, Reason={Reason}",
                employeeId,
                safeReason);
            return 0;
        }
    }


    // ================================================================
    // PROCESS MANUAL LOGOUT PUNCH
    // ================================================================

    public async Task ProcessManualLogoutPunchAsync(int employeeId)
    {
        if (employeeId <= 0) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            await AcquireAttendanceAdvisoryLockAsync(db, employeeId);

            var punchTime = GetIndiaNow();

            var businessDayStart =
                DateTime.SpecifyKind(
                    punchTime.Date,
                    DateTimeKind.Unspecified);

            var businessDayEnd =
                DateTime.SpecifyKind(
                    punchTime.Date.AddDays(1),
                    DateTimeKind.Unspecified);

            var todaysPunches = await db.AttendanceLogs
                .Where(x => x.EmployeeID == employeeId && x.PunchTime >= businessDayStart && x.PunchTime < businessDayEnd)
                .OrderBy(x => x.PunchTime)
                .ThenBy(x => x.LogID)
                .ToListAsync();

            // REQUIREMENT: Only create an OUT punch if they are currently IN (odd number of punches).
            // This ensures manual logout closes the attendance session authoritatively.
            if (todaysPunches.Count % 2 != 0)
            {
                /*
                 * BIOMETRIC and explicit MOBILE punches are authoritative.
                 * If one has already been committed close to this logout,
                 * we skip the fallback to avoid duplicate/noisy events.
                 */
                var recentAuthoritative = todaysPunches
                    .Where(IsAuthoritativeAttendancePunch)
                    .Where(x => Math.Abs((x.PunchTime - punchTime).TotalSeconds) <= AuthoritativePunchProtectionSeconds)
                    .OrderByDescending(x => x.PunchTime)
                    .FirstOrDefault();

                if (recentAuthoritative != null)
                {
                    _logger.LogInformation(
                        "Manual logout OUT punch skipped because an authoritative attendance punch already exists. " +
                        "EmployeeId={EmployeeId}, LogId={LogId}, Time={PunchTime}",
                        employeeId, recentAuthoritative.LogID, recentAuthoritative.PunchTime);
                    return;
                }

                var log = new AttendanceLog
                {
                    EmployeeID = employeeId,
                    BiometricID = "MOBILE_LOGOUT",
                    PunchTime = punchTime,
                    DeviceID = "ManualLogout",
                    LogType = "OUT",
                    IsApproved = true
                };

                db.AttendanceLogs.Add(log);
                await SaveChangesWithSqliteRetryAsync(db);

                // Synchronize to Firebase so the Android app observes the logout punch immediately.
                _ = _firebaseAttendanceMutations.UpsertPunchAsync(log, "CREATED");

                await _refreshService.NotifyAttendanceChangedAsync(employeeId, DateOnly.FromDateTime(punchTime));

                _logger.LogInformation(
                    "Manual logout OUT punch recorded. EmployeeId={EmployeeId}, LogId={LogId}",
                    employeeId, log.LogID);
            }
        }
        catch (Exception ex)
        {
            // Fallback punch creation must never block the authoritative logout.
            _logger.LogError(ex, "Failed to process manual logout punch. EmployeeId={EmployeeId}", employeeId);
        }
    }


    // ================================================================
    // MARK SESSION TIMED OUT
    // ================================================================
    //
    // IMPORTANT SESSION TIMEOUT POLICY:
    //
    // GPS sessions should ONLY timeout after VERY long inactivity.
    //
    // Reasons:
    // 1. Employee may have GPS disabled but still be logged in
    // 2. Network interruptions are temporary
    // 3. GPS watcher may be paused by browser power management
    // 4. Employee is still working even without GPS updates
    //
    // Only mark a session as timed-out if:
    // - No GPS update for 30 minutes (1800 seconds)
    // - Session is still marked as active in database
    //
    // This allows the admin to manually log out an employee
    // or for a new login to invalidate the old session.
    // ================================================================

    public async Task MarkTimedOutSessionsAsync()
    {
        // Network loss, airplane mode, browser suspension and temporary GPS
        // outages are NOT logout events. A GPS session may only end through
        // explicit logout or an explicit force-login replacement. Keep this
        // method for hosted-service compatibility, but never mutate session
        // state merely because LastUpdateAtUtc is old.
        await Task.CompletedTask;
    }

    // ================================================================
    // GET ACTIVE SESSION
    // ================================================================

    public async Task<EmployeeGpsSession?> GetActiveGpsSessionAsync(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        try
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            return await db.EmployeeGpsSessions
                .AsNoTracking()
                .Where(x =>
                    x.EmployeeId == employeeId &&
                    x.EndedAtUtc == null)
                .OrderByDescending(x => x.StartedAtUtc)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to get active GPS session. EmployeeId={EmployeeId}",
                employeeId);

            return null;
        }
    }

    // ================================================================
    // SAVE EMPLOYEE LOCATION HISTORY
    // ================================================================

    public async Task SaveLocationHistoryAsync(
        int employeeId,
        Guid sessionId,
        double latitude,
        double longitude,
        double distanceMeters,
        int allowedRadiusMeters,
        bool isWithinAllowedRadius,
        double accuracyMeters = 0,
        DateTime? capturedAtUtc = null,
        string captureSource = "Online",
        Guid? syncBatchId = null)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty ||
            !IsValidCoordinate(latitude, longitude))
        {
            return;
        }

        var safeAccuracy =
            NormalizeAccuracy(accuracyMeters);

        try
        {
            await using var db =
                await _dbFactory.CreateDbContextAsync();

            var serverRecordedAtUtc = DateTime.UtcNow;
            var originalCaptureUtc = capturedAtUtc ?? serverRecordedAtUtc;
            if (originalCaptureUtc > serverRecordedAtUtc.AddMinutes(5))
                originalCaptureUtc = serverRecordedAtUtc;

            // Smart Route Playback De-duplication:
            // 1. Prevent sub-second / burst duplicate entries (less than 5 seconds apart).
            // 2. Suppress stationary GPS jitter (< 15 meters movement) unless 60 seconds have elapsed.
            var lastPoint = await db.EmployeeLocationHistory
                .AsNoTracking()
                .Where(x => x.EmployeeId == employeeId && x.SessionId == sessionId)
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefaultAsync();

            if (lastPoint != null)
            {
                var timeDiffSeconds = Math.Abs((originalCaptureUtc - lastPoint.CapturedAtUtc).TotalSeconds);

                // Burst duplicate suppression (same second or within 5s)
                if (timeDiffSeconds < 5.0)
                    return;

                // Stationary deadband: suppress micro-jitter within 15 meters unless 60s passed
                var distanceMoved = CalculateDistance(lastPoint.Latitude, lastPoint.Longitude, latitude, longitude);
                if (distanceMoved < 15.0 && timeDiffSeconds < 60.0)
                    return;
            }

            var record = new EmployeeLocationHistory
            {
                EmployeeId = employeeId,
                SessionId = sessionId,
                Latitude = latitude,
                Longitude = longitude,
                AccuracyMeters = safeAccuracy,
                DistanceFromOfficeMeters = NormalizeDistance(distanceMeters),
                AllowedRadiusMeters =
                    allowedRadiusMeters < 0
                        ? 0
                        : allowedRadiusMeters,
                IsWithinAllowedRadius = isWithinAllowedRadius,
                RecordedAtUtc = serverRecordedAtUtc,
                CaptureSource = string.IsNullOrWhiteSpace(captureSource) ? "Online" : captureSource,
                CapturedAtUtc = originalCaptureUtc,
                SyncBatchId = syncBatchId,
                SyncedAtUtc = string.Equals(captureSource, "OfflineSync", StringComparison.OrdinalIgnoreCase)
                    ? serverRecordedAtUtc
                    : null
            };

            db.EmployeeLocationHistory.Add(record);

            await SaveChangesWithSqliteRetryAsync(db);
        }
        catch (Exception ex)
        {
            /*
             * GPS history failure must NEVER stop live tracking.
             */
            _logger.LogError(
                ex,
                "Failed to save GPS history for employee {EmployeeId}",
                employeeId);
        }
    }

    // ================================================================
    // PROCESS MOBILE PUNCH
    // ================================================================

    public async Task<GeoPunchResult> ProcessMobilePunchAsync(
        int employeeId,
        double lat,
        double lon,
        double accuracyMeters = 0,
        Guid? sessionId = null)
    {
        var auditTimeUtc =
            DateTime.UtcNow;

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        var company = await db.CompanySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SettingID == 1);

        var features = await db.FeatureSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == 1);

        var safeAccuracy =
            NormalizeAccuracy(accuracyMeters);

        if (features?.EnableGeoFencing != true)
        {
            var result = new GeoPunchResult
            {
                Success = false,
                Message = "Geo-fencing module is disabled."
            };

            await SavePunchAuditAsync(
                db,
                employeeId,
                sessionId,
                auditTimeUtc,
                lat,
                lon,
                safeAccuracy,
                0,
                0,
                false,
                result);

            return result;
        }

        if (company == null ||
            company.OfficeLatitude == 0 ||
            company.OfficeLongitude == 0)
        {
            var result = new GeoPunchResult
            {
                Success = false,
                Message =
                    "Office location not configured by Admin."
            };

            await SavePunchAuditAsync(
                db,
                employeeId,
                sessionId,
                auditTimeUtc,
                lat,
                lon,
                safeAccuracy,
                0,
                company?.GeoRadiusMeters ?? 0,
                false,
                result);

            return result;
        }

        if (!IsValidCoordinate(lat, lon))
        {
            var result = new GeoPunchResult
            {
                Success = false,
                Message =
                    "Invalid GPS coordinates received."
            };

            await SavePunchAuditAsync(
                db,
                employeeId,
                sessionId,
                auditTimeUtc,
                lat,
                lon,
                safeAccuracy,
                0,
                company.GeoRadiusMeters,
                false,
                result);

            return result;
        }

        var distance = CalculateDistance(
            lat,
            lon,
            company.OfficeLatitude,
            company.OfficeLongitude);

        // UX: 2m buffer for GPS jitter
        var withinRadius =
            distance <= (company.GeoRadiusMeters + 2);

        if (!withinRadius)
        {
            var result = new GeoPunchResult
            {
                Success = false,
                Message =
                    $"You are {distance:F0} meters away. " +
                    $"Allowed radius is " +
                    $"{company.GeoRadiusMeters}m."
            };

            await SavePunchAuditAsync(
                db,
                employeeId,
                sessionId,
                auditTimeUtc,
                lat,
                lon,
                safeAccuracy,
                distance,
                company.GeoRadiusMeters,
                false,
                result);

            return result;
        }

        await using var attendanceTransaction =
            await db.Database.BeginTransactionAsync();

        await AcquireAttendanceAdvisoryLockAsync(
            db,
            employeeId);

        var log = new AttendanceLog
        {
            EmployeeID = employeeId,
            PunchTime = GetIndiaNow(),
            BiometricID = "MOBILE_APP",
            DeviceID = "MobileWeb",
            LogType = "Punch",
            Latitude = lat,
            Longitude = lon,
            IsApproved = true
        };

        db.AttendanceLogs.Add(log);

        if (sessionId.HasValue &&
            sessionId.Value != Guid.Empty)
        {
            db.EmployeeLocationHistory.Add(
                new EmployeeLocationHistory
                {
                    EmployeeId = employeeId,
                    SessionId = sessionId.Value,
                    Latitude = lat,
                    Longitude = lon,
                    AccuracyMeters = safeAccuracy,
                    DistanceFromOfficeMeters = NormalizeDistance(distance),
                    AllowedRadiusMeters = company.GeoRadiusMeters,
                    IsWithinAllowedRadius = true,
                    RecordedAtUtc = auditTimeUtc
                });
        }

        await SaveChangesWithSqliteRetryAsync(db);

        // REQUIREMENT: Synchronize the new mobile punch to the Firebase SSOT
        // attendance_punches node.
        _ = _firebaseAttendanceMutations.UpsertPunchAsync(log, "CREATED");

        var success = new GeoPunchResult
        {
            Success = true,
            Message =
                $"Punch accepted! (Dist: {distance:F0}m)"
        };

        await SavePunchAuditAsync(
            db,
            employeeId,
            sessionId,
            auditTimeUtc,
            lat,
            lon,
            safeAccuracy,
            distance,
            company.GeoRadiusMeters,
            true,
            success,
            log.LogID);

        await ReconcileAutomaticFallbackAsync(
            db,
            employeeId,
            log.PunchTime);

        await attendanceTransaction.CommitAsync();

        try
        {
            await _refreshService.NotifyDataChangedAsync(
                employeeId,
                DateOnly.FromDateTime(log.PunchTime.Date),
                "MOBILE_PUNCH");

            await _refreshService
                .NotifyAttendanceChangedAsync(
                    employeeId,
                    DateOnly.FromDateTime(
                        log.PunchTime.Date));

            await _refreshService.NotifyPunchCreatedAsync(
                employeeId,
                DateOnly.FromDateTime(log.PunchTime.Date));
        }
        catch (Exception ex)
        {
            // Refresh notification must never invalidate a successful punch.
            _logger.LogWarning(
                ex,
                "Mobile punch saved but live attendance notification failed for employee {EmployeeId}.",
                employeeId);
        }

        return success;
    }

    // ================================================================
    // AUTHORITATIVE MOBILE PUNCH RECONCILIATION
    // ================================================================

    public async Task ReconcileAutomaticFallbackAsync(
        AppDbContext db,
        int employeeId,
        DateTime authoritativePunchTime)
    {
        if (employeeId <= 0)
            return;

        var features = await db.FeatureSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == 1);

        if (features?.EnableGeoFencing != true ||
            features.EnableAutomaticGeofencePunching != true)
            return;

        await AcquireAttendanceAdvisoryLockAsync(
            db,
            employeeId);

        var windowStart =
            authoritativePunchTime.AddSeconds(
                -FallbackReconciliationWindowSeconds);

        var windowEnd =
            authoritativePunchTime.AddSeconds(
                FallbackReconciliationWindowSeconds);

        var fallback =
            await db.AttendanceLogs
                .Where(x =>
                    x.EmployeeID == employeeId &&
                    x.DeviceID == "GeofenceAuto" &&
                    x.BiometricID == "GEOFENCE_AUTO" &&
                    x.PunchTime >= windowStart &&
                    x.PunchTime <= windowEnd)
                .OrderBy(x =>
                    Math.Abs(
                        (x.PunchTime - authoritativePunchTime).TotalSeconds))
                .FirstOrDefaultAsync();

        if (fallback == null)
            return;

        db.AttendanceLogs.Remove(fallback);

        _logger.LogInformation(
            "Authoritative mobile punch replaced automatic geofence fallback. " +
            "EmployeeId={EmployeeId}, AuthoritativeTime={AuthoritativeTime}, " +
            "RemovedGeofenceLogId={GeofenceLogId}",
            employeeId,
            authoritativePunchTime,
            fallback.LogID);

        await SaveChangesWithSqliteRetryAsync(db);
    }

    // ================================================================
    // RECONCILE OFFLINE GPS HISTORY INTO ATTENDANCE
    // ================================================================
    //
    // History is normally an immutable ledger and must not resurrect a
    // session. OfflineSync points are the one explicit recovery path:
    // if a GPS fix was captured while the phone was offline, use that
    // recorded capture time to repair a missed automatic IN/OUT punch.
    // The existing attendance engine remains authoritative.
    // ================================================================

    public async Task<bool> ReconcileHistoricalGeofencePointAsync(
        int employeeId,
        Guid sessionId,
        double latitude,
        double longitude,
        double accuracyMeters,
        double distanceMeters,
        int allowedRadiusMeters,
        bool isWithinAllowedRadius,
        DateTime capturedAtUtc)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty ||
            !IsValidCoordinate(latitude, longitude) ||
            capturedAtUtc == default)
        {
            return false;
        }

        var capturedUtc = capturedAtUtc.ToUniversalTime();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var session = await db.EmployeeGpsSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.EmployeeId == employeeId &&
                    x.SessionId == sessionId);

            if (session == null)
            {
                _logger.LogDebug(
                    "Offline GPS attendance reconciliation skipped because session is unknown. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    employeeId,
                    sessionId);
                return false;
            }

            // The GPS evidence must belong to the session. A small clock-skew
            // allowance is permitted, but an unrelated post-logout point cannot
            // reopen attendance.
            if (capturedUtc < session.StartedAtUtc.AddMinutes(-5) ||
                (session.EndedAtUtc.HasValue &&
                 capturedUtc > session.EndedAtUtc.Value.AddMinutes(5)))
            {
                _logger.LogDebug(
                    "Offline GPS attendance reconciliation skipped because capture is outside session window. EmployeeId={EmployeeId}, SessionId={SessionId}, Capture={CaptureTime}, Start={Start}, End={End}",
                    employeeId,
                    sessionId,
                    capturedUtc,
                    session.StartedAtUtc,
                    session.EndedAtUtc);
                return false;
            }

            var stableDistance = NormalizeDistance(distanceMeters);
            var stableRadius = Math.Max(0, allowedRadiusMeters);
            var stableAccuracy = NormalizeAccuracy(accuracyMeters);

            return await ProcessAutomaticGeofencePunchAsync(
                db,
                employeeId,
                sessionId,
                latitude,
                longitude,
                stableAccuracy,
                stableDistance,
                stableRadius,
                previousLocationState: null,
                currentLocationState: isWithinAllowedRadius,
                overridePunchTime: capturedUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Offline GPS attendance reconciliation failed. EmployeeId={EmployeeId}, SessionId={SessionId}",
                employeeId,
                sessionId);
            return false;
        }
    }

    // ================================================================
    // SAVE PUNCH AUDIT
    // ================================================================

    private async Task SavePunchAuditAsync(
        AppDbContext db,
        int employeeId,
        Guid? sessionId,
        DateTime punchTimeUtc,
        double latitude,
        double longitude,
        double accuracyMeters,
        double distanceMeters,
        int allowedRadiusMeters,
        bool withinRadius,
        GeoPunchResult result,
        long? attendanceLogId = null,
        string source = "MOBILE_APP")
    {
        try
        {
            var audit = new GeoPunchAudit
            {
                EmployeeId = employeeId,
                SessionId = sessionId,
                PunchTimeUtc = punchTimeUtc,
                Latitude = latitude,
                Longitude = longitude,
                AccuracyMeters = accuracyMeters,
                DistanceFromOfficeMeters = distanceMeters,
                AllowedRadiusMeters = allowedRadiusMeters,
                IsWithinAllowedRadius = withinRadius,
                Success = result.Success,
                ResultMessage = result.Message,
                Source = source,
                AttendanceLogId = attendanceLogId
            };

            db.GeoPunchAudits.Add(audit);

            await SaveChangesWithSqliteRetryAsync(db);

            // Firebase is the realtime SSOT for connected Web/Android clients.
            // Publish only after the local audit row has committed so the
            // browser never receives an audit event for a failed transaction.
            // This is transport synchronization only and does not alter the
            // punch calculation, audit values, schema, or existing UI flow.
            try
            {
                var ownerUid = _firebase.ResolveOwnerUid($"employee-{employeeId}", "Employee");

                if (audit.Id > 0)
                {
                    await _firebase.SetOwnerRecordAsync(
                        ownerUid,
                        "geo_punch_audits",
                        audit.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        audit);
                }
            }
            catch (Exception firebaseEx)
            {
                // Firebase realtime publication is best-effort. The committed
                // audit row remains authoritative if Firebase is temporarily
                // unavailable; the existing sync path can reconcile it later.
                _logger.LogWarning(
                    firebaseEx,
                    "Geo punch audit committed locally but Firebase realtime publication failed. EmployeeId={EmployeeId}, AuditId={AuditId}",
                    employeeId,
                    audit.Id);
            }

            try
            {
                await _hubContext.Clients.All.SendAsync(
                    "GeoPunchAuditChanged",
                    new
                    {
                        EmployeeId = employeeId,
                        SessionId = sessionId.ToString(),
                        AuditId = audit.Id,
                        PunchTimeUtc = punchTimeUtc,
                        IsWithinAllowedRadius = withinRadius,
                        ResultMessage = result.Message
                    });

                await _refreshService.NotifyLocationChangedAsync(employeeId);
            }
            catch (Exception signalREx)
            {
                _logger.LogWarning(signalREx, "Failed to broadcast GeoPunchAuditChanged event via SignalR. EmployeeId={EmployeeId}", employeeId);
            }
        }
        catch (Exception ex)
        {
            /*
             * Audit failure must NEVER break a valid punch.
             */
            _logger.LogError(
                ex,
                "Failed to save GPS punch audit for employee {EmployeeId}",
                employeeId);
        }
    }

    // ================================================================
    // NORMALIZE GPS ACCURACY
    // ================================================================

    private static double NormalizeAccuracy(
        double value)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value < 0)
        {
            return 0;
        }

        return value;
    }


    private static readonly TimeZoneInfo IndiaTimeZone =
    GetIndiaTimeZone();

    private static TimeZoneInfo GetIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "India Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "India Standard Time");
        }
    }

    private static DateTime GetIndiaNow()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            IndiaTimeZone);
    }

    // ================================================================
    // NORMALIZE DISTANCE
    // ================================================================

    private static double NormalizeDistance(
        double value)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value < 0)
        {
            return 0;
        }

        return value;
    }

    // ================================================================
    // VALIDATE GPS COORDINATE
    // ================================================================

    private static bool IsValidCoordinate(
        double latitude,
        double longitude)
    {
        return
            !double.IsNaN(latitude) &&
            !double.IsNaN(longitude) &&
            !double.IsInfinity(latitude) &&
            !double.IsInfinity(longitude) &&
            latitude >= -90 &&
            latitude <= 90 &&
            longitude >= -180 &&
            longitude <= 180 &&
            !(latitude == 0.0 && longitude == 0.0);
    }

    // ================================================================
    // HAVERSINE DISTANCE
    // ================================================================

    private static double CalculateDistance(
        double lat1,
        double lon1,
        double lat2,
        double lon2)
    {
        const double earthRadiusMeters =
            6371e3;

        var rLat1 =
            lat1 *
            Math.PI /
            180;

        var rLat2 =
            lat2 *
            Math.PI /
            180;

        var dLat =
            (lat2 - lat1) *
            Math.PI /
            180;

        var dLon =
            (lon2 - lon1) *
            Math.PI /
            180;

        var a =
            Math.Sin(dLat / 2) *
            Math.Sin(dLat / 2) +
            Math.Cos(rLat1) *
            Math.Cos(rLat2) *
            Math.Sin(dLon / 2) *
            Math.Sin(dLon / 2);

        var c =
            2 *
            Math.Atan2(
                Math.Sqrt(a),
                Math.Sqrt(1 - a));

        return
            earthRadiusMeters *
            c;
    }
}

// ====================================================================
// GEO PUNCH RESULT
// ====================================================================

public class GeoPunchResult
{
    public bool Success { get; set; }

    public string Message { get; set; } =
        string.Empty;
}

// ====================================================================
// GEO DISTANCE RESULT
// ====================================================================

public class GeoDistanceResult
{
    public bool Success { get; set; }

    public string Message { get; set; } =
        string.Empty;

    public double DistanceMeters { get; set; }

    public int AllowedRadiusMeters { get; set; }

    public bool IsWithinAllowedRadius =>
        Success &&
        DistanceMeters <= AllowedRadiusMeters;
}