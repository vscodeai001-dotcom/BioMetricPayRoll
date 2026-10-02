using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Payroll.Web.Services;
using Payroll.Web.Hubs;

namespace Payroll.Web.Controllers;

/// <summary>
/// HTTP API endpoint for employee GPS location updates.
/// 
/// This endpoint handles GPS location data sent directly from the browser's
/// JavaScript GPS tracker when the Blazor circuit is disconnected or unavailable.
/// 
/// Used for:
/// - Background GPS tracking (tab inactive)
/// - Network recovery (offline -> online)
/// - Circuit disconnection recovery
/// 
/// The JavaScript GPS tracker will use this endpoint as a fallback when
/// JSInterop callbacks to Blazor are not available.
/// </summary>
[ApiController]
[Route("api/employee-location")]
[Authorize(Roles = "Employee,Admin,SuperAdmin")]
public sealed class EmployeeLocationController : ControllerBase
{
    private readonly GeoLocationService _geoLocationService;
    private readonly IHubContext<AttendanceRefreshHub> _attendanceHub;
    private readonly FirebaseEmployeeManagementService _firebaseEmployees;
    private readonly ILogger<EmployeeLocationController> _logger;

    public EmployeeLocationController(
        GeoLocationService geoLocationService,
        IHubContext<AttendanceRefreshHub> attendanceHub,
        FirebaseEmployeeManagementService firebaseEmployees,
        ILogger<EmployeeLocationController> logger)
    {
        _geoLocationService = geoLocationService;
        _attendanceHub = attendanceHub;
        _firebaseEmployees = firebaseEmployees;
        _logger = logger;
    }

    /// <summary>
    /// Receive GPS location update from employee's browser.
    /// 
    /// This is called by the JavaScript GPS tracker when:
    /// 1. Blazor circuit is disconnected
    /// 2. JSInterop callback fails
    /// 3. Browser has lost connection to server
    /// 4. Tab is inactive (background tracking)
    /// 
    /// The endpoint updates the in-memory location store and database session,
    /// then broadcasts the update via SignalR so admin dashboards refresh immediately.
    /// </summary>
    [HttpPost("update")]
    public async Task<IActionResult> UpdateLocation(
        [FromBody] LocationUpdateRequest request)
    {
        if (request == null)
        {
            return BadRequest("Location data is required.");
        }

        _logger.LogDebug("Received GPS update: EmployeeId={EmployeeId}, SessionId={SessionId}", request.EmployeeId, request.SessionId);

        try
        {
            // ============================================================
            // VALIDATE REQUEST
            // ============================================================

            if (request.EmployeeId <= 0)
            {
                return BadRequest("EmployeeId must be positive.");
            }

            // Employee callers may only submit GPS for their own employee record.
            // Admin/SuperAdmin retain the existing ability to operate on any employee.
            if (User.IsInRole("Employee"))
            {
                var claimedEmployeeId = 0;
                var claimValue = User.FindFirst("employee_id")?.Value
                                ?? User.FindFirst("EmployeeID")?.Value;
                if (int.TryParse(claimValue, out var cid) && cid > 0)
                {
                    claimedEmployeeId = cid;
                }
                else
                {
                    // Fallback for existing sessions where the cookie lacks the employee_id claim:
                    // Match the authenticated user's email against the employee repository
                    var email = User.Identity?.Name ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        var emp = await _firebaseEmployees.GetEmployeeByEmailAsync(email);
                        if (emp != null && emp.EmployeeID > 0)
                        {
                            claimedEmployeeId = emp.EmployeeID;
                        }
                    }
                }

                if (claimedEmployeeId <= 0 || claimedEmployeeId != request.EmployeeId)
                {
                    _logger.LogWarning(
                        "Employee location update forbidden. ClaimedId={ClaimedId}, RequestId={RequestId}, User={User}",
                        claimedEmployeeId, request.EmployeeId, User.Identity?.Name);
                    return Forbid();
                }
            }

            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return BadRequest("SessionId is required.");
            }

            if (!Guid.TryParse(request.SessionId, out var sessionId) ||
                sessionId == Guid.Empty)
            {
                return BadRequest("SessionId must be a valid GUID.");
            }

