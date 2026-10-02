using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Final reconciliation bridge for Employee lifecycle changes.
/// The existing Web/SQL Employee row remains the business-data authority for
/// fields and calculations. This service mirrors the same employee identity to
/// Firebase so Web and Android observe one cross-platform state in realtime.
/// It never invents a password or silently creates a Firebase Auth account when
/// no credential-bearing user exists. Such accounts must be created through the
/// privileged User Management boundary (1010).
/// </summary>
public sealed class FirebaseEmployeeProvisioningReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseEmployeeProvisioningReconciliationService> _logger;

    public FirebaseEmployeeProvisioningReconciliationService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseEmployeeProvisioningReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Employee Firebase reconciliation cycle failed; the next cycle will retry.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var employees = await db.Employees.AsNoTracking().ToListAsync(ct);

        foreach (var employee in employees)
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileEmployeeAsync(employee, ct);
        }
    }

    public async Task<EmployeeReconciliationResult> ReconcileEmployeeAsync(Employee employee, CancellationToken ct = default)
    {
        if (employee.EmployeeID <= 0)
            return new(false, "Employee ID is invalid.");

        UserRecord? firebaseUser = await FindFirebaseUserAsync(employee, ct);

        // Resolve target ownerUid for this employee
        string ownerUid = employee.TenantId?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(ownerUid) && firebaseUser?.CustomClaims != null &&
            firebaseUser.CustomClaims.TryGetValue("owner_uid", out var existingOwner) &&
            !string.IsNullOrWhiteSpace(existingOwner?.ToString()))
        {
            ownerUid = existingOwner.ToString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(ownerUid))
        {
            ownerUid = _firebase.ResolveOwnerUid(employee.Email, "Employee");
        }

        if (string.IsNullOrWhiteSpace(ownerUid))
        {
            ownerUid = Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid;
        }

        var firebaseRow = BuildEmployeeRow(employee);
        firebaseRow["tenantId"] = ownerUid;
        firebaseRow["ownerUid"] = ownerUid;
        var employeeWritten = await _firebase.SetOwnerRecordAsync(
            ownerUid, "employees", employee.EmployeeID.ToString(), firebaseRow, ct);
        if (!employeeWritten)
            return new(false, "Firebase employee record synchronization failed.");

        if (firebaseUser == null)
        {
            await _firebase.SetGlobalRecordAsync(
                $"employee_provisioning_status/{employee.EmployeeID}",
                new Dictionary<string, object?>
                {
                    ["employeeId"] = employee.EmployeeID,
                    ["email"] = employee.Email ?? string.Empty,
                    ["firebaseUid"] = null,
                    ["authProvisioned"] = false,
                    ["active"] = !employee.IsDeleted,
                    ["status"] = employee.IsDeleted ? "DEACTIVATED_NO_AUTH" : "PENDING_AUTH_PROVISIONING",
                    ["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, ct);

            return new(true, employee.IsDeleted
                ? "Employee record synchronized; no Firebase Auth account was linked."
                : "Employee record synchronized; Firebase Auth provisioning remains pending.");
        }

        var auth = await _firebase.GetFirebaseAuthAsync(ct)
            ?? throw new InvalidOperationException("Firebase Authentication is not configured.");

        var active = !employee.IsDeleted;
        var existingRole = firebaseUser.CustomClaims?.TryGetValue("role", out var existingRoleValue) == true
            ? existingRoleValue?.ToString() ?? string.Empty
            : string.Empty;
        if (existingRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
            existingRole.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Employee {EmployeeId} is linked to Firebase administrative account {Uid}; employee reconciliation skipped to protect the admin identity.",
                employee.EmployeeID, firebaseUser.Uid);
            return new(false, "Employee is linked to an administrative Firebase account; reconciliation was blocked.");
        }

        var claims = new Dictionary<string, object>
        {
            ["role"] = "Employee",
            ["employee_id"] = employee.EmployeeID,
            ["owner_uid"] = ownerUid
        };
        await auth.SetCustomUserClaimsAsync(firebaseUser.Uid, claims, ct);

        await auth.UpdateUserAsync(new UserRecordArgs
        {
            Uid = firebaseUser.Uid,
            DisplayName = employee.Name,
            // An active employee must not be silently re-enabled if an Admin
            // intentionally disabled the Firebase Auth account. Deactivation
            // is one-way from the Employee lifecycle; re-enable remains an
            // explicit User Management operation.
            Disabled = active ? firebaseUser.Disabled : true
        }, ct);

        if (!active)
        {
            await auth.RevokeRefreshTokensAsync(firebaseUser.Uid, ct);
            await _firebase.DeleteGlobalRecordAsync($"employee_sessions/{firebaseUser.Uid}", ct);
        }

        var profile = new Dictionary<string, object?>
        {
            ["uid"] = firebaseUser.Uid,
            ["name"] = employee.Name,
            ["email"] = employee.Email ?? firebaseUser.Email ?? string.Empty,
            ["employeeId"] = employee.EmployeeID.ToString(),
            ["role"] = "STAFF",
            ["enabled"] = active && !firebaseUser.Disabled,
            ["ownerUid"] = ownerUid,
            ["dataLastModified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _firebase.SetGlobalRecordAsync($"user_profiles/{firebaseUser.Uid}", profile, ct);

        await _firebase.SetGlobalRecordAsync(
            $"employee_provisioning_status/{employee.EmployeeID}",
            new Dictionary<string, object?>
            {
                ["employeeId"] = employee.EmployeeID,
                ["email"] = employee.Email ?? firebaseUser.Email ?? string.Empty,
                ["firebaseUid"] = firebaseUser.Uid,
                ["authProvisioned"] = true,
                ["active"] = active,
                ["status"] = active ? "SYNCHRONIZED" : "DEACTIVATED",
                ["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, ct);

        return new(true, active ? "Employee/Auth/Profile synchronized." : "Employee deactivated, Auth disabled, tokens revoked and session removed.");
    }

    private async Task<UserRecord?> FindFirebaseUserAsync(Employee employee, CancellationToken ct)
    {
        var auth = await _firebase.GetFirebaseAuthAsync(ct);
        if (auth == null) return null;

        if (!string.IsNullOrWhiteSpace(employee.AspNetUserId))
        {
            try
            {
                return await auth.GetUserAsync(employee.AspNetUserId, ct);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                // Fall through to email lookup for legacy rows whose Firebase UID
                // was not stored in the Web Identity link.
            }
        }

        if (string.IsNullOrWhiteSpace(employee.Email)) return null;
        try
        {
            return await auth.GetUserByEmailAsync(employee.Email.Trim(), ct);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildEmployeeRow(Employee employee)
    {
        static long? Unix(DateOnly? value) => value.HasValue
            ? new DateTimeOffset(value.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds()
            : null;

        static string? Time(TimeOnly? value) => value?.ToString("HH:mm:ss");

        return new Dictionary<string, object?>
        {
            ["employeeId"] = employee.EmployeeID.ToString(),
            ["name"] = employee.Name,
            ["email"] = employee.Email,
            ["role"] = employee.Role ?? "Staff",
            ["salaryRate"] = employee.MonthlySalary,
            ["basicSalaryComponent"] = employee.BasicSalaryComponent,
            ["hraComponent"] = employee.HraComponent,
            ["daComponent"] = employee.DaComponent,
            ["standardHours"] = employee.StandardHours,
            ["salaryType"] = employee.PayrollTypeOverride ?? "MONTHLY_FIXED",
            ["salaryCalculationMethod"] = employee.SalaryCalculationMethod ?? "Pro-Rata Hourly",
            ["biometricId"] = employee.BiometricID,
            ["directHourlyWage"] = employee.DirectHourlyWage,
            ["paidLeaveBalance"] = employee.PaidLeaveBalance,
            ["sickLeaveBalance"] = employee.SickLeaveBalance,
            ["shiftStart"] = Time(employee.ShiftStartTime),
            ["shiftEnd"] = Time(employee.ShiftEndTime),
            ["shiftMode"] = employee.ShiftMode ?? "SINGLE_DAY",
            ["breakHours"] = employee.StandardBreakMinutes / 60.0,
            ["hireDate"] = Unix(employee.HireDate),
            ["terminateDate"] = Unix(employee.TerminationDate),
            ["compOffDayOfWeek"] = employee.CompOffDayOfWeek.HasValue ? (int)employee.CompOffDayOfWeek.Value : null,
            ["otRule"] = employee.OT_Rule,
            ["otFlatRate"] = employee.OT_FlatRate,
            ["nightShiftAllowance"] = employee.NightShiftAllowance,
            ["enablePf"] = employee.EnablePF,
            ["enableEsi"] = employee.EnableESI,
            ["uanNumber"] = employee.UAN,
            ["esiNumber"] = employee.ESINumber,
            ["tdsRatePercent"] = employee.TdsRatePercent,
            ["bankAccountNumber"] = employee.BankAccountNumber,
            ["bankIfscCode"] = employee.BankIfscCode,
            ["bankName"] = employee.BankName,
            ["enableShiftRotation"] = employee.EnableShiftRotation,
            ["rotationGroup"] = employee.RotationGroup,
            ["shiftRotationPattern"] = employee.ShiftRotationPattern,
            ["lastRotatedDate"] = Unix(employee.LastRotatedDate),
            ["currentShiftIndex"] = employee.CurrentShiftIndex,
            ["isActive"] = !employee.IsDeleted,
            ["lastModified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}

public sealed record EmployeeReconciliationResult(bool Success, string Message);
