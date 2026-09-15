using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/firebase")]
public sealed class FirebaseAuthController : ControllerBase
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FirebaseRealtimeService _firebase;

    public FirebaseAuthController(
        UserManager<IdentityUser> userManager,
        IDbContextFactory<AppDbContext> dbFactory,
        FirebaseRealtimeService firebase)
    {
        _userManager = userManager;
        _dbFactory = dbFactory;
        _firebase = firebase;
    }

    [HttpGet("auth-token")]
    [Authorize]
    public async Task<IActionResult> GetAuthToken(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return Unauthorized();

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? "Employee";

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var employeeId = await db.Employees.AsNoTracking()
            .Where(x => x.AspNetUserId == user.Id && !x.IsDeleted)
            .Select(x => (int?)x.EmployeeID)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var token = await _firebase.CreateCustomTokenAsync(
            user.Id,
            employeeId,
            role,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { success = false, message = "Firebase realtime bridge is not configured." });
        }

        return Ok(new
        {
            success = true,
            token,
            employeeId,
            role,
            ownerUid = _firebase.ResolveOwnerUid(user.Id, role)
        });
    }
}