            if (!double.IsFinite(request.Latitude) ||
                !double.IsFinite(request.Longitude))
            {
                return BadRequest("Latitude and Longitude must be valid numbers.");
            }

            if (request.Latitude < -90 || request.Latitude > 90 ||
                request.Longitude < -180 || request.Longitude > 180)
            {
                return BadRequest("Coordinates out of valid range.");
            }

            var accuracy = request.Accuracy >= 0 ? request.Accuracy : 0;

            // Preserve the browser GPS capture time for ordering live fixes.
            // A missing timestamp falls back to server time; a clock-skewed
            // future timestamp is normalized by the service rather than used
            // to make a location appear newer than it really is.
            var capturedAtUtc = request.Timestamp.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(request.Timestamp, DateTimeKind.Utc)
                : request.Timestamp.ToUniversalTime();

            var serverNowUtc = DateTime.UtcNow;
            var captureAge = serverNowUtc - capturedAtUtc;
            if (captureAge < TimeSpan.Zero) captureAge = TimeSpan.Zero;

            // A current GPS packet may establish/recover an ACTIVE session.
            // Offline/historical evidence may be stored for audit/reconciliation
            // but can never create or resurrect an ACTIVE session.
            var historicalOrOffline = request.IsOfflineCapture ||
                                      captureAge > TimeSpan.FromMinutes(2);
            var allowSessionRecovery = !historicalOrOffline;

            // Log authenticated user information for diagnostics
            try
            {
                var userIdClaim = User?.FindFirst("sub")?.Value ?? User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                _logger.LogDebug("UpdateLocation called. AuthUserId={AuthUserId}, RequestEmployeeId={RequestEmployeeId}, SessionId={SessionId}",
                    userIdClaim,
                    request.EmployeeId,
                    request.SessionId);
            }
            catch { }

            // ============================================================
            // GET DISTANCE FROM OFFICE
            // ============================================================

            var distanceResult =
                await _geoLocationService.GetDistanceFromOfficeAsync(
                    request.Latitude,
                    request.Longitude);

