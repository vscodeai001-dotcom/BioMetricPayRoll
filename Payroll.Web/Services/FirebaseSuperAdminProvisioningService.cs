using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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

        var email =
            _configuration["Firebase:SuperAdminEmail"] ??
            Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL") ??
            DefaultEmail;

        // Firebase Authentication is the credential used by native Android.
        // Keep provisioning alive until the Firebase account is actually
        // synchronized. The password is read only from configuration/env and
        // is never logged or stored by this service.
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var password =
                    _configuration["Firebase:SuperAdminPassword"]
                    ?? Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD");

                _logger.LogInformation(
                    "SuperAdmin provisioning attempt {Attempt} for {Email}. Password configured: {Configured}",
                    attempt,
                    email,
                    !string.IsNullOrWhiteSpace(password));

                await EnsureFirebaseAuthAsync(email, password, stoppingToken);
                await EnsureLocalIdentityAsync(email, password);

                // Once the canonical account is successfully synchronized,
                // no further startup attempts are necessary.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SuperAdmin provisioning attempt {Attempt} failed.", attempt);
                // Back off progressively if network is slow or delayed
                var delaySeconds = Math.Min(15 * attempt, 120);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
    }

    private async Task EnsureFirebaseAuthAsync(string email, string? password, CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
        {
            throw new InvalidOperationException(
                "Firebase Admin SDK is not initialized. Configure Firebase:ServiceAccountPath, GOOGLE_APPLICATION_CREDENTIALS, FIREBASE_SERVICE_ACCOUNT_JSON, or Application Default Credentials.");
        }

        // An existing Firebase Auth account does not require the Web application's
        // password setting just to synchronize its role/tenant claims. This is
        // important when the canonical SuperAdmin password was already set in
        // Firebase Console. A password is required only when creating the account,
        // or when an explicit configured password is supplied for synchronization.
        UserRecord? user = null;
        try 
        { 
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(TimeSpan.FromSeconds(45));
            user = await auth.GetUserByEmailAsync(email, opCts.Token); 
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(TimeSpan.FromSeconds(45));
            user = await auth.CreateUserAsync(
                new UserRecordArgs
                {
                    Email = email,
                    Password = password,
                    EmailVerified = true,
                    DisplayName = "SuperAdmin"
                },
                opCts.Token);

            _logger.LogInformation(
                "Firebase SuperAdmin {Email} account created successfully.",
                email);
        }
        if (user != null)
        {
            var ownerUid = _configuration["Firebase:OwnerUid"] ?? "biometricpayroll";
            var hasCorrectClaims = user.CustomClaims != null &&
                user.CustomClaims.TryGetValue("role", out var r) && r?.ToString() == "SuperAdmin" &&
                user.CustomClaims.TryGetValue("owner_uid", out var ou) && ou?.ToString() == ownerUid;

            // The canonical SuperAdmin uses one Firebase credential on Web and
            // Android. If an explicit Web password is configured, synchronize it.
            // Otherwise preserve the password already stored in Firebase.
            if (!string.IsNullOrWhiteSpace(password))
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(15));
                await auth.UpdateUserAsync(
                    new UserRecordArgs
                    {
                        Uid = user.Uid,
                        Password = password,
                        EmailVerified = true,
                        DisplayName = "SuperAdmin"
                    },
                    opCts.Token);
            }

            if (!hasCorrectClaims)
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(TimeSpan.FromSeconds(15));
                await auth.SetCustomUserClaimsAsync(
                    user.Uid,
                    new Dictionary<string, object>
                    {
                        ["role"] = "SuperAdmin",
                        ["owner_uid"] = ownerUid
                    },
                    opCts.Token);
            }

            // SINGLETON SUPERADMIN ENFORCEMENT IN FIREBASE AUTH:
            // prakashshiva368@gmail.com is the ONLY SuperAdmin. Demote any other Firebase user to Admin.
            try
            {
                var pagedUsers = auth.ListUsersAsync(null);
                await foreach (var fbUser in pagedUsers.WithCancellation(ct))
                {
                    if (string.Equals(fbUser.Email, email, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fbUser.CustomClaims != null &&
                        fbUser.CustomClaims.TryGetValue("role", out var rVal) &&
                        string.Equals(rVal?.ToString(), "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Singleton SuperAdmin rule: Demoting Firebase Auth user {Email} ({Uid}) from SuperAdmin to Admin", fbUser.Email, fbUser.Uid);
                        var updatedClaims = new Dictionary<string, object>(fbUser.CustomClaims)
                        {
                            ["role"] = "Admin"
                        };
                        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        opCts.CancelAfter(TimeSpan.FromSeconds(15));
                        await auth.SetCustomUserClaimsAsync(fbUser.Uid, updatedClaims, opCts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed while checking/demoting extra SuperAdmins in Firebase Auth.");
            }
        }
    }

    private async Task EnsureLocalIdentityAsync(string email, string? password)
    {
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roles.RoleExistsAsync("SuperAdmin"))
        {
            var createRoleResult = await roles.CreateAsync(new IdentityRole("SuperAdmin"));
            if (!createRoleResult.Succeeded) throw new InvalidOperationException(string.Join("; ", createRoleResult.Errors.Select(x => x.Description)));
        }

        var normalizedEmail = email.ToUpperInvariant();
        var user = await users.FindByEmailAsync(email)
                   ?? await users.FindByNameAsync(email)
                   ?? await users.Users.FirstOrDefaultAsync(u => u.Email == email || u.UserName == email || u.NormalizedEmail == normalizedEmail || u.NormalizedUserName == normalizedEmail);

        if (user == null)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Local SuperAdmin {Email} does not exist. Set SUPERADMIN_PASSWORD once to provision it.", email);
                return;
            }

            user = new IdentityUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                NormalizedEmail = normalizedEmail,
                NormalizedUserName = normalizedEmail
            };

            var createUserResult = await users.CreateAsync(user, password);
            if (!createUserResult.Succeeded)
            {
                // In case it was created concurrently or already exists
                user = await users.FindByEmailAsync(email)
                       ?? await users.FindByNameAsync(email)
                       ?? await users.Users.FirstOrDefaultAsync(u => u.Email == email || u.UserName == email);

                if (user == null)
                {
                    _logger.LogWarning("Create SuperAdmin identity user encountered: {Errors}", string.Join("; ", createUserResult.Errors.Select(x => x.Description)));
                }
            }
        }

        if (user != null && !string.IsNullOrWhiteSpace(password))
        {
            try
            {
                var resetToken = await users.GeneratePasswordResetTokenAsync(user);
                var passwordResult = await users.ResetPasswordAsync(user, resetToken, password);
                if (!passwordResult.Succeeded)
                {
                    _logger.LogDebug("Password update not required or skipped for {Email}: {Errors}", email, string.Join("; ", passwordResult.Errors.Select(x => x.Description)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Password reset token not required for {Email}", email);
            }
        }

        if (user != null)
        {
            // The canonical account is always the Web SuperAdmin, even if an
            // older database row was accidentally created with Employee role.
            var currentRoles = await users.GetRolesAsync(user);
            var nonSuperAdminRoles = currentRoles.Where(r => !string.Equals(r, "SuperAdmin", StringComparison.OrdinalIgnoreCase)).ToList();
            if (nonSuperAdminRoles.Any())
            {
                await users.RemoveFromRolesAsync(user, nonSuperAdminRoles);
            }

            if (!await users.IsInRoleAsync(user, "SuperAdmin"))
            {
                var addRoleResult = await users.AddToRoleAsync(user, "SuperAdmin");
                if (!addRoleResult.Succeeded)
                {
                    _logger.LogWarning("Failed adding SuperAdmin role to {Email}: {Errors}", email, string.Join("; ", addRoleResult.Errors.Select(x => x.Description)));
                }
            }

            // SINGLETON SUPERADMIN ENFORCEMENT:
            // prakashshiva368@gmail.com is the ONE and ONLY SuperAdmin in the entire application.
            // Demote any other user (such as prakashshiva365@gmail.com) from SuperAdmin to Admin.
            var allSuperAdmins = await users.GetUsersInRoleAsync("SuperAdmin");
            foreach (var sa in allSuperAdmins)
            {
                if (!string.Equals(sa.Email, email, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sa.UserName, email, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Singleton SuperAdmin rule: Demoting {Email} from SuperAdmin to Admin", sa.Email);
                    await users.RemoveFromRoleAsync(sa, "SuperAdmin");
                    if (!await users.IsInRoleAsync(sa, "Admin"))
                    {
                        await users.AddToRoleAsync(sa, "Admin");
                    }
                }
            }
        }
    }
}
