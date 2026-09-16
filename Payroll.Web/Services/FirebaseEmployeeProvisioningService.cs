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
        // Firebase claims are the long-lived bridge between the Firebase Auth
        // account and the existing payroll employee record. This reconciliation
        // intentionally keeps running so Firebase Console-created users are
        // provisioned even when they are added after Web startup.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

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

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
                firebaseUser = await auth.GetUserByEmailAsync(email, ct);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                // Do not create an account here because this service must never
                // invent or persist an employee password. User Management or a
                // successful one-time password migration creates the Auth user.
                continue;
            }

            await auth.SetCustomUserClaimsAsync(
                firebaseUser.Uid,
                new Dictionary<string, object>
                {
                    ["role"] = "Employee",
                    ["employee_id"] = employee.EmployeeID,
                    ["owner_uid"] = ownerUid
                },
                ct);

            count++;
        }

        await ProvisionAdministrativeUsersAsync(users, auth, ownerUid, ct);

        _logger.LogInformation(
            "Firebase claim provisioning cycle completed. Employees provisioned: {Count}.",
            count);
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
                firebaseUser = await auth.GetUserByEmailAsync(identity.Email.Trim(), ct);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                // Never invent an administrative password here. The account
                // must first exist in Firebase Authentication.
                continue;
            }

            await auth.SetCustomUserClaimsAsync(
                firebaseUser.Uid,
                new Dictionary<string, object>
                {
                    ["role"] = role,
                    ["owner_uid"] = ownerUid
                },
                ct);
        }
    }

}
