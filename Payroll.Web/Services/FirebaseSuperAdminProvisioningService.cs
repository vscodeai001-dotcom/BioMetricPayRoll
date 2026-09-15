using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;

namespace Payroll.Web.Services;

public sealed class FirebaseSuperAdminProvisioningService : BackgroundService
{
    private const string DefaultEmail = "prakashshiva368@gmail.com";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseSuperAdminProvisioningService> _logger;

    public FirebaseSuperAdminProvisioningService(IServiceScopeFactory scopeFactory, FirebaseRealtimeService firebase, IConfiguration configuration, ILogger<FirebaseSuperAdminProvisioningService> logger)
    {
        _scopeFactory = scopeFactory; _firebase = firebase; _configuration = configuration; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        try
        {
            var email = _configuration["Firebase:SuperAdminEmail"] ?? Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL") ?? DefaultEmail;
            var password = Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD");
            await EnsureFirebaseAuthAsync(email, password, stoppingToken);
            await EnsureLocalIdentityAsync(email, password);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "SuperAdmin provisioning failed."); }
    }

    private async Task EnsureFirebaseAuthAsync(string email, string? password, CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
        {
            _logger.LogWarning(
                "Firebase Admin SDK is not initialized. Configure Firebase:ServiceAccountPath, GOOGLE_APPLICATION_CREDENTIALS, FIREBASE_SERVICE_ACCOUNT_JSON, or Application Default Credentials.");
            return;
        }

        UserRecord? user = null;
        try { user = await auth.GetUserByEmailAsync(email, ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            if (string.IsNullOrWhiteSpace(password)) { _logger.LogWarning("Firebase SuperAdmin {Email} does not exist. Set SUPERADMIN_PASSWORD once to provision it.", email); return; }
            user = await auth.CreateUserAsync(new UserRecordArgs { Email = email, Password = password, EmailVerified = true, DisplayName = "SuperAdmin" }, ct);
        }
        if (user != null)
        {
            // The canonical SuperAdmin uses one credential on Web and Android.
            // When SUPERADMIN_PASSWORD is supplied, synchronize the Firebase
            // password as well as the role claims. Never log the password.
            if (!string.IsNullOrWhiteSpace(password))
            {
                await auth.UpdateUserAsync(
                    new UserRecordArgs
                    {
                        Uid = user.Uid,
                        Password = password,
                        EmailVerified = true,
                        DisplayName = "SuperAdmin"
                    },
                    ct);
            }

            await auth.SetCustomUserClaimsAsync(
                user.Uid,
                new Dictionary<string, object>
                {
                    ["role"] = "SuperAdmin",
                    ["owner_uid"] = _configuration["Firebase:OwnerUid"] ?? "biometricpayroll"
                },
                ct);
        }
    }

    private async Task EnsureLocalIdentityAsync(string email, string? password)
    {
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync("SuperAdmin"))
        {
            var result = await roles.CreateAsync(new IdentityRole("SuperAdmin"));
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
        var user = await users.FindByEmailAsync(email);
        if (user == null)
        {
            if (string.IsNullOrWhiteSpace(password)) { _logger.LogWarning("Local SuperAdmin {Email} does not exist. Set SUPERADMIN_PASSWORD once to provision it.", email); return; }
            user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
        if (!string.IsNullOrWhiteSpace(password))
        {
            var resetToken = await users.GeneratePasswordResetTokenAsync(user);
            var passwordResult = await users.ResetPasswordAsync(user, resetToken, password);
            if (!passwordResult.Succeeded)
                throw new InvalidOperationException(
                    string.Join("; ", passwordResult.Errors.Select(x => x.Description)));
        }

        if (!await users.IsInRoleAsync(user, "SuperAdmin"))
        {
            var result = await users.AddToRoleAsync(user, "SuperAdmin");
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
    }
}
