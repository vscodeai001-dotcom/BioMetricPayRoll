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
[Authorize]
public sealed class EmployeeLocationController : ControllerBase
{
    private readonly GeoLocationService _geoLocationService;
    private readonly IHubContext<AttendanceRefreshHub> _attendanceHub;
    private readonly ILogger<EmployeeLocationController> _logger;

    public EmployeeLocationController(
        GeoLocationService geoLocationService,
        IHubContext<AttendanceRefreshHub> attendanceHub,
        ILogger<EmployeeLocationController> logger)
    {
        _geoLocationService = geoLocationService;
        _attendanceHub = attendanceHub;
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
        _logger.LogDebug("Received GPS update: EmployeeId={EmployeeId}, SessionId={SessionId}", request.EmployeeId, request.SessionId);
        if (request == null)
        {
            return BadRequest("Location data is required.");
        }

        try
        {
            // ============================================================
            // VALIDATE REQUEST
            // ============================================================

            if (request.EmployeeId <= 0)
            {
                return BadRequest("EmployeeId must be positive.");
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
            // AUTHORITATIVE GPS SESSION UPDATE
            //
            // GeoLocationService validates the active DB session before
            // updating LiveLocationStore. This prevents an in-flight GPS
            // request from resurrecting a logged-out employee.
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
                    distanceResult.IsWithinAllowedRadius);

            if (!sessionUpdated)
            {
                _logger.LogDebug(
                    "GPS update ignored because the session is no longer active. EmployeeId={EmployeeId}, SessionId={SessionId}",
                    request.EmployeeId,
                    sessionId);

                return Conflict("GPS session is no longer active.");
            }

            // ============================================================
            // SAVE LOCATION HISTORY
            // ============================================================

            try
            {
                await _geoLocationService.SaveLocationHistoryAsync(
                    request.EmployeeId,
                    sessionId,
                    request.Latitude,
                    request.Longitude,
                    distanceResult.DistanceMeters,
                    distanceResult.AllowedRadiusMeters,
                    distanceResult.IsWithinAllowedRadius,
                    accuracy);
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
    }

    public class LocationUpdateResponse
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}

// Diagnostic endpoints for admin debugging only
[ApiController]
[Route("api/diagnostics")]
[Authorize]
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
