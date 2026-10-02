using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.UI.Services;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase-only Employee Management data access for the Web application.
/// Firebase is the authoritative shared store. This service intentionally does
/// not use EF/SQL for Employee CRUD or settings reads.
/// </summary>
public sealed class FirebaseEmployeeManagementService
{
    private const string EmployeesTable = "employees";
    private const string CompanySettingsTable = "company_settings";
    private const string FeatureSettingsTable = "feature_settings";

    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseEmployeeManagementService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> EmployeeWriteLocks = new();

    public FirebaseEmployeeManagementService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseEmployeeManagementService> logger,
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory scopeFactory)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
    }

    public string OwnerUid => _firebase.ResolveOwnerUid("employee-mgmt", "Admin");

    public async Task<List<Employee>> GetEmployeesAsync(CancellationToken ct = default)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
            if (dbFactory != null)
            {
                using var db = await dbFactory.CreateDbContextAsync(ct);
                var localEmployees = await db.Employees.AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Name)
                    .ToListAsync(ct);

                var appMode = scope.ServiceProvider.GetService<IAppModeService>();
                var isOffline = appMode != null && await appMode.IsOfflineModeAsync();

                if (localEmployees.Count > 0 || isOffline)
                {
                    return localEmployees;
                }
            }
        }

        var snapshot = await _firebase.GetOwnerTableAsync(OwnerUid, EmployeesTable, ct);
        if (!snapshot.HasValue)
        {
            _logger.LogWarning(
                "Firebase employees read returned no data for owner {OwnerUid}.",
                OwnerUid);
            return new List<Employee>();
        }

        var result = new List<Employee>();

        // Firebase Realtime Database returns numeric-keyed collections as a JSON
        // array in REST responses. Older data and some writes can return an
        // object instead. Support both shapes so valid employees are never
        // mistaken for an empty collection.
        if (snapshot.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in snapshot.Value.EnumerateObject())
            {
                try
                {
                    if (item.Value.ValueKind == JsonValueKind.Null)
                        continue;

                    var employee = ToEmployee(item.Value, item.Name);
                    if (!employee.IsDeleted)
                        result.Add(employee);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ignoring malformed Firebase employee record {RecordId}.", item.Name);
                }
            }
        }
        else if (snapshot.Value.ValueKind == JsonValueKind.Array)
        {
            _logger.LogInformation(
                "Firebase employees collection is array-shaped for owner {OwnerUid}; reading numeric employee records.",
                OwnerUid);

            var index = 0;
            foreach (var item in snapshot.Value.EnumerateArray())
            {
                var fallbackId = index.ToString(CultureInfo.InvariantCulture);
                index++;

                try
                {
                    if (item.ValueKind == JsonValueKind.Null)
                        continue;

                    var employee = ToEmployee(item, fallbackId);
                    if (!employee.IsDeleted)
                        result.Add(employee);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ignoring malformed Firebase employee array record {RecordId}.", fallbackId);
                }
            }
        }
        else
        {
            _logger.LogWarning(
                "Firebase employees table returned unsupported JSON shape {ValueKind} for owner {OwnerUid}.",
                snapshot.Value.ValueKind,
                OwnerUid);
        }

        return result
            .GroupBy(x => x.EmployeeID)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Employee?> GetEmployeeAsync(int employeeId, CancellationToken ct = default)
    {
        if (employeeId <= 0) return null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
            if (dbFactory != null)
            {
                using var db = await dbFactory.CreateDbContextAsync(ct);
                var localEmployee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeID == employeeId, ct);
                var appMode = scope.ServiceProvider.GetService<IAppModeService>();
                var isOffline = appMode != null && await appMode.IsOfflineModeAsync();
                if (localEmployee != null || isOffline)
                {
                    return localEmployee;
                }
            }
        }

        var snapshot = await _firebase.GetOwnerRecordAsync(
            OwnerUid, EmployeesTable, employeeId.ToString(CultureInfo.InvariantCulture), ct);
        return snapshot.HasValue && snapshot.Value.ValueKind == JsonValueKind.Object
            ? ToEmployee(snapshot.Value, employeeId.ToString(CultureInfo.InvariantCulture))
            : null;
    }

    public async Task<Dictionary<int, string>> GetEmployeeNameMapAsync(
        IEnumerable<int> employeeIds,
        CancellationToken ct = default)
    {
        var ids = employeeIds
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();

        if (ids.Count == 0)
            return new Dictionary<int, string>();

        var employees = await GetEmployeesAsync(ct);
        return employees
            .Where(e => ids.Contains(e.EmployeeID))
            .ToDictionary(e => e.EmployeeID, e => e.Name ?? string.Empty);
    }

    public async Task<Employee?> GetEmployeeByEmailAsync(string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim();
        var employees = await GetEmployeesAsync(ct);
        return employees.FirstOrDefault(e =>
            string.Equals(e.Email?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Success, Employee Employee, string Message)> SaveEmployeeAsync(
        Employee employee,
        CancellationToken ct = default)
    {
        if (employee == null) throw new ArgumentNullException(nameof(employee));

        using (var scope = _scopeFactory.CreateScope())
        {
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    if (employee.EmployeeID <= 0)
                    {
                        var maxId = await db.Employees.Select(e => (int?)e.EmployeeID).MaxAsync(ct) ?? 0;
                        employee.EmployeeID = maxId + 1;
                        await db.Employees.AddAsync(employee, ct);
                    }
                    else
                    {
                        var existing = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeID == employee.EmployeeID, ct);
                        if (existing != null)
                        {
                            db.Entry(existing).CurrentValues.SetValues(employee);
                        }
                        else
                        {
                            await db.Employees.AddAsync(employee, ct);
                        }
                    }
                    await db.SaveChangesAsync(ct);
                    return (true, employee, "Employee saved successfully to local database (Offline Standalone Mode).");
                }
            }
        }

        var requestedId = employee.EmployeeID;
        var provisionalId = requestedId;
        if (provisionalId <= 0)
        {
            // ID allocation is serialized with the same process-wide gate used
            // for writes. Firebase remains authoritative; this only prevents
            // duplicate IDs from concurrent Web circuits in this process.
            var globalLock = EmployeeWriteLocks.GetOrAdd(0, _ => new SemaphoreSlim(1, 1));
            await globalLock.WaitAsync(ct);
            try
            {
                var all = await GetAllEmployeesIncludingDeletedAsync(ct);
                var maxLocal = all.Select(x => x.EmployeeID).Where(x => x > 0).DefaultIfEmpty(0).Max();

                var maxDb = 0;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                    if (dbFactory != null)
                    {
                        using var db = dbFactory.CreateDbContext();
                        maxDb = db.Employees.Select(e => (int?)e.EmployeeID).Max() ?? 0;
                    }
                }
                catch { }

                provisionalId = Math.Max(maxLocal, maxDb) + 1;
            }
            finally { globalLock.Release(); }
            employee.EmployeeID = provisionalId;
        }

        if (string.IsNullOrWhiteSpace(employee.TenantId))
        {
            employee.TenantId = OwnerUid;
        }

        var gate = EmployeeWriteLocks.GetOrAdd(employee.EmployeeID, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await GetEmployeeAsync(employee.EmployeeID, ct);
            if (existing != null && EmployeeFingerprint(existing) == EmployeeFingerprint(employee))
                return (true, employee, "No employee changes detected; existing Firebase record retained.");

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long previousRevision = 0;
            if (existing != null)
            {
                var existingSnapshot = await _firebase.GetOwnerRecordAsync(OwnerUid, EmployeesTable, employee.EmployeeID.ToString(CultureInfo.InvariantCulture), ct);
                if (existingSnapshot.HasValue)
                {
                    if (TryGet(existingSnapshot.Value, "createdAt", out var createdAtElement) &&
                        createdAtElement.TryGetInt64(out var existingCreatedAt) && existingCreatedAt > 0)
                        createdAt = existingCreatedAt;
                    if (TryGet(existingSnapshot.Value, "_revision", out var revisionElement))
                        long.TryParse(revisionElement.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out previousRevision);
                }
            }

            var writeId = Guid.NewGuid().ToString("N");
            var row = ToFirebaseRow(employee, createdAt);
            row["_entity"] = "Employee";
            row["_key"] = employee.EmployeeID.ToString(CultureInfo.InvariantCulture);
            row["_revision"] = previousRevision + 1;
            row["_writeId"] = writeId;
            row["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var recordId = employee.EmployeeID.ToString(CultureInfo.InvariantCulture);
            var success = await _firebase.SetOwnerRecordAsync(OwnerUid, EmployeesTable, recordId, row, ct);
            if (!success)
                return (false, employee, "Firebase employee write failed.");

            await _firebase.PublishApplicationDataChangedAsync(
                new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Entity"] = "Employee",
                        ["Action"] = requestedId <= 0 ? "CREATED" : "MODIFIED",
                        ["RecordId"] = recordId,
                        ["Revision"] = previousRevision + 1,
                        ["WriteId"] = writeId
                    }
                }, OwnerUid, ct);

            return (true, employee, requestedId <= 0
                ? "Employee synchronized to Firebase."
                : "Employee synchronized to Firebase with duplicate-write protection.");
        }
        finally { gate.Release(); }
    }

    private static string EmployeeFingerprint(Employee employee)
    {
        var json = JsonSerializer.Serialize(employee, new JsonSerializerOptions { WriteIndented = false });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    public async Task<(bool Success, Employee? Employee, string Message)> SoftDeleteEmployeeAsync(
        int employeeId,
        CancellationToken ct = default)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    var existing = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeID == employeeId, ct);
                    if (existing != null)
                    {
                        existing.IsDeleted = true;
                        await db.SaveChangesAsync(ct);
                        return (true, existing, "Employee deleted from local database (Offline Standalone Mode).");
                    }
                    return (false, null, "Employee not found in local database.");
                }
            }
        }

        var employee = await GetEmployeeAsync(employeeId, ct);
        if (employee == null)
            return (false, null, "Employee not found in Firebase.");

        employee.IsDeleted = true;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw = await _firebase.GetOwnerRecordAsync(OwnerUid, EmployeesTable, employeeId.ToString(CultureInfo.InvariantCulture), ct);
        if (raw.HasValue && TryGet(raw.Value, "createdAt", out var createdAtElement) &&
            createdAtElement.TryGetInt64(out var existingCreatedAt) && existingCreatedAt > 0)
            createdAt = existingCreatedAt;
        var previousRevision = 0L;
        if (raw.HasValue && TryGet(raw.Value, "_revision", out var revisionElement))
            long.TryParse(revisionElement.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out previousRevision);
        var writeId = Guid.NewGuid().ToString("N");
        var row = ToFirebaseRow(employee, createdAt);
        row["_entity"] = "Employee";
        row["_key"] = employeeId.ToString(CultureInfo.InvariantCulture);
        row["_revision"] = previousRevision + 1;
        row["_writeId"] = writeId;
        row["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var success = await _firebase.SetOwnerRecordAsync(
            OwnerUid, EmployeesTable, employeeId.ToString(CultureInfo.InvariantCulture), row, ct);

        if (!success)
            return (false, employee, "Firebase employee deletion update failed.");

        await _firebase.PublishApplicationDataChangedAsync(
            new[]
            {
                new Dictionary<string, object?>
                {
                    ["Entity"] = "Employee",
                    ["Action"] = "DELETED",
                    ["RecordId"] = employeeId.ToString(CultureInfo.InvariantCulture),
                    ["Revision"] = previousRevision + 1,
                    ["WriteId"] = writeId
                }
            }, OwnerUid, ct);

        return (true, employee, "Employee moved to the Firebase recycle/deleted state.");
    }


    public async Task<string?> GetFirebaseAuthUidByEmailAsync(
        string email,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
            return null;

        try
        {
            var user = await auth.GetUserByEmailAsync(email.Trim(), ct);
            return user.Uid;
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return null;
        }
    }

    public async Task<string?> EnsureLoginAuthBindingAsync(
        Employee employee,
        string email,
        string password,
        CancellationToken ct = default)
    {
        if (employee.EmployeeID <= 0 || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                return "offline-user";
            }
        }

        // Reuse the existing provisioning path so Web and Android share the
        // same Firebase Auth UID, employee_id, role and owner_uid contract.
        var result = await ReconcileAuthAsync(employee, ct);
        if (!result.Success)
            return null;

        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
            return null;

        try
        {
            var user = await auth.GetUserByEmailAsync(email.Trim(), ct);
            await auth.UpdateUserAsync(
                new UserRecordArgs
                {
                    Uid = user.Uid,
                    Password = password,
                    DisplayName = employee.Name,
                    Disabled = employee.IsDeleted
                },
                ct);

            await auth.SetCustomUserClaimsAsync(
                user.Uid,
                new Dictionary<string, object>
                {
                    ["role"] = "Employee",
                    ["employee_id"] = employee.EmployeeID,
                    ["owner_uid"] = OwnerUid
                },
                ct);

            return user.Uid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to synchronize Firebase Auth password/claims during Web login for EmployeeId={EmployeeId}",
                employee.EmployeeID);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> ReconcileAuthAsync(
        Employee employee,
        CancellationToken ct = default)
    {
        if (employee.EmployeeID <= 0)
            return (false, "Employee ID is invalid.");

        using (var scope = _scopeFactory.CreateScope())
        {
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                return (true, "Offline mode active: Employee record created in local database without Firebase Auth.");
            }
        }

        if (string.IsNullOrWhiteSpace(employee.Email))
        {
            await WriteProvisioningStatusAsync(employee, null, false, ct);
            return (true, "Employee saved. Firebase Auth provisioning is pending because no email is configured.");
        }

        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null)
            return (false, "Firebase Authentication is not configured.");

        var normalizedEmail = employee.Email.Trim();
        UserRecord? user = null;
        var createdAuthAccount = false;

        try
        {
            user = await auth.GetUserByEmailAsync(normalizedEmail, ct);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            // Employee creation must not leave a dead email-only record.
            // Create a Firebase Auth account with a random temporary password,
            // then immediately generate a password-reset link. The temporary
            // password is never stored or returned.
            var temporaryPassword = CreateTemporaryFirebasePassword();

            try
            {
                user = await auth.CreateUserAsync(
                    new UserRecordArgs
                    {
                        Email = normalizedEmail,
                        Password = temporaryPassword,
                        DisplayName = employee.Name,
                        EmailVerified = false,
                        Disabled = false
                    },
                    ct);
                createdAuthAccount = true;
            }
            catch (FirebaseAuthException createEx) when (createEx.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
            {
                // A concurrent provisioning request may have created it.
                user = await auth.GetUserByEmailAsync(normalizedEmail, ct);
            }
        }

        if (user == null)
        {
            await WriteProvisioningStatusAsync(employee, null, false, ct);
            return (false, "Employee Firebase Auth account could not be provisioned.");
        }

        var existingRole = user.CustomClaims?.TryGetValue("role", out var roleValue) == true
            ? roleValue?.ToString() ?? string.Empty
            : string.Empty;
        if (existingRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
            existingRole.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
            return (false, "Employee email is linked to an administrative Firebase account; provisioning was blocked.");

        await auth.SetCustomUserClaimsAsync(
            user.Uid,
            new Dictionary<string, object>
            {
                ["role"] = "Employee",
                ["employee_id"] = employee.EmployeeID,
                ["owner_uid"] = OwnerUid
            },
            ct);

        await auth.UpdateUserAsync(new UserRecordArgs
        {
            Uid = user.Uid,
            DisplayName = employee.Name,
            Disabled = employee.IsDeleted ? true : user.Disabled
        }, ct);

        if (employee.IsDeleted)
        {
            await auth.RevokeRefreshTokensAsync(user.Uid, ct);
            await _firebase.DeleteGlobalRecordAsync($"employee_sessions/{user.Uid}", ct);
        }

        await _firebase.SetGlobalRecordAsync(
            $"user_profiles/{user.Uid}",
            new Dictionary<string, object?>
            {
                ["uid"] = user.Uid,
                ["name"] = employee.Name,
                ["email"] = employee.Email,
                ["employeeId"] = employee.EmployeeID.ToString(CultureInfo.InvariantCulture),
                ["role"] = "STAFF",
                ["enabled"] = !employee.IsDeleted && !user.Disabled,
                ["ownerUid"] = OwnerUid,
                ["dataLastModified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            ct);

        await WriteProvisioningStatusAsync(employee, user.Uid, true, ct);

        if (createdAuthAccount && !employee.IsDeleted)
        {
            try
            {
                var resetLink = await auth.GeneratePasswordResetLinkAsync(normalizedEmail);

                // IEmailSender is registered as scoped. This service is intentionally
                // singleton because many realtime/background consumers share it, so
                // resolve the mail sender inside a short-lived scope instead of
                // capturing a scoped dependency in the singleton.
                using var emailScope = _scopeFactory.CreateScope();
                var emailSender = emailScope.ServiceProvider.GetRequiredService<IEmailSender>();

                await emailSender.SendEmailAsync(
                    normalizedEmail,
                    "BioMetric Payroll - Set your employee password",
                    $"""
                    <p>Hello {System.Net.WebUtility.HtmlEncode(employee.Name)},</p>
                    <p>Your BioMetric Payroll employee account has been created.</p>
                    <p>Please use the link below to set your password before signing in to the Android application:</p>
                    <p><a href="{System.Net.WebUtility.HtmlEncode(resetLink)}">Set / Reset Password</a></p>
                    <p>Your login email is <strong>{System.Net.WebUtility.HtmlEncode(normalizedEmail)}</strong>.</p>
                    <p>If you did not expect this account, please contact your administrator.</p>
                    """);

                await WriteProvisioningStatusAsync(employee, user.Uid, true, ct);
                return (true, "Employee saved. Firebase Auth account was created and a password setup email was sent.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase Auth account was created for {Email}, but the password setup email could not be generated/sent.",
                    normalizedEmail);

                return (true, "Employee saved and Firebase Auth account was created. Password setup email could not be sent; check SMTP settings.");
            }
        }

        return (true, employee.IsDeleted
            ? "Employee Auth account deactivated."
            : "Employee Auth/profile synchronized.");
    }

    private static string CreateTemporaryFirebasePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(32);
        var chars = new char[32];

        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];

        return new string(chars);
    }

    public async Task<(bool Exists, bool IsDeleted, long Revision)> GetEmployeeDeletionStateAsync(
        int employeeId,
        CancellationToken ct = default)
    {
        if (employeeId <= 0)
            return (false, false, 0);

        var snapshot = await _firebase.GetOwnerRecordAsync(
            OwnerUid, EmployeesTable, employeeId.ToString(CultureInfo.InvariantCulture), ct);
        if (!snapshot.HasValue || snapshot.Value.ValueKind != JsonValueKind.Object)
            return (false, false, 0);

        var deleted = TryGet(snapshot.Value, "isDeleted", out var deletedValue) &&
                      bool.TryParse(deletedValue.ToString(), out var isDeleted) && isDeleted;
        var revision = 0L;
        if (TryGet(snapshot.Value, "_revision", out var revisionValue))
            long.TryParse(revisionValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out revision);

        return (true, deleted, revision);
    }

    public async Task LogAuditAsync(
        string action,
        string module,
        string? targetId,
        string? details,
        CancellationToken ct = default)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        var userId = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "SYSTEM";
        var email = principal?.Identity?.Name ?? "System";
        var role = principal?.FindFirst(ClaimTypes.Role)?.Value ?? "";
        var logId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";

        await _firebase.SetOwnerRecordAsync(
            OwnerUid,
            "audit_logs",
            logId,
            new Dictionary<string, object?>
            {
                ["logId"] = logId,
                ["shopId"] = "GLOBAL",
                ["action"] = action,
                ["module"] = module,
                ["oldValue"] = null,
                ["newValue"] = details,
                ["userDisplayName"] = email,
                ["userId"] = userId,
                ["actorRole"] = role,
                ["ownerUid"] = OwnerUid,
                ["targetId"] = targetId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            ct);
    }

    private async Task WriteProvisioningStatusAsync(Employee employee, string? firebaseUid, bool authProvisioned, CancellationToken ct)
    {
        await _firebase.SetGlobalRecordAsync(
            $"employee_provisioning_status/{employee.EmployeeID}",
            new Dictionary<string, object?>
            {
                ["employeeId"] = employee.EmployeeID,
                ["email"] = employee.Email ?? string.Empty,
                ["firebaseUid"] = firebaseUid,
                ["authProvisioned"] = authProvisioned,
                ["active"] = !employee.IsDeleted,
                ["status"] = employee.IsDeleted ? "DEACTIVATED" : (authProvisioned ? "SYNCHRONIZED" : "PENDING_AUTH_PROVISIONING"),
                ["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            ct);
    }

    public async Task<CompanySetting?> GetCompanySettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _firebase.GetOwnerRecordAsync(OwnerUid, CompanySettingsTable, "1", ct);
            var res = Deserialize<CompanySetting>(snapshot);
            if (res != null) return res;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Firebase company setting for owner {OwnerUid}", OwnerUid);
        }

        // Fallback to SQLite company setting for this tenant
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
            if (dbFactory != null)
            {
                using var db = dbFactory.CreateDbContext();
                var tenant = db.CompanyTenants.AsNoTracking().FirstOrDefault(t => t.TenantId == OwnerUid);
                var settingId = tenant?.CompanySettingId ?? 1;
                var dbSetting = db.CompanySettings.AsNoTracking().FirstOrDefault(s => s.SettingID == settingId);
                if (dbSetting != null)
                    return dbSetting;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read fallback SQLite company settings for owner {OwnerUid}", OwnerUid);
        }

        return null;
    }

    public async Task<FeatureSettings?> GetFeatureSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureSettingsTable, "1", ct);
            var res = Deserialize<FeatureSettings>(snapshot);
            if (res != null) return res;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Firebase feature setting for owner {OwnerUid}", OwnerUid);
        }

        // Fallback to SQLite feature setting for this tenant
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
            if (dbFactory != null)
            {
                using var db = dbFactory.CreateDbContext();
                var tenant = db.CompanyTenants.AsNoTracking().FirstOrDefault(t => t.TenantId == OwnerUid);
                var featureId = tenant?.FeatureSettingsId ?? 1;
                var dbFeatures = db.FeatureSettings.AsNoTracking().FirstOrDefault(f => f.Id == featureId);
                if (dbFeatures != null)
                    return dbFeatures;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read fallback SQLite feature settings for owner {OwnerUid}", OwnerUid);
        }

        return null;
    }

    public async Task<List<Employee>> GetAllEmployeesIncludingDeletedAsync(CancellationToken ct = default)
    {
        var snapshot = await _firebase.GetOwnerTableAsync(OwnerUid, EmployeesTable, ct);
        if (!snapshot.HasValue)
            return new List<Employee>();

        var result = new List<Employee>();

        if (snapshot.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in snapshot.Value.EnumerateObject())
            {
                try
                {
                    if (item.Value.ValueKind != JsonValueKind.Null)
                        result.Add(ToEmployee(item.Value, item.Name));
                }
                catch
                {
                    // Preserve existing tolerant read behavior for malformed rows.
                }
            }
        }
        else if (snapshot.Value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in snapshot.Value.EnumerateArray())
            {
                var fallbackId = index.ToString(CultureInfo.InvariantCulture);
                index++;
                try
                {
                    if (item.ValueKind != JsonValueKind.Null)
                        result.Add(ToEmployee(item, fallbackId));
                }
                catch
                {
                    // Preserve existing tolerant read behavior for malformed rows.
                }
            }
        }

        return result
            .GroupBy(x => x.EmployeeID)
            .Select(g => g.First())
            .OrderBy(x => x.EmployeeID)
            .ToList();
    }

    private static T? Deserialize<T>(JsonElement? snapshot) where T : class
    {
        if (!snapshot.HasValue || snapshot.Value.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<T>(
            snapshot.Value.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }

    private static Employee ToEmployee(JsonElement value, string fallbackId)
    {
        string? StringValue(string name)
            => TryGet(value, name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

        decimal DecimalValue(string name)
            => decimal.TryParse(StringValue(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

        double DoubleValue(string name)
            => double.TryParse(StringValue(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0d;

        int IntValue(string name)
            => int.TryParse(StringValue(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        bool BoolValue(string name, bool fallback = false)
        {
            var s = StringValue(name);
            return bool.TryParse(s, out var value) ? value : fallback;
        }

        long? UnixValue(string name)
        {
            var s = StringValue(name);
            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return n;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.ToUnixTimeMilliseconds();
            return null;
        }

        DateOnly? DateValue(string name)
        {
            var unix = UnixValue(name);
            if (unix.HasValue)
                return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unix.Value).Date);
            var s = StringValue(name);
            return DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        }

        TimeOnly? TimeValue(string name)
        {
            var s = StringValue(name);
            if (TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                return TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
            return null;
        }

        DayOfWeek? DayValue(string name)
        {
            var s = StringValue(name);
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= 6)
                return (DayOfWeek)n;
            return Enum.TryParse<DayOfWeek>(s, true, out var day) ? day : null;
        }

        var employeeId = IntValue("employeeId");
        if (employeeId <= 0) int.TryParse(fallbackId, out employeeId);

        var employee = new Employee
        {
            EmployeeID = employeeId,
            Name = StringValue("name") ?? string.Empty,
            Role = StringValue("role"),
            MonthlySalary = DecimalValue("salaryRate"),
            BasicSalaryComponent = DecimalValue("basicSalaryComponent"),
            HraComponent = DecimalValue("hraComponent"),
            DaComponent = DecimalValue("daComponent"),
            StandardHours = Math.Max(8, IntValue("standardHours")),
            OT_Rule = StringValue("otRule") ?? "No Overtime",
            OT_FlatRate = DecimalValue("otFlatRate"),
            BiometricID = StringValue("biometricId"),
            StandardBreakMinutes = (int)Math.Round(DoubleValue("breakHours") * 60d),
            ShiftStartTime = TimeValue("shiftStart"),
            ShiftEndTime = TimeValue("shiftEnd"),
            HireDate = DateValue("hireDate"),
            TerminationDate = DateValue("terminateDate"),
            CompOffDayOfWeek = DayValue("compOffDayOfWeek"),
            Email = StringValue("email"),
            PayrollTypeOverride = StringValue("salaryType"),
            SalaryCalculationMethod = StringValue("salaryCalculationMethod"),
            DirectHourlyWage = decimal.TryParse(StringValue("directHourlyWage"), NumberStyles.Any, CultureInfo.InvariantCulture, out var hourly) ? hourly : null,
            EnablePF = BoolValue("enablePf"),
            EnableESI = BoolValue("enableEsi"),
            UAN = StringValue("uanNumber"),
            ESINumber = StringValue("esiNumber"),
            PaidLeaveBalance = DecimalValue("paidLeaveBalance"),
            SickLeaveBalance = DecimalValue("sickLeaveBalance"),
            NightShiftAllowance = DecimalValue("nightShiftAllowance"),
            TdsRatePercent = DecimalValue("tdsRatePercent"),
            IsDeleted = !BoolValue("isActive", true),

            // --- NEW: ANDROID SYNCHRONIZATION ---
            PhoneNumber = StringValue("phone"),
            SalaryType = StringValue("salaryType") ?? "MONTHLY_FIXED",
            DailyAllowance = DecimalValue("dailyAllowance"),
            IsBonusEligible = BoolValue("isBonusEligibleRule", true),
            IsPaidLeaveEligible = BoolValue("isPaidLeaveEligibleRule", true),
            PaidLeaveOnWeekdays = BoolValue("paidLeaveOnWeekdays", true),
            PaidLeaveOnWeekends = BoolValue("paidLeaveOnWeekends", false),
            // ------------------------------------

            BankAccountNumber = StringValue("bankAccountNumber"),
            BankIfscCode = StringValue("bankIfscCode"),
            BankName = StringValue("bankName"),
            AspNetUserId = StringValue("aspNetUserId"),
            EnableShiftRotation = BoolValue("enableShiftRotation"),
            RotationGroup = StringValue("rotationGroup"),
            ShiftRotationPattern = StringValue("shiftRotationPattern"),
            LastRotatedDate = DateValue("lastRotatedDate"),
            CurrentShiftIndex = IntValue("currentShiftIndex"),
            ShiftMode = StringValue("shiftMode") ?? "SINGLE_DAY",
            TenantId = StringValue("tenantId") ?? StringValue("ownerUid")
        };

        employee.DOB = DateValue("dob");
        return employee;
    }

    private static Dictionary<string, object?> ToFirebaseRow(Employee employee, long createdAt)
    {
        static object? DateUnix(DateOnly? value)
            => value.HasValue
                ? new DateTimeOffset(value.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : null;

        static object? TimeString(TimeOnly? value)
            => value?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["employeeId"] = employee.EmployeeID.ToString(CultureInfo.InvariantCulture),
            ["tenantId"] = employee.TenantId,
            ["ownerUid"] = employee.TenantId,
            ["shopId"] = string.Empty,
            ["name"] = employee.Name,
            ["email"] = employee.Email,
            ["biometricId"] = employee.BiometricID,
            ["role"] = employee.Role ?? "Staff",
            ["salaryType"] = employee.SalaryType ?? employee.PayrollTypeOverride ?? "MONTHLY_FIXED",
            ["salaryRate"] = employee.MonthlySalary,
            ["phone"] = employee.PhoneNumber,
            ["dailyAllowance"] = employee.DailyAllowance,
            ["isBonusEligibleRule"] = employee.IsBonusEligible,
            ["isPaidLeaveEligibleRule"] = employee.IsPaidLeaveEligible,
            ["paidLeaveOnWeekdays"] = employee.PaidLeaveOnWeekdays,
            ["paidLeaveOnWeekends"] = employee.PaidLeaveOnWeekends,
            ["basicSalaryComponent"] = employee.BasicSalaryComponent,
            ["hraComponent"] = employee.HraComponent,
            ["daComponent"] = employee.DaComponent,
            ["standardHours"] = employee.StandardHours,
            ["salaryCalculationMethod"] = employee.SalaryCalculationMethod ?? "Pro-Rata Hourly",
            ["directHourlyWage"] = employee.DirectHourlyWage,
            ["paidLeaveBalance"] = employee.PaidLeaveBalance,
            ["sickLeaveBalance"] = employee.SickLeaveBalance,
            ["shiftStart"] = TimeString(employee.ShiftStartTime),
            ["shiftEnd"] = TimeString(employee.ShiftEndTime),
            ["shiftMode"] = employee.ShiftMode ?? "SINGLE_DAY",
            ["breakHours"] = employee.StandardBreakMinutes / 60d,
            ["compOffDayOfWeek"] = employee.CompOffDayOfWeek.HasValue ? (int)employee.CompOffDayOfWeek.Value : null,
            ["otRule"] = employee.OT_Rule,
            ["otFlatRate"] = employee.OT_FlatRate,
            ["hireDate"] = DateUnix(employee.HireDate),
            ["dob"] = DateUnix(employee.DOB),
            ["terminateDate"] = DateUnix(employee.TerminationDate),
            ["enablePf"] = employee.EnablePF,
            ["enableEsi"] = employee.EnableESI,
            ["uanNumber"] = employee.UAN,
            ["esiNumber"] = employee.ESINumber,
            ["nightShiftAllowance"] = employee.NightShiftAllowance,
            ["tdsRatePercent"] = employee.TdsRatePercent,
            ["bankAccountNumber"] = employee.BankAccountNumber,
            ["bankIfscCode"] = employee.BankIfscCode,
            ["bankName"] = employee.BankName,
            ["enableShiftRotation"] = employee.EnableShiftRotation,
            ["rotationGroup"] = employee.RotationGroup,
            ["shiftRotationPattern"] = employee.ShiftRotationPattern,
            ["lastRotatedDate"] = DateUnix(employee.LastRotatedDate),
            ["currentShiftIndex"] = employee.CurrentShiftIndex,
            ["isActive"] = !employee.IsDeleted,
            ["createdAt"] = createdAt,
            ["lastModified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }


    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
