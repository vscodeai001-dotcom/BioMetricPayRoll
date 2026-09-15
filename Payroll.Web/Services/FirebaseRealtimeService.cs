using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using System.Collections;
using Google.Apis.Auth.OAuth2;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase is a realtime transport/cache layer only. Neon/PostgreSQL remains
/// the business-data source of truth. This service never changes payroll rules
/// or database schema.
/// </summary>
public sealed class FirebaseRealtimeService
{
    private const string DatabaseScope = "https://www.googleapis.com/auth/firebase.database";
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FirebaseRealtimeService> _logger;
    private readonly Lazy<Task<FirebaseContext?>> _context;

    public FirebaseRealtimeService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<FirebaseRealtimeService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _context = new Lazy<Task<FirebaseContext?>>(InitializeAsync);
    }

    public string ResolveOwnerUid(string actorUid, string? role = null)
    {
        var configured = _configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID");

        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        // Backward-compatible fallback for a single-user installation.
        // Multi-user/company installations should set FIREBASE_OWNER_UID.
        return actorUid;
    }

    public async Task<string?> CreateCustomTokenAsync(
        string uid,
        int employeeId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return null;

        var context = await _context.Value;
        if (context == null)
            return null;

        try
        {
            var claims = new Dictionary<string, object>
            {
                ["employee_id"] = employeeId,
                ["role"] = role ?? string.Empty,
                ["owner_uid"] = ResolveOwnerUid(uid, role)
            };

            return await FirebaseAuth.GetAuth(context.App)
                .CreateCustomTokenAsync(uid, claims, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to create Firebase custom token for Firebase UID {Uid}", uid);
            return null;
        }
    }

    public async Task<bool> PublishApplicationDataChangedAsync(
        IReadOnlyCollection<object> changes,
        string? ownerUid = null,
        CancellationToken cancellationToken = default)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var payload = new Dictionary<string, object?>
        {
            ["eventId"] = eventId,
            ["source"] = "NEON",
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["changes"] = changes
        };

        var updates = new Dictionary<string, object?>
        {
            [$"application_events/{eventId}"] = payload
        };

        if (!string.IsNullOrWhiteSpace(ownerUid))
            updates[$"owner_events/{ownerUid}/{eventId}"] = payload;

        return await UpdateAsync(updates, cancellationToken);
    }

    public async Task<bool> PublishLocalApplicationChangeAsync(
        string ownerUid,
        string entity,
        string action = "MODIFIED",
        string? recordId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || string.IsNullOrWhiteSpace(entity))
            return false;

        var eventId = Guid.NewGuid().ToString("N");
        var change = new Dictionary<string, object?>
        {
            ["Entity"] = entity.Trim(),
            ["Action"] = string.IsNullOrWhiteSpace(action) ? "MODIFIED" : action.Trim().ToUpperInvariant()
        };

        if (!string.IsNullOrWhiteSpace(recordId))
            change["RecordId"] = recordId;

        var payload = new Dictionary<string, object?>
        {
            ["eventId"] = eventId,
            ["source"] = "FIREBASE_CLIENT",
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["changes"] = new[] { change }
        };

        return await SetAsync($"owner_events/{ownerUid}/{eventId}", payload, cancellationToken);
    }

    public async Task<bool> PublishLiveLocationAsync(
        int employeeId,
        Guid sessionId,
        string clientEventId,
        long sequence,
        double latitude,
        double longitude,
        double accuracyMeters,
        double speedMps,
        long capturedAtUnixMs,
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(clientEventId))
            return false;

        var payload = new Dictionary<string, object?>
        {
            ["EmployeeId"] = employeeId,
            ["SessionId"] = sessionId.ToString(),
            ["Latitude"] = latitude,
            ["Longitude"] = longitude,
            ["AccuracyMeters"] = Math.Max(0, accuracyMeters),
            ["SpeedMps"] = Math.Max(0, speedMps),
            ["Sequence"] = sequence,
            ["Timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(capturedAtUnixMs).UtcDateTime.ToString("O"),
            ["LastUpdatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["Source"] = "ANDROID_FIREBASE"
        };

        var updates = new Dictionary<string, object?>
        {
            [$"tracking/live/{employeeId}"] = payload,
            [$"tracking/history/{employeeId}/{clientEventId}"] = payload
        };

        return await UpdateAsync(updates, cancellationToken);
    }



    /// <summary>
    /// Publishes the committed Neon row snapshots that changed in one EF save.
    /// Firebase is only a realtime read model; Neon remains authoritative.
    /// Only scalar properties are exported, so EF navigation graphs/cycles and
    /// calculated client state are never copied into Firebase.
    /// </summary>
    public async Task<bool> PublishNeonChangesAsync(
        DbContext db,
        IReadOnlyCollection<EntityEntry> entries,
        string ownerUid,
        CancellationToken cancellationToken = default)
    {
        if (db == null || string.IsNullOrWhiteSpace(ownerUid) || entries.Count == 0)
            return false;

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var entityName = entry.Metadata.ClrType.Name;
            if (!IsRealtimeEntity(entityName))
                continue;

            var key = BuildKey(entry);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var path = $"owners/{ownerUid}/data/{entityName}/{EscapeFirebaseKey(key)}";
            if (entry.State == EntityState.Deleted)
            {
                updates[path] = null;
                continue;
            }

            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in entry.Properties)
            {
                if (property.Metadata.IsShadowProperty())
                    continue;
                row[property.Metadata.Name] = NormalizeFirebaseValue(property.CurrentValue);
            }
            row["_entity"] = entityName;
            row["_key"] = key;
            row["_updatedUtc"] = DateTime.UtcNow.ToString("O");
            updates[path] = row;
        }

        if (updates.Count == 0)
            return false;

        return await UpdateAsync(updates, cancellationToken);
    }

    /// <summary>
    /// One-time/bootstrap synchronization from Neon to Firebase. Disabled by
    /// default. Enable with FIREBASE_NEON_BOOTSTRAP=true after configuring the
    /// Firebase service account. This is intentionally allowlisted and paged so
    /// a large payroll database cannot accidentally become an unbounded export.
    /// </summary>
    public async Task<int> BootstrapNeonReadModelAsync(
        DbContext db,
        string ownerUid,
        int pageSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (db == null || string.IsNullOrWhiteSpace(ownerUid))
            return 0;

        var total = 0;
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityName = entityType.ClrType.Name;
            if (!IsRealtimeEntity(entityName))
                continue;

            var table = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(table))
                continue;
            var schema = entityType.GetSchema() ?? "public";
            var keyProperties = entityType.FindPrimaryKey()?.Properties;
            if (keyProperties == null || keyProperties.Count == 0)
                continue;

            var keyColumns = keyProperties
                .Select(p => p.GetColumnName(StoreObjectIdentifier.Table(table, schema)))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (keyColumns.Length != keyProperties.Count)
                continue;

            await using var conn = new NpgsqlConnection(GetConnectionString(db));
            await conn.OpenAsync(cancellationToken);
            var offset = 0;
            while (true)
            {
                var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT to_jsonb(t) FROM {qualified} t OFFSET @offset LIMIT @limit";
                cmd.Parameters.AddWithValue("offset", offset);
                cmd.Parameters.AddWithValue("limit", pageSize);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var page = new Dictionary<string, object?>(StringComparer.Ordinal);
                var count = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var json = reader.GetFieldValue<string>(0);
                    using var doc = JsonDocument.Parse(json);
                    var keyParts = new List<string>();
                    foreach (var keyColumn in keyColumns)
                    {
                        if (doc.RootElement.TryGetProperty(keyColumn, out var keyValue))
                            keyParts.Add(keyValue.ToString());
                    }
                    if (keyParts.Count != keyColumns.Length)
                        continue;
                    var key = string.Join("|", keyParts);
                    var row = JsonElementToObject(doc.RootElement) as Dictionary<string, object?>
                              ?? new Dictionary<string, object?>();
                    row["_entity"] = entityName;
                    row["_key"] = key;
                    row["_syncedUtc"] = DateTime.UtcNow.ToString("O");
                    page[$"owners/{ownerUid}/data/{entityName}/{EscapeFirebaseKey(key)}"] = row;
                    count++;
                }
                if (page.Count > 0)
                {
                    await UpdateAsync(page, cancellationToken);
                    total += count;
                }
                if (count < pageSize)
                    break;
                offset += pageSize;
            }
        }
        return total;
    }

    private static readonly HashSet<string> RealtimeEntities = new(StringComparer.Ordinal)
    {
        "Employee", "AttendanceLog", "SalaryAdvance", "PayrollHistory",
        "LeaveRequest", "ShiftSchedule", "CompanyHoliday", "CompanySetting",
        "DailySummary", "FeatureSettings", "ProfessionalTaxSlab", "BonusRecord",
        "YearEndSummary", "TaxDeclaration", "ResignationRequest", "FnFSettlement",
        "ReportDefinition", "AttendanceRegularization", "FBPComponent",
        "FlexibleBenefitDeclaration", "GeoPunchAudit"
    };

    private static bool IsRealtimeEntity(string entityName) => RealtimeEntities.Contains(entityName);

    private static string BuildKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key == null) return string.Empty;
        return string.Join("|", key.Properties.Select(p =>
        {
            var value = entry.Property(p.Name).CurrentValue ?? entry.Property(p.Name).OriginalValue;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }));
    }

    private static string EscapeFirebaseKey(string value)
        => value.Replace(".", "%2E").Replace("#", "%23").Replace("$", "%24")
            .Replace("[", "%5B").Replace("]", "%5D").Replace("/", "%2F");

    private static object? NormalizeFirebaseValue(object? value)
    {
        if (value == null) return null;
        if (value is DateTime dt) return dt.ToUniversalTime().ToString("O");
        if (value is DateTimeOffset dto) return dto.ToUniversalTime().ToString("O");
        if (value is DateOnly d) return d.ToString("yyyy-MM-dd");
        if (value is TimeOnly t) return t.ToString("HH:mm:ss.fffffff");
        if (value is Guid g) return g.ToString();
        if (value is decimal dec) return dec;
        if (value is Enum e) return e.ToString();
        if (value is byte[] bytes) return Convert.ToBase64String(bytes);
        return value;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string GetConnectionString(DbContext db)
        => db.Database.GetDbConnection().ConnectionString;

    public bool IsConfigured => _context.IsValueCreated && _context.Value.IsCompletedSuccessfully && _context.Value.Result != null;

    private async Task<bool> SetAsync(string path, object value, CancellationToken cancellationToken)
        => await UpdateAsync(new Dictionary<string, object?> { [path] = value }, cancellationToken);

    private async Task<bool> UpdateAsync(
        IReadOnlyDictionary<string, object?> updates,
        CancellationToken cancellationToken)
    {
        var context = await _context.Value;
        if (context == null)
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/.json");
            using var request = new HttpRequestMessage(HttpMethod.Patch, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await context.GetAccessTokenAsync());
            request.Content = new StringContent(
                JsonSerializer.Serialize(updates),
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Firebase realtime write failed with HTTP {Status}: {Body}",
                (int)response.StatusCode,
                body.Length > 500 ? body[..500] : body);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase realtime write deferred");
            return false;
        }
    }

    private async Task<FirebaseContext?> InitializeAsync()
    {
        try
        {
            var json = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON");
            GoogleCredential credential;

            if (!string.IsNullOrWhiteSpace(json))
            {
                credential = GoogleCredential
                    .FromJson(json)
                    .CreateScoped(DatabaseScope);
            }
            else
            {
                var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
                credential = !string.IsNullOrWhiteSpace(credentialsPath)
                    ? GoogleCredential.FromFile(credentialsPath).CreateScoped(DatabaseScope)
                    : GoogleCredential.GetApplicationDefault().CreateScoped(DatabaseScope);
            }

            var projectId = _configuration["Firebase:ProjectId"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
                ?? "biometricpayroll";

            var databaseUrl = _configuration["Firebase:DatabaseUrl"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_DATABASE_URL")
                ?? "https://biometricpayroll-default-rtdb.asia-southeast1.firebasedatabase.app";

            FirebaseApp app;
            try
            {
                app = FirebaseApp.DefaultInstance;
            }
            catch
            {
                app = FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                    ProjectId = projectId
                });
            }

            if (app == null)
            {
                app = FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                    ProjectId = projectId
                });
            }

            return new FirebaseContext(app, databaseUrl, credential);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Firebase Admin bridge is not configured. Existing Neon/SignalR paths remain active. Configure FIREBASE_SERVICE_ACCOUNT_JSON or GOOGLE_APPLICATION_CREDENTIALS to enable Firebase realtime transport.");
            return null;
        }
    }

    private sealed class FirebaseContext
    {
        private readonly GoogleCredential _credential;

        public FirebaseContext(FirebaseApp app, string databaseUrl, GoogleCredential credential)
        {
            App = app;
            DatabaseUrl = databaseUrl;
            _credential = credential;
        }

        public FirebaseApp App { get; }
        public string DatabaseUrl { get; }

        public async Task<string> GetAccessTokenAsync()
            => await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync();
    }
}
