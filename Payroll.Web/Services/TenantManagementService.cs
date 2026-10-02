using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Services
{
    public class CreateTenantRequest
    {
        public string CompanyName { get; set; } = string.Empty;
        public string CompanyCode { get; set; } = string.Empty;
        public string IconEmoji { get; set; } = "🏢";
        public string PlanMode { get; set; } = "Spark"; // "Spark" or "Blaze"
        public bool IsOfflineMode { get; set; } = false; // true = 100% standalone local SQLite DB, false = Firebase Cloud sync
        public string DeploymentMode { get; set; } = "Online"; // "Online" or "Offline"

        public string AdminEmail { get; set; } = string.Empty;
        public string AdminPassword { get; set; } = string.Empty;
        public string AdminName { get; set; } = string.Empty;
        public string? AdminPhone { get; set; }

        public double OfficeLatitude { get; set; } = 11.9416;
        public double OfficeLongitude { get; set; } = 79.8083;
        public int GeoRadiusMeters { get; set; } = 100;
        public int WorkDayCutoffHour { get; set; } = 22;

        public FeatureSettings InitialFeatures { get; set; } = new FeatureSettings();
    }

    public class TenantOperationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public CompanyTenant? Tenant { get; set; }
    }

    public class TenantManagementService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly FirebaseRealtimeService _firebase;
        private readonly AttendanceRefreshService _refreshService;
        private readonly ILogger<TenantManagementService> _logger;

        public TenantManagementService(
            IDbContextFactory<AppDbContext> dbFactory,
            UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager,
            FirebaseRealtimeService firebase,
            AttendanceRefreshService refreshService,
            ILogger<TenantManagementService> logger)
        {
            _dbFactory = dbFactory;
            _userManager = userManager;
            _roleManager = roleManager;
            _firebase = firebase;
            _refreshService = refreshService;
            _logger = logger;
        }

        public async Task EnsureDefaultTenantSeededAsync()
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();

                // Ensure the table exists in SQLite
                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""CompanyTenants"" (
                        ""id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                        ""tenant_id"" TEXT NOT NULL,
                        ""company_name"" TEXT NOT NULL,
                        ""company_code"" TEXT NOT NULL,
                        ""admin_user_id"" TEXT NULL,
                        ""admin_email"" TEXT NOT NULL,
                        ""admin_name"" TEXT NOT NULL,
                        ""admin_phone"" TEXT NULL,
                        ""icon_emoji"" TEXT NOT NULL DEFAULT '🏢',
                        ""plan_mode"" TEXT NOT NULL DEFAULT 'Spark',
                        ""is_active"" INTEGER NOT NULL DEFAULT 1,
                        ""created_at_utc"" TEXT NOT NULL,
                        ""company_setting_id"" INTEGER NOT NULL DEFAULT 1,
                        ""feature_settings_id"" INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CompanyTenants_tenant_id"" ON ""CompanyTenants"" (""tenant_id"");
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CompanyTenants_company_code"" ON ""CompanyTenants"" (""company_code"");
                ");

                await AppDbContext.EnsureSqliteSchemaUpdatedAsync(db);

                var primaryTenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.TenantId == TenantContextService.DefaultTenantId);
                if (primaryTenant == null)
                {
                    var defaultCompany = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == 1);
                    var companyName = defaultCompany?.CompanyName;
                    if (string.IsNullOrWhiteSpace(companyName) || companyName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
                    {
                        companyName = "Yes company";
                    }

                    var tenant = new CompanyTenant
                    {
                        TenantId = TenantContextService.DefaultTenantId,
                        CompanyName = companyName,
                        CompanyCode = "PRIMARY",
                        AdminEmail = FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail,
                        AdminName = "Super Administrator",
                        AdminPhone = "",
                        IconEmoji = "🏢",
                        PlanMode = "Spark",
                        IsActive = true,
                        CreatedAtUtc = DateTime.UtcNow,
                        CompanySettingId = 1,
                        FeatureSettingsId = 1
                    };

                    db.CompanyTenants.Add(tenant);
                    await db.SaveChangesAsync();
                    _logger.LogInformation("Seeded default primary tenant {TenantId} ({CompanyName})", tenant.TenantId, tenant.CompanyName);
                }
                else
                {
                    // Detect and repair any accidental name collision between primary and secondary tenants
                    var secondaryTenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.TenantId != TenantContextService.DefaultTenantId);
                    if (secondaryTenant != null && string.Equals(primaryTenant.CompanyName, secondaryTenant.CompanyName, StringComparison.OrdinalIgnoreCase))
                    {
                        primaryTenant.CompanyName = "Yes company";
                        var setting1 = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == 1);
                        if (setting1 != null)
                        {
                            setting1.CompanyName = "Yes company";
                        }
                        await db.SaveChangesAsync();
                        _logger.LogInformation("Restored primary tenant {TenantId} to 'Yes company' after collision with tenant {SecondaryId}", primaryTenant.TenantId, secondaryTenant.TenantId);
                    }
                }

                // Ensure every tenant has their initial company settings node in Firebase
                var allTenants = await db.CompanyTenants.ToListAsync();
                foreach (var t in allTenants)
                {
                    try
                    {
                        var cs = await db.CompanySettings.FirstOrDefaultAsync(s => s.SettingID == t.CompanySettingId);
                        if (cs == null)
                        {
                            cs = new CompanySetting
                            {
                                SettingID = t.CompanySettingId,
                                CompanyName = t.CompanyName,
                                AddressLine1 = "Office Location",
                                CityStatePincode = "",
                                OfficeLatitude = 11.9416,
                                OfficeLongitude = 79.8083,
                                GeoRadiusMeters = 100,
                                WorkDayCutoffHour = 22,
                                LateGraceMinutes = 15,
                                EndTimeGraceMinutes = 15
                            };
                            db.CompanySettings.Add(cs);
                            await db.SaveChangesAsync();
                        }

                        var existingFb = await _firebase.GetOwnerRecordAsync(t.TenantId, "company_settings", "1");
                        if (!existingFb.HasValue || existingFb.Value.ValueKind == System.Text.Json.JsonValueKind.Null)
                        {
                            var compPayload = new Dictionary<string, object?>
                            {
                                ["companyName"] = cs.CompanyName,
                                ["addressLine1"] = cs.AddressLine1,
                                ["cityStatePincode"] = cs.CityStatePincode,
                                ["officeLatitude"] = cs.OfficeLatitude,
                                ["officeLongitude"] = cs.OfficeLongitude,
                                ["geoRadiusMeters"] = cs.GeoRadiusMeters,
                                ["workDayCutoffHour"] = cs.WorkDayCutoffHour,
                                ["lateGraceMinutes"] = cs.LateGraceMinutes,
                                ["endTimeGraceMinutes"] = cs.EndTimeGraceMinutes
                            };
                            await _firebase.SetAsync($"owners/{t.TenantId}/company_settings/1", compPayload, default);
                            _logger.LogInformation("Synchronized initial company setting for tenant {TenantId} to Firebase.", t.TenantId);
                        }
                    }
                    catch (Exception exSync)
                    {
                        _logger.LogWarning(exSync, "Could not verify/sync initial Firebase node for tenant {TenantId}", t.TenantId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while seeding default primary tenant.");
            }
        }

        public async Task<TenantOperationResult> CreateTenantAsync(CreateTenantRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.CompanyName))
                return new TenantOperationResult { Success = false, ErrorMessage = "Company Name is required." };

            if (string.IsNullOrWhiteSpace(request.AdminEmail))
                return new TenantOperationResult { Success = false, ErrorMessage = "Admin Email is required." };

            if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 6)
                return new TenantOperationResult { Success = false, ErrorMessage = "Password must be at least 6 characters." };

            var code = string.IsNullOrWhiteSpace(request.CompanyCode)
                ? Regex.Replace(request.CompanyName.ToUpperInvariant(), "[^A-Z0-9]", "")
                : Regex.Replace(request.CompanyCode.ToUpperInvariant(), "[^A-Z0-9]", "");

            if (code.Length > 10) code = code[..10];
            if (string.IsNullOrWhiteSpace(code)) code = "COMP" + new Random().Next(100, 999);

            var tenantId = "tenant_" + code.ToLowerInvariant();

            await using var db = await _dbFactory.CreateDbContextAsync();

            // Check if tenant_id or code already exists
            if (await db.CompanyTenants.AnyAsync(t => t.TenantId == tenantId || t.CompanyCode == code))
            {
                tenantId = $"{tenantId}_{new Random().Next(10, 99)}";
                code = $"{code}{new Random().Next(1, 9)}";
            }

            // Check admin email
            var existingUser = await _userManager.FindByEmailAsync(request.AdminEmail.Trim());
            IdentityUser adminUser;

            if (existingUser != null)
            {
                adminUser = existingUser;
                if (!await _userManager.IsInRoleAsync(adminUser, "Admin") && !await _userManager.IsInRoleAsync(adminUser, "SuperAdmin"))
                {
                    await _userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }
            else
            {
                // Ensure Admin role exists
                if (!await _roleManager.RoleExistsAsync("Admin"))
                {
                    await _roleManager.CreateAsync(new IdentityRole("Admin"));
                }

                adminUser = new IdentityUser
                {
                    UserName = request.AdminEmail.Trim(),
                    Email = request.AdminEmail.Trim(),
                    EmailConfirmed = true
                };

                var createRes = await _userManager.CreateAsync(adminUser, request.AdminPassword);
                if (!createRes.Succeeded)
                {
                    var err = string.Join("; ", createRes.Errors.Select(e => e.Description));
                    return new TenantOperationResult { Success = false, ErrorMessage = $"Admin account creation failed: {err}" };
                }

                await _userManager.AddToRoleAsync(adminUser, "Admin");
            }

            var isOffline = request.IsOfflineMode || string.Equals(request.DeploymentMode, "Offline", StringComparison.OrdinalIgnoreCase);

            // Provision Firebase Auth account for tenant admin (if online)
            if (!isOffline)
            {
                try
                {
                    await _firebase.EnsureFirebaseUserAsync(
                        request.AdminEmail.Trim(),
                        request.AdminPassword,
                        "Admin",
                        displayName: string.IsNullOrWhiteSpace(request.AdminName) ? request.CompanyName.Trim() + " Admin" : request.AdminName.Trim(),
                        updatePasswordIfExisting: true);
                }
                catch (Exception authEx)
                {
                    _logger.LogWarning(authEx, "Firebase Auth provisioning deferred for admin {Email}", request.AdminEmail);
                }
            }

            // Create CompanySetting
            var maxSettingId = await db.CompanySettings.MaxAsync(c => (int?)c.SettingID) ?? 0;
            var companySetting = new CompanySetting
            {
                SettingID = maxSettingId + 1,
                CompanyName = request.CompanyName.Trim(),
                AddressLine1 = "Office Location",
                CityStatePincode = "",
                OfficeLatitude = request.OfficeLatitude,
                OfficeLongitude = request.OfficeLongitude,
                GeoRadiusMeters = request.GeoRadiusMeters,
                WorkDayCutoffHour = request.WorkDayCutoffHour,
                LateGraceMinutes = 15,
                EndTimeGraceMinutes = 15
            };

            db.CompanySettings.Add(companySetting);
            await db.SaveChangesAsync();

            // Create FeatureSettings
            var maxFeatureId = await db.FeatureSettings.MaxAsync(f => (int?)f.Id) ?? 0;
            var features = request.InitialFeatures ?? new FeatureSettings();
            features.Id = maxFeatureId + 1;
            features.FirebasePlanMode = request.PlanMode;
            features.IsOfflineMode = isOffline;
            features.DeploymentMode = isOffline ? "Offline" : "Online";
            db.FeatureSettings.Add(features);
            await db.SaveChangesAsync();

            // Create CompanyTenant
            var tenant = new CompanyTenant
            {
                TenantId = tenantId,
                CompanyName = request.CompanyName.Trim(),
                CompanyCode = code,
                AdminUserId = adminUser.Id,
                AdminEmail = request.AdminEmail.Trim(),
                AdminName = string.IsNullOrWhiteSpace(request.AdminName) ? request.CompanyName.Trim() + " Admin" : request.AdminName.Trim(),
                AdminPhone = request.AdminPhone,
                IconEmoji = string.IsNullOrWhiteSpace(request.IconEmoji) ? "🏢" : request.IconEmoji.Trim(),
                PlanMode = request.PlanMode,
                IsOfflineMode = isOffline,
                DeploymentMode = isOffline ? "Offline" : "Online",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                CompanySettingId = companySetting.SettingID,
                FeatureSettingsId = features.Id
            };

            db.CompanyTenants.Add(tenant);
            await db.SaveChangesAsync();

            // Initialize Firebase tenant nodes (only if online)
            if (!isOffline)
            {
                try
                {
                    var tenantPayload = new Dictionary<string, object?>
                    {
                        ["tenantId"] = tenant.TenantId,
                        ["companyName"] = tenant.CompanyName,
                        ["companyCode"] = tenant.CompanyCode,
                        ["adminEmail"] = tenant.AdminEmail,
                        ["adminName"] = tenant.AdminName,
                        ["iconEmoji"] = tenant.IconEmoji,
                        ["planMode"] = tenant.PlanMode,
                        ["isOfflineMode"] = false,
                        ["deploymentMode"] = "Online",
                        ["isActive"] = tenant.IsActive,
                        ["createdAtUtc"] = tenant.CreatedAtUtc.ToString("O")
                    };

                    await _firebase.SetAsync($"tenants/{tenant.TenantId}", tenantPayload, default);

                    // Publish initial company setting to Firebase owner
                    var companyPayload = new Dictionary<string, object?>
                    {
                        ["companyName"] = tenant.CompanyName,
                        ["officeLatitude"] = request.OfficeLatitude,
                        ["officeLongitude"] = request.OfficeLongitude,
                        ["geoRadiusMeters"] = request.GeoRadiusMeters,
                        ["workDayCutoffHour"] = request.WorkDayCutoffHour
                    };
                    await _firebase.SetAsync($"owners/{tenant.TenantId}/company_settings/1", companyPayload, default);
                }
                catch (Exception fbEx)
                {
                    _logger.LogWarning(fbEx, "Firebase tenant metadata write deferred for {TenantId}", tenant.TenantId);
                }
            }

            await _refreshService.NotifyGlobalRefreshAsync("TENANTS_UPDATED");
            _logger.LogInformation("Successfully created tenant {TenantId} for {CompanyName}", tenant.TenantId, tenant.CompanyName);

            return new TenantOperationResult
            {
                Success = true,
                Tenant = tenant
            };
        }

        public async Task<TenantOperationResult> UpdateTenantProfileAndCredentialsAsync(
            CompanyTenant updated,
            string? newAdminEmail,
            string? newAdminPassword)
        {
            if (string.IsNullOrWhiteSpace(updated.CompanyName))
                return new TenantOperationResult { Success = false, ErrorMessage = "Company Name is required." };

            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.CompanyTenants.FirstOrDefaultAsync(t => t.Id == updated.Id);
            if (existing == null)
                return new TenantOperationResult { Success = false, ErrorMessage = "Company Tenant record not found." };

            string targetEmail = string.IsNullOrWhiteSpace(newAdminEmail) ? existing.AdminEmail.Trim() : newAdminEmail.Trim();

            // Validate email format
            if (!Regex.IsMatch(targetEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return new TenantOperationResult { Success = false, ErrorMessage = "Invalid Administrator Email format." };

            bool emailChanged = !string.Equals(existing.AdminEmail, targetEmail, StringComparison.OrdinalIgnoreCase);

            if (emailChanged)
            {
                // Check if target email belongs to another company
                var emailInUseByOtherTenant = await db.CompanyTenants.AnyAsync(t => t.Id != existing.Id && t.AdminEmail.ToLower() == targetEmail.ToLower());
                if (emailInUseByOtherTenant)
                    return new TenantOperationResult { Success = false, ErrorMessage = $"Email '{targetEmail}' is already assigned as administrator of another company." };
            }

            if (!string.IsNullOrWhiteSpace(newAdminPassword) && newAdminPassword.Length < 6)
                return new TenantOperationResult { Success = false, ErrorMessage = "Password must be at least 6 characters long." };

            // Find or create Identity admin user
            IdentityUser? adminUser = null;
            if (!string.IsNullOrWhiteSpace(existing.AdminUserId))
            {
                adminUser = await _userManager.FindByIdAsync(existing.AdminUserId);
            }
            if (adminUser == null)
            {
                adminUser = await _userManager.FindByEmailAsync(existing.AdminEmail) ?? await _userManager.FindByEmailAsync(targetEmail);
            }

            if (adminUser != null)
            {
                if (emailChanged)
                {
                    var setEmailRes = await _userManager.SetEmailAsync(adminUser, targetEmail);
                    var setUsernameRes = await _userManager.SetUserNameAsync(adminUser, targetEmail);
                    if (!setEmailRes.Succeeded || !setUsernameRes.Succeeded)
                    {
                        var err = string.Join("; ", setEmailRes.Errors.Concat(setUsernameRes.Errors).Select(e => e.Description));
                        _logger.LogWarning("Failed to update Identity email: {Err}", err);
                    }
                }

                if (!string.IsNullOrWhiteSpace(newAdminPassword))
                {
                    var token = await _userManager.GeneratePasswordResetTokenAsync(adminUser);
                    var resetRes = await _userManager.ResetPasswordAsync(adminUser, token, newAdminPassword);
                    if (!resetRes.Succeeded)
                    {
                        await _userManager.RemovePasswordAsync(adminUser);
                        var addRes = await _userManager.AddPasswordAsync(adminUser, newAdminPassword);
                        if (!addRes.Succeeded)
                        {
                            var err = string.Join("; ", addRes.Errors.Select(e => e.Description));
                            return new TenantOperationResult { Success = false, ErrorMessage = $"Could not update admin password: {err}" };
                        }
                    }
                }

                if (!await _roleManager.RoleExistsAsync("Admin"))
                {
                    await _roleManager.CreateAsync(new IdentityRole("Admin"));
                }

                if (!await _userManager.IsInRoleAsync(adminUser, "Admin") && !await _userManager.IsInRoleAsync(adminUser, "SuperAdmin"))
                {
                    await _userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }
            else
            {
                if (!await _roleManager.RoleExistsAsync("Admin"))
                {
                    await _roleManager.CreateAsync(new IdentityRole("Admin"));
                }

                adminUser = new IdentityUser
                {
                    UserName = targetEmail,
                    Email = targetEmail,
                    EmailConfirmed = true
                };

                var initialPassword = string.IsNullOrWhiteSpace(newAdminPassword) ? "Admin@123" : newAdminPassword;
                var createRes = await _userManager.CreateAsync(adminUser, initialPassword);
                if (!createRes.Succeeded)
                {
                    var err = string.Join("; ", createRes.Errors.Select(e => e.Description));
                    return new TenantOperationResult { Success = false, ErrorMessage = $"Failed to create admin user: {err}" };
                }
                await _userManager.AddToRoleAsync(adminUser, "Admin");
            }

            // Sync to Firebase Auth
            try
            {
                await _firebase.EnsureFirebaseUserAsync(
                    targetEmail,
                    string.IsNullOrWhiteSpace(newAdminPassword) ? "" : newAdminPassword,
                    "Admin",
                    displayName: string.IsNullOrWhiteSpace(updated.AdminName) ? updated.CompanyName.Trim() + " Admin" : updated.AdminName.Trim(),
                    updatePasswordIfExisting: !string.IsNullOrWhiteSpace(newAdminPassword));
            }
            catch (Exception fbAuthEx)
            {
                _logger.LogWarning(fbAuthEx, "Firebase Auth credential sync deferred for {Email}", targetEmail);
            }

            // Update Tenant entity
            existing.CompanyName = updated.CompanyName.Trim();
            existing.AdminUserId = adminUser.Id;
            existing.AdminEmail = targetEmail;
            existing.AdminName = string.IsNullOrWhiteSpace(updated.AdminName) ? existing.CompanyName + " Admin" : updated.AdminName.Trim();
            existing.AdminPhone = updated.AdminPhone;
            existing.IconEmoji = string.IsNullOrWhiteSpace(updated.IconEmoji) ? "🏢" : updated.IconEmoji.Trim();
            existing.PlanMode = updated.PlanMode;
            existing.IsActive = updated.IsActive;

            // Keep CompanySettings in sync
            var cs = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == existing.CompanySettingId);
            if (cs != null)
            {
                cs.CompanyName = existing.CompanyName;
            }

            await db.SaveChangesAsync();

            // Sync tenant metadata to Firebase Realtime Database
            try
            {
                var tenantPayload = new Dictionary<string, object?>
                {
                    ["companyName"] = existing.CompanyName,
                    ["adminEmail"] = existing.AdminEmail,
                    ["adminName"] = existing.AdminName,
                    ["adminPhone"] = existing.AdminPhone,
                    ["iconEmoji"] = existing.IconEmoji,
                    ["planMode"] = existing.PlanMode,
                    ["isActive"] = existing.IsActive
                };
                await _firebase.UpdateAsync(new Dictionary<string, object?>
                {
                    [$"tenants/{existing.TenantId}"] = tenantPayload,
                    [$"owners/{existing.TenantId}/company_settings/1/companyName"] = existing.CompanyName
                }, default);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Firebase tenant metadata update deferred.");
            }

            await _refreshService.NotifyGlobalRefreshAsync("TENANTS_UPDATED");
            return new TenantOperationResult { Success = true, Tenant = existing };
        }

        public async Task<bool> UpdateTenantAsync(CompanyTenant updated)
        {
            var res = await UpdateTenantProfileAndCredentialsAsync(updated, updated.AdminEmail, null);
            return res.Success;
        }

        public async Task<bool> SaveTenantFeaturesAsync(int tenantId, FeatureSettings newSettings)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return false;

            var existingFeatures = await db.FeatureSettings.FirstOrDefaultAsync(f => f.Id == tenant.FeatureSettingsId);
            if (existingFeatures != null)
            {
                db.Entry(existingFeatures).CurrentValues.SetValues(newSettings);
            }
            else
            {
                db.FeatureSettings.Add(newSettings);
                await db.SaveChangesAsync();
                tenant.FeatureSettingsId = newSettings.Id;
            }

            var isOffline = newSettings.IsOfflineMode || string.Equals(newSettings.DeploymentMode, "Offline", StringComparison.OrdinalIgnoreCase);
            tenant.IsOfflineMode = isOffline;
            tenant.DeploymentMode = isOffline ? "Offline" : "Online";

            await db.SaveChangesAsync();

            // Mirror feature settings to Firebase under tenant's owner node (only if online)
            if (!tenant.IsOfflineMode)
            {
                try
                {
                    await _firebase.SetAsync($"owners/{tenant.TenantId}/feature_settings/1", newSettings, default);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to mirror tenant feature settings to Firebase.");
                }
            }

            await _refreshService.NotifyGlobalRefreshAsync("FEATURE_TOGGLES_UPDATED");
            return true;
        }

        public async Task<List<CompanyTenant>> GetAllTenantsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.CompanyTenants
                .AsNoTracking()
                .OrderByDescending(t => t.IsActive)
                .ThenBy(t => t.CompanyName)
                .ToListAsync();
        }

        public async Task<FeatureSettings?> GetTenantFeaturesAsync(int tenantId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tenant = await db.CompanyTenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return null;

            return await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == tenant.FeatureSettingsId);
        }

        public async Task<CompanySetting?> GetTenantCompanySettingAsync(int tenantId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tenant = await db.CompanyTenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return null;

            return await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(c => c.SettingID == tenant.CompanySettingId);
        }

        public async Task<bool> UpdateTenantCompanySettingAsync(int tenantId, CompanySetting setting)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return false;

            var existing = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == tenant.CompanySettingId);
            if (existing != null)
            {
                existing.CompanyName = setting.CompanyName;
                existing.AddressLine1 = setting.AddressLine1;
                existing.CityStatePincode = setting.CityStatePincode;
                existing.WorkDayCutoffHour = setting.WorkDayCutoffHour;
                existing.LateGraceMinutes = setting.LateGraceMinutes;
                existing.EndTimeGraceMinutes = setting.EndTimeGraceMinutes;
                existing.OfficeLatitude = setting.OfficeLatitude;
                existing.OfficeLongitude = setting.OfficeLongitude;
                existing.GeoRadiusMeters = setting.GeoRadiusMeters;
                existing.ZktecoIP = setting.ZktecoIP;
                existing.ZktecoPort = setting.ZktecoPort;

                if (!string.IsNullOrWhiteSpace(setting.CompanyName))
                {
                    tenant.CompanyName = setting.CompanyName.Trim();
                }

                await db.SaveChangesAsync();

                // Mirror to Firebase
                try
                {
                    var companyPayload = new Dictionary<string, object?>
                    {
                        ["companyName"] = existing.CompanyName,
                        ["addressLine1"] = existing.AddressLine1,
                        ["cityStatePincode"] = existing.CityStatePincode,
                        ["officeLatitude"] = existing.OfficeLatitude,
                        ["officeLongitude"] = existing.OfficeLongitude,
                        ["geoRadiusMeters"] = existing.GeoRadiusMeters,
                        ["workDayCutoffHour"] = existing.WorkDayCutoffHour,
                        ["lateGraceMinutes"] = existing.LateGraceMinutes,
                        ["endTimeGraceMinutes"] = existing.EndTimeGraceMinutes
                    };
                    await _firebase.SetAsync($"owners/{tenant.TenantId}/company_settings/1", companyPayload, default);

                    await _firebase.UpdateAsync(new Dictionary<string, object?>
                    {
                        [$"tenants/{tenant.TenantId}/companyName"] = tenant.CompanyName
                    }, default);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to mirror tenant company setting to Firebase.");
                }

                await _refreshService.NotifyGlobalRefreshAsync("TENANTS_UPDATED");
                return true;
            }
            return false;
        }

        public async Task<bool> ToggleTenantActiveAsync(int tenantId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return false;

            tenant.IsActive = !tenant.IsActive;
            await db.SaveChangesAsync();

            try
            {
                await _firebase.UpdateAsync(new Dictionary<string, object?>
                {
                    [$"tenants/{tenant.TenantId}/isActive"] = tenant.IsActive
                }, default);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update tenant status in Firebase.");
            }

            await _refreshService.NotifyGlobalRefreshAsync("TENANTS_UPDATED");
            return true;
        }

        public const string AuthorizedSuperAdminDeleteEmail = "prakashshiva368@gmail.com";

        /// <summary>
        /// Permanently deletes an entire company, all employee records, attendance logs, GPS tracking,
        /// and operational records from BOTH SQLite Local Database and Firebase Realtime Cloud.
        /// STRICTLY RESTRICTED: Executable ONLY by SuperAdmin prakashshiva368@gmail.com.
        /// </summary>
        public async Task<TenantOperationResult> DeleteEntireCompanyAsync(
            string? tenantId,
            string currentUserEmail,
            CancellationToken ct = default)
        {
            // 1. Strict security check: Only SuperAdmin prakashshiva368@gmail.com can delete
            if (!string.Equals(currentUserEmail?.Trim(), AuthorizedSuperAdminDeleteEmail, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentUserEmail?.Trim(), FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Unauthorized attempt to delete company by {UserEmail}", currentUserEmail);
                return new TenantOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Unauthorized: Only SuperAdmin {AuthorizedSuperAdminDeleteEmail} is permitted to delete companies."
                };
            }

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);

                var targetTenantId = !string.IsNullOrWhiteSpace(tenantId)
                    ? tenantId.Trim()
                    : TenantContextService.DefaultTenantId;

                var tenant = await db.CompanyTenants.FirstOrDefaultAsync(
                    t => t.TenantId == targetTenantId || t.Id.ToString() == targetTenantId, ct);

                var effectiveTenantId = tenant?.TenantId ?? targetTenantId;
                var companyName = tenant?.CompanyName ?? "Company";

                _logger.LogInformation("SuperAdmin {UserEmail} initiated permanent deletion of company {CompanyName} ({TenantId})",
                    currentUserEmail, companyName, effectiveTenantId);

                // 2. Cloud Wipe: Permanently delete entire company tree from Firebase Realtime Database
                try
                {
                    await _firebase.DeletePathAsync($"owners/{effectiveTenantId}", ct);
                    await _firebase.DeletePathAsync($"owner_events/{effectiveTenantId}", ct);
                    await _firebase.DeletePathAsync($"shops/{effectiveTenantId}", ct);
                    await _firebase.DeletePathAsync($"tenants/{effectiveTenantId}", ct);
                    _logger.LogInformation("Firebase Realtime Cloud data purged for {TenantId}", effectiveTenantId);
                }
                catch (Exception fbEx)
                {
                    _logger.LogWarning(fbEx, "Firebase cloud deletion completed with warnings for {TenantId}", effectiveTenantId);
                }

                // 3. Local SQLite Wipe: Purge all operational and employee data tied to this tenant
                var employeeIds = await db.Employees
                    .Where(e => e.TenantId == effectiveTenantId || (effectiveTenantId == TenantContextService.DefaultTenantId && (e.TenantId == null || e.TenantId == "" || e.TenantId == TenantContextService.DefaultTenantId)))
                    .Select(e => e.EmployeeID)
                    .ToListAsync(ct);

                if (employeeIds.Count > 0)
                {
                    var idList = string.Join(",", employeeIds);
                    var deleteQueries = new[]
                    {
                        $"DELETE FROM \"AttendanceLogs\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"SalaryAdvances\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"PayrollHistories\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"LeaveRequests\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"ShiftSchedules\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"DailySummaries\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"employee_gps_sessions\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"employee_location_history\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"employee_device_locks\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"attendance_punches\" WHERE \"employeeid\" IN ({idList});",
                        $"DELETE FROM \"bonus_records\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"tax_declarations\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"resignation_requests\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"flexible_benefit_declarations\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"fnf_settlements\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"attendance_regularizations\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"geo_punch_audits\" WHERE \"employee_id\" IN ({idList});",
                        $"DELETE FROM \"Employees\" WHERE \"employeeid\" IN ({idList});"
                    };

                    foreach (var sql in deleteQueries)
                    {
                        try
                        {
                            await db.Database.ExecuteSqlRawAsync(sql, ct);
                        }
                        catch (Exception sqlEx)
                        {
                            _logger.LogDebug(sqlEx, "Wipe query ignored during company deletion: {Sql}", sql);
                        }
                    }
                }

                // Clean company-level tables (Holidays, Tax Slabs, Audit Logs)
                try
                {
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CompanyHolidays\";", ct);
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ProfessionalTaxSlabs\";", ct);
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLogs\";", ct);
                }
                catch { }

                // Clean CompanySettings & FeatureSettings
                if (tenant != null && tenant.CompanySettingId > 1)
                {
                    var cs = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == tenant.CompanySettingId, ct);
                    if (cs != null) db.CompanySettings.Remove(cs);
                }
                else
                {
                    // Reset setting 1 to pristine empty
                    var cs = await db.CompanySettings.FirstOrDefaultAsync(c => c.SettingID == 1, ct);
                    if (cs != null)
                    {
                        cs.CompanyName = "";
                        cs.AddressLine1 = "";
                        cs.CityStatePincode = "";
                        cs.OfficeLatitude = 0;
                        cs.OfficeLongitude = 0;
                        cs.GeoRadiusMeters = 100;
                    }
                }

                if (tenant != null && tenant.FeatureSettingsId > 1)
                {
                    var fs = await db.FeatureSettings.FirstOrDefaultAsync(f => f.Id == tenant.FeatureSettingsId, ct);
                    if (fs != null) db.FeatureSettings.Remove(fs);
                }
                else
                {
                    var fs = await db.FeatureSettings.FirstOrDefaultAsync(f => f.Id == 1, ct);
                    if (fs != null)
                    {
                        fs.IsOfflineMode = false;
                        fs.DeploymentMode = "Online";
                    }
                }

                // Remove CompanyTenant record
                if (tenant != null)
                {
                    db.CompanyTenants.Remove(tenant);
                }

                await db.SaveChangesAsync(ct);

                // 4. Delete non-SuperAdmin Identity accounts from AspNetUsers
                // NEVER delete the authorized SuperAdmin!
                try
                {
                    var allUsers = await _userManager.Users.ToListAsync(ct);
                    foreach (var user in allUsers)
                    {
                        var isSuperAdminUser = string.Equals(user.Email, AuthorizedSuperAdminDeleteEmail, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(user.UserName, AuthorizedSuperAdminDeleteEmail, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(user.Email, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(user.UserName, FirebaseAuthSecurityConstants.CanonicalSuperAdminEmail, StringComparison.OrdinalIgnoreCase);

                        if (!isSuperAdminUser)
                        {
                            await _userManager.DeleteAsync(user);
                        }
                    }
                }
                catch (Exception idEx)
                {
                    _logger.LogWarning(idEx, "Warning while purging employee identity users");
                }

                // 5. Invalidate global caches
                await _refreshService.NotifyGlobalRefreshAsync($"COMPANY_DELETED:{effectiveTenantId}");

                _logger.LogInformation("Company {CompanyName} ({TenantId}) deleted successfully from Cloud and Local DB.",
                    companyName, effectiveTenantId);

                return new TenantOperationResult
                {
                    Success = true,
                    ErrorMessage = null,
                    Tenant = null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete company {TenantId}", tenantId);
                return new TenantOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Error deleting company: {ex.Message}"
                };
            }
        }
    }
}

