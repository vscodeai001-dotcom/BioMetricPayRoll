using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Security;

public sealed class MobileTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly MobileEmployeeTokenService _tokens;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly FirebaseEmployeeManagementService _employeeManagement;

    public MobileTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        MobileEmployeeTokenService tokens,
        IDbContextFactory<AppDbContext> dbFactory,
        FirebaseRealtimeService firebase,
        FirebaseEmployeeManagementService employeeManagement)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _dbFactory = dbFactory;
        _firebase = firebase;
        _employeeManagement = employeeManagement;
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

        // Preserve the existing opaque mobile-token path unchanged.
        if (_tokens.TryRead(token, out var payload))
            return await AuthenticateOpaqueTokenAsync(payload);

        // Firebase-native Employee sessions use the Firebase ID token directly.
        // This keeps all existing Employee controllers/screens while removing
        // the Web login/session bridge from Android authentication.
        var firebaseToken = await _firebase.VerifyIdTokenAsync(
            token,
            checkRevoked: true,
            Context.RequestAborted);

        if (firebaseToken == null)
        {
            Response.Headers["X-Mobile-Session-State"] = "REAUTH_REQUIRED";
            return AuthenticateResult.Fail("Invalid or expired mobile session.");
        }

        var uid = firebaseToken.Uid;
        var email = firebaseToken.Claims.TryGetValue("email", out var emailValue)
            ? emailValue?.ToString()
            : null;
        var role = firebaseToken.Claims.TryGetValue("role", out var roleValue)
            ? roleValue?.ToString() ?? "Employee"
            : "Employee";
        var employeeId = firebaseToken.Claims.TryGetValue("employee_id", out var employeeValue)
            ? Convert.ToInt32(employeeValue)
            : 0;
        var ownerUid = firebaseToken.Claims.TryGetValue("owner_uid", out var ownerValue)
            ? ownerValue?.ToString()
            : null;

        // Firebase-native authentication resolves the employee projection from
        // the Firebase SSOT. SQL remains the compatibility authority only for
        // the legacy opaque-token path below.
        // A Firebase token is cryptographically verified, but tenant scope and
        // Employee lifecycle state still have to be checked against the server
        // side SSOT before issuing an application principal.
        var expectedOwnerUid = _employeeManagement.OwnerUid;
        if (!string.IsNullOrWhiteSpace(ownerUid) &&
            !string.Equals(ownerUid, expectedOwnerUid, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Firebase session belongs to a different owner scope.");
        }

        var employee = employeeId > 0
            ? await _employeeManagement.GetEmployeeAsync(employeeId, Context.RequestAborted)
            : null;

        if (employee == null && !string.IsNullOrWhiteSpace(email))
        {
            employee = await _employeeManagement.GetEmployeeByEmailAsync(email, Context.RequestAborted);
            employeeId = employee?.EmployeeID ?? employeeId;
        }

        var isAdmin = role.Contains("Admin", StringComparison.OrdinalIgnoreCase);
        if (employee == null && !isAdmin)
            return AuthenticateResult.Fail("Employee session is invalid or not linked.");

        if (!isAdmin && employee?.IsDeleted == true)
            return AuthenticateResult.Fail("Employee account is disabled.");

        if (!isAdmin && (employee is null || employee.EmployeeID != employeeId))
            return AuthenticateResult.Fail("Firebase Employee claim does not match the Employee record.");

        if (!isAdmin)
        {
            var sessionPath = $"employee_sessions/{uid}";
            var session = await _firebase.GetGlobalRecordAsync(sessionPath, Context.RequestAborted);
            if (session == null || session.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                return AuthenticateResult.Fail("Employee Firebase session is not active on this device.");

            var sessionDevice = session.Value.TryGetProperty("deviceId", out var deviceElement)
                ? deviceElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(sessionDevice))
                return AuthenticateResult.Fail("Employee Firebase session is not active on this device.");

            // The client supplies its Android ID as the compatibility device
            // claim on protected calls. Existing screens do not change.
            var requestedDevice = Request.Headers["X-Android-Device-Id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requestedDevice) &&
                !string.Equals(sessionDevice, requestedDevice, StringComparison.Ordinal))
                return AuthenticateResult.Fail("Mobile session is no longer active on this device.");
        }

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, uid),
            new Claim(ClaimTypes.Name, employee?.Name ?? email ?? "Administrator"),
            new Claim(ClaimTypes.Role, role),
            new Claim("employee_id", (employee?.EmployeeID ?? employeeId).ToString()),
            new Claim("mobile_session", "true")
        };
        var firebaseDeviceId = Request.Headers["X-Android-Device-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firebaseDeviceId))
        {
            claims.Add(new Claim("device_id", firebaseDeviceId));
            claims.Add(new Claim("BioMetric-Employee-Device", firebaseDeviceId));
        }
        if (!string.IsNullOrWhiteSpace(email)) claims.Add(new Claim(ClaimTypes.Email, email));
        if (!string.IsNullOrWhiteSpace(ownerUid)) claims.Add(new Claim("owner_uid", ownerUid));
        if (!string.IsNullOrWhiteSpace(employee?.Email)) claims.Add(new Claim("employee_email", employee.Email));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private async Task<AuthenticateResult> AuthenticateOpaqueTokenAsync(MobileEmployeeTokenPayload payload)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var lockRecord = await db.EmployeeDeviceLocks
            .FirstOrDefaultAsync(x => x.UserId == payload.UserId, Context.RequestAborted);

        if (lockRecord == null)
            return AuthenticateResult.Fail("Mobile session is no longer active on this device.");

        const string mobilePrefix = "ANDROID:";
        var normalizedPayloadDeviceId = payload.DeviceId.StartsWith(mobilePrefix, StringComparison.OrdinalIgnoreCase)
            ? payload.DeviceId
            : mobilePrefix + payload.DeviceId;

        var deviceMatches =
            string.Equals(lockRecord.DeviceId, payload.DeviceId, StringComparison.Ordinal) ||
            string.Equals(lockRecord.DeviceId, normalizedPayloadDeviceId, StringComparison.Ordinal);

        if (!deviceMatches)
            return AuthenticateResult.Fail("Mobile session is no longer active on this device.");

        lockRecord.LastSeenAtUtc = DateTime.UtcNow;
        if (!string.Equals(lockRecord.DeviceId, normalizedPayloadDeviceId, StringComparison.Ordinal))
            lockRecord.DeviceId = normalizedPayloadDeviceId;
        await db.SaveChangesAsync(CancellationToken.None);

        var employee = await db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EmployeeID == payload.EmployeeId && !x.IsDeleted, Context.RequestAborted);

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
