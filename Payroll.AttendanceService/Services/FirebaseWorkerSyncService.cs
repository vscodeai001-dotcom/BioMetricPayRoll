using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.AttendanceService.Services;

/// <summary>
/// Firebase bridge for the Windows attendance worker.
/// Firebase is the durable shared SSOT. SQLite is only a local operational
/// cache used to preserve the existing worker/entity contracts.
/// </summary>
public sealed class FirebaseWorkerSyncService
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string DatabaseScope = "https://www.googleapis.com/auth/firebase.database";

    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseWorkerSyncService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Lazy<Task<FirebaseContext?>> _context;

    public FirebaseWorkerSyncService(
        IConfiguration configuration,
        ILogger<FirebaseWorkerSyncService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _context = new Lazy<Task<FirebaseContext?>>(InitializeAsync);
    }

    public async Task<bool> IsConfiguredAsync()
        => await _context.Value is not null;

    /// <summary>
    /// Pull only the operational records required by the existing biometric
    /// worker into SQLite. No business rules or entity schema are changed.
    /// </summary>
    public async Task<bool> HydrateOperationalCacheAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var context = await _context.Value;
        if (context is null)
        {
            _logger.LogWarning("Firebase worker bridge is not configured; local SQLite cache will be used.");
            return false;
        }

        var ownerUid = ResolveOwnerUid();

        try
        {
            await UpsertFeatureSettingsAsync(db, await GetJsonAsync($"owners/{ownerUid}/feature_settings", cancellationToken), cancellationToken);
            await UpsertCompanySettingsAsync(db, await GetJsonAsync($"owners/{ownerUid}/company_settings", cancellationToken), cancellationToken);
            await UpsertEmployeesAsync(db, await GetJsonAsync($"owners/{ownerUid}/employees", cancellationToken), cancellationToken);
            await UpsertAttendanceLogsAsync(db, await GetJsonAsync($"owners/{ownerUid}/attendance", cancellationToken), cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase -> Worker SQLite hydration deferred. Existing local cache remains available.");
            return false;
        }
    }

    /// <summary>
    /// Publishes biometric attendance rows after the existing local save has
    /// succeeded. The physical ZKTeco punch remains authoritative.
    /// </summary>
    public async Task<bool> PublishAttendanceLogsAsync(
        IReadOnlyCollection<AttendanceLog> logs,
        CancellationToken cancellationToken)
    {
        if (logs.Count == 0)
            return true;

        var context = await _context.Value;
        if (context is null)
            return false;

        var ownerUid = ResolveOwnerUid();
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var log in logs)
        {
            if (log.LogID <= 0)
                continue;

            var payload = new Dictionary<string, object?>
            {
                ["attendanceId"] = log.LogID.ToString(CultureInfo.InvariantCulture),
                ["employeeId"] = log.EmployeeID?.ToString(CultureInfo.InvariantCulture),
                ["biometricId"] = log.BiometricID,
                ["checkInTime"] = new DateTimeOffset(DateTime.SpecifyKind(log.PunchTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                ["createdAt"] = new DateTimeOffset(DateTime.SpecifyKind(log.PunchTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                ["note"] = log.LogType ?? "Punch",
                ["synced"] = true,
                ["source"] = "ZKTECO_WORKER",
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O")
            };

            updates[$"owners/{ownerUid}/attendance/{EscapeKey(log.LogID.ToString(CultureInfo.InvariantCulture))}"] = payload;
            var eventId = Guid.NewGuid().ToString("N");
            updates[$"owner_events/{ownerUid}/{eventId}"] = new
            {
                eventId,
                source = "ZKTECO_WORKER",
                timestamp = DateTime.UtcNow.ToString("O"),
                changes = new[]
                {
                    new
                    {
                        Entity = "AttendanceLog",
                        Action = "ADDED",
                        RecordId = log.LogID.ToString(CultureInfo.InvariantCulture)
                    }
                }
            };
        }

        if (updates.Count == 0)
            return true;

        return await PatchAsync(updates, cancellationToken);
    }

    private async Task UpsertFeatureSettingsAsync(
        AppDbContext db,
        JsonElement? json,
        CancellationToken cancellationToken)
    {
        if (!TryGetSingleRow(json, out var row))
            return;

        var settings = await db.FeatureSettings.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new FeatureSettings { Id = 1 };
            db.FeatureSettings.Add(settings);
        }

        settings.EnableDualAttendance = Bool(row, "enableDualAttendance", settings.EnableDualAttendance);
        settings.EnableGeoFencing = Bool(row, "enableGeoFencing", settings.EnableGeoFencing);
        settings.EnableAutomaticGeofencePunching = Bool(row, "enableAutomaticGeofencePunching", settings.EnableAutomaticGeofencePunching);
    }

    private async Task UpsertCompanySettingsAsync(
        AppDbContext db,
        JsonElement? json,
        CancellationToken cancellationToken)
    {
        if (!TryGetSingleRow(json, out var row))
            return;

        var settings = await db.CompanySettings.FirstOrDefaultAsync(x => x.SettingID == 1, cancellationToken);
        if (settings is null)
        {
            settings = new CompanySetting { SettingID = 1 };
            db.CompanySettings.Add(settings);
        }

        settings.ZktecoIP = String(row, "zktecoIP") ?? String(row, "ZktecoIP") ?? settings.ZktecoIP;
        settings.ZktecoPort = Int(row, "zktecoPort", settings.ZktecoPort);
        settings.ZktecoMachineNumber = Int(row, "zktecoMachineNumber", settings.ZktecoMachineNumber);
        settings.OfficeLatitude = Double(row, "officeLatitude", settings.OfficeLatitude);
        settings.OfficeLongitude = Double(row, "officeLongitude", settings.OfficeLongitude);
        settings.GeoRadiusMeters = Int(row, "geoRadiusMeters", settings.GeoRadiusMeters);
    }

    private async Task UpsertEmployeesAsync(
        AppDbContext db,
        JsonElement? json,
        CancellationToken cancellationToken)
    {
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var item in json.Value.EnumerateObject())
        {
            var row = item.Value;
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var employeeId = Int(row, "employeeId", 0);
            if (employeeId <= 0 && int.TryParse(item.Name, out var keyId))
                employeeId = keyId;
            if (employeeId <= 0)
                continue;

            var employee = await db.Employees.FirstOrDefaultAsync(x => x.EmployeeID == employeeId, cancellationToken);
            if (employee is null)
            {
                employee = new Employee { EmployeeID = employeeId };
                db.Employees.Add(employee);
            }

            employee.Name = String(row, "name") ?? employee.Name;
            employee.BiometricID = String(row, "biometricId") ?? employee.BiometricID;
            employee.Email = String(row, "email") ?? employee.Email;
            employee.Role = String(row, "role") ?? employee.Role;
            employee.IsDeleted = !Bool(row, "isActive", !employee.IsDeleted);
        }
    }

    private async Task UpsertAttendanceLogsAsync(
        AppDbContext db,
        JsonElement? json,
        CancellationToken cancellationToken)
    {
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var item in json.Value.EnumerateObject())
        {
            var row = item.Value;
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var logId = Int(row, "attendanceId", 0);
            if (logId <= 0 && int.TryParse(item.Name, out var keyId))
                logId = keyId;
            if (logId <= 0)
                continue;

            var log = await db.AttendanceLogs.FirstOrDefaultAsync(x => x.LogID == logId, cancellationToken);
            if (log is null)
            {
                log = new AttendanceLog { LogID = logId };
                db.AttendanceLogs.Add(log);
            }

            log.EmployeeID = IntNullable(row, "employeeId") ?? log.EmployeeID;
            log.BiometricID = String(row, "biometricId") ?? log.BiometricID;
            var timestamp = Long(row, "checkInTime", 0);
            if (timestamp > 0)
                log.PunchTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            log.LogType = String(row, "note") ?? log.LogType ?? "Punch";
            log.DeviceID ??= "ZKTeco_Firebase";
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var context = await _context.Value;
        if (context is null)
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{context.DatabaseUrl.TrimEnd('/')}/{path.TrimStart('/')}.json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await context.GetAccessTokenAsync());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Firebase worker read failed with HTTP {Status} for {Path}", (int)response.StatusCode, path);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content) || content == "null")
            return null;

        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private async Task<bool> PatchAsync(
        IReadOnlyDictionary<string, object?> updates,
        CancellationToken cancellationToken)
    {
        var context = await _context.Value;
        if (context is null)
            return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{context.DatabaseUrl.TrimEnd('/')}/.json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await context.GetAccessTokenAsync());
        request.Content = new StringContent(
            JsonSerializer.Serialize(updates),
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return true;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Firebase worker write failed with HTTP {Status}: {Body}", (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
        return false;
    }

    private async Task<FirebaseContext?> InitializeAsync()
    {
        try
        {
            var credentialsPath = _configuration["Firebase:ServiceAccountPath"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            if (string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(@"C:\FirebaseSecrets\firebase-service-account.json"))
                credentialsPath = @"C:\FirebaseSecrets\firebase-service-account.json";

            GoogleCredential credential;
            if (!string.IsNullOrWhiteSpace(credentialsPath))
            {
                if (!File.Exists(credentialsPath))
                    throw new FileNotFoundException($"Firebase service-account file was not found: {credentialsPath}");

                credential = GoogleCredential
                    .FromFile(credentialsPath)
                    .CreateScoped(CloudPlatformScope, DatabaseScope);
            }
            else
            {
                credential = (await GoogleCredential.GetApplicationDefaultAsync())
                    .CreateScoped(CloudPlatformScope, DatabaseScope);
            }

            var projectId = _configuration["Firebase:ProjectId"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
                ?? "biometricpayroll";
            var databaseUrl = _configuration["Firebase:DatabaseUrl"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_DATABASE_URL")
                ?? "https://biometricpayroll-default-rtdb.asia-southeast1.firebasedatabase.app";

            _logger.LogInformation("Firebase worker bridge initialized for project {ProjectId}.", projectId);
            return new FirebaseContext(databaseUrl, credential);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase worker bridge is unavailable. Configure Firebase:ServiceAccountPath or GOOGLE_APPLICATION_CREDENTIALS.");
            return null;
        }
    }

    private string ResolveOwnerUid()
        => (_configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")
            ?? "biometricpayroll").Trim();

    private static bool TryGetSingleRow(JsonElement? json, out JsonElement row)
    {
        row = default;
        if (json is null || json.Value.ValueKind != JsonValueKind.Object)
            return false;

        var props = json.Value.EnumerateObject().ToList();
        if (props.Count == 0)
            return false;

        if (props.Any(x => x.NameEquals("id") || x.NameEquals("settingID") || x.NameEquals("settingId")))
        {
            row = json.Value;
            return true;
        }

        var firstObject = props.FirstOrDefault(x => x.Value.ValueKind == JsonValueKind.Object);
        if (firstObject.Value.ValueKind == JsonValueKind.Object)
        {
            row = firstObject.Value;
            return true;
        }

        row = json.Value;
        return true;
    }

    private static string? String(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static int Int(JsonElement row, string name, int fallback)
    {
        if (!row.TryGetProperty(name, out var value)) return fallback;
        if (value.TryGetInt32(out var result)) return result;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static int? IntNullable(JsonElement row, string name)
    {
        var value = String(row, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static long Long(JsonElement row, string name, long fallback)
    {
        if (!row.TryGetProperty(name, out var value)) return fallback;
        if (value.TryGetInt64(out var result)) return result;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static double Double(JsonElement row, string name, double fallback)
    {
        if (!row.TryGetProperty(name, out var value)) return fallback;
        if (value.TryGetDouble(out var result)) return result;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static bool Bool(JsonElement row, string name, bool fallback)
    {
        if (!row.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(value.ToString(), out var result) ? result : fallback;
    }

    private static string EscapeKey(string value)
        => value.Replace(".", "%2E").Replace("#", "%23").Replace("$", "%24")
            .Replace("[", "%5B").Replace("]", "%5D").Replace("/", "%2F");

    private sealed record FirebaseContext(string DatabaseUrl, GoogleCredential Credential)
    {
        public Task<string> GetAccessTokenAsync()
            => Credential.UnderlyingCredential.GetAccessTokenForRequestAsync();
    }
}
