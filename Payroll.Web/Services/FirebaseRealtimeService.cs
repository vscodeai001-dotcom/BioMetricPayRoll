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

    // ---------------------------------------------------------------------
    // Firebase SSOT owner-store primitives
    // ---------------------------------------------------------------------
    // These methods are deliberately table-whitelisted. They provide the Web
    // layer with a Firebase-native CRUD path without exposing arbitrary
    // database paths to callers. The existing Neon-backed business services
    // can be migrated module-by-module without changing UI/layout/business
    // rules.

    private static readonly HashSet<string> FirebaseSsotTables =
        new(StringComparer.Ordinal)
        {
            "employees",
            "shops",
            "attendance",
            "attendance_punches",
            "advance_payments",
            "employee_history",
            "shop_closed_days",
            "regularizations",
            "leave_requests",
            "resignation_requests",
            "salary_snapshots",
            "audit_logs",
            "daily_summaries",
            "shift_schedules",
            "payroll_history",
            "bonus_records",
            "tax_declarations",
            "fbp_components",
            "fbp_declarations",
            "company_settings",
            "feature_settings",
            "professional_tax_slabs",
            "year_end_summaries",
            "fnf_settlements",
            "report_definitions",
            "geo_punch_audits"
        };

    public bool IsFirebaseSsotTable(string table)
        => !string.IsNullOrWhiteSpace(table) &&
           FirebaseSsotTables.Contains(table.Trim());

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
            _ => null
        };

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
        // contracts; Neon schema and business rules remain unchanged.
        switch (entityName)
        {
            case "Employee":
                Put("employeeId", StringValue("EmployeeID"));
                Put("name", Value("Name"));
                Put("dob", ToUnixMilliseconds(Value("DOB")));
                Put("role", Value("Role"));
                Put("salaryRate", Value("MonthlySalary"));
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

            case "SalaryAdvance":
                Put("advanceId", StringValue("AdvanceID"));
                Put("employeeId", StringValue("EmployeeID"));
                Put("amount", Value("Amount"));
                Put("date", ToUnixMilliseconds(Value("AdvanceDate")));
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
                Put("salaryRate", Get("monthlysalary")); Put("salaryType", Get("payroll_type_override"));
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

    private async Task<bool> SetAsync(string path, object value, CancellationToken cancellationToken)
        => await UpdateAsync(new Dictionary<string, object?> { [path] = value }, cancellationToken);

    private async Task<JsonElement?> GetJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var context = await _context.Value;
        if (context == null)
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("FirebaseRealtime");
            var uri = new Uri(
                $"{context.DatabaseUrl.TrimEnd('/')}/{path.TrimStart('/')}.json");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    await context.GetAccessTokenAsync());

            using var response =
                await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Firebase realtime read failed with HTTP {Status} for {Path}",
                    (int)response.StatusCode,
                    path);
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
            GoogleCredential credential;
            string credentialSource;

            // Prefer the explicit service-account file. This is important for
            // local development because Visual Studio may not inherit a
            // GOOGLE_APPLICATION_CREDENTIALS variable that was set in another
            // PowerShell process.
            var credentialsPath =
                _configuration["Firebase:ServiceAccountPath"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            // Known local development location used for this installation.
            // It is only used when the explicit path is not configured.
            if (string.IsNullOrWhiteSpace(credentialsPath) &&
                File.Exists(@"C:\FirebaseSecrets\firebase-service-account.json"))
            {
                credentialsPath = @"C:\FirebaseSecrets\firebase-service-account.json";
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
