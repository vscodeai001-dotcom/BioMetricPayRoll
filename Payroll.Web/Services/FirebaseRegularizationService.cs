using System.Globalization;
using System.Text.Json;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase SSOT boundary for attendance regularization requests.
/// The existing AttendanceRegularization SQL model and approval/calculation
/// path remain available as the compatibility/calculation boundary.
/// </summary>
public sealed class FirebaseRegularizationService
{
    private const string Table = "regularizations";
    private const string EmployeeTable = "employees";
    private const string FeatureTable = "feature_settings";

    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;

    public FirebaseRegularizationService(FirebaseRealtimeService firebase, IConfiguration configuration)
    {
        _firebase = firebase;
        _configuration = configuration;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("regularization-service", "Admin");

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var row = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureTable, "1", ct);
        if (row is null || row.Value.ValueKind != JsonValueKind.Object) return true;
        return Bool(row.Value, "enableRegularizationRequest")
            ?? Bool(row.Value, "EnableRegularizationRequest") ?? true;
    }

    public async Task<Employee?> GetEmployeeAsync(int employeeId, CancellationToken ct = default)
    {
        if (employeeId <= 0) return null;
        var row = await _firebase.GetOwnerRecordAsync(OwnerUid, EmployeeTable,
            employeeId.ToString(CultureInfo.InvariantCulture), ct);
        if (row is null || row.Value.ValueKind != JsonValueKind.Object) return null;
        return new Employee
        {
            EmployeeID = employeeId,
            Name = String(row.Value, "name") ?? String(row.Value, "Name") ?? "Employee",
            Email = String(row.Value, "email") ?? String(row.Value, "Email"),
            Role = String(row.Value, "role") ?? String(row.Value, "Role"),
            IsDeleted = !(Bool(row.Value, "isActive") ?? true)
        };
    }

    public async Task<List<Employee>> GetActiveEmployeesAsync(CancellationToken ct = default)
    {
        var json = await _firebase.GetOwnerTableAsync(OwnerUid, EmployeeTable, ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();
        var result = new List<Employee>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var employee = ParseActiveEmployee(item.Value, item.Name);
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
            Name = String(row, "name") ?? String(row, "Name") ?? "Employee",
            Email = String(row, "email") ?? String(row, "Email"),
            Role = String(row, "role") ?? String(row, "Role"),
            IsDeleted = false
        };
    }

    public async Task<List<AttendanceRegularization>> GetAsync(int employeeId = 0, CancellationToken ct = default)
    {
        var json = employeeId > 0
            ? await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, Table, "staffId", employeeId.ToString(CultureInfo.InvariantCulture), ct)
            : await _firebase.GetOwnerTableAsync(OwnerUid, Table, ct);

        // Older records may use employeeId instead of staffId. When an employee
        // query returns no rows, retry the canonical numeric employeeId field.
        if ((json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array) ||
            (json.Value.ValueKind == JsonValueKind.Object && !json.Value.EnumerateObject().Any()) ||
            (json.Value.ValueKind == JsonValueKind.Array && json.Value.GetArrayLength() == 0)) && employeeId > 0)
        {
            json = await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, Table, "employeeId", employeeId, ct);
        }

        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();

        var result = new List<AttendanceRegularization>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var reg = ParseRegularization(item.Value, item.Name, employeeId);
                if (reg != null) result.Add(reg);
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
                var reg = ParseRegularization(row, fallbackId, employeeId);
                if (reg != null) result.Add(reg);
            }
        }
        return result.OrderByDescending(x => x.SubmissionDate).ThenByDescending(x => x.RegularizationId).ToList();
    }

    private static AttendanceRegularization? ParseRegularization(JsonElement row, string key, int employeeId)
    {
        var empId = Int(row, "employeeId") ?? Int(row, "staffId") ?? IntFromKey(key);
        if (!empId.HasValue || empId <= 0 || (employeeId > 0 && empId != employeeId)) return null;

        var date = DateOnlyValue(row, "date") ?? DateOnlyValue(row, "dateOfPunch");
        if (!date.HasValue) return null;
        var requested = TimeOnlyValue(row, "requestedTime", date) ?? TimeOnlyValue(row, "punchTimeNew", date) ?? TimeOnly.MinValue;

        return new AttendanceRegularization
        {
            RegularizationId = Int(row, "regularizationId") ?? Int(row, "id") ?? IntFromKey(key) ?? 0,
            FirebaseKey = key,
            EmployeeId = empId.Value,
            DateOfPunch = date.Value,
            IsInPunch = string.Equals(String(row, "punchType"), "IN", StringComparison.OrdinalIgnoreCase)
                || Bool(row, "isInPunch") == true,
            PunchTimeNew = requested,
            Reason = String(row, "reason") ?? "",
            Status = String(row, "status") ?? "Pending",
            AdminRemarks = String(row, "adminRemarks") ?? String(row, "AdminRemarks"),
            SubmissionDate = DateTimeValue(row, "submittedAt") ?? DateTimeValue(row, "submissionDate") ?? DateTime.Now
        };
    }

    public async Task<bool> SaveAsync(AttendanceRegularization request, CancellationToken ct = default)
    {
        if (request.EmployeeId <= 0 || request.DateOfPunch == default || string.IsNullOrWhiteSpace(request.Reason)) return false;
        var key = !string.IsNullOrWhiteSpace(request.FirebaseKey)
            ? request.FirebaseKey!
            : request.RegularizationId != 0
                ? request.RegularizationId.ToString(CultureInfo.InvariantCulture)
                : (-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture);

        var staffName = (await GetEmployeeAsync(request.EmployeeId, ct))?.Name ?? "Employee";
        var row = new Dictionary<string, object?>
        {
            ["id"] = request.RegularizationId != 0 ? (object)request.RegularizationId : key,
            ["regularizationId"] = request.RegularizationId != 0 ? (object)request.RegularizationId : key,
            ["staffId"] = request.EmployeeId.ToString(CultureInfo.InvariantCulture),
            ["employeeId"] = request.EmployeeId,
            ["staffName"] = staffName,
            ["employeeName"] = staffName,
            ["date"] = request.DateOfPunch.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dateOfPunch"] = request.DateOfPunch.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["punchType"] = request.IsInPunch ? "IN" : "OUT",
            ["isInPunch"] = request.IsInPunch,
            ["requestedTime"] = new DateTimeOffset(request.DateOfPunch.ToDateTime(request.PunchTimeNew)).ToUnixTimeMilliseconds(),
            ["punchTimeNew"] = request.PunchTimeNew.ToString("HH:mm"),
            ["reason"] = request.Reason.Trim(),
            ["status"] = string.IsNullOrWhiteSpace(request.Status) ? "Pending" : request.Status,
            ["adminRemarks"] = request.AdminRemarks,
            ["submittedAt"] = new DateTimeOffset(request.SubmissionDate).ToUnixTimeMilliseconds(),
            ["submissionDate"] = new DateTimeOffset(request.SubmissionDate).ToUnixTimeMilliseconds(),
            ["_entity"] = "AttendanceRegularization",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await _firebase.SetOwnerRecordAsync(OwnerUid, Table, key, row, ct);
        if (ok)
        {
            request.FirebaseKey = key;
            await _firebase.PublishLocalApplicationChangeAsync(OwnerUid, "AttendanceRegularization", "MODIFIED", key, ct);
        }
        return ok;
    }

    private static string? String(JsonElement row, string name) => TryGet(row, name, out var p) ? p.ToString() : null;
    private static int? Int(JsonElement row, string name) => TryGet(row, name, out var p)
        ? p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : int.TryParse(p.ToString(), out var v) ? v : null : null;
    private static int? IntFromKey(string key) => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static bool? Bool(JsonElement row, string name) => TryGet(row, name, out var p)
        ? p.ValueKind == JsonValueKind.True ? true : p.ValueKind == JsonValueKind.False ? false : bool.TryParse(p.ToString(), out var v) ? v : null : null;
    private static DateTime? DateTimeValue(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return DateTime.TryParse(p.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    }
    private static DateOnly? DateOnlyValue(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (DateOnly.TryParse(p.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var ms)) return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Date);
        return null;
    }
    private static TimeOnly? TimeOnlyValue(JsonElement row, string name, DateOnly? date)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var ms)) return TimeOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime);
        return TimeOnly.TryParse(p.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }
    private static bool TryGet(JsonElement row, string name, out JsonElement value)
    {
        if (row.TryGetProperty(name, out value)) return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return row.TryGetProperty(pascal, out value);
    }
}
