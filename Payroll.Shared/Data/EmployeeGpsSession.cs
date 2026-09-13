using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared.Data;

[Table("employee_gps_sessions")]
public class EmployeeGpsSession
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("employee_id")]
    public int EmployeeId { get; set; }

    [Column("session_id")]
    public Guid SessionId { get; set; }

    [Column("started_at_utc")]
    public DateTime StartedAtUtc { get; set; }

    [Column("last_update_at_utc")]
    public DateTime LastUpdateAtUtc { get; set; }

    [Column("ended_at_utc")]
    public DateTime? EndedAtUtc { get; set; }

    [Column("end_reason")]
    [MaxLength(40)]
    public string? EndReason { get; set; }

    [Column("last_latitude")]
    public double? LastLatitude { get; set; }

    [Column("last_longitude")]
    public double? LastLongitude { get; set; }

    [Column("last_accuracy_meters")]
    public double? LastAccuracyMeters { get; set; }

    [Column("last_distance_from_office_meters")]
    public double? LastDistanceFromOfficeMeters { get; set; }

    [Column("last_allowed_radius_meters")]
    public int? LastAllowedRadiusMeters { get; set; }

    [Column("last_is_within_allowed_radius")]
    public bool? LastIsWithinAllowedRadius { get; set; }

    [Column("total_points")]
    public int TotalPoints { get; set; }

    [Column("total_distance_meters")]
    public double TotalDistanceMeters { get; set; }

    [Column("average_accuracy_meters")]
    public double? AverageAccuracyMeters { get; set; }
}