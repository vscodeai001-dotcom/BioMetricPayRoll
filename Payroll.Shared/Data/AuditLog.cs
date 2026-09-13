using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared.Data
{
    [Table("audit_logs")]
    public class AuditLog
    {
        [Key]
        [Column("logid")]
        public long LogID { get; set; }

        [Required]
        [Column("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Required]
        [Column("user_id")]
        public string UserID { get; set; } = "SYSTEM";

        [Required]
        [Column("user_email")]
        public string UserEmail { get; set; } = "System";

        [Required]
        [StringLength(50)]
        [Column("action_type")] // CREATE, UPDATE, DELETE, LOGIN
        public string ActionType { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        [Column("entity_type")] // Employee, SalaryAdvance, CompanySetting
        public string EntityType { get; set; } = string.Empty;

        [Column("entity_id")]
        public string? EntityID { get; set; } // ID of the record changed

        [Column("details")]
        public string? Details { get; set; } // JSON or text description of change
    }
}