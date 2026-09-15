using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/firebase")]
public sealed class FirebaseAuthController : ControllerBase
{
    private readonly FirebaseRealtimeService _firebase;

    public FirebaseAuthController(FirebaseRealtimeService firebase)
    {
        _firebase = firebase;
    }

    /// <summary>
    /// Creates the Firebase custom token used by the browser realtime layer.
    /// This endpoint deliberately does not query Neon/PostgreSQL. The
    /// authenticated ASP.NET identity claims are already sufficient for the
    /// Firebase transport identity and role.
    ///
    /// Existing login/session flow is unchanged. This only removes an
    /// unnecessary Neon dependency from the Firebase realtime connection.
    /// </summary>
    [HttpGet("auth-token")]
    [Authorize]
    public async Task<IActionResult> GetAuthToken(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var role = User.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(role))
            role = "Employee";

        // EmployeeId is retained in the Firebase token contract for backward
        // compatibility. The existing Android employee identity remains in
        // its authenticated session; Firebase realtime access does not need
        // a fresh Neon lookup here.
        var employeeIdClaim =
            User.FindFirst("employee_id")?.Value
            ?? User.FindFirst("EmployeeId")?.Value
            ?? User.FindFirst("employeeId")?.Value;

        _ = int.TryParse(employeeIdClaim, out var employeeId);

        var token = await _firebase.CreateCustomTokenAsync(
            userId,
            employeeId,
            role,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    success = false,
                    message = "Firebase realtime bridge is not configured."
                });
        }

        return Ok(new
        {
            success = true,
            token,
            employeeId,
            role,
            ownerUid = _firebase.ResolveOwnerUid(userId, role)
        });
    }
}
