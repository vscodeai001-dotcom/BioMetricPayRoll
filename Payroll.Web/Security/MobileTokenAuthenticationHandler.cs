using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payroll.Shared.Data;

namespace Payroll.Web.Security;

public sealed class MobileTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly MobileEmployeeTokenService _tokens;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public MobileTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        MobileEmployeeTokenService tokens,
        IDbContextFactory<AppDbContext> dbFactory)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _dbFactory = dbFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
            return AuthenticateResult.NoResult();

        var header = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (!_tokens.TryRead(token, out var payload))
            return AuthenticateResult.Fail("Invalid or expired mobile session.");

        await using var db = await _dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var lockRecord = await db.EmployeeDeviceLocks
            .FirstOrDefaultAsync(x => x.UserId == payload.UserId, Context.RequestAborted);

        if (lockRecord == null)
        {
            return AuthenticateResult.Fail("Mobile session is no longer active on this device.");
        }

        const string mobilePrefix = "ANDROID:";
        var normalizedPayloadDeviceId = payload.DeviceId.StartsWith(
            mobilePrefix, StringComparison.OrdinalIgnoreCase)
            ? payload.DeviceId
            : mobilePrefix + payload.DeviceId;

        var deviceMatches =
            string.Equals(lockRecord.DeviceId, payload.DeviceId, StringComparison.Ordinal) ||
            string.Equals(lockRecord.DeviceId, normalizedPayloadDeviceId, StringComparison.Ordinal);

        if (!deviceMatches)
        {
            return AuthenticateResult.Fail("Mobile session is no longer active on this device.");
        }

        // Upgrade legacy mobile locks on the first authenticated request and
        // refresh the mobile lease. No schema change is required.
        if (!string.Equals(lockRecord.DeviceId, normalizedPayloadDeviceId, StringComparison.Ordinal))
        {
            lockRecord.DeviceId = normalizedPayloadDeviceId;
        }

        lockRecord.LastSeenAtUtc = DateTime.UtcNow;

        // Ensure the mobile heartbeat is saved even if the request is aborted
        // by a rapid socket close or poor network.
        await db.SaveChangesAsync(CancellationToken.None);

        var employee = await db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EmployeeID == payload.EmployeeId && !x.IsDeleted, Context.RequestAborted);

        // Security check for staff: they MUST be linked to an employee record.
        // Admins/SuperAdmins can use the portal even if unlinked (EmployeeId 0).
        var role = payload.Role ?? "Employee";
        var isAdmin = role.Contains("Admin", StringComparison.OrdinalIgnoreCase);

        if (employee == null && !isAdmin)
            return AuthenticateResult.Fail("Employee session is invalid or not linked.");

        if (employee != null && !string.Equals(employee.AspNetUserId, payload.UserId, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Employee link mismatch.");

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, payload.UserId),
            new Claim(ClaimTypes.Name, employee?.Name ?? "Administrator"),
            new Claim(ClaimTypes.Role, role),
            new Claim("employee_id", (employee?.EmployeeID ?? 0).ToString()),
            new Claim("device_id", normalizedPayloadDeviceId),
            new Claim("mobile_session", "true")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
