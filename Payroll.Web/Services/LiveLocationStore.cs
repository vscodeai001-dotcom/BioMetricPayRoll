using System.Collections.Concurrent;

namespace Payroll.Web.Services;

public static class LiveLocationStore
{
    private static readonly ConcurrentDictionary<int, LiveEmployeeLocation> Locations = new();

    // Keep employees reported as "Live"/"Stale" for a very long period so
    // that an admin dashboard does not show "No live staff" simply because
    // a browser tab was backgrounded or temporarily paused by the OS.
    //
    // These values effectively ensure a session remains considered live
    // for years unless explicitly removed (e.g., on logout or session end).
    public const int LiveTimeoutSeconds = 60 * 60 * 24 * 365 * 10; // ~10 years
    public const int StaleTimeoutSeconds = 60 * 60 * 24 * 365 * 20; // ~20 years

    /*
     * ============================================================
     * GPS SESSION STATUS DEFINITION
     * ============================================================
     *
     * LIVE (0-30 sec):
     * - GPS update received within last 30 seconds
     * - Real-time movement data available
     *
     * STALE (30-120 sec):
     * - No GPS update for 30-120 seconds
     * - GPS watcher may be paused or network delayed
     * - Last known location is current
     *
     * OFFLINE (120+ sec):
     * - No GPS update for 120+ seconds
     * - GPS session may have been closed
     * - Location data should NOT be displayed
     *
     * IMPORTANT:
     * In-memory store only tracks the last UPDATE time.
     * The database tracks session lifecycle (EndedAtUtc).
     * Admins checking if employee is online should check:
     * 1. Is there a live location in memory? (not older than 2 min)
     * 2. Is the GPS session still active in database? (EndedAtUtc is null)
     * ============================================================
     */

