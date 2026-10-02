using System.Globalization;
using System.Text.Json;
using Payroll.Shared;
using Payroll.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase SSOT boundary for Salary Advances.
/// Existing SalaryAdvance model, screens and payroll calculations remain intact.
/// FirebaseKey is only the transport key for records created by Android using UUIDs.
/// In Offline Standalone Mode, reads and writes directly to local SQLite database.
/// </summary>
public sealed class FirebaseAdvanceService
{
    private const string Table = "advance_payments";
    private const string EmployeeTable = "employees";
    private const string FeatureTable = "feature_settings";

    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory? _scopeFactory;

    public FirebaseAdvanceService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        IServiceScopeFactory? scopeFactory = null)
    {
        _firebase = firebase;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("advance-service", "Admin");

    private static bool IsAdmin(System.Security.Claims.ClaimsPrincipal user) =>
        user.IsInRole("Admin") || user.IsInRole("SuperAdmin") ||
        user.IsInRole("ADMIN") || user.IsInRole("SUPER_ADMIN");

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var row = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureTable, "1", ct);
        if (row is null || row.Value.ValueKind != JsonValueKind.Object) return true;
        return Bool(row.Value, "enableSalaryAdvance")
            ?? Bool(row.Value, "EnableSalaryAdvance")
            ?? true;
    }

    public async Task<bool> CanEmployeeViewAsync(CancellationToken ct = default)
    {
        var row = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureTable, "1", ct);
        if (row is null || row.Value.ValueKind != JsonValueKind.Object) return true;
        return Bool(row.Value, "employeeCanViewAdvance")
            ?? Bool(row.Value, "EmployeeCanViewAdvance")
            ?? true;
    }

    public async Task<List<Employee>> GetActiveEmployeesAsync(CancellationToken ct = default)
    {
        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    return await db.Employees.AsNoTracking().Where(e => !e.IsDeleted).OrderBy(e => e.Name).ToListAsync(ct);
                }
            }
        }

        var json = await _firebase.GetOwnerTableAsync(OwnerUid, EmployeeTable, ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();

        var result = new List<Employee>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                var row = item.Value;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var employee = ParseActiveEmployee(row, item.Name);
                if (employee != null) result.Add(employee);
            }
        }
        else
        {
            var index = 0;
            foreach (var row in json.Value.EnumerateArray())
            {
                var fallbackId = index.ToString(CultureInfo.InvariantCulture);
                index++;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var employee = ParseActiveEmployee(row, fallbackId);
                if (employee != null) result.Add(employee);
            }
        }
        return result.OrderBy(e => e.Name).ToList();
    }

    private static Employee? ParseActiveEmployee(JsonElement row, string key)
    {
        var id = Int(row, "employeeId") ?? IntFromKey(key);
        if (!id.HasValue || id <= 0) return null;
        if (!(Bool(row, "isActive") ?? true)) return null;
        return new Employee
        {
            EmployeeID = id.Value,
            Name = String(row, "name") ?? "Employee",
            Email = String(row, "email"),
            Role = String(row, "role"),
            IsDeleted = false
        };
    }

    public async Task<List<SalaryAdvance>> GetAsync(
        int employeeId = 0,
        DateTime? from = null,
        DateTime? to = null,
        bool unpaidOnly = false,
        CancellationToken ct = default)
    {
        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    var query = db.SalaryAdvances.AsNoTracking().AsQueryable();
                    if (employeeId > 0) query = query.Where(a => a.EmployeeID == employeeId);
                    if (from.HasValue) query = query.Where(a => a.AdvanceDate >= from.Value);
                    if (to.HasValue) query = query.Where(a => a.AdvanceDate <= to.Value);
                    if (unpaidOnly) query = query.Where(a => a.PayrollID_Paid == null);
                    return await query.OrderByDescending(a => a.AdvanceDate).ThenBy(a => a.EmployeeID).ToListAsync(ct);
                }
            }
        }

        var json = employeeId > 0
            ? await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, Table, "employeeId", employeeId, ct)
            : await _firebase.GetOwnerTableAsync(OwnerUid, Table, ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();

        var result = new List<SalaryAdvance>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var advance = ParseAdvance(item.Value, item.Name, employeeId, from, to, unpaidOnly);
                if (advance != null) result.Add(advance);
            }
        }
        else
        {
            var index = 0;
            foreach (var row in json.Value.EnumerateArray())
            {
                var fallbackId = index.ToString(CultureInfo.InvariantCulture);
                index++;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var advance = ParseAdvance(row, fallbackId, employeeId, from, to, unpaidOnly);
                if (advance != null) result.Add(advance);
            }
        }

        // Deduplicate solely by unique record identity (FirebaseKey or positive AdvanceID)
        // Never delete or group distinct advances on the same date/amount
        var distinctAdvances = result
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.FirebaseKey)
                ? x.FirebaseKey
                : x.AdvanceID > 0
                    ? x.AdvanceID.ToString(CultureInfo.InvariantCulture)
                    : Guid.NewGuid().ToString("N"))
            .Select(g => g.First())
            .OrderByDescending(x => x.AdvanceDate)
            .ThenBy(x => x.EmployeeID)
            .ToList();

        return distinctAdvances;
    }

    private static SalaryAdvance? ParseAdvance(JsonElement row, string key, int employeeId, DateTime? from, DateTime? to, bool unpaidOnly)
    {
        var empId = Int(row, "employeeId") ?? Int(row, "EmployeeId");
        if (!empId.HasValue || empId <= 0) return null;
        if (employeeId > 0 && empId != employeeId) return null;

        var date = Date(row, "date") ?? Date(row, "advanceDate");
        if (from.HasValue && (!date.HasValue || date.Value.Date < from.Value.Date)) return null;
        if (to.HasValue && (!date.HasValue || date.Value.Date > to.Value.Date)) return null;

        var recovered = Bool(row, "isRecovered") ?? Bool(row, "IsRecovered") ?? false;
        var payrollId = Int(row, "payrollIdPaid") ?? Int(row, "PayrollID_Paid");
        if (unpaidOnly && (recovered || payrollId.HasValue)) return null;

        var status = String(row, "status") ?? String(row, "Status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Deleted", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var numericId = Int(row, "advanceId") ?? Int(row, "AdvanceID") ?? IntFromKey(key) ?? StableInt(key);
        return new SalaryAdvance
        {
            AdvanceID = numericId,
            EmployeeID = empId.Value,
            AdvanceDate = date,
            Amount = Decimal(row, "amount") ?? Decimal(row, "Amount") ?? 0m,
            AdvanceType = String(row, "advanceType") ?? String(row, "AdvanceType") ?? String(row, "type") ?? "General",
            PayrollID_Paid = payrollId,
            FirebaseKey = key
        };
    }

    public async Task<bool> SaveAsync(SalaryAdvance advance, CancellationToken ct = default)
    {
        if (advance.EmployeeID <= 0 || advance.Amount <= 0m || !advance.AdvanceDate.HasValue) return false;

        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var appMode = scope.ServiceProvider.GetService<IAppModeService>();
            if (appMode != null && await appMode.IsOfflineModeAsync())
            {
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    if (advance.AdvanceID <= 0)
                    {
                        var maxId = await db.SalaryAdvances.Select(a => (int?)a.AdvanceID).MaxAsync(ct) ?? 0;
                        advance.AdvanceID = maxId + 1;
                        await db.SalaryAdvances.AddAsync(advance, ct);
                    }
                    else
                    {
                        var existing = await db.SalaryAdvances.FirstOrDefaultAsync(a => a.AdvanceID == advance.AdvanceID, ct);
                        if (existing != null)
                        {
                            db.Entry(existing).CurrentValues.SetValues(advance);
                        }
                        else
                        {
                            await db.SalaryAdvances.AddAsync(advance, ct);
                        }
                    }
                    await db.SaveChangesAsync(ct);
                    return true;
                }
            }
        }

        var key = !string.IsNullOrWhiteSpace(advance.FirebaseKey)
            ? advance.FirebaseKey!
            : advance.AdvanceID > 0
                ? advance.AdvanceID.ToString(CultureInfo.InvariantCulture)
                : Guid.NewGuid().ToString("N");

        var employee = await _firebase.GetOwnerRecordAsync(OwnerUid, EmployeeTable, advance.EmployeeID.ToString(CultureInfo.InvariantCulture), ct);
        var shopId = employee.HasValue && employee.Value.ValueKind == JsonValueKind.Object
            ? String(employee.Value, "shopId") ?? String(employee.Value, "ShopId") ?? ""
            : "";

        var row = new Dictionary<string, object?>
        {
            ["advanceId"] = advance.AdvanceID > 0 ? (object)advance.AdvanceID : key,
            ["id"] = advance.AdvanceID > 0 ? (object)advance.AdvanceID : key,
            ["employeeId"] = advance.EmployeeID,
            ["staffId"] = advance.EmployeeID.ToString(CultureInfo.InvariantCulture),
            ["shopId"] = shopId,
            ["amount"] = advance.Amount,
            ["date"] = new DateTimeOffset(advance.AdvanceDate.Value).ToUnixTimeMilliseconds(),
            ["advanceDate"] = advance.AdvanceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["isRecovered"] = advance.PayrollID_Paid.HasValue,
            ["recoveryPaymentId"] = advance.PayrollID_Paid?.ToString(CultureInfo.InvariantCulture),
            ["advanceType"] = advance.AdvanceType ?? "General",
            ["payrollIdPaid"] = advance.PayrollID_Paid,
            ["_entity"] = "SalaryAdvance",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await _firebase.SetOwnerRecordAsync(OwnerUid, Table, key, row, ct);
        if (ok)
        {
            advance.FirebaseKey = key;
            await _firebase.PublishLocalApplicationChangeAsync(OwnerUid, "AdvancePayment", "MODIFIED", key, ct);
        }
        return ok;
    }

    public async Task<bool> DeleteAsync(SalaryAdvance advance, CancellationToken ct = default)
    {
        if (advance.PayrollID_Paid.HasValue) return false;
        var key = advance.FirebaseKey ?? (advance.AdvanceID > 0 ? advance.AdvanceID.ToString(CultureInfo.InvariantCulture) : null);
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (_scopeFactory != null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = await dbFactory.CreateDbContextAsync(ct);
                    SalaryAdvance? existing = null;
                    if (advance.AdvanceID > 0)
                    {
                        existing = await db.SalaryAdvances.FirstOrDefaultAsync(a => a.AdvanceID == advance.AdvanceID, ct);
                    }
                    if (existing == null && advance.EmployeeID > 0 && advance.Amount > 0)
                    {
                        var candidates = await db.SalaryAdvances
                            .Where(a => a.EmployeeID == advance.EmployeeID && a.Amount == advance.Amount)
                            .ToListAsync(ct);
                        existing = candidates.FirstOrDefault(a =>
                            advance.AdvanceDate.HasValue && a.AdvanceDate.HasValue &&
                            Math.Abs((a.AdvanceDate.Value - advance.AdvanceDate.Value).TotalMinutes) < 2)
                            ?? candidates.FirstOrDefault();
                    }
                    if (existing != null)
                    {
                        db.SalaryAdvances.Remove(existing);
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch
            {
                // Best-effort local cleanup
            }
        }

        var ok = await _firebase.DeleteOwnerRecordAsync(OwnerUid, Table, key, ct);
        if (ok) await _firebase.PublishLocalApplicationChangeAsync(OwnerUid, "AdvancePayment", "DELETED", key, ct);
        return ok;
    }

    public async Task<SalaryAdvance?> GetEmployeeAdvanceAsync(int employeeId, string firebaseKey, CancellationToken ct = default)
    {
        if (employeeId <= 0 || string.IsNullOrWhiteSpace(firebaseKey)) return null;
        var row = await _firebase.GetOwnerRecordAsync(OwnerUid, Table, firebaseKey, ct);
        if (row is null || row.Value.ValueKind != JsonValueKind.Object) return null;
        var emp = Int(row.Value, "employeeId") ?? Int(row.Value, "EmployeeId");
        if (emp != employeeId) return null;
        return (await GetAsync(employeeId, ct: ct)).FirstOrDefault(x => x.FirebaseKey == firebaseKey);
    }

    private static string? String(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
            return null;

        return value.ToString();
    }

    private static int? Int(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var number))
                return number;

            return null;
        }

        var text = value.ToString();

        if (int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? IntFromKey(string key)
    {
        if (int.TryParse(
                key,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        return null;
    }

    private static decimal? Decimal(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            return number;
        }

        var text = value.ToString();

        if (decimal.TryParse(
                text,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? Bool(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.True)
            return true;

        if (value.ValueKind == JsonValueKind.False)
            return false;

        var text = value.ToString();

        if (bool.TryParse(text, out var parsed))
            return parsed;

        return null;
    }

    private static DateTime? Date(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var milliseconds))
            {
                try
                {
                    return DateTimeOffset
                        .FromUnixTimeMilliseconds(milliseconds)
                        .LocalDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            return null;
        }

        var text = value.ToString();

        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var dateTime))
        {
            return dateTime;
        }

        if (long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var millisecondsFromText))
        {
            try
            {
                return DateTimeOffset
                    .FromUnixTimeMilliseconds(millisecondsFromText)
                    .LocalDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static int StableInt(string value)
    {
        unchecked
        {
            var hash = 23;

            foreach (var character in value)
                hash = hash * 31 + character;

            hash &= int.MaxValue;

            return hash == 0 ? 1 : hash;
        }
    }

    private static bool TryGet(
        JsonElement row,
        string name,
        out JsonElement value)
    {
        if (row.TryGetProperty(name, out value))
            return true;

        if (string.IsNullOrEmpty(name))
        {
            value = default;
            return false;
        }

        var pascal =
            char.ToUpperInvariant(name[0]) +
            name[1..];

        return row.TryGetProperty(pascal, out value);
    }
}