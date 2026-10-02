using Payroll.Shared.Firebase;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections;
using Google.Apis.Auth.OAuth2;
using Payroll.Shared.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase is the shared realtime data store for the Android/Web sync layer.
/// Existing payroll calculations and database schema are intentionally left
/// unchanged while individual modules are migrated to Firebase.
/// </summary>
public sealed class FirebaseRealtimeService
{
    private const string DatabaseScope = "https://www.googleapis.com/auth/firebase.database";
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string UserInfoEmailScope = "https://www.googleapis.com/auth/userinfo.email";
    private const string FirebaseAppName = "PayrollWebFirebase";
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FirebaseRealtimeService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Lazy<Task<FirebaseContext?>> _context;

    public FirebaseRealtimeService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<FirebaseRealtimeService> logger,
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory scopeFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
        _context = new Lazy<Task<FirebaseContext?>>(InitializeAsync);
    }

    public string ResolveOwnerUid(string? actorUid = null, string? role = null)
    {
        // 1. If actorUid is explicitly a tenant ID (e.g. starts with "tenant_" or equals "biometricpayroll"), return it
        if (!string.IsNullOrWhiteSpace(actorUid))
        {
            if (actorUid.StartsWith("tenant_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actorUid, Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid, StringComparison.OrdinalIgnoreCase))
            {
                return actorUid.Trim();
            }
        }

        // 2. Ambient context resolution: Check the current request or Blazor circuit's authenticated user
        try
        {
            var httpContext = _httpContextAccessor?.HttpContext;
            if (httpContext != null)
            {
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    var isSuperAdmin = user.IsInRole("SuperAdmin") ||
                        string.Equals(user.Identity?.Name, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase) ||
                        user.HasClaim("IsSuperAdmin", "true");

                    if (isSuperAdmin)
                    {
                        var superAdminEmail = user.Identity?.Name ?? FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail;
                        if (TenantContextService.TryGetSuperAdminTenant(superAdminEmail, out var cachedTenantId) &&
                            !string.IsNullOrWhiteSpace(cachedTenantId))
                        {
                            return cachedTenantId.Trim();
                        }

                        // Check if SuperAdmin switched to a specific tenant workspace via cookie
                        if (httpContext.Request.Cookies.TryGetValue("BioMetric_SuperAdmin_ActiveTenant", out var cookieTenant) &&
                            !string.IsNullOrWhiteSpace(cookieTenant))
                        {
                            return cookieTenant.Trim();
                        }

                        // SuperAdmin defaults to default primary workspace
                        return Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid;
                    }

                    // Company Admin or Employee: Check explicit TenantId / OwnerUid claims
                    var tenantClaim = user.FindFirst("TenantId")?.Value 
                        ?? user.FindFirst("OwnerUid")?.Value;
                    if (!string.IsNullOrWhiteSpace(tenantClaim))
                    {
                        return tenantClaim.Trim();
                    }

                    // If claims don't have it, query database by user ID or email
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
                    var email = user.FindFirst(ClaimTypes.Email)?.Value ?? user.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(email))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                        if (dbFactory != null)
                        {
                            using var db = dbFactory.CreateDbContext();
                            var tenant = db.CompanyTenants.AsNoTracking().FirstOrDefault(t =>
                                (userId != null && t.AdminUserId == userId) ||
                                (email != null && t.AdminEmail.ToLower() == email.ToLower()));

                            if (tenant != null && !string.IsNullOrWhiteSpace(tenant.TenantId))
                            {
                                return tenant.TenantId.Trim();
                            }

                            // If user is Employee and CompanyTenants didn't match, check db.Employees
                            if (user.IsInRole("Employee"))
                            {
                                var emp = db.Employees.AsNoTracking().FirstOrDefault(e =>
                                    (!string.IsNullOrWhiteSpace(userId) && e.AspNetUserId == userId) ||
                                    (!string.IsNullOrWhiteSpace(email) && e.Email != null && e.Email.ToLower() == email.ToLower()));

                                if (emp != null && !string.IsNullOrWhiteSpace(emp.TenantId))
                                {
                                    return emp.TenantId.Trim();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ambient tenant resolution encountered an issue; falling back.");
        }

        // 3. Database lookup by actorUid if provided (for API calls or background tasks passing email or userId)
        if (!string.IsNullOrWhiteSpace(actorUid))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    using var db = dbFactory.CreateDbContext();
                    var tenant = db.CompanyTenants.AsNoTracking().FirstOrDefault(t =>
                        t.TenantId == actorUid ||
                        t.AdminUserId == actorUid ||
                        t.AdminEmail.ToLower() == actorUid.ToLower());

                    if (tenant != null && !string.IsNullOrWhiteSpace(tenant.TenantId))
                    {
                        return tenant.TenantId.Trim();
                    }

                    // Check if actorUid matches an Employee (by email, aspNetUserId, or employeeId)
                    var emp = db.Employees.AsNoTracking().FirstOrDefault(e =>
                        (e.Email != null && e.Email.ToLower() == actorUid.ToLower()) ||
                        (e.AspNetUserId != null && e.AspNetUserId == actorUid) ||
                        e.EmployeeID.ToString() == actorUid ||
                        actorUid == $"employee-{e.EmployeeID}");

                    if (emp != null && !string.IsNullOrWhiteSpace(emp.TenantId))
                    {
                        return emp.TenantId.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Database tenant lookup for actorUid {ActorUid} encountered an issue; falling back.", actorUid);
            }
        }

        return Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid;
    }

    public async Task<FirebaseToken?> VerifyIdTokenAsync(
        string idToken,
        bool checkRevoked = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        var context = await _context.Value;
        if (context == null)
            return null;

        try
        {
            // Firebase Admin SDK validates signature, issuer, audience and expiry.
            // This is used only as the authentication bridge for the existing
            // Employee mobile session; payroll business rules remain unchanged.
            return await FirebaseAuth.GetAuth(context.App)
                .VerifyIdTokenAsync(idToken, checkRevoked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase ID token verification failed for mobile authentication.");
            return null;
        }
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

    /// <summary>
    /// Ensures an Identity user also has a Firebase Authentication account and
    /// carries the same role/employee/owner contract used by native Android.
    /// Password is supplied only at account creation time and is never stored.
    /// </summary>
    public async Task<string?> EnsureFirebaseUserAsync(
        string email,
        string password,
        string role,
        int employeeId = 0,
        string? displayName = null,
        string? existingUid = null,
        bool updatePasswordIfExisting = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var context = await _context.Value;
        if (context == null)
            return null;

        try
        {
            var auth = FirebaseAuth.GetAuth(context.App);
            UserRecord? user = null;

            if (!string.IsNullOrWhiteSpace(existingUid))
            {
                try
                {
                    user = await auth.GetUserAsync(existingUid, cancellationToken);
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
                {
                    user = null;
                }
            }

            if (user == null)
            {
                try
                {
                    user = await auth.GetUserByEmailAsync(email, cancellationToken);
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
                {
                    if (string.IsNullOrWhiteSpace(password))
                    {
                        _logger.LogWarning(
                            "Firebase account for {Email} does not exist and no password was supplied for provisioning.",
                            email);
                        return null;
                    }

                    user = await auth.CreateUserAsync(
                        new UserRecordArgs
                        {
                            Email = email,
                            Password = password,
                            EmailVerified = true,
                            DisplayName = displayName
                        },
                        cancellationToken);
                }
            }

            // A successful Identity password check is a safe one-time migration
            // point for native Firebase Email/Password authentication. Never
            // store the password. If the caller explicitly requests migration,
            // synchronize the Firebase password so the next Android login can be
            // Firebase-only.
            if (updatePasswordIfExisting && !string.IsNullOrWhiteSpace(password))
            {
                await auth.UpdateUserAsync(
                    new UserRecordArgs
                    {
                        Uid = user.Uid,
                        Password = password
                    },
                    cancellationToken);
            }

            var claims = new Dictionary<string, object>
            {
                ["role"] = string.IsNullOrWhiteSpace(role) ? "Employee" : role,
                ["employee_id"] = employeeId,
                ["owner_uid"] = ResolveOwnerUid(user.Uid, role)
            };

            await auth.SetCustomUserClaimsAsync(
                user.Uid,
                claims,
                cancellationToken);

            return user.Uid;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to provision Firebase Authentication user for {Email}",
                email);
            return null;
        }
    }

    /// <summary>
    /// Authenticates email/password against Firebase Authentication via REST API.
    /// Used as a live bridge when an employee account was created in Firebase Console
    /// or password was reset via email, enabling Web login to match Firebase Auth instantly.
    /// </summary>
    public async Task<FirebasePasswordVerificationResult> VerifyEmailPasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new(false, null, null, null, "Email and password are required.");

        var apiKey = _configuration["Firebase:ApiKey"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_API_KEY")
            ?? "AIzaSyDE6qAFRWzKkZiH2G2Hr6a6GC98wjEzucg";

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={apiKey}";

            var payload = new
            {
                email = email.Trim(),
                password = password,
                returnSecureToken = true
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Firebase Auth verification failed for {Email}: HTTP {Status}", email, (int)response.StatusCode);
                return new(false, null, null, null, "Invalid email or password.");
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var localId = root.TryGetProperty("localId", out var idProp) ? idProp.GetString() : null;
            var verifiedEmail = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : email;
            var idToken = root.TryGetProperty("idToken", out var tokenProp) ? tokenProp.GetString() : null;

            return new(true, localId, verifiedEmail, idToken, "Verification successful.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verifying Firebase email/password for {Email}.", email);
            return new(false, null, null, null, ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // Firebase SSOT owner-store primitives
    // ---------------------------------------------------------------------
    // These methods are deliberately table-whitelisted. They provide the Web
    // layer with a Firebase-native CRUD path without exposing arbitrary
    // database paths to callers. The existing legacy database-backed business services
    // can be migrated module-by-module without changing UI/layout/business
    // rules.

    public bool IsFirebaseSsotTable(string table)
        => FirebaseSsotSchema.IsTable(table);

    /// <summary>
    /// Opens Firebase Realtime Database's REST streaming endpoint for one
    /// owner's node. Firebase sends an initial snapshot followed by put/patch
    /// events whenever Web or Android changes the SSOT. The callback receives
    /// the Firebase-relative path and event data. The stream is intentionally
    /// read-only; writes continue through the normal Firebase CRUD methods.
    /// </summary>
    public async Task StreamOwnerChangesAsync(
        string ownerUid,
        Func<string, JsonElement?, CancellationToken, Task> onChange,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            throw new ArgumentException("Firebase owner UID is required.", nameof(ownerUid));

        ArgumentNullException.ThrowIfNull(onChange);

        var context = await _context.Value;
        if (context == null)
            throw new InvalidOperationException("Firebase Admin bridge is not configured.");

        var client = _httpClientFactory.CreateClient("FirebaseRealtime");
        client.Timeout = Timeout.InfiniteTimeSpan;

        var path = $"owners/{ownerUid.Trim()}";
        var uri = new Uri(
            $"{context.DatabaseUrl.TrimEnd('/')}/{path}.json");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await context.GetAccessTokenAsync());
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.ConnectionClose = false;

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Firebase realtime stream failed with HTTP {(int)response.StatusCode}: " +
                (body.Length > 500 ? body[..500] : body));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
                break;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
                continue;
            }

            if (line.Length != 0 || dataLines.Count == 0)
                continue;

            var data = string.Join("\n", dataLines);
            dataLines.Clear();

            if (string.IsNullOrWhiteSpace(data) ||
                string.Equals(eventType, "keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                eventType = null;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var relativePath = root.TryGetProperty("path", out var pathElement)
                    ? pathElement.GetString() ?? "/"
                    : "/";
                JsonElement? eventData = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : null;

                await onChange(relativePath, eventData, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ignoring malformed Firebase realtime stream event.");
            }
            finally
            {
                eventType = null;
            }
        }
    }

    /// <summary>
    /// Streams the global Firebase tracking tree used by native Android GPS.
    /// This is intentionally separate from the owner CRUD stream because the
    /// existing live-map contract already uses tracking/live and tracking/history.
    /// </summary>
    public async Task StreamTrackingChangesAsync(
        Func<string, JsonElement?, CancellationToken, Task> onChange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        var context = await _context.Value;
        if (context == null)
            throw new InvalidOperationException("Firebase Admin bridge is not configured.");

        var client = _httpClientFactory.CreateClient("FirebaseRealtime");
        client.Timeout = Timeout.InfiniteTimeSpan;

        // This legacy helper remains available to callers that explicitly use it,
        // but must never be used for a tenant dashboard. Owner-scoped callers should
        // use StreamGlobalChangesAsync with owners/{ownerUid}/tracking.
        var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/tracking.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await context.GetAccessTokenAsync());
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.ConnectionClose = false;

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Firebase tracking stream failed with HTTP {(int)response.StatusCode}: " +
                (body.Length > 500 ? body[..500] : body));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventType = null;
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line[6..].Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
                continue;
            }
            if (line.Length != 0 || dataLines.Count == 0) continue;

            var data = string.Join("\n", dataLines);
            dataLines.Clear();
            if (string.IsNullOrWhiteSpace(data) ||
                string.Equals(eventType, "keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                eventType = null;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var relativePath = root.TryGetProperty("path", out var pathElement)
                    ? pathElement.GetString() ?? "/"
                    : "/";
                JsonElement? eventData = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : null;
                await onChange(relativePath, eventData, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ignoring malformed Firebase tracking stream event.");
            }
            finally
            {
                eventType = null;
            }
        }
    }

    public async Task StreamGlobalChangesAsync(
        string rootNode,
        Func<string, JsonElement?, CancellationToken, Task> onChange,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootNode))
            throw new ArgumentException("Firebase root node is required.", nameof(rootNode));
        ArgumentNullException.ThrowIfNull(onChange);

        var context = await _context.Value;
        if (context == null)
            throw new InvalidOperationException("Firebase Admin bridge is not configured.");

        var client = _httpClientFactory.CreateClient("FirebaseRealtime");
        client.Timeout = Timeout.InfiniteTimeSpan;
        var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/{rootNode.Trim('/')}.json");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await context.GetAccessTokenAsync());
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.ConnectionClose = false;

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Firebase {rootNode} stream failed with HTTP {(int)response.StatusCode}: " +
                (body.Length > 500 ? body[..500] : body));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventType = null;
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line[6..].Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
                continue;
            }
            if (line.Length != 0 || dataLines.Count == 0) continue;

            var data = string.Join("\n", dataLines);
            dataLines.Clear();
            if (string.IsNullOrWhiteSpace(data) ||
                string.Equals(eventType, "keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                eventType = null;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var relativePath = root.TryGetProperty("path", out var pathElement)
                    ? pathElement.GetString() ?? "/"
                    : "/";
                JsonElement? eventData = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : null;
                await onChange(relativePath, eventData, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ignoring malformed Firebase {RootNode} stream event.", rootNode);
            }
            finally
            {
                eventType = null;
            }
        }
    }

    public async Task<JsonElement?> GetOwnerTableAsync(
        string ownerUid,
        string table,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || !IsFirebaseSsotTable(table))
            return null;

        return await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/{table.Trim()}",
            cancellationToken);
    }

    /// <summary>
    /// Reads an owner table using a Firebase Realtime Database child query.
    /// This is used for employee-scoped reads where the security rules require
    /// the client to constrain the collection by employeeId.
    /// </summary>
    public async Task<JsonElement?> GetOwnerTableByChildValueAsync(
        string ownerUid,
        string table,
        string child,
        object equalTo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            string.IsNullOrWhiteSpace(table) ||
            string.IsNullOrWhiteSpace(child) ||
            !IsFirebaseSsotTable(table))
            return null;

        var orderBy = Uri.EscapeDataString($"\"{child.Trim()}\"");
        var encodedValue = equalTo switch
        {
            bool b => b ? "true" : "false",
            string text => Uri.EscapeDataString($"\"{text}\""),
            _ => Uri.EscapeDataString(Convert.ToString(equalTo, CultureInfo.InvariantCulture) ?? string.Empty)
        };

        var result = await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/{table.Trim()}",
            cancellationToken,
            $"?orderBy={orderBy}&equalTo={encodedValue}");

        if (result != null)
            return result;

        // Fallback: If indexed query fails (e.g. index not defined in Firebase rules),
        // read the table directly so callers do not receive null / fail to render.
        return await GetOwnerTableAsync(ownerUid, table, cancellationToken);
    }

    /// <summary>
    /// Reads an owner table using an indexed child range. This keeps large
    /// time-series collections bounded and avoids owner-wide snapshots.
    /// </summary>
    public async Task<JsonElement?> GetOwnerTableByChildRangeAsync(
        string ownerUid,
        string table,
        string child,
        object? startAt,
        object? endAt,
        int? limitToLast = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            string.IsNullOrWhiteSpace(table) ||
            string.IsNullOrWhiteSpace(child) ||
            !IsFirebaseSsotTable(table))
            return null;

        static string EncodeValue(object? value)
        {
            if (value is null) return string.Empty;
            return value switch
            {
                bool b => b ? "true" : "false",
                string text => Uri.EscapeDataString($"\"{text}\""),
                _ => Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            };
        }

        var orderBy = Uri.EscapeDataString($"\"{child.Trim()}\"");
        var parameters = new List<string> { $"orderBy={orderBy}" };
        if (startAt is not null) parameters.Add($"startAt={EncodeValue(startAt)}");
        if (endAt is not null) parameters.Add($"endAt={EncodeValue(endAt)}");
        if (limitToLast.HasValue) parameters.Add($"limitToLast={Math.Clamp(limitToLast.Value, 1, 10000)}");

        var result = await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/{table.Trim()}",
            cancellationToken,
            "?" + string.Join("&", parameters));

        if (result != null)
            return result;

        // Fallback: If indexed query fails (e.g. index not defined in Firebase rules),
        // read the table directly so callers do not receive null / fail to render.
        return await GetOwnerTableAsync(ownerUid, table, cancellationToken);
    }

    public async Task<JsonElement?> GetGlobalRecordAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return await GetJsonAsync(path.Trim('/'), cancellationToken);
    }

    /// <summary>
    /// Reads the owner-scoped live tracking collection used by the native
    /// Android application. Dashboard aggregation must never mix tracking
    /// records from another owner's Firebase namespace.
    /// </summary>
    public async Task<JsonElement?> GetOwnerTrackingLiveAsync(
        string ownerUid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            return null;

        return await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/tracking/live",
            cancellationToken);
    }

    /// <summary>
    /// Reads one employee's live tracking node. This is intentionally bounded
    /// to a single employee and must be preferred over downloading the complete
    /// owner tracking/live collection when reconciling a nested SSE field event.
    /// </summary>
    public async Task<JsonElement?> GetOwnerTrackingLiveEmployeeAsync(
        string ownerUid,
        int employeeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || employeeId <= 0)
            return null;

        return await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/tracking/live/{employeeId}",
            cancellationToken);
    }

    /// <summary>
    /// Reads only the most recent GPS history records for one employee.
    /// Never downloads the complete owner tracking/history tree.
    /// </summary>
    public async Task<JsonElement?> GetOwnerTrackingHistoryAsync(
        string ownerUid,
        int employeeId,
        int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || employeeId <= 0)
            return null;

        limit = Math.Clamp(limit, 1, 5000);

        var path =
            $"owners/{ownerUid.Trim()}/tracking/history/{employeeId}";

        var queryString =
            $"?orderBy=%22Timestamp%22&limitToLast={limit}";

        return await GetJsonAsync(
            path,
            cancellationToken,
            queryString);
    }

    public async Task<bool> SetGlobalRecordAsync(
        string path,
        object value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return await SetAsync(path.Trim('/'), value, cancellationToken);
    }

    public async Task<bool> DeleteGlobalRecordAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return await UpdateAsync(
            new Dictionary<string, object?> { [path.Trim('/')] = null },
            cancellationToken);
    }

    public async Task<JsonElement?> GetOwnerRecordAsync(
        string ownerUid,
        string table,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return null;

        return await GetJsonAsync(
            $"owners/{ownerUid.Trim()}/{table.Trim()}/{EscapeFirebaseKey(recordId.Trim())}",
            cancellationToken);
    }

    public async Task<bool> SetOwnerRecordAsync(
        string ownerUid,
        string table,
        string recordId,
        object value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            string.IsNullOrWhiteSpace(recordId) ||
            !IsFirebaseSsotTable(table))
            return false;

        return await SetAsync(
            $"owners/{ownerUid.Trim()}/{table.Trim()}/{EscapeFirebaseKey(recordId.Trim())}",
            value,
            cancellationToken);
    }

    /// <summary>
    /// Atomically writes multiple records under one Firebase SSOT table.
    /// Used by module migrations that must publish a set of related rows
    /// without falling back to the legacy database as the runtime authority.
    /// </summary>
    public async Task<bool> SetOwnerRecordsAsync(
        string ownerUid,
        string table,
        IReadOnlyDictionary<string, object?> records,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            string.IsNullOrWhiteSpace(table) ||
            records == null ||
            records.Count == 0 ||
            !IsFirebaseSsotTable(table))
            return false;

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Key)) continue;
            updates[$"owners/{ownerUid.Trim()}/{table.Trim()}/{EscapeFirebaseKey(record.Key.Trim())}"] = record.Value;
        }

        return updates.Count > 0 && await UpdateAsync(updates, cancellationToken);
    }

    public async Task<bool> DeleteOwnerRecordAsync(
        string ownerUid,
        string table,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) ||
            string.IsNullOrWhiteSpace(recordId) ||
            !IsFirebaseSsotTable(table))
            return false;

        return await UpdateAsync(
            new Dictionary<string, object?>
            {
                [$"owners/{ownerUid.Trim()}/{table.Trim()}/{EscapeFirebaseKey(recordId.Trim())}"] = null
            },
            cancellationToken);
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
            ["source"] = "FIREBASE_SSoT",
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["changes"] = changes
        };

        var updates = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(ownerUid))
            updates[$"owner_events/{ownerUid}/{eventId}"] = payload;

        if (updates.Count == 0)
            return true;

        return await UpdateAsync(updates, cancellationToken);
    }

    /// <summary>
    /// Closes the owner-scoped durable GPS session when Web is the platform
    /// that ends the employee session. The session record is intentionally
    /// separate from tracking/live so Android cannot continue using an old
    /// session after another platform logs the employee out.
    /// </summary>
    public async Task<bool> TerminateTrackingSessionAsync(
        int employeeId,
        Guid sessionId,
        string? ownerUid,
        string endReason = "LOGGED_OUT",
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return false;

        var timestamp = DateTime.UtcNow.ToString("O");
        var safeReason = string.IsNullOrWhiteSpace(endReason)
            ? "ENDED"
            : endReason.Length > 40
                ? endReason[..40]
                : endReason;

        var updates = new Dictionary<string, object?>
        {
            [$"tracking/sessions/{employeeId}/{sessionId}/EmployeeId"] = employeeId,
            [$"tracking/sessions/{employeeId}/{sessionId}/SessionId"] = sessionId.ToString(),
            [$"tracking/sessions/{employeeId}/{sessionId}/EndedAtUtc"] = timestamp,
            [$"tracking/sessions/{employeeId}/{sessionId}/EndReason"] = safeReason,
            [$"tracking/sessions/{employeeId}/{sessionId}/State"] = "ENDED",
            [$"tracking/sessions/{employeeId}/{sessionId}/Source"] = "WEB"
        };

        if (!string.IsNullOrWhiteSpace(ownerUid))
        {
            var ownerPath = $"owners/{ownerUid.Trim()}/tracking/sessions/{employeeId}/{sessionId}";
            var current = await GetGlobalRecordAsync(ownerPath, cancellationToken);

            if (current.HasValue &&
                current.Value.ValueKind == JsonValueKind.Object &&
                current.Value.TryGetProperty("SessionId", out var remoteSession) &&
                remoteSession.ValueKind == JsonValueKind.String &&
                !string.Equals(
                    remoteSession.GetString(),
                    sessionId.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                // A newer/different session owns the remote record. Never end it.
                return false;
            }

            updates[$"{ownerPath}/EmployeeId"] = employeeId;
            updates[$"{ownerPath}/SessionId"] = sessionId.ToString();
            updates[$"{ownerPath}/EndedAtUtc"] = timestamp;
            updates[$"{ownerPath}/EndReason"] = safeReason;
            updates[$"{ownerPath}/State"] = "ENDED";
            updates[$"{ownerPath}/Source"] = "WEB";
        }

        return await UpdateAsync(updates, cancellationToken);
    }

    /// <summary>
    /// Ends only the specified owner-scoped GPS session when Web discovers
    /// that Firebase is carrying an already-ended SQL session.
    /// </summary>
    public async Task<bool> MarkTrackingSessionEndedAsync(
        int employeeId,
        Guid sessionId,
        string? ownerUid,
        string endReason = "SESSION_RECOVERED",
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return false;

        var timestamp = DateTime.UtcNow.ToString("O");
        var safeReason = string.IsNullOrWhiteSpace(endReason)
            ? "SESSION_RECOVERED"
            : endReason.Length > 40 ? endReason[..40] : endReason;

        var updates = new Dictionary<string, object?>
        {
            [$"tracking/sessions/{employeeId}/{sessionId}/EmployeeId"] = employeeId,
            [$"tracking/sessions/{employeeId}/{sessionId}/SessionId"] = sessionId.ToString(),
            [$"tracking/sessions/{employeeId}/{sessionId}/EndedAtUtc"] = timestamp,
            [$"tracking/sessions/{employeeId}/{sessionId}/EndReason"] = safeReason,
            [$"tracking/sessions/{employeeId}/{sessionId}/State"] = "ENDED",
            [$"tracking/sessions/{employeeId}/{sessionId}/Source"] = "WEB_RECOVERY"
        };

        if (!string.IsNullOrWhiteSpace(ownerUid))
        {
            var ownerPath = $"owners/{ownerUid.Trim()}/tracking/sessions/{employeeId}/{sessionId}";
            updates[$"{ownerPath}/EmployeeId"] = employeeId;
            updates[$"{ownerPath}/SessionId"] = sessionId.ToString();
            updates[$"{ownerPath}/EndedAtUtc"] = timestamp;
            updates[$"{ownerPath}/EndReason"] = safeReason;
            updates[$"{ownerPath}/State"] = "ENDED";
            updates[$"{ownerPath}/Source"] = "WEB_RECOVERY";

            var livePath = $"owners/{ownerUid.Trim()}/tracking/live/{employeeId}";
            var live = await GetGlobalRecordAsync(livePath, cancellationToken);
            if (live.HasValue && live.Value.ValueKind == JsonValueKind.Object &&
                live.Value.TryGetProperty("SessionId", out var liveSession) &&
                liveSession.ValueKind == JsonValueKind.String &&
                Guid.TryParse(liveSession.GetString(), out var liveGuid) &&
                liveGuid == sessionId)
            {
                updates[$"{livePath}/State"] = "ENDED";
                updates[$"{livePath}/LastUpdatedUtc"] = timestamp;
            }
        }

        return await UpdateAsync(updates, cancellationToken);
    }

    public async Task<bool> TerminateLiveLocationAsync(
        int employeeId,
        string? ownerUid = null,
        Guid? expectedSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0)
            return false;

        var timestamp = DateTime.UtcNow.ToString("O");

        // The REST transport has no native transaction helper here, so when
        // an expected session is supplied, first verify that the owner-scoped
        // live marker still belongs to that exact session. Never terminate a
        // newer session because an older logout arrived late.
        if (expectedSessionId.HasValue && expectedSessionId.Value != Guid.Empty &&
            !string.IsNullOrWhiteSpace(ownerUid))
        {
            var current = await GetGlobalRecordAsync(
                $"owners/{ownerUid.Trim()}/tracking/live/{employeeId}",
                cancellationToken);

            if (current.HasValue &&
                current.Value.ValueKind == JsonValueKind.Object &&
                current.Value.TryGetProperty("SessionId", out var currentSession) &&
                currentSession.ValueKind == JsonValueKind.String &&
                Guid.TryParse(currentSession.GetString(), out var parsedCurrent) &&
                parsedCurrent != expectedSessionId.Value)
            {
                return false;
            }
        }

        var updates = new Dictionary<string, object?>
        {
            [$"tracking/live/{employeeId}/State"] = "ENDED",
            [$"tracking/live/{employeeId}/LastUpdatedUtc"] = timestamp
        };

        if (!string.IsNullOrWhiteSpace(ownerUid))
        {
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}/State"] = "ENDED";
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}/LastUpdatedUtc"] = timestamp;
        }

        return await UpdateAsync(updates, cancellationToken);
    }

    public async Task<bool> BindLiveLocationAsync(
        int employeeId,
        Guid sessionId,
        string? ownerUid = null,
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return false;

        var timestamp = DateTime.UtcNow.ToString("O");
        var updates = new Dictionary<string, object?>
        {
            [$"tracking/live/{employeeId}/SessionId"] = sessionId.ToString(),
            [$"tracking/live/{employeeId}/State"] = "ACTIVE",
            [$"tracking/live/{employeeId}/LastUpdatedUtc"] = timestamp,
            [$"tracking/sessions/{employeeId}/{sessionId}/EmployeeId"] = employeeId,
            [$"tracking/sessions/{employeeId}/{sessionId}/SessionId"] = sessionId.ToString(),
            [$"tracking/sessions/{employeeId}/{sessionId}/StartedAtUtc"] = timestamp,
            [$"tracking/sessions/{employeeId}/{sessionId}/State"] = "ACTIVE",
            [$"tracking/sessions/{employeeId}/{sessionId}/Source"] = "WEB"
        };

        if (!string.IsNullOrWhiteSpace(ownerUid))
        {
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}/SessionId"] = sessionId.ToString();
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}/State"] = "ACTIVE";
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}/LastUpdatedUtc"] = timestamp;
            updates[$"owners/{ownerUid}/tracking/sessions/{employeeId}/{sessionId}/EmployeeId"] = employeeId;
            updates[$"owners/{ownerUid}/tracking/sessions/{employeeId}/{sessionId}/SessionId"] = sessionId.ToString();
            updates[$"owners/{ownerUid}/tracking/sessions/{employeeId}/{sessionId}/StartedAtUtc"] = timestamp;
            updates[$"owners/{ownerUid}/tracking/sessions/{employeeId}/{sessionId}/State"] = "ACTIVE";
            updates[$"owners/{ownerUid}/tracking/sessions/{employeeId}/{sessionId}/Source"] = "WEB";
        }

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
        int allowedRadiusMeters = 0,
        bool isWithinAllowedRadius = false,
        DateTime? sessionStartedUtc = null,
        double distanceMeters = 0.0,
        bool recordHistory = false,
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
            ["DistanceMeters"] = Math.Max(0, distanceMeters),
            ["SpeedMps"] = Math.Max(0, speedMps),
            ["Sequence"] = sequence,
            ["Timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(capturedAtUnixMs).UtcDateTime.ToString("O"),
            ["LastUpdatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["AllowedRadiusMeters"] = Math.Max(0, allowedRadiusMeters),
            ["IsWithinAllowedRadius"] = isWithinAllowedRadius,
            ["State"] = "ACTIVE",
            ["SessionStartedUtc"] = (sessionStartedUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("O"),
            ["Source"] = "FIREBASE_WEB"
        };

        var updates = new Dictionary<string, object?>
        {
            [$"tracking/live/{employeeId}"] = payload
        };

        if (recordHistory)
        {
            updates[$"tracking/history/{employeeId}/{clientEventId}"] = payload;
        }

        var ownerUid = ResolveOwnerUid($"employee-{employeeId}", "Employee");
        if (!string.IsNullOrWhiteSpace(ownerUid))
        {
            updates[$"owners/{ownerUid}/tracking/live/{employeeId}"] = payload;
            if (recordHistory)
            {
                updates[$"owners/{ownerUid}/tracking/history/{employeeId}/{clientEventId}"] = payload;
            }
        }

        return await UpdateAsync(updates, cancellationToken);
    }

    public async Task PurgeOldTrackingHistoryAsync(string ownerUid, int employeeId, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || employeeId <= 0) return;

        var historyJson = await GetOwnerTrackingHistoryAsync(ownerUid, employeeId, limit: 100);
        if (historyJson == null || historyJson.Value.ValueKind != JsonValueKind.Object) return;

        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var toDelete = new List<string>();

        foreach (var prop in historyJson.Value.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("Timestamp", out var tsProp) &&
                DateTime.TryParse(tsProp.GetString(), out var ts) && ts < cutoff)
            {
                toDelete.Add($"owners/{ownerUid.Trim()}/tracking/history/{employeeId}/{prop.Name}");
                toDelete.Add($"tracking/history/{employeeId}/{prop.Name}");
            }
        }

        if (toDelete.Count > 0)
        {
            var updates = toDelete.ToDictionary(k => k, v => (object?)null);
            await UpdateAsync(updates, CancellationToken.None);
        }
    }



    /// <summary>
    /// Publishes committed local row snapshots that changed in one EF save.
    /// Firebase is the shared realtime SSOT.
    /// Only scalar properties are exported, so EF navigation graphs/cycles and
    /// calculated client state are never copied into Firebase.
    /// </summary>
    public async Task<bool> PublishCommittedChangesAsync(
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

            var firebaseTable = GetFirebaseTable(entityName);
            if (string.IsNullOrWhiteSpace(firebaseTable))
                continue;

            var path = $"owners/{ownerUid}/{firebaseTable}/{EscapeFirebaseKey(key)}";
            if (entry.State == EntityState.Deleted)
            {
                updates[path] = null;
                continue;
            }

            var row = BuildFirebaseRow(entry, entityName, key);
            row["_entity"] = entityName;
            row["_key"] = key;
            row["_updatedUtc"] = DateTime.UtcNow.ToString("O");
            updates[path] = row;
        }

        if (updates.Count == 0)
            return false;

        return await UpdateAsync(updates, cancellationToken);
    }


    // Firebase paths intentionally match the existing Android owner-node
    // layout. This is a transport/read-model mapping only; it does not alter
    // the logical application schema or any business logic.
    private static string? GetFirebaseTable(string entityName)
        => entityName switch
        {
            "Employee" => "employees",
            "AttendanceLog" => "attendance",
            "SalaryAdvance" => "advance_payments",
            "PayrollHistory" => "payroll_history",
            "LeaveRequest" => "leave_requests",
            "ShiftSchedule" => "shift_schedules",
            "CompanyHoliday" => "shop_closed_days",
            "CompanySetting" => "company_settings",
            "DailySummary" => "daily_summaries",
            "FeatureSettings" => "feature_settings",
            "ProfessionalTaxSlab" => "professional_tax_slabs",
            "AuditLog" => "audit_logs",
            "BonusRecord" => "bonus_records",
            "YearEndSummary" => "year_end_summaries",
            "TaxDeclaration" => "tax_declarations",
            "ResignationRequest" => "resignation_requests",
            "FnFSettlement" => "fnf_settlements",
            "ReportDefinition" => "report_definitions",
            "AttendanceRegularization" => "regularizations",
            "FBPComponent" => "fbp_components",
            "FlexibleBenefitDeclaration" => "fbp_declarations",
            "GeoPunchAudit" => "geo_punch_audits",
            "Shop" => "shops",
            "EmployeePresence" => "presence",
            "EmployeeHistory" => "employee_history",
            "SalarySnapshot" => "salary_snapshots",
            "PayrollPreview" => "payroll_previews",
            "PayrollFinalization" => "payroll_finalization",
            "EmployeeGpsSession" => "tracking/sessions",
            "EmployeeLocationHistory" => "tracking/history",
            _ => null
        };

    private static readonly HashSet<string> RealtimeEntities = new(StringComparer.Ordinal)
    {
        "Employee", "AttendanceLog", "SalaryAdvance", "PayrollHistory",
        "LeaveRequest", "ShiftSchedule", "CompanyHoliday", "CompanySetting",
        "DailySummary", "FeatureSettings", "ProfessionalTaxSlab", "BonusRecord",
        "YearEndSummary", "TaxDeclaration", "ResignationRequest", "FnFSettlement",
        "ReportDefinition", "AttendanceRegularization", "FBPComponent",
        "FlexibleBenefitDeclaration", "GeoPunchAudit", "Shop", "EmployeePresence",
        "EmployeeHistory", "SalarySnapshot", "PayrollPreview", "PayrollFinalization",
        "EmployeeGpsSession", "EmployeeLocationHistory"
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

    private static Dictionary<string, object?> BuildFirebaseRow(
        EntityEntry entry,
        string entityName,
        string key)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);

        object? Raw(params string[] names)
        {
            foreach (var name in names)
            {
                var property = entry.Metadata.FindProperty(name);
                if (property != null)
                    return entry.Property(property.Name).CurrentValue;
            }
            return null;
        }

        object? Value(params string[] names) => NormalizeFirebaseValue(Raw(names));

        string? StringValue(params string[] names)
            => Raw(names)?.ToString();

        void Put(string name, object? value)
        {
            if (value != null) row[name] = value;
        }

        // Canonical Firebase contract matches the existing Android entity names.
        // Extra server-only fields are intentionally omitted from these module
        // contracts; legacy schema and business rules remain unchanged.
        switch (entityName)
        {
            case "Employee":
                Put("employeeId", StringValue("EmployeeID"));
                Put("name", Value("Name"));
                Put("dob", ToUnixMilliseconds(Value("DOB")));
                Put("role", Value("Role"));
                Put("salaryRate", Value("MonthlySalary"));
                Put("paidLeaveBalance", Value("PaidLeaveBalance"));
                Put("sickLeaveBalance", Value("SickLeaveBalance"));
                Put("salaryType", Value("PayrollTypeOverride"));
                Put("salaryCalculationMethod", Value("SalaryCalculationMethod"));
                Put("biometricId", Value("BiometricID"));
                Put("shiftStart", Value("ShiftStartTime"));
                Put("shiftEnd", Value("ShiftEndTime"));
                Put("breakHours", ToDoubleHours(Value("StandardBreakMinutes")));
                Put("hireDate", ToUnixMilliseconds(Value("HireDate")));
                Put("terminateDate", ToUnixMilliseconds(Value("TerminationDate")));
                Put("compOffDayOfWeek", Value("CompOffDayOfWeek"));
                Put("email", Value("Email"));
                Put("enablePf", Value("EnablePF"));
                Put("enableEsi", Value("EnableESI"));
                Put("uanNumber", Value("UAN"));
                Put("esiNumber", Value("ESINumber"));
                Put("nightShiftAllowance", Value("NightShiftAllowance"));
                Put("tdsRatePercent", Value("TdsRatePercent"));
                Put("bankAccountNumber", Value("BankAccountNumber"));
                Put("bankIfscCode", Value("BankIfscCode"));
                Put("bankName", Value("BankName"));
                Put("enableShiftRotation", Value("EnableShiftRotation"));
                Put("rotationGroup", Value("RotationGroup"));
                Put("shiftRotationPattern", Value("ShiftRotationPattern"));
                Put("aspNetUserId", Value("AspNetUserId"));
                Put("shiftMode", Value("ShiftMode") ?? "SINGLE_DAY");
                Put("trackingMode", Value("TrackingMode") ?? "24/7");
                Put("isActive", Value("IsDeleted") is bool deleted ? !deleted : true);
                break;

            case "AttendanceLog":
                Put("attendanceId", StringValue("LogID"));
                Put("employeeId", StringValue("EmployeeID"));
                Put("biometricId", Value("BiometricID"));
                Put("checkInTime", ToUnixMilliseconds(Raw("PunchTime")));
                Put("createdAt", ToUnixMilliseconds(Raw("PunchTime")));
                Put("note", Value("LogType"));
                Put("synced", true);
                break;

            case "PayrollHistory":
                Put("payrollId", Value("PayrollID"));
                Put("employeeId", Value("EmployeeID"));
                Put("employeeName", null);
                Put("payMonth", Value("PayMonth"));
                Put("payYear", Value("PayYear"));
                Put("baseSalary", Value("BaseSalary"));
                Put("totalHoursWorked", Value("TotalHoursWorked"));
                Put("overtimePay", Value("OvertimePay"));
                Put("deductionsHours", Value("Deductions_Hours"));
                Put("deductionsAdvance", Value("Deductions_Advance"));
                Put("bonus", Value("Bonus"));
                Put("netSalary", Value("NetSalary"));
                Put("manualLeaveDays", Value("ManualLeaveDays"));
                Put("absentDays", Value("AbsentDays"));
                Put("totalPenaltyMs", entry.Property("TotalPenaltyDuration").CurrentValue is TimeSpan penalty ? penalty.TotalMilliseconds : 0d);
                Put("totalOvertimeMs", entry.Property("TotalOvertimeDuration").CurrentValue is TimeSpan overtime ? overtime.TotalMilliseconds : 0d);
                Put("hourlyRate", Value("HourlyRate"));
                Put("basicComponent", Value("BasicComponent"));
                Put("pfDeduction", Value("PfDeduction"));
                Put("esiDeduction", Value("EsiDeduction"));
                Put("employerPfContribution", Value("EmployerPfContribution"));
                Put("employerEsiContribution", Value("EmployerEsiContribution"));
                Put("ptDeduction", Value("PtDeduction"));
                Put("tdsDeduction", Value("TdsDeduction"));
                Put("totalShiftAllowance", Value("TotalShiftAllowance"));
                break;


            case "SalaryAdvance":
                Put("advanceId", StringValue("AdvanceID"));
                Put("employeeId", StringValue("EmployeeID"));
                Put("amount", Value("Amount"));
                Put("date", ToUnixMilliseconds(Value("AdvanceDate")));
                Put("advanceType", Value("AdvanceType"));
                Put("recoveryPaymentId", Value("PayrollID_Paid"));
                Put("isRecovered", Value("PayrollID_Paid") != null);
                break;

            case "LeaveRequest":
                Put("id", StringValue("LeaveRequestID"));
                Put("staffId", StringValue("EmployeeID"));
                Put("leaveType", Value("LeaveType"));
                Put("startDate", ToUnixMilliseconds(Value("LeaveDate")));
                Put("endDate", ToUnixMilliseconds(Value("EndDate")));
                Put("reason", Value("Notes"));
                Put("status", Value("IsApproved") is bool approved ? (approved ? "Approved" : "Rejected") : "Pending");
                Put("adminNotes", Value("Notes"));
                Put("isHalfDay", Value("IsHalfDay"));
                Put("createdAt", ToUnixMilliseconds(Raw("LeaveDate")));
                break;

            case "AttendanceRegularization":
                Put("id", StringValue("RegularizationId"));
                Put("staffId", StringValue("EmployeeId"));
                Put("date", Value("DateOfPunch"));
                Put("punchType", Value("IsInPunch") is bool inPunch ? (inPunch ? "IN" : "OUT") : "IN");
                Put("requestedTime", ToUnixMilliseconds(Raw("PunchTimeNew"), Raw("DateOfPunch")));
                Put("reason", Value("Reason"));
                Put("status", Value("Status"));
                Put("adminRemarks", Value("AdminRemarks"));
                Put("submittedAt", ToUnixMilliseconds(Raw("SubmissionDate")));
                break;

            case "ResignationRequest":
                Put("requestId", StringValue("RequestId"));
                Put("employeeId", StringValue("EmployeeId"));
                Put("submissionDate", ToUnixMilliseconds(Raw("SubmissionDate")));
                Put("desiredLastWorkingDay", ToUnixMilliseconds(Raw("DesiredLastWorkingDay")));
                Put("reason", Value("Reason"));
                Put("status", Value("Status"));
                Put("approvedLastWorkingDay", ToUnixMilliseconds(Raw("ApprovedLastWorkingDay")));
                Put("adminRemarks", Value("AdminRemarks"));
                Put("isSettled", Value("IsSettled"));
                break;

            case "ShiftSchedule":
                Put("scheduleId", Value("ScheduleID"));
                Put("employeeId", StringValue("EmployeeID"));
                Put("shiftDate", Value("ShiftDate"));
                Put("startTime", Value("StartTime"));
                Put("endTime", Value("EndTime"));
                Put("isRecurringPattern", Value("IsRecurringPattern"));
                Put("patternDurationDays", Value("PatternDurationDays"));
                Put("appliesToDayOfWeek", Value("AppliesToDayOfWeek"));
                break;

            case "CompanyHoliday":
                Put("id", StringValue("HolidayID"));
                Put("shopId", "");
                Put("date", ToUnixMilliseconds(Raw("HolidayDate")));
                Put("paySalary", true);
                Put("reason", Value("HolidayName"));
                Put("affectedEmployeeIds", Array.Empty<string>());
                break;

            case "DailySummary":
                Put("summaryId", Value("SummaryID"));
                Put("employeeId", Value("EmployeeID"));
                Put("staffId", Value("EmployeeID"));
                Put("shiftDate", Value("ShiftDate"));
                Put("date", Value("ShiftDate"));
                Put("status", Value("Status"));
                Put("earnedStandardHours", Value("EarnedStandardHours"));
                Put("totalOvertimeMs", TimeSpanToMilliseconds(Raw("TotalOvertimeDuration")));
                Put("totalPenaltyMs", TimeSpanToMilliseconds(Raw("TotalPenaltyDuration")));
                Put("totalLatenessMs", TimeSpanToMilliseconds(Raw("TotalLateness")));
                Put("totalBreakPenaltyMs", TimeSpanToMilliseconds(Raw("TotalBreakPenalty")));
                Put("scheduledShiftDurationMs", TimeSpanToMilliseconds(Raw("ScheduledShiftDuration")));
                Put("shiftAllowanceEarned", Value("ShiftAllowanceEarned"));
                Put("isManualOverride", Value("IsManualOverride"));
                break;

            case "BonusRecord":
                Put("bonusId", Value("BonusID"));
                Put("employeeId", Value("EmployeeID"));
                Put("amount", Value("Amount"));
                Put("date", ToUnixMilliseconds(Raw("BonusDate")));
                Put("bonusDate", ToUnixMilliseconds(Raw("BonusDate")));
                Put("description", Value("Description"));
                Put("payrollIdPaid", Value("PayrollID_Paid"));
                break;

            case "FBPComponent":
                Put("componentId", Value("ComponentId"));
                Put("name", Value("Name"));
                Put("maxAnnualLimit", Value("MaxAnnualLimit"));
                Put("isActive", Value("IsActive"));
                Put("isTaxExempt", Value("IsTaxExempt"));
                break;

            case "FlexibleBenefitDeclaration":
                Put("declarationId", Value("DeclarationId"));
                Put("employeeId", Value("EmployeeId"));
                Put("financialYear", Value("FinancialYear"));
                Put("componentName", Value("ComponentName"));
                Put("annualAllocatedAmount", Value("AnnualAllocatedAmount"));
                Put("monthlyAllocatedAmount", Value("MonthlyAllocatedAmount"));
                Put("status", Value("Status"));
                Put("submissionDate", ToUnixMilliseconds(Raw("SubmissionDate")));
                Put("isActive", Value("IsActive"));
                Put("adminRemarks", Value("AdminRemarks"));
                break;

            case "EmployeeGpsSession":
                Put("employeeId", Value("EmployeeId"));
                Put("sessionId", StringValue("SessionId"));
                Put("startedAtUtc", Value("StartedAtUtc"));
                Put("lastUpdateAtUtc", Value("LastUpdateAtUtc"));
                Put("endedAtUtc", Value("EndedAtUtc"));
                Put("endReason", Value("EndReason"));
                Put("totalPoints", Value("TotalPoints"));
                Put("totalDistanceMeters", Value("TotalDistanceMeters"));
                break;

            case "EmployeeLocationHistory":
                Put("employeeId", Value("EmployeeId"));
                Put("sessionId", StringValue("SessionId"));
                Put("latitude", Value("Latitude"));
                Put("longitude", Value("Longitude"));
                Put("accuracyMeters", Value("AccuracyMeters"));
                Put("distanceFromOfficeMeters", Value("DistanceFromOfficeMeters"));
                Put("allowedRadiusMeters", Value("AllowedRadiusMeters"));
                Put("isWithinAllowedRadius", Value("IsWithinAllowedRadius"));
                Put("recordedAtUtc", Value("RecordedAtUtc"));
                Put("captureSource", Value("CaptureSource"));
                Put("capturedAtUtc", Value("CapturedAtUtc"));
                break;

            case "EmployeePresence":
                Put("employeeId", Value("EmployeeId"));
                Put("authUid", Value("AuthUid"));
                Put("email", Value("Email"));
                Put("platform", Value("Platform"));
                Put("deviceId", Value("DeviceId"));
                Put("active", Value("Active"));
                Put("lastSeenAt", Value("LastSeenAt"));
                break;

            default:
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.IsShadowProperty()) continue;
                    var name = ToFirebasePropertyName(property.Metadata.Name);
                    row[name] = NormalizeFirebaseValue(property.CurrentValue);
                }
                break;
        }

        return row;
    }

    private static Dictionary<string, object?> MapDatabaseRowToFirebase(
        string entityName,
        Dictionary<string, object?> raw)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        object? Get(params string[] names)
        {
            foreach (var name in names)
                if (raw.TryGetValue(name, out var value)) return value;
            return null;
        }
        void Put(string name, object? value) { if (value != null) row[name] = value; }

        switch (entityName)
        {
            case "Employee":
                Put("employeeId", Get("employeeid")); Put("name", Get("name"));
                Put("dob", ToUnixMilliseconds(Get("dob"))); Put("role", Get("role"));
                Put("salaryRate", Get("monthlysalary"));
                Put("paidLeaveBalance", Get("paidleavebalance"));
                Put("sickLeaveBalance", Get("sickleavebalance"));
                Put("salaryType", Get("payroll_type_override"));
                Put("salaryCalculationMethod", Get("salarycalculationmethod")); Put("biometricId", Get("biometricid"));
                Put("shiftStart", Get("shiftstarttime")); Put("shiftEnd", Get("shiftendtime"));
                Put("breakHours", ToDoubleHours(Get("standardbreakminutes")));
                Put("hireDate", ToUnixMilliseconds(Get("hiredate"))); Put("terminateDate", ToUnixMilliseconds(Get("terminationdate")));
                Put("compOffDayOfWeek", Get("comp_off_day")); Put("email", Get("email"));
                Put("enablePf", Get("enable_pf")); Put("enableEsi", Get("enable_esi"));
                Put("uanNumber", Get("uan_number")); Put("esiNumber", Get("esi_number"));
                Put("nightShiftAllowance", Get("nightshiftallowance")); Put("tdsRatePercent", Get("tds_rate_percent"));
                Put("bankAccountNumber", Get("bank_account_number")); Put("bankIfscCode", Get("bank_ifsc_code"));
                Put("bankName", Get("bank_name")); Put("enableShiftRotation", Get("enable_shift_rotation"));
                Put("rotationGroup", Get("rotation_group")); Put("shiftRotationPattern", Get("shift_rotation_pattern"));
                Put("isActive", Get("is_deleted") is bool deleted ? !deleted : true);
                break;
            case "AttendanceLog":
                Put("attendanceId", Get("logid")); Put("employeeId", Get("employeeid"));
                Put("biometricId", Get("biometricid")); Put("checkInTime", ToUnixMilliseconds(Get("punchtime")));
                Put("createdAt", ToUnixMilliseconds(Get("punchtime"))); Put("note", Get("logtype")); Put("synced", true);
                break;
            case "SalaryAdvance":
                Put("advanceId", Get("advanceid")); Put("employeeId", Get("employeeid"));
                Put("amount", Get("amount")); Put("date", ToUnixMilliseconds(Get("advancedate")));
                Put("recoveryPaymentId", Get("payrollid_paid")); Put("isRecovered", Get("payrollid_paid") != null); break;
            case "LeaveRequest":
                Put("id", Get("leaverequestid")); Put("staffId", Get("employeeid")); Put("leaveType", Get("leavetype"));
                Put("startDate", ToUnixMilliseconds(Get("leavedate"))); Put("endDate", ToUnixMilliseconds(Get("leavedate")));
                Put("reason", Get("notes")); Put("status", Get("isapproved") is bool a ? (a ? "Approved" : "Rejected") : "Pending");
                Put("adminNotes", Get("notes")); Put("isHalfDay", Get("is_half_day")); Put("createdAt", ToUnixMilliseconds(Get("leavedate"))); break;
            case "AttendanceRegularization":
                Put("id", Get("regularization_id")); Put("staffId", Get("employee_id")); Put("date", Get("date_of_punch"));
                Put("punchType", Get("is_in_punch") is bool i ? (i ? "IN" : "OUT") : "IN");
                Put("requestedTime", ToUnixMilliseconds(Get("punch_time_new"), Get("date_of_punch"))); Put("reason", Get("reason")); Put("status", Get("status"));
                Put("adminRemarks", Get("admin_remarks")); Put("submittedAt", ToUnixMilliseconds(Get("submission_date"))); break;
            case "ResignationRequest":
                Put("requestId", Get("request_id")); Put("employeeId", Get("employee_id")); Put("submissionDate", ToUnixMilliseconds(Get("submission_date")));
                Put("desiredLastWorkingDay", ToUnixMilliseconds(Get("desired_last_working_day"))); Put("reason", Get("reason"));
                Put("status", Get("status")); Put("approvedLastWorkingDay", ToUnixMilliseconds(Get("approved_last_working_day")));
                Put("adminRemarks", Get("admin_remarks")); Put("isSettled", Get("is_settled")); break;
            case "ShiftSchedule":
                Put("scheduleId", Get("scheduleid")); Put("employeeId", Get("employeeid")); Put("shiftDate", Get("shiftdate"));
                Put("startTime", Get("starttime")); Put("endTime", Get("endtime")); Put("isRecurringPattern", Get("is_recurring_pattern"));
                Put("patternDurationDays", Get("pattern_duration_days")); Put("appliesToDayOfWeek", Get("applies_to_day_of_week")); break;
            case "CompanyHoliday":
                Put("id", Get("holidayid")); Put("shopId", ""); Put("date", ToUnixMilliseconds(Get("holidaydate")));
                Put("paySalary", true); Put("reason", Get("holidayname")); Put("affectedEmployeeIds", Array.Empty<string>()); break;
            default:
                foreach (var kv in raw) row[ToFirebasePropertyName(kv.Key)] = NormalizeFirebaseValue(kv.Value);
                break;
        }
        return row;
    }

    private static string ToFirebasePropertyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var normalized = name.Replace("_", " ");
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
            normalized = words[0] + string.Concat(words.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant()));
        else
            normalized = char.ToLowerInvariant(normalized[0]) + normalized[1..];
        normalized = normalized.Replace("Id", "Id", StringComparison.Ordinal);
        return normalized;
    }

    private static object? ToUnixMilliseconds(object? value, object? secondary = null)
    {
        if (value == null) return null;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        if (value is DateTimeOffset dto) return dto.ToUnixTimeMilliseconds();
        if (value is DateOnly d) return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        if (value is TimeOnly t)
        {
            if (secondary is DateOnly date) return new DateTimeOffset(date.ToDateTime(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            return t.ToTimeSpan().TotalMilliseconds;
        }
        if (value is string s)
        {
            if (long.TryParse(s, out var l)) return l;
            if (DateTimeOffset.TryParse(s, out var parsed)) return parsed.ToUnixTimeMilliseconds();
            if (DateOnly.TryParse(s, out var date))
            {
                if (secondary is TimeOnly time)
                    return new DateTimeOffset(date.ToDateTime(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                if (secondary is string secondaryText && TimeOnly.TryParse(secondaryText, out var secondaryTime))
                    return new DateTimeOffset(date.ToDateTime(secondaryTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            }
            if (secondary is DateOnly secondaryDate && TimeOnly.TryParse(s, out var parsedTime))
                return new DateTimeOffset(secondaryDate.ToDateTime(parsedTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            if (secondary is string secondaryDateText && DateOnly.TryParse(secondaryDateText, out var parsedDate) && TimeOnly.TryParse(s, out var parsedTime2))
                return new DateTimeOffset(parsedDate.ToDateTime(parsedTime2, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }
        return value;
    }

    private static object? ToDoubleHours(object? minutes)
    {
        if (minutes is null) return null;
        if (double.TryParse(minutes.ToString(), out var m)) return m / 60.0;
        return null;
    }

    private static object? TimeSpanToMilliseconds(object? value)
    {
        if (value is TimeSpan ts) return ts.TotalMilliseconds;
        if (value is string text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
            return parsed.TotalMilliseconds;
        if (value is double d) return d;
        if (value is decimal dec) return (double)dec;
        if (value is long l) return l;
        return null;
    }

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

    /// <summary>
    /// One-time/backfill migration from the existing local compatibility database
    /// into Firebase. Existing Firebase records are never overwritten. Once a
    /// record exists in Firebase, Firebase remains the runtime SSOT.
    /// </summary>
    public async Task<int> SeedMissingFirebaseRecordsAsync(
        IDbContextFactory<AppDbContext> factory,
        string ownerUid,
        CancellationToken cancellationToken = default)
    {
        if (factory == null || string.IsNullOrWhiteSpace(ownerUid)) return 0;
        var seeded = 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        foreach (var entityName in RealtimeEntities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // EmployeeLocationHistory is stored under tracking/history,
            // which is an unbounded GPS collection.
            //
            // NEVER perform a generic whole-tree read here:
            //
            // owners/{ownerUid}/tracking/history
            //
            // GPS history is handled by employee-scoped, limited reads.
            if (entityName == "EmployeeLocationHistory" ||
                entityName == "AttendanceLog")
            {
                // Both branches are potentially unbounded collections. GPS history
                // already uses employee-scoped limited reads. AttendanceLog is
                // published through the canonical attendance_punches path and must
                // never be downloaded as one owner-wide snapshot during startup.
                _logger.LogInformation(
                    "Skipping generic Firebase seed for {EntityName}. " +
                    "The collection uses scoped/realtime synchronization instead.",
                    entityName);

                continue;
            }

            var table = GetFirebaseTable(entityName);
            if (string.IsNullOrWhiteSpace(table))
                continue;

            var remote = await GetOwnerTableAsync(
                ownerUid,
                table,
                cancellationToken);
            var existingKeys = new HashSet<string>(StringComparer.Ordinal);
            if (remote.HasValue)
            {
                if (remote.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in remote.Value.EnumerateObject()) existingKeys.Add(p.Name);
                }
                else if (remote.Value.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var item in remote.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Null) existingKeys.Add(index.ToString(CultureInfo.InvariantCulture));
                        index++;
                    }
                }
            }

            var entityType = db.Model.GetEntityTypes()
                .FirstOrDefault(x => x.ClrType.Name == entityName);
            if (entityType == null) continue;

            var rows = await GetEntitySet(db, entityType.ClrType).Cast<object>().ToListAsync(cancellationToken);
            var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                var key = BuildKey(entry);
                if (string.IsNullOrWhiteSpace(key) || existingKeys.Contains(key)) continue;

                var firebaseRow = BuildFirebaseRow(entry, entityName, key);
                firebaseRow["_entity"] = entityName;
                firebaseRow["_key"] = key;
                firebaseRow["_updatedUtc"] = DateTime.UtcNow.ToString("O");
                updates[$"owners/{ownerUid}/{table}/{EscapeFirebaseKey(key)}"] = firebaseRow;

                if (updates.Count >= 100)
                {
                    if (await UpdateAsync(updates, cancellationToken)) seeded += updates.Count;
                    updates.Clear();
                }
            }

            if (updates.Count > 0 && await UpdateAsync(updates, cancellationToken))
                seeded += updates.Count;
        }

        return seeded;
    }

    /// <summary>
    /// Deletes a specific path in Firebase Realtime Database using native HTTP DELETE.
    /// Returns true on success (HTTP 200/204), cleanly wiping the entire node subtree.
    /// </summary>
    public async Task<bool> DeletePathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var context = await _context.Value;
        if (context == null)
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var cleanPath = path.Trim().TrimStart('/').TrimEnd('/');
            var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/{cleanPath}.json");
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await context.GetAccessTokenAsync());

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Handle Firebase error when node data size exceeds single-request limit:
            // "Data to write exceeds the maximum size that can be modified with a single request."
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                body.Contains("Data to write exceeds the maximum size", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Node {Path} exceeds Firebase single-request write limit. Initiating recursive shallow-chunked deletion.",
                    cleanPath);
                return await DeleteLargeNodeInChunksAsync(cleanPath, cancellationToken);
            }

            _logger.LogWarning(
                "Firebase realtime DELETE failed with HTTP {Status} for {Path}: {Body}",
                (int)response.StatusCode,
                path,
                body.Length > 500 ? body[..500] : body);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase realtime DELETE deferred for {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Deletes large Firebase nodes (e.g. tracking/history containing tens of thousands of GPS breadcrumbs)
    /// by inspecting shallow keys and deleting child trees in chunks, preventing HTTP 400 "exceeds maximum size".
    /// </summary>
    private async Task<bool> DeleteLargeNodeInChunksAsync(
        string cleanPath,
        CancellationToken cancellationToken = default)
    {
        var context = await _context.Value;
        if (context == null)
            return false;

        try
        {
            var shallow = await GetJsonAsync(cleanPath, cancellationToken, "shallow=true");
            if (!shallow.HasValue || shallow.Value.ValueKind != JsonValueKind.Object)
            {
                // Nothing or not an object, consider it already cleared
                return true;
            }

            var keys = new List<string>();
            foreach (var prop in shallow.Value.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(prop.Name))
                    keys.Add(prop.Name);
            }

            _logger.LogInformation("Chunking deletion of large node {Path}: {Count} child key(s) discovered.", cleanPath, keys.Count);

            var allChildrenOk = true;
            foreach (var key in keys)
            {
                var childPath = $"{cleanPath}/{key}";
                var childOk = await DeletePathAsync(childPath, cancellationToken);
                if (!childOk) allChildrenOk = false;
            }

            // Once all child nodes are deleted, delete the now empty parent node
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/{cleanPath}.json");
            using var req = new HttpRequestMessage(HttpMethod.Delete, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await context.GetAccessTokenAsync());
            using var resp = await client.SendAsync(req, cancellationToken);

            return resp.IsSuccessStatusCode || allChildrenOk;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed chunked deletion for large node {Path}", cleanPath);
            return false;
        }
    }

    /// <summary>
    /// Discovers all owner UIDs currently stored under the /owners tree in Firebase,
    /// along with the configured and default owner UIDs.
    /// </summary>
    public async Task<List<string>> GetAvailableOwnerUidsAsync(CancellationToken cancellationToken = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configured = _configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID");
        if (!string.IsNullOrWhiteSpace(configured))
            set.Add(configured.Trim());

        set.Add(Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid);

        try
        {
            var shallow = await GetJsonAsync("owners", cancellationToken, "shallow=true");
            if (shallow.HasValue && shallow.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in shallow.Value.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(prop.Name))
                        set.Add(prop.Name.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not discover shallow owner keys from Firebase.");
        }

        return set.ToList();
    }

    /// <summary>
    /// Partial Wipe: Wipes operational data (attendance, tracking, payroll, leaves, advances, audits)
    /// but strictly preserves Employees, Shops, Company Settings, Feature Toggles, and Holidays in Firebase.
    /// Uses native HTTP DELETE per table to prevent path conflicts or 400 errors.
    /// </summary>
    public async Task<bool> WipeOwnerOperationalDataOnlyAsync(
        string ownerUid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            return false;

        var cleanUid = ownerUid.Trim();
        var operationalTables = new[]
        {
            "attendance",
            "attendance_punches",
            "daily_summaries",
            "advance_payments",
            "regularizations",
            "leave_requests",
            "resignation_requests",
            "salary_snapshots",
            "audit_logs",
            "shift_schedules",
            "payroll_history",
            "payroll_previews",
            "payroll_finalization",
            "bonus_records",
            "bonuses",
            "tax_declarations",
            "fbp_components",
            "fbp_declarations",
            "year_end_summaries",
            "fnf_settlements",
            "report_definitions",
            "geo_punch_audits",
            "presence",
            "tracking/history",
            "tracking/sessions",
            "tracking/live",
            "tracking",
            "offline_tracking",
            "offline_tracking_events",
            "notifications",
            "events"
        };

        var allOk = true;
        foreach (var table in operationalTables)
        {
            var ok = await DeletePathAsync($"owners/{cleanUid}/{table}", cancellationToken);
            if (!ok) allOk = false;
        }

        await DeletePathAsync($"owner_events/{cleanUid}", cancellationToken);
        await DeletePathAsync("mobile_auth_events", cancellationToken);

        _logger.LogInformation("WipeOwnerOperationalDataOnlyAsync for owner {OwnerUid}: success={Success}", cleanUid, allOk);
        return allOk;
    }

    /// <summary>
    /// Full Wipe: Wipes all operational data PLUS employees, shops, and employee history.
    /// Preserves only configuration: feature_settings, company_settings, professional_tax_slabs, and shop_closed_days.
    /// </summary>
    public async Task<bool> WipeOwnerAllDataAsync(
        string ownerUid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            return false;

        var cleanUid = ownerUid.Trim();
        var allTables = new[]
        {
            "employees",
            "employee_history",
            "shops",
            "attendance",
            "attendance_punches",
            "daily_summaries",
            "advance_payments",
            "regularizations",
            "leave_requests",
            "resignation_requests",
            "salary_snapshots",
            "audit_logs",
            "shift_schedules",
            "payroll_history",
            "payroll_previews",
            "payroll_finalization",
            "bonus_records",
            "bonuses",
            "tax_declarations",
            "fbp_components",
            "fbp_declarations",
            "year_end_summaries",
            "fnf_settlements",
            "report_definitions",
            "geo_punch_audits",
            "presence",
            "tracking/history",
            "tracking/sessions",
            "tracking/live",
            "tracking",
            "offline_tracking",
            "offline_tracking_events",
            "notifications",
            "events"
        };

        var allOk = true;
        foreach (var table in allTables)
        {
            var ok = await DeletePathAsync($"owners/{cleanUid}/{table}", cancellationToken);
            if (!ok) allOk = false;
        }

        await DeletePathAsync($"owner_events/{cleanUid}", cancellationToken);
        await DeletePathAsync("mobile_auth_events", cancellationToken);

        _logger.LogInformation("WipeOwnerAllDataAsync for owner {OwnerUid}: success={Success}", cleanUid, allOk);
        return allOk;
    }

    public Task<bool> WipeOwnerOperationalDataAsync(string ownerUid, CancellationToken cancellationToken = default)
        => WipeOwnerAllDataAsync(ownerUid, cancellationToken);

    /// <summary>
    /// Enterprise Cloud Pruner: Automatically purges historical GPS points older than maxGpsAge (e.g. 24h)
    /// from Firebase Realtime Database.
    /// This keeps the cloud database permanently under 2–5 MB on the free Spark plan,
    /// while local SQLite preserves complete multi-year history.
    /// </summary>
    public async Task<int> PruneExpiredTrackingAndAuditsAsync(
        string ownerUid,
        TimeSpan maxGpsAge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            return 0;

        var context = await _context.Value;
        if (context == null)
            return 0;

        var prunedCount = 0;
        var cleanUid = ownerUid.Trim();

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var token = await context.GetAccessTokenAsync();

            // 1. Prune tracking/history older than maxGpsAge (e.g. 24 hours)
            var historyPath = $"owners/{cleanUid}/tracking/history";
            var shallow = await GetJsonAsync(historyPath, cancellationToken, "shallow=true");
            if (shallow.HasValue && shallow.Value.ValueKind == JsonValueKind.Object)
            {
                var cutoff = DateTime.UtcNow.Subtract(maxGpsAge);
                var subKeys = new List<string>();
                foreach (var prop in shallow.Value.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(prop.Name))
                        subKeys.Add(prop.Name);
                }

                foreach (var sk in subKeys)
                {
                    var childPath = $"{historyPath}/{sk}";
                    var childData = await GetJsonAsync(childPath, cancellationToken);
                    if (childData.HasValue && childData.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (childData.Value.TryGetProperty("Timestamp", out var tsProp) ||
                            childData.Value.TryGetProperty("timestamp", out tsProp))
                        {
                            if (DateTime.TryParse(tsProp.GetString(), out var eventTime) && eventTime < cutoff)
                            {
                                await DeletePathAsync(childPath, cancellationToken);
                                prunedCount++;
                            }
                        }
                        else
                        {
                            var leafKeysToDelete = new List<string>();
                            foreach (var leaf in childData.Value.EnumerateObject())
                            {
                                if (leaf.Value.ValueKind == JsonValueKind.Object &&
                                    (leaf.Value.TryGetProperty("Timestamp", out var lts) || leaf.Value.TryGetProperty("timestamp", out lts)))
                                {
                                    if (DateTime.TryParse(lts.GetString(), out var lTime) && lTime < cutoff)
                                    {
                                        leafKeysToDelete.Add(leaf.Name);
                                    }
                                }
                            }

                            if (leafKeysToDelete.Count > 0)
                            {
                                const int batchSize = 200;
                                for (int i = 0; i < leafKeysToDelete.Count; i += batchSize)
                                {
                                    var count = Math.Min(batchSize, leafKeysToDelete.Count - i);
                                    var batch = leafKeysToDelete.GetRange(i, count);
                                    var patch = new Dictionary<string, object?>();
                                    foreach (var k in batch) patch[k] = null;

                                    var patchUri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/{childPath}.json");
                                    using var patchReq = new HttpRequestMessage(HttpMethod.Patch, patchUri)
                                    {
                                        Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
                                    };
                                    patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                                    await client.SendAsync(patchReq, cancellationToken);
                                    prunedCount += batch.Count;
                                }
                            }
                        }
                    }
                }
            }

            if (prunedCount > 0)
            {
                _logger.LogInformation("🧹 Cloud Pruner: Deleted {Count} expired GPS points (> {Hours}h) for owner {OwnerUid}",
                    prunedCount, maxGpsAge.TotalHours, cleanUid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud Pruner encountered an issue for owner {OwnerUid}", cleanUid);
        }

        return prunedCount;
    }

    /// <summary>
    /// Restores/pushes all local SQLite data up to the Cloud (Firebase Realtime Database),
    /// ensuring Cloud matches the restored local database state.
    /// </summary>
    public async Task<int> PushAllLocalDataToFirebaseAsync(
        string ownerUid,
        IDbContextFactory<AppDbContext> factory,
        CancellationToken cancellationToken = default)
    {
        if (factory == null || string.IsNullOrWhiteSpace(ownerUid)) return 0;
        var pushed = 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Wipe operational data in cloud first so deleted records don't persist
        await WipeOwnerOperationalDataAsync(ownerUid, cancellationToken);

        foreach (var entityName in RealtimeEntities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = GetFirebaseTable(entityName);
            if (string.IsNullOrWhiteSpace(table)) continue;

            var entityType = db.Model.GetEntityTypes().FirstOrDefault(x => x.ClrType.Name == entityName);
            if (entityType == null) continue;

            var rows = await GetEntitySet(db, entityType.ClrType).Cast<object>().ToListAsync(cancellationToken);
            var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                var key = BuildKey(entry);
                if (string.IsNullOrWhiteSpace(key)) continue;

                var firebaseRow = BuildFirebaseRow(entry, entityName, key);
                firebaseRow["_entity"] = entityName;
                firebaseRow["_key"] = key;
                firebaseRow["_updatedUtc"] = DateTime.UtcNow.ToString("O");
                updates[$"owners/{ownerUid}/{table}/{EscapeFirebaseKey(key)}"] = firebaseRow;

                if (updates.Count >= 100)
                {
                    if (await UpdateAsync(updates, cancellationToken)) pushed += updates.Count;
                    updates.Clear();
                }
            }

            if (updates.Count > 0 && await UpdateAsync(updates, cancellationToken))
                pushed += updates.Count;
        }

        _logger.LogInformation("PushAllLocalDataToFirebaseAsync restored {Count} records to Firebase owner {OwnerUid}", pushed, ownerUid);
        return pushed;
    }

    public async Task<bool> EnsureConfiguredAsync()
    {
        return await _context.Value != null;
    }

    /// <summary>
    /// Returns Firebase Authentication bound to the exact FirebaseApp initialized
    /// by this service.
    /// </summary>
    public async Task<FirebaseAuth?> GetFirebaseAuthAsync(CancellationToken cancellationToken = default)
    {
        var context = await _context.Value;
        if (context == null)
            return null;

        return FirebaseAuth.GetAuth(context.App);
    }

    public bool IsConfigured => _context.IsValueCreated && _context.Value.IsCompletedSuccessfully && _context.Value.Result != null;

    public async Task<bool> SetAsync(string path, object value, CancellationToken cancellationToken = default)
        => await UpdateAsync(new Dictionary<string, object?> { [path] = value }, cancellationToken);

    private async Task<JsonElement?> GetJsonAsync(
        string path,
        CancellationToken cancellationToken,
        string? queryString = null)
    {
        var context = await _context.Value;
        if (context == null)
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var uri = new Uri(
                $"{context.DatabaseUrl.TrimEnd('/')}/{path.TrimStart('/')}.json{queryString}");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    await context.GetAccessTokenAsync());

            using var response =
                await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // In Firebase Realtime Database REST API, an empty or uninitialized node returns HTTP 404.
                    // This is an expected and benign condition (no records exist yet for this path).
                    _logger.LogDebug(
                        "Firebase realtime node not found at {Path} (empty or not yet created).",
                        path);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning(
                        "Firebase REST 401 for {Path}. The Firebase OAuth credential was rejected. " +
                        "Ensure the same service-account JSON used by the Firebase Admin SDK is available " +
                        "through Firebase:ServiceAccountPath or GOOGLE_APPLICATION_CREDENTIALS. Response: {Body}",
                        path,
                        body.Length > 300 ? body[..300] : body);
                }
                else
                {
                    _logger.LogWarning(
                        "Firebase realtime read failed with HTTP {Status} for {Path}: {Body}",
                        (int)response.StatusCode,
                        path,
                        body.Length > 300 ? body[..300] : body);
                }
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json) || json == "null")
                return null;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase realtime read deferred for {Path}", path);
            return null;
        }
    }

    public async Task<bool> UpdateAsync(
        IReadOnlyDictionary<string, object?> updates,
        CancellationToken cancellationToken = default)
    {
        var context = await _context.Value;
        if (context == null)
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var uri = new Uri($"{context.DatabaseUrl.TrimEnd('/')}/.json?print=silent");
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
            GoogleCredential credential;
            string credentialSource;

            // Prefer the explicit service-account file. This is important for
            // local development because Visual Studio may not inherit a
            // GOOGLE_APPLICATION_CREDENTIALS variable that was set in another
            // PowerShell process.
            var credentialsPath =
                _configuration["Firebase:ServiceAccountPath"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            // If not found at configured path or if no path was provided, check local application directory first,
            // then fallback to development C:\FirebaseSecrets.
            if (string.IsNullOrWhiteSpace(credentialsPath) || !File.Exists(credentialsPath))
            {
                var localAppPath = Path.Combine(AppContext.BaseDirectory, "firebase-service-account.json");
                if (File.Exists(localAppPath))
                {
                    credentialsPath = localAppPath;
                }
                else if (File.Exists(@"C:\FirebaseSecrets\firebase-service-account.json"))
                {
                    credentialsPath = @"C:\FirebaseSecrets\firebase-service-account.json";
                }
            }

            if (!string.IsNullOrWhiteSpace(credentialsPath))
            {
                if (!File.Exists(credentialsPath))
                    throw new FileNotFoundException(
                        $"Firebase service-account file was not found: {credentialsPath}");

                credential = CredentialFactory
                    .FromFile(
                        credentialsPath,
                        JsonCredentialParameters.ServiceAccountCredentialType)
                    .CreateScoped(CloudPlatformScope, DatabaseScope, UserInfoEmailScope);

                credentialSource = $"SERVICE_ACCOUNT_FILE:{credentialsPath}";
            }
            else
            {
                // Inline JSON is retained only as a fallback for deployments
                // that intentionally provide it. It can no longer override an
                // explicitly available service-account file.
                var json = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON");

                if (!string.IsNullOrWhiteSpace(json))
                {
                    credential = CredentialFactory
                        .FromJson(
                            json,
                            JsonCredentialParameters.ServiceAccountCredentialType)
                        .CreateScoped(CloudPlatformScope, DatabaseScope, UserInfoEmailScope);

                    credentialSource = "FIREBASE_SERVICE_ACCOUNT_JSON";
                }
                else
                {
                    credential = (await GoogleCredential.GetApplicationDefaultAsync())
                        .CreateScoped(CloudPlatformScope, DatabaseScope, UserInfoEmailScope);

                    credentialSource = "APPLICATION_DEFAULT_CREDENTIALS";
                }
            }

            var projectId = _configuration["Firebase:ProjectId"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
                ?? "biometricpayroll";

            var databaseUrl = _configuration["Firebase:DatabaseUrl"]
                ?? Environment.GetEnvironmentVariable("FIREBASE_DATABASE_URL")
                ?? "https://biometricpayroll-default-rtdb.asia-southeast1.firebasedatabase.app";

            _logger.LogInformation(
                "Initializing Firebase Admin with credential source {CredentialSource} for project {ProjectId}.",
                credentialSource,
                projectId);

            FirebaseApp? app = null;

            try
            {
                app = FirebaseApp.GetInstance(FirebaseAppName);
            }
            catch
            {
                // The named app has not been created yet.
            }

            if (app == null)
            {
                app = FirebaseApp.Create(
                    new AppOptions
                    {
                        Credential = credential,
                        ProjectId = projectId
                    },
                    FirebaseAppName);
            }

            if (app == null)
                throw new InvalidOperationException(
                    $"Firebase Admin app '{FirebaseAppName}' could not be initialized.");

            return new FirebaseContext(app, databaseUrl, credential);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Firebase Admin bridge is not configured. Configure Firebase:ServiceAccountPath, GOOGLE_APPLICATION_CREDENTIALS, FIREBASE_SERVICE_ACCOUNT_JSON, or Application Default Credentials.");
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

public sealed record FirebasePasswordVerificationResult(
    bool Success,
    string? LocalId,
    string? Email,
    string? IdToken,
    string Message);

