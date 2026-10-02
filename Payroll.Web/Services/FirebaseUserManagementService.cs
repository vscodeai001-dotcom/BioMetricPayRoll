using System.Text.Json;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Models;

namespace Payroll.Web.Services;

/// <summary>
/// Privileged user-governance boundary. Only the server may perform Firebase
/// Authentication account lifecycle operations. Clients never write/delete
/// Firebase Auth users directly.
/// </summary>
public sealed class FirebaseUserManagementService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly UserManager<IdentityUser> _users;
    private readonly FirebaseRealtimeService _firebase;
    private readonly ILogger<FirebaseUserManagementService> _logger;

    public FirebaseUserManagementService(
        IDbContextFactory<AppDbContext> dbFactory,
        UserManager<IdentityUser> users,
        FirebaseRealtimeService firebase,
        ILogger<FirebaseUserManagementService> logger)
    {
        _dbFactory = dbFactory;
        _users = users;
        _firebase = firebase;
        _logger = logger;
    }

    public static bool IsSupportedRole(string role) =>
        role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("Employee", StringComparison.OrdinalIgnoreCase);

    public async Task<List<UserRoleViewModel>> GetAllUsersAsync(
        List<Employee> allEmployees,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, UserRoleViewModel>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // 1. Load Identity users and roles
            var identityUsers = await db.Users
                .AsNoTracking()
                .ToListAsync(ct);

            var rolesById = await db.Roles
                .AsNoTracking()
                .ToDictionaryAsync(r => r.Id, r => r.Name ?? "Employee", ct);

            var roleIdsByUserId = await db.UserRoles
                .AsNoTracking()
                .GroupBy(ur => ur.UserId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(x => x.RoleId).ToList(),
                    ct);

            foreach (var u in identityUsers)
            {
                var role = "Employee";
                var email = u.Email ?? u.UserName ?? string.Empty;

                if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                    continue;

                if (roleIdsByUserId.TryGetValue(u.Id, out var roleIds))
                {
                    var roleName = roleIds
                        .Select(id => rolesById.TryGetValue(id, out var name) ? name : null)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                    if (!string.IsNullOrWhiteSpace(roleName))
                        role = roleName!;
                }

                var isCanonicalSuperAdmin = !string.IsNullOrEmpty(email) &&
                    string.Equals(email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase);

                if (isCanonicalSuperAdmin)
                    role = "SuperAdmin";
                else if (string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                    role = "Admin";

                var key = email.Trim().ToLowerInvariant();

                result[key] = new UserRoleViewModel
                {
                    UserId = u.Id,
                    FirebaseUid = null,
                    Email = email,
                    UserName = u.UserName ?? email,
                    CurrentRole = role,
                    IsDisabled = u.LockoutEnd.HasValue && u.LockoutEnd > DateTimeOffset.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load users from local Identity/EF store.");
        }

        // 2. Load Firebase Authentication users
        try
        {
            var auth = await _firebase.GetFirebaseAuthAsync(ct);
            if (auth != null)
            {
                var pagedEnumerable = auth.ListUsersAsync(null);
                await foreach (var fbUser in pagedEnumerable.WithCancellation(ct))
                {
                    var email = fbUser.Email ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                    {
                        // Ghost/empty user in Firebase Auth without email. Clean it up and skip!
                        try
                        {
                            await auth.DeleteUserAsync(fbUser.Uid, ct);
                        }
                        catch { }
                        continue;
                    }

                    var key = email.Trim().ToLowerInvariant();

                    var fbRole = ResolveRole(fbUser);
                    var isCanonicalSuperAdmin = !string.IsNullOrEmpty(email) &&
                        string.Equals(email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase);

                    if (isCanonicalSuperAdmin)
                    {
                        fbRole = "SuperAdmin";
                    }
                    else if (string.Equals(fbRole, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                    {
                        fbRole = "Admin";
                        // Immediately correct custom claims in Firebase Auth asynchronously
                        var targetUid = fbUser.Uid;
                        var existingClaims = fbUser.CustomClaims != null
                            ? new Dictionary<string, object>(fbUser.CustomClaims)
                            : new Dictionary<string, object>();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                existingClaims["role"] = "Admin";
                                await auth.SetCustomUserClaimsAsync(targetUid, existingClaims);
                            }
                            catch { }
                        });
                    }

                    int? fbEmployeeId = null;
                    if (fbUser.CustomClaims != null &&
                        fbUser.CustomClaims.TryGetValue("employee_id", out var eidVal) &&
                        int.TryParse(eidVal?.ToString(), out var parsedEid) && parsedEid > 0)
                    {
                        fbEmployeeId = parsedEid;
                    }

                    if (result.TryGetValue(key, out var existing))
                    {
                        existing.FirebaseUid = fbUser.Uid;
                        if (existing.CurrentRole == "Employee" && fbRole != "Employee")
                            existing.CurrentRole = fbRole;
                        if (fbEmployeeId.HasValue && !existing.EmployeeId.HasValue)
                            existing.EmployeeId = fbEmployeeId;
                        existing.IsDisabled = existing.IsDisabled || fbUser.Disabled;
                    }
                    else
                    {
                        result[key] = new UserRoleViewModel
                        {
                            UserId = fbUser.Uid,
                            FirebaseUid = fbUser.Uid,
                            Email = email,
                            UserName = fbUser.DisplayName ?? email,
                            CurrentRole = fbRole,
                            EmployeeId = fbEmployeeId,
                            IsDisabled = fbUser.Disabled
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch users from Firebase Authentication; relying on local Identity store.");
        }

        // 3. Resolve linked employee for each user in result
        var userList = result.Values.ToList();
        foreach (var user in userList)
        {
            var isCanonicalSuperAdmin = string.Equals(user.Email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase);

            if (isCanonicalSuperAdmin)
            {
                user.CurrentRole = "SuperAdmin";
                user.EmployeeName = "SuperAdmin";
                continue;
            }

            if (string.Equals(user.CurrentRole, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
            {
                user.CurrentRole = "Admin";
            }

            Employee? linked = null;

            if (user.EmployeeId.HasValue && user.EmployeeId.Value > 0)
            {
                linked = allEmployees.FirstOrDefault(e => e.EmployeeID == user.EmployeeId.Value);
            }

            if (linked == null && !string.IsNullOrWhiteSpace(user.UserId))
            {
                linked = allEmployees.FirstOrDefault(e => e.AspNetUserId == user.UserId);
            }

            if (linked == null && !string.IsNullOrWhiteSpace(user.FirebaseUid))
            {
                linked = allEmployees.FirstOrDefault(e => e.AspNetUserId == user.FirebaseUid);
            }

            if (linked == null && !string.IsNullOrWhiteSpace(user.Email))
            {
                linked = allEmployees.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.Email) &&
                    string.Equals(e.Email.Trim(), user.Email.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (linked != null)
            {
                user.EmployeeId = linked.EmployeeID;
                user.EmployeeName = !string.IsNullOrWhiteSpace(linked.BiometricID)
                    ? $"{linked.Name} (Bio: {linked.BiometricID})"
                    : linked.Name;
            }
            else
            {
                user.EmployeeName = user.CurrentRole == "Admin" ? "Administrator" :
                    user.CurrentRole == "SuperAdmin" ? "SuperAdmin" : "Unlinked User ⚠️";
            }
        }

        return userList
            .Where(u => !string.IsNullOrWhiteSpace(u.Email) && u.Email.Contains('@'))
            .OrderBy(u => u.Email)
            .ToList();
    }

    public async Task<UserManagementResult> CreateAsync(
        string email,
        string password,
        string role,
        int employeeId,
        string? displayName,
        CancellationToken ct)
    {
        email = email.Trim();
        role = NormalizeRole(role);
        if (!IsSupportedRole(role))
            return Fail("Only Admin and Employee accounts can be created here.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            return Fail("Password must be at least 6 characters.");
        if (role == "Admin" && employeeId > 0)
            return Fail("Admin accounts cannot be linked to an Employee record.");

        // Employee accounts must always correspond to a saved Employee master
        // record. Do not create empty/unlinked Employee users.
        if (role == "Employee" && employeeId <= 0)
            return Fail("Employee accounts must be linked to a saved Employee record.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existingIdentity = await _users.FindByEmailAsync(email);
        if (existingIdentity != null)
            return Fail("A Web Identity account with this email already exists.");

        Employee? employee = null;
        if (employeeId > 0)
        {
            employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeID == employeeId && !e.IsDeleted, ct);
            if (employee == null)
                return Fail("Employee record was not found.");
            
            if (!string.IsNullOrWhiteSpace(employee.AspNetUserId))
            {
                var existingIdentityUser = await _users.FindByIdAsync(employee.AspNetUserId);
                UserRecord? existingFbUser = null;
                try
                {
                    var authCheck = await _firebase.GetFirebaseAuthAsync(ct);
                    if (authCheck != null)
                        existingFbUser = await authCheck.GetUserAsync(employee.AspNetUserId, ct);
                }
                catch { }

                if (existingIdentityUser != null || existingFbUser != null)
                {
                    return Fail("The selected Employee is already linked to a user.");
                }
            }
        }

        var identity = new IdentityUser { Email = email, UserName = email, EmailConfirmed = true };
        var identityResult = await _users.CreateAsync(identity, password);
        if (!identityResult.Succeeded)
            return Fail(string.Join(", ", identityResult.Errors.Select(e => e.Description)));

        try
        {
            var roleResult = await _users.AddToRoleAsync(identity, role);
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(string.Join(", ", roleResult.Errors.Select(e => e.Description)));

            if (employee != null)
            {
                employee.Email = email;
                employee.AspNetUserId = identity.Id;
                await db.SaveChangesAsync(ct);
            }

            var firebaseUid = await _firebase.EnsureFirebaseUserAsync(
                email,
                password,
                role,
                employee?.EmployeeID ?? 0,
                displayName ?? employee?.Name ?? email,
                existingUid: null,
                updatePasswordIfExisting: true, // MIRROR: Ensure Firebase password matches Web/SQL password
                cancellationToken: ct);

            if (string.IsNullOrWhiteSpace(firebaseUid))
                throw new InvalidOperationException("Firebase Authentication provisioning failed.");

            await SyncProfileAsync(firebaseUid, email, role, employee, enabled: true, ct);
            return new UserManagementResult(true, firebaseUid, identity.Id, "User created and synchronized.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User creation failed for {Email}; compensating local Identity state.", email);
            if (employee != null)
            {
                employee.AspNetUserId = null;
                employee.Email = null;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await _users.DeleteAsync(identity);
            return Fail(ex.Message);
        }
    }

    public async Task<UserManagementResult> ChangeRoleAsync(string firebaseUid, string newRole, CancellationToken ct)
    {
        newRole = NormalizeRole(newRole);
        if (string.Equals(newRole, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
            return Fail("Only prakashshiva368@gmail.com can be SuperAdmin. No other user can be assigned the SuperAdmin role.");
        if (!IsSupportedRole(newRole))
            return Fail("Only Admin and Employee roles can be assigned.");
        if (string.IsNullOrWhiteSpace(firebaseUid))
            return Fail("Firebase UID is required.");

        var auth = await _firebase.GetFirebaseAuthAsync(ct) ?? throw new InvalidOperationException("Firebase Authentication is not configured.");
        UserRecord? firebaseUser = null;
        try { firebaseUser = await auth.GetUserAsync(firebaseUid, ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            var identityFallback = await _users.FindByIdAsync(firebaseUid) ?? await _users.FindByEmailAsync(firebaseUid);
            if (identityFallback?.Email != null)
            {
                try { firebaseUser = await auth.GetUserByEmailAsync(identityFallback.Email, ct); }
                catch { firebaseUser = null; }
            }
        }

        var effectiveUid = firebaseUser?.Uid ?? firebaseUid;
        var effectiveEmail = firebaseUser?.Email;

        if (effectiveEmail?.Equals(FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase) == true)
            return Fail("The canonical SuperAdmin role cannot be changed.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = !string.IsNullOrWhiteSpace(effectiveEmail)
            ? await _users.FindByEmailAsync(effectiveEmail)
            : await _users.FindByIdAsync(firebaseUid);

        Employee? employee = null;
        if (identity != null)
            employee = await db.Employees.FirstOrDefaultAsync(e => e.AspNetUserId == identity.Id && !e.IsDeleted, ct);

        if (employee == null && firebaseUser?.CustomClaims != null &&
            firebaseUser.CustomClaims.TryGetValue("employee_id", out var eidVal) &&
            int.TryParse(eidVal?.ToString(), out var parsedEid) && parsedEid > 0)
        {
            employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeID == parsedEid && !e.IsDeleted, ct);
        }

        if (newRole == "Admin" && employee != null)
            return Fail("An Employee-linked account cannot be changed to Admin.");
        if (newRole == "Employee" && employee == null)
            return Fail("Employee role requires a linked Employee record.");

        var oldRoles = identity == null ? new List<string>() : (await _users.GetRolesAsync(identity)).ToList();
        if (identity != null)
        {
            if (oldRoles.Any())
            {
                var remove = await _users.RemoveFromRolesAsync(identity, oldRoles);
                if (!remove.Succeeded) return Fail(string.Join(", ", remove.Errors.Select(e => e.Description)));
            }
            var add = await _users.AddToRoleAsync(identity, newRole);
            if (!add.Succeeded) return Fail(string.Join(", ", add.Errors.Select(e => e.Description)));
        }

        try
        {
            if (firebaseUser != null)
            {
                await auth.SetCustomUserClaimsAsync(firebaseUser.Uid, new Dictionary<string, object>
                {
                    ["role"] = newRole,
                    ["employee_id"] = employee?.EmployeeID ?? 0,
                    ["owner_uid"] = _firebase.ResolveOwnerUid(firebaseUser.Uid, newRole)
                }, ct);
                await SyncProfileAsync(firebaseUser.Uid, firebaseUser.Email ?? string.Empty, newRole, employee, !firebaseUser.Disabled, ct);
            }
            return new UserManagementResult(true, effectiveUid, identity?.Id, "Role changed and claims synchronized.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Role synchronization failed for UID {Uid}; reverting Identity roles.", effectiveUid);
            if (identity != null)
            {
                var now = await _users.GetRolesAsync(identity);
                if (now.Any()) await _users.RemoveFromRolesAsync(identity, now);
                if (oldRoles.Any()) await _users.AddToRolesAsync(identity, oldRoles);
            }
            return Fail(ex.Message);
        }
    }

    public async Task<UserManagementResult> SetDisabledAsync(string firebaseUid, bool disabled, CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct) ?? throw new InvalidOperationException("Firebase Authentication is not configured.");
        UserRecord? user = null;
        try { user = await auth.GetUserAsync(firebaseUid, ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            var identityFallback = await _users.FindByIdAsync(firebaseUid) ?? await _users.FindByEmailAsync(firebaseUid);
            if (identityFallback?.Email != null)
            {
                try { user = await auth.GetUserByEmailAsync(identityFallback.Email, ct); }
                catch { user = null; }
            }
        }

        var effectiveUid = user?.Uid ?? firebaseUid;
        var effectiveEmail = user?.Email;

        if (effectiveEmail?.Equals(FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase) == true && disabled)
            return Fail("The canonical SuperAdmin cannot be disabled.");

        if (user != null)
        {
            await auth.UpdateUserAsync(new UserRecordArgs { Uid = user.Uid, Disabled = disabled }, ct);
            if (disabled)
                await auth.RevokeRefreshTokensAsync(user.Uid, ct);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = !string.IsNullOrWhiteSpace(effectiveEmail) ? await _users.FindByEmailAsync(effectiveEmail) : await _users.FindByIdAsync(firebaseUid);
        if (identity != null)
        {
            identity.LockoutEnabled = true;
            identity.LockoutEnd = disabled ? DateTimeOffset.MaxValue : null;
            var result = await _users.UpdateAsync(identity);
            if (!result.Succeeded)
                return Fail(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        var employee = identity == null ? null : await db.Employees.FirstOrDefaultAsync(e => e.AspNetUserId == identity.Id && !e.IsDeleted, ct);
        if (user != null)
        {
            await SyncProfileAsync(user.Uid, user.Email ?? string.Empty, ResolveRole(user), employee, !disabled, ct);
        }
        return new UserManagementResult(true, effectiveUid, identity?.Id, disabled ? "User disabled and sessions revoked." : "User enabled.");
    }

    public async Task<UserManagementResult> DeleteAsync(string firebaseUid, CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct) ?? throw new InvalidOperationException("Firebase Authentication is not configured.");
        UserRecord? user = null;
        try { user = await auth.GetUserAsync(firebaseUid, ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            var identityFallback = await _users.FindByIdAsync(firebaseUid) ?? await _users.FindByEmailAsync(firebaseUid);
            if (identityFallback?.Email != null)
            {
                try { user = await auth.GetUserByEmailAsync(identityFallback.Email, ct); }
                catch { user = null; }
            }
        }

        if (user != null)
        {
            if (user.Email?.Equals(FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase) == true)
                return Fail("The canonical SuperAdmin cannot be deleted.");

            await auth.RevokeRefreshTokensAsync(user.Uid, ct);
            await auth.DeleteUserAsync(user.Uid, ct);
            await _firebase.DeleteGlobalRecordAsync($"user_profiles/{user.Uid}", ct);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var email = user?.Email;
        var identity = !string.IsNullOrWhiteSpace(email) ? await _users.FindByEmailAsync(email) : await _users.FindByIdAsync(firebaseUid);
        if (identity != null)
        {
            var employee = await db.Employees.FirstOrDefaultAsync(e => e.AspNetUserId == identity.Id, ct);
            if (employee != null)
            {
                employee.AspNetUserId = null;
                employee.Email = null;
            }
            var result = await _users.DeleteAsync(identity);
            if (!result.Succeeded && user == null)
                return Fail("Firebase account was deleted, but Web Identity cleanup failed: " + string.Join(", ", result.Errors.Select(e => e.Description)));
            await db.SaveChangesAsync(ct);
        }

        if (user == null && identity == null)
            return Fail("User was not found in Firebase Authentication or Web Identity store.");

        return new UserManagementResult(true, user?.Uid ?? firebaseUid, identity?.Id, "Firebase Auth, sessions, profile and Web Identity synchronized for deletion.");
    }

    private async Task SyncProfileAsync(string uid, string email, string role, Employee? employee, bool enabled, CancellationToken ct)
    {
        var profile = new Dictionary<string, object?>
        {
            ["uid"] = uid,
            ["email"] = email,
            ["name"] = employee?.Name ?? email,
            ["phone"] = "",
            ["employeeId"] = employee?.EmployeeID.ToString() ?? "",
            ["role"] = role,
            ["enabled"] = enabled,
            ["ownerUid"] = _firebase.ResolveOwnerUid(uid, role),
            ["dataLastModified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        if (!await _firebase.SetGlobalRecordAsync($"user_profiles/{uid}", profile, ct))
            throw new InvalidOperationException("Firebase user profile synchronization failed.");
    }

    public async Task<(bool Success, string Message)> ResetPasswordAsync(
        string emailOrUid,
        string newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emailOrUid))
            return (false, "User identifier is required.");

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            return (false, "Password must be at least 6 characters.");

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var user = await _users.FindByEmailAsync(emailOrUid) ?? await _users.FindByIdAsync(emailOrUid);

            if (user != null)
            {
                var token = await _users.GeneratePasswordResetTokenAsync(user);
                var resetResult = await _users.ResetPasswordAsync(user, token, newPassword);
                if (!resetResult.Succeeded)
                {
                    var errors = string.Join("; ", resetResult.Errors.Select(e => e.Description));
                    return (false, $"Password update failed: {errors}");
                }
            }

            // Sync with Firebase Authentication
            var auth = await _firebase.GetFirebaseAuthAsync(ct);
            if (auth != null)
            {
                UserRecord? fbUser = null;
                try
                {
                    fbUser = await auth.GetUserAsync(emailOrUid, ct);
                }
                catch
                {
                    try
                    {
                        fbUser = await auth.GetUserByEmailAsync(emailOrUid, ct);
                    }
                    catch
                    {
                        // Firebase user not found
                    }
                }

                if (fbUser != null)
                {
                    await auth.UpdateUserAsync(new UserRecordArgs
                    {
                        Uid = fbUser.Uid,
                        Password = newPassword
                    }, ct);
                }
            }

            return (true, "Password successfully updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password for {User}", emailOrUid);
            return (false, $"Error resetting password: {ex.Message}");
        }
    }

    private static string NormalizeRole(string role) =>
        role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) ? "SuperAdmin" :
        role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "Employee";

    private static string ResolveRole(UserRecord user)
    {
        if (user.CustomClaims != null && user.CustomClaims.TryGetValue("role", out var value))
            return value?.ToString() ?? "Employee";
        return "Employee";
    }

    private static UserManagementResult Fail(string message) => new(false, null, null, message);
}

public sealed record UserManagementResult(bool Success, string? FirebaseUid, string? IdentityUserId, string Message);

public static class FirebaseAuthSecurityConstants
{
    public const string CanonicalSuperAdminEmail = "prakashshiva368@gmail.com";
}
