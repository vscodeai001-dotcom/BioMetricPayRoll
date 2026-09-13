using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class AuditService
    {
        private readonly AppDbContext _dbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;


        public AuditService(AppDbContext dbContext, IHttpContextAccessor httpContextAccessor)
        {
            _dbContext = dbContext;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogAsync(string actionType, string entityType, string entityId, string details, string? actorUserId = null, string? actorEmail = null)
        {
            // 1. Check if Audit is enabled (Enforcing the feature gate)
            // Use the injected context to check settings.
            var settings = await _dbContext.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            if (settings == null || !settings.EnableAuditLog)
            {
                return; // Exit if disabled by feature toggle
            }

            var user = _httpContextAccessor.HttpContext?.User;
            string userId = actorUserId ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "SYSTEM";
            string userEmail = actorEmail ?? user?.Identity?.Name ?? "System";

            var log = new AuditLog
            {
                UserID = userId,
                UserEmail = userEmail,
                ActionType = actionType,
                EntityType = entityType,
                EntityID = entityId,
                Details = details
            };

            _dbContext.AuditLogs.Add(log);
            // Save the log immediately without blocking the main transaction
            await _dbContext.SaveChangesAsync();
        }
    }
}