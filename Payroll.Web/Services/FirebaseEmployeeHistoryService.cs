using System.Globalization;
using System.Text.Json;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase SSOT read model for employee-scoped history shown by Web employee
/// screens. This service contains no EF/SQL dependency. Existing UI models and
/// business calculations are preserved; only the history read source changes.
/// </summary>
public sealed class FirebaseEmployeeHistoryService
{
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;

    public FirebaseEmployeeHistoryService(FirebaseRealtimeService firebase, IConfiguration configuration)
    {
        _firebase = firebase;
        _configuration = configuration;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("employee-history", "Admin");

    public async Task<List<SalaryAdvance>> GetAdvancesAsync(int employeeId, CancellationToken ct = default)
    {
        var service = new FirebaseAdvanceService(_firebase, _configuration);
        return await service.GetAsync(employeeId, ct: ct);
    }

    public async Task<List<BonusRecord>> GetBonusesAsync(int employeeId, CancellationToken ct = default)
    {
        var service = new FirebaseBonusService(_firebase, _configuration);
        return await service.GetAsync(employeeId, ct: ct);
    }

    public async Task<List<LeaveRequest>> GetLeaveAsync(int employeeId, CancellationToken ct = default)
    {
        // BANDWIDTH OPTIMIZATION: Query only this employee's leave records instead of
        // downloading the entire leave_requests table and filtering in C#.
        // Uses the same orderBy/equalTo pattern already applied to advances.
        var json = employeeId > 0
            ? await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, "leave_requests", "employeeId", employeeId, ct)
            : await _firebase.GetOwnerTableAsync(OwnerUid, "leave_requests", ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();

        var result = new List<LeaveRequest>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var leave = ParseLeave(item.Value, item.Name, employeeId);
                if (leave != null) result.Add(leave);
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
                var leave = ParseLeave(row, fallbackId, employeeId);
                if (leave != null) result.Add(leave);
            }
        }
        return result.OrderByDescending(x => x.LeaveDate).ToList();
    }

    private static LeaveRequest? ParseLeave(JsonElement item, string key, int employeeId)
    {
        var id = Int(item, "id") ?? Int(item, "leaveRequestId") ?? IntFromKey(key);
        var emp = Int(item, "employeeId") ?? Int(item, "staffId");
        if (id is null || emp != employeeId) return null;
        var leaveDate = UnixDate(item, "startDate") ?? DateTimeFrom(item, "leaveDate");
        var endDate = UnixDate(item, "endDate");
        var status = String(item, "status") ?? "Pending";
        return new LeaveRequest
        {
            LeaveRequestID = id.Value,
            EmployeeID = employeeId,
            LeaveDate = leaveDate,
            EndDate = endDate,
            LeaveType = String(item, "leaveType") ?? string.Empty,
            IsHalfDay = Bool(item, "isHalfDay") ?? false,
            IsApproved = status.Equals("Approved", StringComparison.OrdinalIgnoreCase),
            Notes = String(item, "reason") ?? String(item, "notes")
        };
    }

    public async Task<List<PayrollHistory>> GetPayrollAsync(int employeeId, int minimumYear, CancellationToken ct = default)
    {
        var json = await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, "payroll_history", "employeeId", employeeId, ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array))
            return new();

        var result = new List<PayrollHistory>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var payroll = ParsePayroll(item.Value, item.Name, employeeId, minimumYear);
                if (payroll != null) result.Add(payroll);
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
                var payroll = ParsePayroll(row, fallbackId, employeeId, minimumYear);
                if (payroll != null) result.Add(payroll);
            }
        }
        return result.OrderByDescending(x => x.PayYear).ThenByDescending(x => x.PayMonth).ToList();
    }

    private static PayrollHistory? ParsePayroll(JsonElement item, string key, int employeeId, int minimumYear)
    {
        var emp = Int(item, "employeeId");
        var year = Int(item, "payYear");

        if (emp != employeeId || !year.HasValue || year.Value < minimumYear) return null;

        return new PayrollHistory
        {
            PayrollID = Int(item, "payrollId") ?? IntFromKey(key) ?? 0,
            EmployeeID = employeeId,
            PayMonth = Int(item, "payMonth") ?? 0,
            PayYear = year.Value,
            BaseSalary = Decimal(item, "baseSalary"),
            TotalHoursWorked = Decimal(item, "totalHoursWorked"),
            OvertimePay = Decimal(item, "overtimePay"),
            Deductions_Hours = Decimal(item, "deductionsHours"),
            Deductions_Advance = Decimal(item, "deductionsAdvance"),
            Bonus = Decimal(item, "bonus"),
            NetSalary = Decimal(item, "netSalary") ?? 0m,
            ManualLeaveDays = Int(item, "manualLeaveDays") ?? 0,
            AbsentDays = Int(item, "absentDays") ?? 0,
            TotalPenaltyDuration = DurationFromMilliseconds(item, "totalPenaltyMs"),
            TotalOvertimeDuration = DurationFromMilliseconds(item, "totalOvertimeMs"),
            HourlyRate = Decimal(item, "hourlyRate") ?? 0m,
            BasicComponent = Decimal(item, "basicComponent") ?? 0m,
            PfDeduction = Decimal(item, "pfDeduction") ?? 0m,
            EsiDeduction = Decimal(item, "esiDeduction") ?? 0m,
            EmployerPfContribution = Decimal(item, "employerPfContribution") ?? 0m,
            EmployerEsiContribution = Decimal(item, "employerEsiContribution") ?? 0m,
            PtDeduction = Decimal(item, "ptDeduction") ?? 0m,
            TdsDeduction = Decimal(item, "tdsDeduction") ?? 0m,
            TotalShiftAllowance = Decimal(item, "totalShiftAllowance") ?? 0m
        };
    }

    public async Task<List<AuditLog>> GetEmployeeAuditAsync(int employeeId, int take = 20, CancellationToken ct = default)
    {
        // BANDWIDTH OPTIMIZATION: Query only this employee's audit records instead of
        // downloading the entire audit_logs table and filtering in C#.
        // Firebase orderBy="employeeId"&equalTo=N returns only records for this employee.
        var json = employeeId > 0
            ? await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, "audit_logs", "employeeId", employeeId, ct)
            : await _firebase.GetOwnerTableAsync(OwnerUid, "audit_logs", ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();
        var result = new List<AuditLog>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var audit = ParseAudit(item.Value, item.Name, employeeId);
                if (audit != null) result.Add(audit);
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
                var audit = ParseAudit(row, fallbackId, employeeId);
                if (audit != null) result.Add(audit);
            }
        }
        return result.OrderByDescending(x => x.Timestamp).Take(Math.Max(1, take)).ToList();
    }

    private static AuditLog? ParseAudit(JsonElement item, string key, int employeeId)
    {
        var entityId = String(item, "entityId") ?? String(item, "targetId");
        var entityType = String(item, "entityType") ?? string.Empty;
        if (!string.Equals(entityType, "Employee", StringComparison.OrdinalIgnoreCase) || entityId != employeeId.ToString(CultureInfo.InvariantCulture)) return null;
        return new AuditLog
        {
            LogID = Long(item, "logId") ?? LongFromKey(key),
            Timestamp = DateTimeFrom(item, "timestamp") ?? DateTime.UtcNow,
            UserID = String(item, "userId") ?? "SYSTEM",
            UserEmail = String(item, "userEmail") ?? String(item, "userDisplayName") ?? "System",
            ActionType = String(item, "actionType") ?? String(item, "action") ?? string.Empty,
            EntityType = entityType,
            EntityID = entityId,
            Details = String(item, "details") ?? String(item, "message")
        };
    }

    public async Task<List<DailySummary>> GetDailySummariesAsync(int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var json = await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, "daily_summaries", "employeeId", employeeId, ct);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array)) return new();
        var result = new List<DailySummary>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in json.Value.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                var summary = ParseDailySummary(item.Value, item.Name, employeeId, from, to);
                if (summary != null) result.Add(summary);
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
                var summary = ParseDailySummary(row, fallbackId, employeeId, from, to);
                if (summary != null) result.Add(summary);
            }
        }
        return result.OrderBy(x => x.ShiftDate).ToList();
    }

    private static DailySummary? ParseDailySummary(JsonElement item, string key, int employeeId, DateOnly from, DateOnly to)
    {
        var emp = Int(item, "employeeId");
        var date = DateOnlyFrom(item, "shiftDate") ?? DateOnlyFrom(item, "date");
        if (emp != employeeId || date is null || date < from || date > to) return null;
        return new DailySummary
        {
            SummaryID = Int(item, "summaryId") ?? IntFromKey(key) ?? 0,
            EmployeeID = employeeId,
            ShiftDate = date.Value,
            Status = String(item, "status") ?? "Absent",
            EarnedStandardHours = Decimal(item, "earnedStandardHours") ?? 0m,
            TotalOvertimeDuration = Duration(item, "totalOvertimeMs", "totalOvertimeDuration"),
            TotalPenaltyDuration = Duration(item, "totalPenaltyMs", "totalPenaltyDuration"),
            TotalLateness = Duration(item, "totalLatenessMs", "totalLateness"),
            TotalBreakPenalty = Duration(item, "totalBreakPenaltyMs", "totalBreakPenalty"),
            ScheduledShiftDuration = Duration(item, "scheduledShiftMs", "scheduledShiftDuration"),
            ShiftAllowanceEarned = Decimal(item, "shiftAllowanceEarned") ?? 0m,
            IsManualOverride = Bool(item, "isManualOverride") ?? false
        };
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value) => e.TryGetProperty(name, out value);
    private static string? String(JsonElement e, string name) => TryGet(e, name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
    private static int? Int(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number)) return number;
        return int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (int?)parsed
            : null;
    }

    private static long? Long(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var number)) return number;
        return long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (long?)parsed
            : null;
    }

    private static decimal? Decimal(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number)) return number;
        return decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? (decimal?)parsed
            : null;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(v.ToString(), out var parsed)
            ? (bool?)parsed
            : null;
    }
    private static int? IntFromKey(string key) => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static long LongFromKey(string key) => long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0L;
    private static DateTime? UnixDate(JsonElement e, string name) { var n = Long(e, name); return n.HasValue && n.Value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(n.Value).LocalDateTime : null; }
    private static DateTime? DateTimeFrom(JsonElement e, string name) { var s = String(e, name); return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null; }
    private static DateOnly? DateOnlyFrom(JsonElement e, string name) { var d = UnixDate(e, name); if (d.HasValue) return DateOnly.FromDateTime(d.Value); var s = String(e, name); return DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : null; }
    private static TimeSpan DurationFromMilliseconds(JsonElement e, string name)
    {
        var milliseconds = Decimal(e, name) ?? 0m;

        return TimeSpan.FromMilliseconds(
            (double)milliseconds
        );
    }
    private static TimeSpan Duration(
     JsonElement e,
     string millisecondsName,
     string durationName)
    {
        var milliseconds = Decimal(e, millisecondsName);

        if (milliseconds.HasValue)
        {
            return TimeSpan.FromMilliseconds(
                (double)milliseconds.Value
            );
        }

        var text = String(e, durationName);

        return TimeSpan.TryParse(
            text,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : TimeSpan.Zero;
    }
}