            if (!distanceResult.Success)
            {
                _logger.LogWarning(
                    "GPS location validation failed. " +
                    "EmployeeId={EmployeeId}, " +
                    "Message={Message}",
                    request.EmployeeId,
                    distanceResult.Message);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "Unable to validate location.");
            }

            // ============================================================
            // HISTORICAL/OFFLINE GPS NEVER MUTATES LIVE SESSION STATE
            // ============================================================
            if (historicalOrOffline)
            {
                if (request.PersistHistory)
                {
                    await _geoLocationService.SaveLocationHistoryAsync(
                        request.EmployeeId,
                        sessionId,
                        request.Latitude,
                        request.Longitude,
                        distanceResult.DistanceMeters,
                        distanceResult.AllowedRadiusMeters,
                        distanceResult.IsWithinAllowedRadius,
                        accuracy,
                        capturedAtUtc,
                        "OfflineSync",
                        Guid.NewGuid());

                    // Use historical GPS evidence to repair missing geofence
                    // attendance inside the original session window. This does
                    // not reopen or create an ACTIVE GPS session.
                    await _geoLocationService.ReconcileHistoricalGeofencePointAsync(
                        request.EmployeeId,
                        sessionId,
                        request.Latitude,
                        request.Longitude,
                        accuracy,
                        distanceResult.DistanceMeters,
                        distanceResult.AllowedRadiusMeters,
                        distanceResult.IsWithinAllowedRadius,
                        capturedAtUtc);
                }

                return Ok(new LocationUpdateResponse
                {
                    Success = true,
                    HistoricalOnly = true,
                    Live = false,
                    SessionId = sessionId.ToString("D"),
                    Message = "Offline GPS evidence stored for reconciliation.",
                    Timestamp = DateTime.UtcNow
                });
            }

            // ============================================================
            // CURRENT GPS SESSION UPDATE
            // ============================================================
            var sessionUpdated =
                await _geoLocationService.UpdateGpsSessionAsync(
                    request.EmployeeId,
                    sessionId,
                    request.Latitude,
                    request.Longitude,
                    accuracy,
                    distanceResult.DistanceMeters,
                    distanceResult.AllowedRadiusMeters,
                    distanceResult.IsWithinAllowedRadius,
                    capturedAtUtc,
                    allowSessionRecovery: true);

            if (!sessionUpdated)
            {
                _logger.LogDebug(
                    "Current GPS update rejected because the session is not active. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    request.EmployeeId,
                    sessionId);

                return Conflict("GPS session is no longer active.");
            }

            var effectiveActiveSession =
                await _geoLocationService.GetActiveGpsSessionAsync(request.EmployeeId);

            var effectiveSessionId =
                effectiveActiveSession?.SessionId ?? sessionId;

            // ============================================================
            // SAVE LOCATION HISTORY
            // ============================================================

            if (request.PersistHistory)
            {
                try
                {
                    await _geoLocationService.SaveLocationHistoryAsync(
                        request.EmployeeId,
                        effectiveSessionId,
                        request.Latitude,
                        request.Longitude,
                        distanceResult.DistanceMeters,
                        distanceResult.AllowedRadiusMeters,
                        distanceResult.IsWithinAllowedRadius,
                        accuracy,
                        capturedAtUtc,
                        "Online");
                }
                catch (Exception historyEx)
                {
                    _logger.LogError(
                        historyEx,
                        "GPS history save failed. " +
                        "EmployeeId={EmployeeId}",
                        request.EmployeeId);

                    // Continue anyway - session update succeeded
                }
            }

            _logger.LogInformation(
                "GPS location updated via HTTP API. " +
                "EmployeeId={EmployeeId}, " +
                "Lat={Latitude}, " +
                "Lon={Longitude}, " +
                "Distance={Distance}m",
                request.EmployeeId,
                Math.Round(request.Latitude, 6),
                Math.Round(request.Longitude, 6),
                Math.Round(distanceResult.DistanceMeters, 1));

            return Ok(new LocationUpdateResponse
            {
                Success = true,
                Live = true,
                HistoricalOnly = false,
                SessionId = effectiveSessionId.ToString("D"),
                Message = "Location updated successfully.",
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "GPS location update failed. " +
                "EmployeeId={EmployeeId}",
                request.EmployeeId);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "An error occurred while processing the location update.");
        }
    }

    /// <summary>
    /// Lightweight keepalive endpoint used by the browser to
    /// touch the authenticated session without sending location data.
    /// Accepts HEAD/GET requests and returns 200 OK so that client-side
    /// keepalive pings refresh the authentication cookie sliding expiration.
    /// </summary>
    [HttpHead("keepalive")]
    [HttpGet("keepalive")]
    public IActionResult KeepAlive()
    {
        return Ok();
    }

    // ============================================================
    // REQUEST / RESPONSE MODELS
    // ============================================================

    public class LocationUpdateRequest
    {
        /// <summary>
        /// Employee ID from database (from JWT claim or session)
        /// </summary>
        public int EmployeeId { get; set; }

        /// <summary>
        /// Unique GPS session identifier (UUID)
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Latitude coordinate (WGS84)
        /// </summary>
        public double Latitude { get; set; }

        /// <summary>
        /// Longitude coordinate (WGS84)
        /// </summary>
        public double Longitude { get; set; }

        /// <summary>
        /// GPS accuracy in meters (reported by browser)
        /// </summary>
        public double Accuracy { get; set; }

        /// <summary>
        /// Timestamp when location was captured
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>True when the point was captured while offline or is a queued historical replay.</summary>
        public bool IsOfflineCapture { get; set; }

        /// <summary>True when this point should be persisted into GPS playback/history.</summary>
        public bool PersistHistory { get; set; } = true;
    }

    public class LocationUpdateResponse
    {
        public bool Success { get; set; }

        public bool Live { get; set; }

        public bool HistoricalOnly { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}

// Diagnostic endpoints for admin debugging only
[ApiController]
[Route("api/diagnostics")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class DiagnosticsController : ControllerBase
{
    [HttpGet("live-locations")]
    public IActionResult GetLiveLocations()
    {
        try
        {
            var list = LiveLocationStore.GetAll().Select(x => new
            {
                x.EmployeeId,
                x.Latitude,
                x.Longitude,
                x.LastUpdatedUtc,
                x.SessionStartedUtc,
                x.SessionId
            }).ToList();

            return Ok(list);
        }
        catch
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
