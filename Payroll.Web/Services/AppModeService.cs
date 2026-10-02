using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Payroll.Shared.Data;

namespace Payroll.Web.Services
{
    public interface IAppModeService
    {
        bool IsOfflineMode { get; }
        bool IsMultiTenantEnabled { get; }
        Task<bool> IsOfflineModeAsync();
        Task<string> GetModeDescriptionAsync();
        Task SetOfflineModeAsync(bool offline);
        event Action? OnAppModeChanged;
    }

    public class AppModeService : IAppModeService
    {
        private readonly IConfiguration _configuration;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger<AppModeService> _logger;

        private bool? _cachedIsOffline;
        public event Action? OnAppModeChanged;

        public AppModeService(
            IConfiguration configuration,
            IDbContextFactory<AppDbContext> dbFactory,
            ILogger<AppModeService> logger)
        {
            _configuration = configuration;
            _dbFactory = dbFactory;
            _logger = logger;

            var configMode = _configuration["ApplicationMode"] ?? _configuration["OperationMode"];
            if (!string.IsNullOrWhiteSpace(configMode) && string.Equals(configMode, "Offline", StringComparison.OrdinalIgnoreCase))
            {
                _cachedIsOffline = true;
            }
        }

        public bool IsOfflineMode => _cachedIsOffline ?? false;

        public bool IsMultiTenantEnabled => !IsOfflineMode;

        public async Task<bool> IsOfflineModeAsync()
        {
            if (_cachedIsOffline.HasValue)
                return _cachedIsOffline.Value;

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == 1);
                if (features != null)
                {
                    _cachedIsOffline = features.IsOfflineMode ||
                        string.Equals(features.DeploymentMode, "Offline", StringComparison.OrdinalIgnoreCase);
                    return _cachedIsOffline.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load offline mode from database; defaulting to config or Online.");
            }

            var configMode = _configuration["ApplicationMode"] ?? _configuration["OperationMode"];
            _cachedIsOffline = string.Equals(configMode, "Offline", StringComparison.OrdinalIgnoreCase);
            return _cachedIsOffline.Value;
        }

        public async Task<string> GetModeDescriptionAsync()
        {
            var offline = await IsOfflineModeAsync();
            return offline
                ? "📴 Offline Standalone (Built-in Local Database)"
                : "🌐 Online Cloud (Multi-Tenant + Firebase)";
        }

        public async Task SetOfflineModeAsync(bool offline)
        {
            _cachedIsOffline = offline;

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var features = await db.FeatureSettings.FirstOrDefaultAsync(f => f.Id == 1);
                if (features != null)
                {
                    features.IsOfflineMode = offline;
                    features.DeploymentMode = offline ? "Offline" : "Online";
                }

                var tenant = await db.CompanyTenants.FirstOrDefaultAsync(t => t.TenantId == TenantContextService.DefaultTenantId);
                if (tenant != null)
                {
                    tenant.IsOfflineMode = offline;
                    tenant.DeploymentMode = offline ? "Offline" : "Online";
                }

                await db.SaveChangesAsync();
                _logger.LogInformation("System application mode switched to: {Mode}", offline ? "Offline (Standalone Local DB)" : "Online (Multi-Tenant Cloud)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist offline mode to database.");
            }

            OnAppModeChanged?.Invoke();
        }
    }
}
