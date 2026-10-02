using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// One-time/backfill provisioning for Firebase Console-created Employee Auth
/// accounts. It adds the canonical employee_id/role/owner_uid claims without
/// changing passwords or payroll data.
/// </summary>
public sealed class FirebaseEmployeeProvisioningService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseEmployeeProvisioningService> _logger;

    public FirebaseEmployeeProvisioningService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseEmployeeProvisioningService> logger)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup slightly so it does not collide with SuperAdmin provisioning
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProvisionLinkedEmployeesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase Employee claim provisioning cycle failed; the next cycle will retry.");
            }

            // Periodic claim check every 2 minutes instead of aggressive 30s hammering
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    private async Task ProvisionLinkedEmployeesAsync(CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
            throw new InvalidOperationException("Firebase Admin SDK is not configured.");

        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var employees = await db.Employees.AsNoTracking()
            .Where(x => !x.IsDeleted && x.EmployeeID > 0 && x.Email != null && x.Email != "")
            .ToListAsync(ct);

        var ownerUid = _configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")
            ?? "biometricpayroll";

        var count = 0;
        foreach (var employee in employees)
        {
            ct.ThrowIfCancellationRequested();
            var email = employee.Email!.Trim();

            // The payroll Employee row is the canonical link for an Employee.
            // Identity is consulted only to avoid accidentally stamping an
            // Admin/SuperAdmin account as Employee. Firebase Console-created
            // users do not need an Identity row for this claim backfill.
            IdentityUser? identity = null;
            if (!string.IsNullOrWhiteSpace(employee.AspNetUserId))
                identity = await users.FindByIdAsync(employee.AspNetUserId);
            identity ??= await users.FindByEmailAsync(email);

            if (identity != null)
            {
                var identityRoles = await users.GetRolesAsync(identity);
                if (identityRoles.Any(role =>
                        role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                        role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            UserRecord? firebaseUser;
            try
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(10));
                firebaseUser = await auth.GetUserByEmailAsync(email, opCts.Token);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve Firebase user for employee {Email}.", email);
                continue;
            }

            if (firebaseUser == null) continue;

            // Resolve which tenant this employee belongs to
            string targetOwnerUid = employee.TenantId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(targetOwnerUid) &&
                firebaseUser.CustomClaims != null &&
                firebaseUser.CustomClaims.TryGetValue("owner_uid", out var existingOwner) &&
                !string.IsNullOrWhiteSpace(existingOwner?.ToString()))
            {
                targetOwnerUid = existingOwner.ToString()!.Trim();
            }

            if (string.IsNullOrWhiteSpace(targetOwnerUid))
            {
                targetOwnerUid = _firebase.ResolveOwnerUid(employee.Email, "Employee");
            }

            if (string.IsNullOrWhiteSpace(targetOwnerUid))
            {
                targetOwnerUid = Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid;
            }

            // PERFORMANCE OPTIMIZATION: Check if claims are already accurately set.
            // Avoids making redundant Google Auth API write calls every cycle.
            if (firebaseUser.CustomClaims != null &&
                firebaseUser.CustomClaims.TryGetValue("role", out var r) && r?.ToString() == "Employee" &&
                firebaseUser.CustomClaims.TryGetValue("employee_id", out var eid) && Convert.ToInt32(eid) == employee.EmployeeID &&
                firebaseUser.CustomClaims.TryGetValue("owner_uid", out var ouid) && string.Equals(ouid?.ToString(), targetOwnerUid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(10));
                await auth.SetCustomUserClaimsAsync(
                    firebaseUser.Uid,
                    new Dictionary<string, object>
                    {
                        ["role"] = "Employee",
                        ["employee_id"] = employee.EmployeeID,
                        ["owner_uid"] = targetOwnerUid
                    },
                    opCts.Token);

                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set claims for employee {Email}.", email);
            }
        }

        await ProvisionAdministrativeUsersAsync(users, auth, ownerUid, ct);

        if (count > 0)
        {
            _logger.LogInformation(
                "Firebase claim provisioning cycle completed. Employees updated: {Count}.",
                count);
        }
    }

    private async Task ProvisionAdministrativeUsersAsync(
        UserManager<IdentityUser> users,
        FirebaseAuth auth,
        string ownerUid,
        CancellationToken ct)
    {
        var identities = await users.Users.AsNoTracking().ToListAsync(ct);

        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();

            var roles = await users.GetRolesAsync(identity);
            var role = roles.FirstOrDefault(r =>
                r.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("Admin", StringComparison.OrdinalIgnoreCase));

            if (role == null || string.IsNullOrWhiteSpace(identity.Email))
                continue;

            UserRecord firebaseUser;
            try
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(10));
                firebaseUser = await auth.GetUserByEmailAsync(identity.Email.Trim(), opCts.Token);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve Firebase user for admin {Email}.", identity.Email);
                continue;
            }

            if (firebaseUser == null) continue;

            // Skip if already matching
            if (firebaseUser.CustomClaims != null &&
                firebaseUser.CustomClaims.TryGetValue("role", out var r) && r?.ToString() == role &&
                firebaseUser.CustomClaims.TryGetValue("owner_uid", out var ouid) && ouid?.ToString() == ownerUid)
            {
                continue;
            }

            try
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(10));
                await auth.SetCustomUserClaimsAsync(
                    firebaseUser.Uid,
                    new Dictionary<string, object>
                    {
                        ["role"] = role,
                        ["owner_uid"] = ownerUid
                    },
                    opCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set claims for admin {Email}.", identity.Email);
            }
        }
    }

}
