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
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await ProvisionLinkedEmployeesAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Firebase Employee claim provisioning attempt {Attempt} failed; retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
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
            var identity = !string.IsNullOrWhiteSpace(employee.AspNetUserId)
                ? await users.FindByIdAsync(employee.AspNetUserId)
                : await users.FindByEmailAsync(email);

            if (identity == null)
                continue;

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

        _logger.LogInformation("Firebase Employee claim provisioning completed. Linked employees provisioned: {Count}.", count);
    }
}
