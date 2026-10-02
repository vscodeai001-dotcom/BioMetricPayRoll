using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared.Data
{
    [Table("CompanyTenants", Schema = "public")]
    public class CompanyTenant
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(64)]
        [Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty; // Firebase ownerUid / tenant slug e.g. "biometricpayroll", "acme_corp"

        [Required]
        [MaxLength(150)]
        [Column("company_name")]
        public string CompanyName { get; set; } = string.Empty;

        [Required]
        [MaxLength(30)]
        [Column("company_code")]
        public string CompanyCode { get; set; } = string.Empty;

        [MaxLength(450)]
        [Column("admin_user_id")]
        public string? AdminUserId { get; set; } // Foreign key to AspNetUsers.Id

        [Required]
        [MaxLength(256)]
        [Column("admin_email")]
        public string AdminEmail { get; set; } = string.Empty;

        [MaxLength(150)]
        [Column("admin_name")]
        public string AdminName { get; set; } = string.Empty;

        [MaxLength(50)]
        [Column("admin_phone")]
        public string? AdminPhone { get; set; }

        [MaxLength(10)]
        [Column("icon_emoji")]
        public string IconEmoji { get; set; } = "🏢";

        [MaxLength(20)]
        [Column("plan_mode")]
        public string PlanMode { get; set; } = "Spark"; // "Spark" (80% bandwidth optimized) or "Blaze"

        [Column("is_offline_mode")]
        public bool IsOfflineMode { get; set; } = false; // true = 100% standalone local SQLite DB, false = Firebase Cloud sync

        [MaxLength(20)]
        [Column("deployment_mode")]
        public string DeploymentMode { get; set; } = "Online"; // "Online" or "Offline"

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("created_at_utc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [Column("company_setting_id")]
        public int CompanySettingId { get; set; } = 1;

        [Column("feature_settings_id")]
        public int FeatureSettingsId { get; set; } = 1;
    }
}
