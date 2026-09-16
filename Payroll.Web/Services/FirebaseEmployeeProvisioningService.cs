using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Background reconciliation service for Firebase Employee/Admin/SuperAdmin
/// claims.
///
/// IMPORTANT:
/// - This service does NOT generate passwords.
/// - This service does NOT change Firebase passwords.
/// - This service does NOT send credentials by email.
/// - Employee Firebase Authentication accounts are created automatically
///   during Employee creation, using the password entered by the Admin.
/// - This service only synchronizes Firebase custom claims for accounts
///   that already exist.
///
/// Firebase is the realtime synchronization/SSOT layer.
/// Existing application/database logic is preserved.
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
        // Give the application a moment to finish startup.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProvisionLinkedEmployeesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase claim provisioning cycle failed. " +
                    "The next cycle will retry automatically.");
            }

            // Background reconciliation only.
            // Realtime CRUD synchronization is handled by the Firebase
            // realtime pipeline and does not depend on this interval.
            await Task.Delay(
                TimeSpan.FromSeconds(300),
                stoppingToken);
        }
    }

    private async Task ProvisionLinkedEmployeesAsync(CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);

        if (auth == null)
        {
            throw new InvalidOperationException(
                "Firebase Admin SDK is not configured.");
        }

        using var scope = _scopeFactory.CreateScope();

        var users =
            scope.ServiceProvider
                .GetRequiredService<UserManager<IdentityUser>>();

        var factory =
            scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var db =
            await factory.CreateDbContextAsync(ct);

        var employees = await db.Employees
            .AsNoTracking()
            .Where(x =>
                !x.IsDeleted &&
                x.EmployeeID > 0 &&
                x.Email != null &&
                x.Email != "")
            .ToListAsync(ct);

        var ownerUid =
            _configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")
            ?? "biometricpayroll";

        var count = 0;

        foreach (var employee in employees)
        {
            ct.ThrowIfCancellationRequested();

            var email = employee.Email!.Trim();

            if (string.IsNullOrWhiteSpace(email))
                continue;

            /*
             * Check ASP.NET Identity only to make sure that an Admin or
             * SuperAdmin account is never accidentally assigned Employee
             * Firebase claims.
             */
            IdentityUser? identity = null;

            if (!string.IsNullOrWhiteSpace(employee.AspNetUserId))
            {
                identity = await users.FindByIdAsync(
                    employee.AspNetUserId);
            }

            identity ??= await users.FindByEmailAsync(email);

            if (identity != null)
            {
                var identityRoles =
                    await users.GetRolesAsync(identity);

                if (identityRoles.Any(role =>
                        role.Equals(
                            "Admin",
                            StringComparison.OrdinalIgnoreCase) ||
                        role.Equals(
                            "SuperAdmin",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            UserRecord firebaseUser;

            try
            {
                /*
                 * IMPORTANT:
                 *
                 * We ONLY look up an existing Firebase Auth account here.
                 *
                 * We do NOT create a random password.
                 * We do NOT log a password.
                 * We do NOT send a generated password.
                 *
                 * New employee accounts are created automatically during
                 * Employee creation using the Admin-entered password.
                 */
                firebaseUser =
                    await auth.GetUserByEmailAsync(
                        email,
                        ct);
            }
            catch (FirebaseAuthException ex)
                when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                _logger.LogInformation(
                    "Firebase Auth account does not yet exist for employee {Email}. " +
                    "Waiting for automatic provisioning during Employee creation.",
                    email);

                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to look up Firebase employee account for {Email}. " +
                    "The next reconciliation cycle will retry.",
                    email);

                continue;
            }

            /*
             * Firebase claims are the canonical bridge between the
             * Firebase Authentication user and the payroll Employee.
             */
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

        /*
         * Keep Admin and SuperAdmin claim synchronization.
         *
         * This also does NOT create passwords or Firebase accounts.
         */
        await ProvisionAdministrativeUsersAsync(
            users,
            auth,
            ownerUid,
            ct);

        _logger.LogInformation(
            "Firebase claim provisioning cycle completed. " +
            "Employees provisioned: {Count}.",
            count);
    }

    private async Task ProvisionAdministrativeUsersAsync(
        UserManager<IdentityUser> users,
        FirebaseAuth auth,
        string ownerUid,
        CancellationToken ct)
    {
        var identities =
            await users.Users
                .AsNoTracking()
                .ToListAsync(ct);

        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(identity.Email))
                continue;

            var roles =
                await users.GetRolesAsync(identity);

            var role =
                roles.FirstOrDefault(r =>
                    r.Equals(
                        "SuperAdmin",
                        StringComparison.OrdinalIgnoreCase) ||
                    r.Equals(
                        "Admin",
                        StringComparison.OrdinalIgnoreCase));

            if (role == null)
                continue;

            var email = identity.Email.Trim();

            UserRecord firebaseUser;

            try
            {
                /*
                 * Admin/SuperAdmin Firebase accounts must already exist.
                 *
                 * Never generate or change an administrative password from
                 * this background reconciliation service.
                 */
                firebaseUser =
                    await auth.GetUserByEmailAsync(
                        email,
                        ct);
            }
            catch (FirebaseAuthException ex)
                when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                _logger.LogInformation(
                    "Firebase Auth account does not exist for {Role} {Email}. " +
                    "Waiting for normal administrative provisioning.",
                    role,
                    email);

                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to look up Firebase account for {Role} {Email}. " +
                    "The next reconciliation cycle will retry.",
                    role,
                    email);

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