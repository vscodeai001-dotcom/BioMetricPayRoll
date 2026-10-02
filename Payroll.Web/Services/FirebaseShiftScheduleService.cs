using System.Globalization;
using System.Text.Json;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase SSOT implementation of shift scheduling.
/// The existing ShiftSchedule domain model and UI remain unchanged. This service
/// only replaces the persistence transport for this module.
/// </summary>
public sealed class FirebaseShiftScheduleService
{
    private const string Table = "shift_schedules";
    private const string EmployeeTable = "employees";
    private const string FeatureTable = "feature_settings";

    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseShiftScheduleService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public FirebaseShiftScheduleService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseShiftScheduleService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("shift-service", "Admin");

    public async Task<List<ShiftSchedule>> GetSchedulesAsync(
        int employeeId = 0,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var json = employeeId > 0
            ? await _firebase.GetOwnerTableByChildValueAsync(OwnerUid, Table, "employeeId", employeeId, cancellationToken)
            : await _firebase.GetOwnerTableAsync(OwnerUid, Table, cancellationToken);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array))
            return new List<ShiftSchedule>();

        var result = new List<ShiftSchedule>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var schedule = ParseScheduleRecord(property.Value, property.Name, employeeId, from, to);
                if (schedule != null) result.Add(schedule);
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
                var schedule = ParseScheduleRecord(row, fallbackId, employeeId, from, to);
                if (schedule != null) result.Add(schedule);
            }
        }

        return result
            .OrderBy(x => x.ShiftDate)
            .ThenBy(x => x.EmployeeID)
            .ThenBy(x => x.StartTime)
            .ToList();
    }

    private static ShiftSchedule? ParseScheduleRecord(JsonElement row, string key, int employeeId, DateOnly? from, DateOnly? to)
    {
        var schedule = ParseSchedule(row, key);
        if (schedule == null) return null;
        if (employeeId > 0 && schedule.EmployeeID != employeeId) return null;

        // Recurring patterns are retained as context exactly like the
        // existing Web scheduler. Concrete shifts are filtered by date.
        if (!schedule.IsRecurringPattern)
        {
            if (from.HasValue && schedule.ShiftDate < from.Value) return null;
            if (to.HasValue && schedule.ShiftDate > to.Value) return null;
        }

        return schedule;
    }

    public async Task<Employee?> GetEmployeeAsync(
        int employeeId,
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0) return null;

        var json = await _firebase.GetOwnerRecordAsync(
            OwnerUid, EmployeeTable, employeeId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return null;

        var id = Int(json.Value, "employeeId") ?? employeeId;
        if (id <= 0) return null;

        var active = Bool(json.Value, "isActive") ?? true;
        if (!active) return null;

        return new Employee
        {
            EmployeeID = id,
            Name = String(json.Value, "name") ?? "Employee",
            Role = String(json.Value, "role"),
            Email = String(json.Value, "email"),
            ShiftStartTime = Time(json.Value, "shiftStart"),
            ShiftEndTime = Time(json.Value, "shiftEnd"),
            CompOffDayOfWeek = Enum.TryParse<DayOfWeek>(String(json.Value, "compOffDayOfWeek"), true, out var off)
                ? off
                : Int(json.Value, "compOffDayOfWeek") is int offInt && offInt >= 0 && offInt <= 6
                    ? (DayOfWeek?)offInt
                    : null,
            IsDeleted = false
        };
    }

    public async Task<List<Employee>> GetActiveEmployeesAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _firebase.GetOwnerTableAsync(OwnerUid, EmployeeTable, cancellationToken);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array))
            return new List<Employee>();

        var employees = new List<Employee>();
        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var employee = ParseActiveEmployee(property.Value, property.Name);
                if (employee != null) employees.Add(employee);
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
                if (employee != null) employees.Add(employee);
            }
        }

        return employees.OrderBy(x => x.Name).ToList();
    }

    private static Employee? ParseActiveEmployee(JsonElement row, string key)
    {
        var id = Int(row, "employeeId") ?? IntFromKey(key);
        if (!id.HasValue || id.Value <= 0) return null;

        var isActive = Bool(row, "isActive") ?? true;
        if (!isActive) return null;

        return new Employee
        {
            EmployeeID = id.Value,
            Name = String(row, "name") ?? "Employee",
            Role = String(row, "role"),
            Email = String(row, "email"),
            ShiftStartTime = Time(row, "shiftStart"),
            ShiftEndTime = Time(row, "shiftEnd"),
            CompOffDayOfWeek = Enum.TryParse<DayOfWeek>(String(row, "compOffDayOfWeek"), true, out var off)
                ? off
                : Int(row, "compOffDayOfWeek") is int offInt && offInt >= 0 && offInt <= 6
                    ? (DayOfWeek?)offInt
                    : null,
            IsDeleted = false
        };
    }

    public async Task<bool> CanEmployeeViewShiftsAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureTable, "1", cancellationToken);
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return true;

        return Bool(json.Value, "employeeCanViewShifts")
            ?? Bool(json.Value, "EmployeeCanViewShifts")
            ?? true;
    }

    public async Task<bool> IsShiftSchedulingEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _firebase.GetOwnerRecordAsync(OwnerUid, FeatureTable, "1", cancellationToken);
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return true; // Preserve the existing UI behavior if the feature row is not present.

        return Bool(json.Value, "enableShiftScheduling")
            ?? Bool(json.Value, "EnableShiftScheduling")
            ?? true;
    }

    public async Task<bool> SaveAsync(
        ShiftSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        if (schedule.EmployeeID <= 0 ||
            schedule.ShiftDate == default ||
            schedule.StartTime == default ||
            schedule.EndTime == default)
            return false;

        var id = schedule.ScheduleID;
        if (id <= 0)
            id = await NextIdAsync(cancellationToken);

        // Preserve the existing Web behavior for recurring pattern records:
        // their stored date is normalized to the Sunday of that week.
        if (schedule.IsRecurringPattern)
        {
            var delta = (int)schedule.ShiftDate.DayOfWeek;
            schedule.ShiftDate = schedule.ShiftDate.AddDays(-delta);
            schedule.AppliesToDayOfWeek = schedule.ShiftDate.AddDays(delta).DayOfWeek;
        }

        schedule.ScheduleID = id;

        var row = new Dictionary<string, object?>
        {
            ["scheduleId"] = schedule.ScheduleID,
            ["employeeId"] = schedule.EmployeeID,
            ["shiftDate"] = schedule.ShiftDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["startTime"] = schedule.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["endTime"] = schedule.EndTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["isRecurringPattern"] = schedule.IsRecurringPattern,
            ["patternDurationDays"] = schedule.PatternDurationDays,
            ["appliesToDayOfWeek"] = (int)schedule.AppliesToDayOfWeek,
            ["_entity"] = "ShiftSchedule",
            ["_key"] = schedule.ScheduleID.ToString(CultureInfo.InvariantCulture),
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await _firebase.SetOwnerRecordAsync(
            OwnerUid,
            Table,
            schedule.ScheduleID.ToString(CultureInfo.InvariantCulture),
            row,
            cancellationToken);

        if (ok)
        {
            await _firebase.PublishLocalApplicationChangeAsync(
                OwnerUid,
                "ShiftSchedule",
                "MODIFIED",
                schedule.ScheduleID.ToString(CultureInfo.InvariantCulture),
                cancellationToken);

            // A concrete shift changes the inputs used by DailySummary.
            // Recalculate immediately from Firebase schedule data so the
            // attendance result does not wait for the compatibility sync loop.
            if (!schedule.IsRecurringPattern)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var impact = scope.ServiceProvider.GetRequiredService<ShiftScheduleAttendanceImpactService>();
                await impact.RecalculateAsync(schedule.EmployeeID, schedule.ShiftDate, cancellationToken);
            }
        }

        return ok;
    }

    public async Task<bool> DeleteAsync(
        int scheduleId,
        CancellationToken cancellationToken = default)
    {
        if (scheduleId <= 0) return false;

        var schedules = await GetSchedulesAsync(0, null, null, cancellationToken);
        var existing = schedules.FirstOrDefault(x => x.ScheduleID == scheduleId);

        var ok = await _firebase.DeleteOwnerRecordAsync(
            OwnerUid,
            Table,
            scheduleId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        if (ok)
        {
            await _firebase.PublishLocalApplicationChangeAsync(
                OwnerUid,
                "ShiftSchedule",
                "DELETED",
                scheduleId.ToString(CultureInfo.InvariantCulture),
                cancellationToken);

            if (existing != null && !existing.IsRecurringPattern)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var impact = scope.ServiceProvider.GetRequiredService<ShiftScheduleAttendanceImpactService>();
                await impact.RecalculateAsync(existing.EmployeeID, existing.ShiftDate, cancellationToken);
            }
        }

        return ok;
    }

    public async Task<int> GenerateScheduleFromPatternsAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate) return 0;

        var featureEnabled = await IsShiftSchedulingEnabledAsync(cancellationToken);
        if (!featureEnabled)
        {
            _logger.LogWarning("Firebase shift generation skipped because EnableShiftScheduling is disabled.");
            return 0;
        }

        var employees = await GetActiveEmployeesAsync(cancellationToken);
        var existing = await GetSchedulesAsync(0, null, null, cancellationToken);

        var patterns = existing
            .Where(x => x.IsRecurringPattern)
            .GroupBy(x => x.EmployeeID)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.AppliesToDayOfWeek)
                    .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.ScheduleID).First()));

        var existingConcrete = existing
            .Where(x => !x.IsRecurringPattern)
            .Select(x => $"{x.EmployeeID}|{x.ShiftDate:yyyy-MM-dd}")
            .ToHashSet(StringComparer.Ordinal);

        var maxId = existing.Select(x => x.ScheduleID).DefaultIfEmpty(0).Max();
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
        var created = 0;
        var createdDates = new List<(int EmployeeId, DateOnly Date)>();

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var employee in employees)
            {
                if (!patterns.TryGetValue(employee.EmployeeID, out var byDay) ||
                    !byDay.TryGetValue(date.DayOfWeek, out var pattern))
                    continue;

                var uniqueness = $"{employee.EmployeeID}|{date:yyyy-MM-dd}";
                if (existingConcrete.Contains(uniqueness)) continue;

                var id = ++maxId;
                var row = new Dictionary<string, object?>
                {
                    ["scheduleId"] = id,
                    ["employeeId"] = employee.EmployeeID,
                    ["shiftDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["startTime"] = pattern.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["endTime"] = pattern.EndTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["isRecurringPattern"] = false,
                    ["patternDurationDays"] = 0,
                    ["appliesToDayOfWeek"] = (int)date.DayOfWeek,
                    ["_entity"] = "ShiftSchedule",
                    ["_key"] = id.ToString(CultureInfo.InvariantCulture),
                    ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };

                updates[id.ToString(CultureInfo.InvariantCulture)] = row;
                existingConcrete.Add(uniqueness);
                createdDates.Add((employee.EmployeeID, date));
                created++;
            }
        }

        if (updates.Count == 0) return 0;

        var ok = await _firebase.SetOwnerRecordsAsync(
            OwnerUid,
            Table,
            updates,
            cancellationToken);

        if (ok)
        {
            await _firebase.PublishLocalApplicationChangeAsync(
                OwnerUid,
                "ShiftSchedule",
                "BULK_MODIFIED",
                null,
                cancellationToken);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var impact = scope.ServiceProvider.GetRequiredService<ShiftScheduleAttendanceImpactService>();
            foreach (var affected in createdDates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await impact.RecalculateAsync(affected.EmployeeId, affected.Date, cancellationToken);
            }

            _logger.LogInformation("Firebase shift generation created {Count} daily schedules and reconciled attendance.", created);
            return created;
        }

        return 0;
    }

    private async Task<int> NextIdAsync(CancellationToken cancellationToken)
    {
        var existing = await GetSchedulesAsync(0, null, null, cancellationToken);
        return existing.Select(x => x.ScheduleID).DefaultIfEmpty(0).Max() + 1;
    }

    private static ShiftSchedule? ParseSchedule(JsonElement row, string key)
    {
        var id = Int(row, "scheduleId") ?? IntFromKey(key);
        var employeeId = Int(row, "employeeId");
        if (!id.HasValue || !employeeId.HasValue || employeeId.Value <= 0)
            return null;

        if (!DateOnly.TryParse(String(row, "shiftDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        if (!TimeOnly.TryParse(String(row, "startTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return null;
        if (!TimeOnly.TryParse(String(row, "endTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return null;

        var recurring = Bool(row, "isRecurringPattern") ?? false;
        var duration = Int(row, "patternDurationDays") ?? (recurring ? 7 : 0);
        var day = Int(row, "appliesToDayOfWeek") ?? (int)date.DayOfWeek;
        if (day < 0 || day > 6) day = (int)date.DayOfWeek;

        return new ShiftSchedule
        {
            ScheduleID = id.Value,
            EmployeeID = employeeId.Value,
            ShiftDate = date,
            StartTime = start,
            EndTime = end,
            IsRecurringPattern = recurring,
            PatternDurationDays = duration,
            AppliesToDayOfWeek = (DayOfWeek)day
        };
    }

    private static string? String(JsonElement row, string name)
    {
        if (row.TryGetProperty(name, out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();

        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        if (row.TryGetProperty(pascal, out p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();

        return null;
    }

    private static int? Int(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value)) return value;
        return int.TryParse(p.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private static int? IntFromKey(string key)
        => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Bool(JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.True) return true;
        if (p.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(p.ToString(), out var value) ? value : null;
    }

    private static TimeOnly? Time(JsonElement row, string name)
    {
        var text = String(row, name);
        return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    private static bool TryGet(JsonElement row, string name, out JsonElement value)
    {
        if (row.TryGetProperty(name, out value)) return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return row.TryGetProperty(pascal, out value);
    }
}
