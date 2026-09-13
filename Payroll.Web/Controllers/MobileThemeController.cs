using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/employee/theme")]
[Authorize(AuthenticationSchemes = "MobileBearer", Roles = "Employee,Admin,SuperAdmin")]
public sealed class MobileThemeController : ControllerBase
{
    private readonly ThemeService _themeService;

    public MobileThemeController(ThemeService themeService)
        => _themeService = themeService;

    [HttpGet]
    public async Task<ActionResult<MobileThemeDto>> Get(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var theme = await _themeService.GetThemeAsync(userId) ?? "light";
        return Ok(new MobileThemeDto(theme));
    }

    [HttpPut]
    public async Task<ActionResult<MobileThemeDto>> Save(
        [FromBody] MobileThemeRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var theme = request?.Theme?.Equals("dark", StringComparison.OrdinalIgnoreCase) == true
            ? "dark"
            : "light";

        await _themeService.SaveThemeAsync(userId, theme);
        return Ok(new MobileThemeDto(theme));
    }
}

public sealed record MobileThemeRequest(string? Theme);
public sealed record MobileThemeDto(string Theme);
