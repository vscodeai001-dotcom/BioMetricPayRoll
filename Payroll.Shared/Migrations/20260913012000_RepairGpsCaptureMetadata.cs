using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payroll.Shared.Migrations
{
    /// <summary>
    /// Repairs the GPS capture metadata columns for deployments where the
    /// original AddGpsCaptureMetadata migration was recorded as applied but
    /// the physical PostgreSQL columns/indexes are missing.
    ///
    /// All statements are idempotent so this migration is safe against a
    /// partially-applied deployment.
    /// </summary>
    public partial class RepairGpsCaptureMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "employee_location_history"
                ADD COLUMN IF NOT EXISTS "capture_source"
                    character varying(20) NOT NULL DEFAULT 'Online';

                ALTER TABLE "employee_location_history"
                ADD COLUMN IF NOT EXISTS "captured_at_utc"
                    timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP;

                ALTER TABLE "employee_location_history"
                ADD COLUMN IF NOT EXISTS "sync_batch_id"
                    uuid NULL;

                ALTER TABLE "employee_location_history"
                ADD COLUMN IF NOT EXISTS "synced_at_utc"
                    timestamp without time zone NULL;

                CREATE INDEX IF NOT EXISTS "IX_employee_location_history_EmployeeId_CaptureSource_CapturedAtUtc"
                ON "employee_location_history" ("EmployeeId", "capture_source", "captured_at_utc");

                CREATE INDEX IF NOT EXISTS "IX_employee_location_history_sync_batch_id"
                ON "employee_location_history" ("sync_batch_id");
            """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_employee_location_history_EmployeeId_CaptureSource_CapturedAtUtc";
                DROP INDEX IF EXISTS "IX_employee_location_history_sync_batch_id";
            """);
        }
    }
}
