using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Payroll.Web.Services;

public sealed class FirebaseSqliteSyncService : BackgroundService
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["Employee"] = "employees",
        ["AttendanceLog"] = "attendance",
        ["SalaryAdvance"] = "advance_payments",
        ["PayrollHistory"] = "payroll_history",
        ["LeaveRequest"] = "leave_requests",
        ["ShiftSchedule"] = "shift_schedules",
        ["CompanyHoliday"] = "shop_closed_days",
        ["CompanySetting"] = "company_settings",
        ["DailySummary"] = "daily_summaries",
        ["FeatureSettings"] = "feature_settings",
        ["ProfessionalTaxSlab"] = "professional_tax_slabs",
        ["AuditLog"] = "audit_logs",
        ["BonusRecord"] = "bonus_records",
        ["YearEndSummary"] = "year_end_summaries",
        ["TaxDeclaration"] = "tax_declarations",
        ["ResignationRequest"] = "resignation_requests",
        ["FnFSettlement"] = "fnf_settlements",
        ["ReportDefinition"] = "report_definitions",
        ["AttendanceRegularization"] = "regularizations",
        ["FBPComponent"] = "fbp_components",
        ["FlexibleBenefitDeclaration"] = "fbp_declarations",
        ["GeoPunchAudit"] = "geo_punch_audits"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseSqliteSyncService> _logger;
    private readonly FirebaseSyncWriteScope _firebaseSyncWriteScope;
    private readonly AttendanceRefreshService _refreshService;

    public FirebaseSqliteSyncService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseSqliteSyncService> logger,
        FirebaseSyncWriteScope firebaseSyncWriteScope,
        AttendanceRefreshService refreshService)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
        _firebaseSyncWriteScope = firebaseSyncWriteScope;
        _refreshService = refreshService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var ownerUid = _configuration["Firebase:OwnerUid"]?.Trim();
        if (string.IsNullOrWhiteSpace(ownerUid))
            ownerUid = Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")?.Trim();
        if (string.IsNullOrWhiteSpace(ownerUid))
            ownerUid = "biometricpayroll";

        // First hydrate the local compatibility projection so the existing Web
        // screens have a complete initial view of the Firebase SSOT.
        await SyncAllTablesAsync(ownerUid, stoppingToken);

        // From this point forward Firebase's REST event stream is the trigger.
        // There is no fixed polling interval. A dropped stream is reconnected
        // automatically; Web UI notifications are raised only after the local
        // compatibility projection has been updated.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _firebase.StreamOwnerChangesAsync(
                    ownerUid,
                    async (relativePath, eventData, ct) =>
                    {
                        var target = ParseFirebasePath(relativePath);
                        if (target is null)
                            return;

                        if (!Tables.TryGetValue(target.Value.EntityName, out var firebaseTable))
                            return;

                        bool changed;

                        // Firebase sends data:null when a record/table is deleted.
                        // Re-reading the table alone cannot remove the stale local row.
                        if (eventData.HasValue &&
                            eventData.Value.ValueKind == JsonValueKind.Null)
                        {
                            changed = await DeleteLocalFirebaseRecordAsync(
                                target.Value.EntityName,
                                target.Value.RecordKey,
                                ct);
                        }
                        else
                        {
                            // PUT/PATCH events may contain only a partial row. Fetch the
                            // canonical Firebase table so the local compatibility cache
                            // always receives the complete record.
                            changed = await SyncTableAsync(
                                target.Value.EntityName,
                                firebaseTable,
                                ownerUid,
                                ct);
                        }

                        if (changed)
                        {
                            await _refreshService.NotifyApplicationDataChangedAsync(
                                new[] { target.Value.EntityName });
                        }
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase realtime stream disconnected. Reconnecting.");

                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task SyncAllTablesAsync(string ownerUid, CancellationToken ct)
    {
        foreach (var table in Tables)
        {
            ct.ThrowIfCancellationRequested();
            await SyncTableAsync(table.Key, table.Value, ownerUid, ct);
        }
    }

    private async Task<bool> SyncTableAsync(
        string entityName,
        string firebaseTable,
        string ownerUid,
        CancellationToken ct)
    {
        var json = await _firebase.GetOwnerTableAsync(ownerUid, firebaseTable, ct);
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return false;

        return await UpsertTableAsync(entityName, json.Value, ct);
    }

    private readonly record struct FirebasePathTarget(string EntityName, string? RecordKey);

    private static FirebasePathTarget? ParseFirebasePath(string relativePath)
    {
        var normalized = (relativePath ?? "/").Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var firebaseTable = parts[0];
        var entity = Tables.FirstOrDefault(x =>
            string.Equals(x.Value, firebaseTable, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(entity.Key))
            return null;

        if (parts.Length == 1)
            return new FirebasePathTarget(entity.Key, null);

        var key = Uri.UnescapeDataString(parts[1]);
        key = key.Replace("%2E", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("%23", "#", StringComparison.OrdinalIgnoreCase)
            .Replace("%24", "$", StringComparison.OrdinalIgnoreCase)
            .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
            .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
            .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        return new FirebasePathTarget(entity.Key, key);
    }

    private async Task<bool> DeleteLocalFirebaseRecordAsync(
        string entityName,
        string? firebaseKey,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(x => x.ClrType.Name == entityName);
        if (entityType == null)
            return false;

        if (string.IsNullOrWhiteSpace(firebaseKey))
        {
            var query = GetEntitySet(db, entityType.ClrType);

            var rows = await query
                .Cast<object>()
                .ToListAsync(ct);
            if (rows.Count == 0)
                return false;

            using var tableScope = _firebaseSyncWriteScope.Enter();
            db.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return true;
        }

        var keys = entityType.FindPrimaryKey()?.Properties;
        if (keys == null || keys.Count == 0)
            return false;

        var keyParts = firebaseKey.Split('|');
        if (keyParts.Length < keys.Count)
            return false;

        var keyValues = new object?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            keyValues[i] = ConvertStringValue(keyParts[i], keys[i].ClrType);
            if (keyValues[i] is null)
                return false;
        }

        var existing = await db.FindAsync(entityType.ClrType, keyValues, ct);
        if (existing == null)
            return false;

        using var recordScope = _firebaseSyncWriteScope.Enter();
        db.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UpsertTableAsync(
        string entityName,
        JsonElement table,
        CancellationToken ct)
    {
        await using var scope =
            _scopeFactory.CreateAsyncScope();

        var factory =
            scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var db =
            await factory.CreateDbContextAsync(ct);

        var entityType =
            db.Model.GetEntityTypes()
                .FirstOrDefault(x => x.ClrType.Name == entityName);

        if (entityType == null)
            return false;

        var keys = entityType.FindPrimaryKey()?.Properties;
        if (keys == null || keys.Count == 0)
            return false;

        var changedAny = false;

        foreach (var child in table.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object)
                continue;

            try
            {
                changedAny |= await UpsertRecordAsync(
                    db,
                    entityType,
                    keys,
                    child.Name,
                    child.Value,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping Firebase row {Entity}/{Key}.",
                    entityName,
                    child.Name);
            }
        }

        if (changedAny)
        {
            using var syncScope = _firebaseSyncWriteScope.Enter();
            await db.SaveChangesAsync(ct);
        }

        return changedAny;
    }

    private static async Task<bool> UpsertRecordAsync(
        AppDbContext db,
        IEntityType entityType,
        IReadOnlyList<IProperty> keys,
        string firebaseKey,
        JsonElement json,
        CancellationToken ct)
    {
        var keyParts = firebaseKey.Split('|');
        var keyValues = new object?[keys.Count];

        for (var i = 0; i < keys.Count; i++)
        {
            var value =
                FindJsonValue(
                    json,
                    keys[i].Name);

            if (value is not null)
            {
                keyValues[i] =
                    ConvertValue(
                        value,
                        keys[i].ClrType);
            }
            else if (keyParts.Length > i)
            {
                keyValues[i] =
                    ConvertStringValue(
                        keyParts[i],
                        keys[i].ClrType);
            }
            else
            {
                keyValues[i] = null;
            }
        }

        if (keyValues.Any(x => x is null))
            return false;

        var existing =
            await db.FindAsync(
                entityType.ClrType,
                keyValues,
                ct);

        var target =
            existing ??
            Activator.CreateInstance(entityType.ClrType);

        if (target == null)
            return false;

        var changed = false;

        foreach (var property in entityType.GetProperties())
        {
            if (property.IsShadowProperty() ||
                property.PropertyInfo == null)
                continue;

            var value =
                FindJsonValue(
                    json,
                    property.Name);

            if (value is null)
                continue;

            var converted =
                ConvertValue(
                    value,
                    property.ClrType);

            if (converted is null &&
                Nullable.GetUnderlyingType(property.ClrType) == null &&
                property.ClrType.IsValueType)
                continue;

            var current =
                property.PropertyInfo.GetValue(target);

            if (!Equals(current, converted))
            {
                property.PropertyInfo.SetValue(
                    target,
                    converted);

                changed = true;
            }
        }

        if (existing == null && changed)
            db.Add(target);
        else if (existing != null && changed)
            db.Entry(target).State = EntityState.Modified;

        return changed;
    }

    private static JsonElement? FindJsonValue(
        JsonElement json,
        string propertyName)
    {
        var wanted = Normalize(propertyName);

        foreach (var property in json.EnumerateObject())
        {
            if (Normalize(property.Name) == wanted)
                return property.Value;

            if (AliasMatches(propertyName, property.Name))
                return property.Value;
        }

        return null;
    }

    private static bool AliasMatches(
        string clrName,
        string firebaseName)
        => (Normalize(clrName), Normalize(firebaseName)) switch
        {
            ("logid", "attendanceid") => true,
            ("leaverequestid", "id") => true,
            ("regularizationid", "id") => true,
            ("employeeid", "staffid") => true,
            _ => false
        };

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static IQueryable GetEntitySet(
        DbContext db,
        Type entityType)
    {
        var setMethod = typeof(DbContext)
            .GetMethods()
            .First(method =>
                method.Name == nameof(DbContext.Set) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 &&
                method.GetParameters().Length == 0);

        return (IQueryable)setMethod
            .MakeGenericMethod(entityType)
            .Invoke(db, null)!;
    }

    private static object? ConvertStringValue(
        string value,
        Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            if (type == typeof(string)) return value;
            if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(Guid)) return Guid.Parse(value);
            if (type == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateOnly)) return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(TimeOnly)) return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type.IsEnum) return Enum.Parse(type, value, true);
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static object? ConvertValue(
        JsonElement? value,
        Type targetType)
    {
        if (value is null ||
            value.Value.ValueKind == JsonValueKind.Null)
            return null;

        var type =
            Nullable.GetUnderlyingType(targetType)
            ?? targetType;

        try
        {
            if (type == typeof(string))
                return value.Value.ToString();

            if (type == typeof(int))
                return int.Parse(value.Value.ToString(), CultureInfo.InvariantCulture);

            if (type == typeof(long))
                return long.Parse(value.Value.ToString(), CultureInfo.InvariantCulture);

            if (type == typeof(decimal))
                return decimal.Parse(value.Value.ToString(), CultureInfo.InvariantCulture);

            if (type == typeof(double))
                return double.Parse(value.Value.ToString(), CultureInfo.InvariantCulture);

            if (type == typeof(float))
                return float.Parse(value.Value.ToString(), CultureInfo.InvariantCulture);

            if (type == typeof(bool))
                return value.Value.ValueKind == JsonValueKind.True ||
                    (value.Value.ValueKind == JsonValueKind.String &&
                     bool.Parse(value.Value.GetString()!));

            if (type == typeof(Guid))
                return Guid.Parse(value.Value.ToString());

            if (type == typeof(DateTime))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

                return DateTime.Parse(
                    value.Value.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateTimeOffset))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);

                return DateTimeOffset.Parse(
                    value.Value.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateOnly))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt64(out var ms))
                    return DateOnly.FromDateTime(
                        DateTimeOffset.FromUnixTimeMilliseconds(ms).DateTime);

                return DateOnly.Parse(
                    value.Value.ToString(),
                    CultureInfo.InvariantCulture);
            }

            if (type == typeof(TimeOnly))
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetDouble(out var ms))
                    return TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(ms));

                return TimeOnly.Parse(
                    value.Value.ToString(),
                    CultureInfo.InvariantCulture);
            }

            if (type.IsEnum)
                return Enum.Parse(type, value.Value.ToString(), true);

            return JsonSerializer.Deserialize(
                value.Value.GetRawText(),
                type);
        }
        catch
        {
            return null;
        }
    }
}