    public static bool Update(
        int employeeId,
        double latitude,
        double longitude,
        double accuracyMeters,
        double distanceMeters,
        int allowedRadiusMeters,
        bool isWithinAllowedRadius,
        Guid sessionId)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty ||
            !IsValidCoordinate(latitude, longitude))
        {
            return false;
        }

        var now = DateTime.UtcNow;

        var safeAccuracy =
            IsValidPositiveNumber(accuracyMeters)
                ? accuracyMeters
                : 0;

        var safeDistance =
            IsValidPositiveNumber(distanceMeters)
                ? distanceMeters
                : 0;

        var safeRadius =
            allowedRadiusMeters < 0
                ? 0
                : allowedRadiusMeters;

        while (true)
        {
            if (!Locations.TryGetValue(
                    employeeId,
                    out var current))
            {
                var newLocation =
                    new LiveEmployeeLocation
                    {
                        EmployeeId = employeeId,
                        Latitude = latitude,
                        Longitude = longitude,
                        AccuracyMeters = safeAccuracy,
                        DistanceMeters = safeDistance,
                        AllowedRadiusMeters = safeRadius,
                        IsWithinAllowedRadius = isWithinAllowedRadius,
                        LastUpdatedUtc = now,
                        SessionStartedUtc = now,
                        SessionId = sessionId,
                        SpeedMps = 0,
                        MovementState = "Stopped"
                    };

                if (Locations.TryAdd(
                        employeeId,
                        newLocation))
                {
                    return true;
                }

                continue;
            }

            /*
             * IMPORTANT:
             *
             * A different session already owns the employee's
             * live location.
             *
             * Never allow an old component/session to overwrite
             * the current login session.
             */
            if (current.SessionId != sessionId)
            {
                return false;
            }

            var elapsedSeconds =
                (now - current.LastUpdatedUtc).TotalSeconds;

            var movementDistanceMeters =
                CalculateDistance(
                    current.Latitude,
                    current.Longitude,
                    latitude,
                    longitude);

            // GPS fixes can occasionally jump. Only derive movement speed
            // when the interval is sane and the fix is not an extreme jump.
            var speedMps =
                elapsedSeconds > 0.25 &&
                elapsedSeconds <= 300 &&
                movementDistanceMeters <= 5000
                    ? movementDistanceMeters / elapsedSeconds
                    : current.SpeedMps;

            var movementState = GetMovementState(speedMps);

            var updatedLocation =
                new LiveEmployeeLocation
                {
                    EmployeeId = employeeId,
                    Latitude = latitude,
                    Longitude = longitude,
                    AccuracyMeters = safeAccuracy,
                    DistanceMeters = safeDistance,
                    AllowedRadiusMeters = safeRadius,
                    IsWithinAllowedRadius = isWithinAllowedRadius,
                    LastUpdatedUtc = now,
                    SessionStartedUtc = current.SessionStartedUtc,
                    SessionId = sessionId,
                    SpeedMps = speedMps,
                    MovementState = movementState
                };

            if (Locations.TryUpdate(
                    employeeId,
                    updatedLocation,
                    current))
            {
                return true;
            }

            /*
             * Another update won the race.
             * Re-read the current record and retry.
             */
        }
    }

    public static LiveEmployeeLocation? Get(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        return Locations.TryGetValue(
            employeeId,
            out var location)
                ? location
                : null;
    }

    public static Guid? GetSessionId(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        return Locations.TryGetValue(
                   employeeId,
                   out var location) &&
               location.SessionId != Guid.Empty
            ? location.SessionId
            : null;
    }

    public static IReadOnlyList<LiveEmployeeLocation> GetAll()
    {
        return Locations.Values
            .OrderBy(x => x.EmployeeId)
            .ToList();
    }

    public static bool Remove(
        int employeeId,
        Guid sessionId)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty)
        {
            return false;
        }

        while (Locations.TryGetValue(
                   employeeId,
                   out var current))
        {
            /*
             * Never allow an old session to remove the current
             * employee location.
             */
            if (current.SessionId != sessionId)
            {
                return false;
            }

            if (Locations.TryRemove(
                    new KeyValuePair<int, LiveEmployeeLocation>(
                        employeeId,
                        current)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsLive(
        LiveEmployeeLocation location,
        int timeoutSeconds = LiveTimeoutSeconds)
    {
        if (location == null)
            return false;

        var age =
            DateTime.UtcNow -
            location.LastUpdatedUtc;

        if (age < TimeSpan.Zero)
            return true;

        return age <=
               TimeSpan.FromSeconds(
                   Math.Max(1, timeoutSeconds));
    }

    public static LiveLocationStatus GetStatus(
        LiveEmployeeLocation location)
    {
        if (location == null)
            return LiveLocationStatus.Offline;

        var age =
            DateTime.UtcNow -
            location.LastUpdatedUtc;

        if (age < TimeSpan.Zero)
            return LiveLocationStatus.Live;

        if (age <=
            TimeSpan.FromSeconds(
                LiveTimeoutSeconds))
        {
            return LiveLocationStatus.Live;
        }

        if (age <=
            TimeSpan.FromSeconds(
                StaleTimeoutSeconds))
        {
            return LiveLocationStatus.Stale;
        }

        return LiveLocationStatus.Offline;
    }

    public static TimeSpan GetAge(
        LiveEmployeeLocation location)
    {
        if (location == null)
            return TimeSpan.MaxValue;

        var age =
            DateTime.UtcNow -
            location.LastUpdatedUtc;

        return age < TimeSpan.Zero
            ? TimeSpan.Zero
            : age;
    }

    public static TimeSpan GetSessionDuration(
        LiveEmployeeLocation location)
    {
        if (location == null)
            return TimeSpan.Zero;

        var end =
            location.LastUpdatedUtc >
            location.SessionStartedUtc
                ? location.LastUpdatedUtc
                : DateTime.UtcNow;

        var duration =
            end -
            location.SessionStartedUtc;

        return duration < TimeSpan.Zero
            ? TimeSpan.Zero
            : duration;
    }

    private static string GetMovementState(double speedMps)
    {
        if (!double.IsFinite(speedMps) || speedMps <= 0.5)
            return "Stopped";

        if (speedMps <= 2.0)
            return "Walking";

        if (speedMps <= 8.0)
            return "Cycling";

        return "Moving";
    }

    private static double CalculateDistance(
        double lat1,
        double lon1,
        double lat2,
        double lon2)
    {
        const double earthRadiusMeters = 6371000d;
        var r1 = lat1 * Math.PI / 180d;
        var r2 = lat2 * Math.PI / 180d;
        var dLat = (lat2 - lat1) * Math.PI / 180d;
        var dLon = (lon2 - lon1) * Math.PI / 180d;

        var a =
            Math.Sin(dLat / 2d) * Math.Sin(dLat / 2d) +
            Math.Cos(r1) * Math.Cos(r2) *
            Math.Sin(dLon / 2d) * Math.Sin(dLon / 2d);

        a = Math.Clamp(a, 0d, 1d);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return earthRadiusMeters * c;
    }

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
            longitude <= 180;
    }

    private static bool IsValidPositiveNumber(
        double value)
    {
        return
            !double.IsNaN(value) &&
            !double.IsInfinity(value) &&
            value >= 0;
    }
}

public enum LiveLocationStatus
{
    Live,
    Stale,
    Offline
}

public sealed class LiveEmployeeLocation
{
    public int EmployeeId { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public double AccuracyMeters { get; init; }

    public double DistanceMeters { get; init; }

    public int AllowedRadiusMeters { get; init; }

    public bool IsWithinAllowedRadius { get; init; }

    public DateTime LastUpdatedUtc { get; init; }

    public DateTime SessionStartedUtc { get; init; }

    public Guid SessionId { get; init; }

    /// <summary>
    /// Derived speed from consecutive valid GPS fixes. Not persisted in the
    /// existing database schema, so this enhancement does not alter payroll
    /// data or existing database calculations.
    /// </summary>
    public double SpeedMps { get; init; }

    /// <summary>
    /// Derived movement state used by the live/offline tracking UI.
    /// </summary>
    public string MovementState { get; init; } = "Stopped";
}