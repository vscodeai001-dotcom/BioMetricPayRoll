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

    public FirebaseSqliteSyncService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseSqliteSyncService> logger)
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
                var ownerUid =
                    _configuration["Firebase:OwnerUid"]
                    ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")
                    ?? "biometricpayroll";

                foreach (var table in Tables)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    var json =
                        await _firebase.GetOwnerTableAsync(
                            ownerUid,
                            table.Value,
                            stoppingToken);

                    if (json is null ||
                        json.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    await UpsertTableAsync(
                        table.Key,
                        json.Value,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Firebase -> SQLite synchronization deferred.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(5),
                stoppingToken);
        }
    }

    private async Task UpsertTableAsync(
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
            return;

        var keys = entityType.FindPrimaryKey()?.Properties;
        if (keys == null || keys.Count == 0)
            return;

        foreach (var child in table.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object)
                continue;

            try
            {
                await UpsertRecordAsync(
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

        await db.SaveChangesAsync(ct);
    }

    private static async Task UpsertRecordAsync(
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

            if (value is null && keyParts.Length > i)
                value = keyParts[i];

            keyValues[i] =
                ConvertValue(
                    value,
                    keys[i].ClrType);
        }

        if (keyValues.Any(x => x is null))
            return;

        var existing =
            await db.FindAsync(
                entityType.ClrType,
                keyValues,
                ct);

        var target =
            existing ??
            Activator.CreateInstance(entityType.ClrType);

        if (target == null)
            return;

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
