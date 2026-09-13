using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payroll.Shared.Migrations
{
    /// <summary>
    /// Phase 1 GPS analytics upgrade. Adds device/server speed, movement
    /// classification and session movement totals. Existing rows receive
    /// safe zero/Stopped defaults so this migration does not alter history.
    /// </summary>
    public partial class UpgradeGpsTrackingAnalytics : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "speed_mps",
                table: "employee_location_history",
                type: "double precision",
                nullable: false,
                defaultValue: 0d);

            migrationBuilder.AddColumn<string>(
                name: "movement_state",
                table: "employee_location_history",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Stopped");

            migrationBuilder.AddColumn<double>(
                name: "last_speed_mps",
                table: "employee_gps_sessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_movement_state",
                table: "employee_gps_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "total_moving_seconds",
                table: "employee_gps_sessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0d);

            migrationBuilder.AddColumn<double>(
                name: "total_stationary_seconds",
                table: "employee_gps_sessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0d);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "speed_mps", table: "employee_location_history");
            migrationBuilder.DropColumn(name: "movement_state", table: "employee_location_history");
            migrationBuilder.DropColumn(name: "last_speed_mps", table: "employee_gps_sessions");
            migrationBuilder.DropColumn(name: "last_movement_state", table: "employee_gps_sessions");
            migrationBuilder.DropColumn(name: "total_moving_seconds", table: "employee_gps_sessions");
            migrationBuilder.DropColumn(name: "total_stationary_seconds", table: "employee_gps_sessions");
        }
    }
}
