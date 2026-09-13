using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payroll.Shared.Migrations
{
    public partial class AddGpsCaptureMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "capture_source",
                table: "employee_location_history",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Online");

            migrationBuilder.AddColumn<DateTime>(
                name: "captured_at_utc",
                table: "employee_location_history",
                type: "timestamp without time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<Guid>(
                name: "sync_batch_id",
                table: "employee_location_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "synced_at_utc",
                table: "employee_location_history",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_location_history_EmployeeId_CaptureSource_CapturedAtUtc",
                table: "employee_location_history",
                columns: new[] { "EmployeeId", "capture_source", "captured_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_location_history_sync_batch_id",
                table: "employee_location_history",
                column: "sync_batch_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex("IX_employee_location_history_EmployeeId_CaptureSource_CapturedAtUtc", "employee_location_history");
            migrationBuilder.DropIndex("IX_employee_location_history_sync_batch_id", "employee_location_history");
            migrationBuilder.DropColumn("capture_source", "employee_location_history");
            migrationBuilder.DropColumn("captured_at_utc", "employee_location_history");
            migrationBuilder.DropColumn("sync_batch_id", "employee_location_history");
            migrationBuilder.DropColumn("synced_at_utc", "employee_location_history");
        }
    }
}
