using System.Text.Json;
using Payroll.Shared.Firebase;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase-only employee dependency check. It preserves the existing
/// Employee Management deletion guard while removing SQL as the runtime
/// source for this screen.
/// </summary>
public sealed class FirebaseEmployeeDeletionService
{
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseEmployeeDeletionService> _logger;

    public FirebaseEmployeeDeletionService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseEmployeeDeletionService> logger)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("employee-deletion", "Admin");

    public sealed class EmployeeDeletionDependencies
    {
        public int EmployeeID { get; set; }
        public string EmployeeName { get; set; } = "";
        public bool CanDelete { get; set; }
        public string BlockReason { get; set; } = "";
        public int AttendanceLogsCount { get; set; }
        public int PayrollHistoryCount { get; set; }
        public int SalaryAdvancesCount { get; set; }
        public int LeaveRequestsCount { get; set; }
        public int ShiftSchedulesCount { get; set; }
        public int BonusRecordsCount { get; set; }
        public int DailySummariesCount { get; set; }

        public int GetTotalDependencies() =>
            AttendanceLogsCount + PayrollHistoryCount + SalaryAdvancesCount +
            LeaveRequestsCount + ShiftSchedulesCount + BonusRecordsCount + DailySummariesCount;

        public List<(string Category, int Count)> GetDependenciesList()
        {
            var list = new List<(string, int)>();
            if (AttendanceLogsCount > 0) list.Add(("Attendance Logs", AttendanceLogsCount));
            if (PayrollHistoryCount > 0) list.Add(("Payroll History", PayrollHistoryCount));
            if (SalaryAdvancesCount > 0) list.Add(("Salary Advances", SalaryAdvancesCount));
            if (LeaveRequestsCount > 0) list.Add(("Leave Requests", LeaveRequestsCount));
            if (ShiftSchedulesCount > 0) list.Add(("Shift Schedules", ShiftSchedulesCount));
            if (BonusRecordsCount > 0) list.Add(("Bonus Records", BonusRecordsCount));
            if (DailySummariesCount > 0) list.Add(("Daily Summaries", DailySummariesCount));
            return list;
        }
    }

    public async Task<EmployeeDeletionDependencies> CheckDeletionDependenciesAsync(
        int employeeId,
        CancellationToken ct = default)
    {
        var result = new EmployeeDeletionDependencies { EmployeeID = employeeId };
        try
        {
            var employees = await _firebase.GetOwnerTableAsync(OwnerUid, "employees", ct);
            if (!employees.HasValue || employees.Value.ValueKind != JsonValueKind.Object)
            {
                result.CanDelete = false;
                result.BlockReason = "Firebase employee data is unavailable.";
                return result;
            }

            var employee = employees.Value.EnumerateObject()
                .FirstOrDefault(x => string.Equals(x.Name, employeeId.ToString(), StringComparison.Ordinal));
            if (employee.Equals(default(JsonProperty)))
            {
                result.CanDelete = false;
                result.BlockReason = "Employee not found in Firebase.";
                return result;
            }

            result.EmployeeName = GetString(employee.Value, "name") ?? "Employee";

            result.AttendanceLogsCount = await CountForEmployeeAsync("attendance", employeeId, ct);
            result.PayrollHistoryCount = await CountForEmployeeAsync("payroll_history", employeeId, ct);
            result.SalaryAdvancesCount = await CountForEmployeeAsync("advance_payments", employeeId, ct);
            result.LeaveRequestsCount = await CountForEmployeeAsync("leave_requests", employeeId, ct, "employeeId", "staffId");
            result.ShiftSchedulesCount = await CountForEmployeeAsync("shift_schedules", employeeId, ct);
            result.BonusRecordsCount = await CountForEmployeeAsync("bonus_records", employeeId, ct);
            result.DailySummariesCount = await CountForEmployeeAsync("daily_summaries", employeeId, ct);

            var total = result.GetTotalDependencies();
            result.CanDelete = total == 0;
            if (!result.CanDelete)
            {
                var summary = string.Join(", ", result.GetDependenciesList().Select(x => $"{x.Count} {x.Category}"));
                result.BlockReason = $"Cannot delete employee. Related records exist: {summary}. Please delete these records first.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firebase deletion dependency check failed for employee {EmployeeId}.", employeeId);
            result.CanDelete = false;
            result.BlockReason = $"Error checking dependencies: {ex.Message}";
        }

        return result;
    }

    private async Task<int> CountForEmployeeAsync(
        string table,
        int employeeId,
        CancellationToken ct,
        params string[] preferredFields)
    {
        var snapshot = await _firebase.GetOwnerTableAsync(OwnerUid, table, ct);
        if (!snapshot.HasValue || snapshot.Value.ValueKind != JsonValueKind.Object)
            return 0;

        var fields = preferredFields.Length == 0
            ? new[] { "employeeId", "staffId" }
            : preferredFields;
        var id = employeeId.ToString();

        return snapshot.Value.EnumerateObject().Count(item =>
            fields.Any(field => TryGet(item.Value, field, out var value) &&
                string.Equals(GetString(value), id, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? GetString(JsonElement value, string? ignored = null)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

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
