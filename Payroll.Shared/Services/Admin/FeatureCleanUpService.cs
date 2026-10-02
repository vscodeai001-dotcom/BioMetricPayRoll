using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using System.Threading.Tasks;

namespace Payroll.Shared.Services
{
    public class FeatureCleanUpService
    {
        private readonly AppDbContext _dbContext;

        public FeatureCleanUpService(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // --- THIS IS THE NEW CONSOLIDATED METHOD ---
        public async Task ResetAllTogglesAsync()
        {
            // Find the existing settings (Id = 1)
            var entity = await _dbContext.FeatureSettings.FindAsync(1);
            if (entity != null)
            {
                _dbContext.FeatureSettings.Remove(entity);
                await _dbContext.SaveChangesAsync();
            }

            // Add a new default record
            await InsertDefaultSettingsAsync();
        }

        // --- Helper Method to Re-Insert Defaults ---
        private async Task InsertDefaultSettingsAsync()
        {
            if (!await _dbContext.FeatureSettings.AnyAsync(t => t.Id == 1))
            {
                _dbContext.FeatureSettings.Add(new FeatureSettings());
                await _dbContext.SaveChangesAsync();
            }
        }

        // --- WIPE ALL TRANSACTIONAL DATA ---
        // Dynamically discovers all existing transactional tables from sqlite_master
        // or information_schema, disables foreign key constraints during bulk delete,
        // and clears sqlite_sequence.
        public async Task WipeAllTransactionalDataAsync()
        {
            var isSqlite = _dbContext.Database.IsSqlite();

            if (isSqlite)
            {
                await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            }

            try
            {
                var ignoredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "__EFMigrationsHistory",
                    "feature_settings",
                    "CompanySettings",
                    "holidays",
                    "professional_tax_slabs",
                    "AspNetRoles",
                    "AspNetRoleClaims",
                    "AspNetUsers",
                    "AspNetUserRoles",
                    "UserThemePreferences"
                };

                var connection = _dbContext.Database.GetDbConnection();
                var wasOpen = connection.State == System.Data.ConnectionState.Open;
                if (!wasOpen)
                {
                    await connection.OpenAsync();
                }

                var existingTables = new List<string>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = isSqlite
                        ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"
                        : "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';";

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var tableName = reader.GetString(0);
                        if (!ignoredTables.Contains(tableName))
                        {
                            existingTables.Add(tableName);
                        }
                    }
                }

                foreach (var table in existingTables)
                {
                    var deleteSql = "DELETE FROM \"" + table.Replace("\"", "") + "\";";
                    await _dbContext.Database.ExecuteSqlRawAsync(deleteSql);
                }

                if (isSqlite && existingTables.Count > 0)
                {
                    var tableList = string.Join(",", existingTables.Select(t => "'" + t.Replace("'", "''") + "'"));
                    var seqSql = "DELETE FROM sqlite_sequence WHERE name IN (" + tableList + ");";
                    await _dbContext.Database.ExecuteSqlRawAsync(seqSql);
                }
            }
            finally
            {
                if (isSqlite)
                {
                    await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
                }
            }
        }

        // --- WIPE OPERATIONAL DATA ONLY (PARTIAL WIPE) ---
        // Preserves all Admin Settings screens:
        // Employee Records, Holiday Management, Company Settings, User & Role Mgmt, Feature Toggles, Tax Slabs, Shops.
        // Wipes attendance logs, punches, GPS tracking, payroll, leaves, advances, and audit logs.
        public async Task WipeOperationalDataOnlyAsync()
        {
            var isSqlite = _dbContext.Database.IsSqlite();

            if (isSqlite)
            {
                await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            }

            try
            {
                var ignoredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "__EFMigrationsHistory",
                    "feature_settings",
                    "CompanySettings",
                    "holidays",
                    "professional_tax_slabs",
                    "AspNetRoles",
                    "AspNetRoleClaims",
                    "AspNetUsers",
                    "AspNetUserRoles",
                    "AspNetUserClaims",
                    "AspNetUserLogins",
                    "AspNetUserTokens",
                    "Employees",
                    "Shops",
                    "UserThemePreferences"
                };

                var connection = _dbContext.Database.GetDbConnection();
                var wasOpen = connection.State == System.Data.ConnectionState.Open;
                if (!wasOpen)
                {
                    await connection.OpenAsync();
                }

                var existingTables = new List<string>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = isSqlite
                        ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"
                        : "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';";

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var tableName = reader.GetString(0);
                        if (!ignoredTables.Contains(tableName))
                        {
                            existingTables.Add(tableName);
                        }
                    }
                }

                foreach (var table in existingTables)
                {
                    var deleteSql = "DELETE FROM \"" + table.Replace("\"", "") + "\";";
                    await _dbContext.Database.ExecuteSqlRawAsync(deleteSql);
                }

                if (isSqlite && existingTables.Count > 0)
                {
                    var tableList = string.Join(",", existingTables.Select(t => "'" + t.Replace("'", "''") + "'"));
                    var seqSql = "DELETE FROM sqlite_sequence WHERE name IN (" + tableList + ");";
                    await _dbContext.Database.ExecuteSqlRawAsync(seqSql);
                }
            }
            finally
            {
                if (isSqlite)
                {
                    await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
                }
            }
        }


        // --- OLD METHODS (NOW DELETED) ---
        // public async Task ResetClientTogglesAsync()...
        // public async Task ResetAdminTogglesAsync()...
        // public async Task ResetEmployeeTogglesAsync()...
    }
}