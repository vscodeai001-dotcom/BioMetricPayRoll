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

    public FirebaseSuperAdminProvisioningService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseSuperAdminProvisioningService> logger)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        var email =
            _configuration["Firebase:SuperAdminEmail"] ??
            Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL") ??
            DefaultEmail;

        // Give development environments time to provide SUPERADMIN_PASSWORD
        // and recover from transient Firebase startup/authentication failures.
        // Never log the password itself.
        for (
            var attempt = 1;
            attempt <= 10 && !stoppingToken.IsCancellationRequested;
            attempt++)
        {
            try
            {
                var password =
                    Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD");

                _logger.LogInformation(
                    "SuperAdmin provisioning attempt {Attempt}/10 for {Email}. Password configured: {Configured}",
                    attempt,
                    email,
                    !string.IsNullOrWhiteSpace(password));

                await EnsureFirebaseAuthAsync(
                    email,
                    password,
                    stoppingToken);

                await EnsureLocalIdentityAsync(
                    email,
                    password);

                // Once the canonical account is successfully synchronized,
                // no further startup attempts are necessary.
                return;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "SuperAdmin provisioning attempt {Attempt} failed.",
                    attempt);

                if (attempt < 10)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(15),
                        stoppingToken);
                }
            }
        }
    }

    private async Task EnsureFirebaseAuthAsync(
        string email,
        string? password,
        CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);

        if (auth == null)
        {
            throw new InvalidOperationException(
                "Firebase Admin SDK is not initialized. " +
                "Configure Firebase:ServiceAccountPath, " +
                "GOOGLE_APPLICATION_CREDENTIALS, " +
                "FIREBASE_SERVICE_ACCOUNT_JSON, or Application Default Credentials.");
        }

        UserRecord? user = null;

        try
        {
            user = await auth.GetUserByEmailAsync(
                email,
                ct);
        }
        catch (FirebaseAuthException ex)
            when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning(
                    "Firebase SuperAdmin {Email} does not exist. " +
                    "Set SUPERADMIN_PASSWORD once to provision it.",
                    email);

                return;
            }

            user = await auth.CreateUserAsync(
                new UserRecordArgs
                {
                    Email = email,
                    Password = password,
                    EmailVerified = true,
                    DisplayName = "SuperAdmin"
                },
                ct);

            _logger.LogInformation(
                "Firebase SuperAdmin {Email} was created successfully.",
                email);
        }

        if (user == null)
        {
            throw new InvalidOperationException(
                $"Unable to resolve Firebase SuperAdmin account '{email}'.");
        }

        // The canonical SuperAdmin uses one credential on Web and Android.
        // When SUPERADMIN_PASSWORD is supplied, synchronize the Firebase
        // password as well as the role claims.
        // Never log the password.
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
                ["owner_uid"] =
                    _configuration["Firebase:OwnerUid"]
                    ?? "biometricpayroll"
            },
            ct);

        _logger.LogInformation(
            "Firebase SuperAdmin {Email} synchronized successfully. UID={Uid}",
            email,
            user.Uid);
    }

    private async Task EnsureLocalIdentityAsync(
        string email,
        string? password)
    {
        using var scope = _scopeFactory.CreateScope();

        var users =
            scope.ServiceProvider
                .GetRequiredService<UserManager<IdentityUser>>();

        var roles =
            scope.ServiceProvider
                .GetRequiredService<RoleManager<IdentityRole>>();

        // Ensure the SuperAdmin role exists.
        if (!await roles.RoleExistsAsync("SuperAdmin"))
        {
            var createRoleResult =
                await roles.CreateAsync(
                    new IdentityRole("SuperAdmin"));

            if (!createRoleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join(
                        "; ",
                        createRoleResult.Errors.Select(
                            x => x.Description)));
            }
        }

        // Find the canonical Web SuperAdmin.
        var user =
            await users.FindByEmailAsync(email);

        // Create local Identity user if it doesn't exist.
        if (user == null)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning(
                    "Local SuperAdmin {Email} does not exist. " +
                    "Set SUPERADMIN_PASSWORD once to provision it.",
                    email);

                return;
            }

            user = new IdentityUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };

            var createUserResult =
                await users.CreateAsync(
                    user,
                    password);

            if (!createUserResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join(
                        "; ",
                        createUserResult.Errors.Select(
                            x => x.Description)));
            }

            _logger.LogInformation(
                "Local SuperAdmin {Email} was created successfully.",
                email);
        }

        // Synchronize the Web password with the same SuperAdmin password
        // used by Firebase/Android.
        if (!string.IsNullOrWhiteSpace(password))
        {
            var resetToken =
                await users.GeneratePasswordResetTokenAsync(user);

            var passwordResult =
                await users.ResetPasswordAsync(
                    user,
                    resetToken,
                    password);

            if (!passwordResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join(
                        "; ",
                        passwordResult.Errors.Select(
                            x => x.Description)));
            }
        }

        // The canonical account is ALWAYS SuperAdmin.
        // If an older row was incorrectly assigned Employee/Admin/etc.,
        // remove those roles before assigning SuperAdmin.
        var currentRoles =
            await users.GetRolesAsync(user);

        if (currentRoles.Any())
        {
            var removeRolesResult =
                await users.RemoveFromRolesAsync(
                    user,
                    currentRoles);

            if (!removeRolesResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join(
                        "; ",
                        removeRolesResult.Errors.Select(
                            x => x.Description)));
            }
        }

        // IMPORTANT:
        // Use a different variable name here.
        // This avoids the CS0136 duplicate 'result' declaration.
        var addSuperAdminRoleResult =
            await users.AddToRoleAsync(
                user,
                "SuperAdmin");

        if (!addSuperAdminRoleResult.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join(
                    "; ",
                    addSuperAdminRoleResult.Errors.Select(
                        x => x.Description)));
        }

        _logger.LogInformation(
            "Local SuperAdmin {Email} synchronized successfully.",
            email);
    }
}