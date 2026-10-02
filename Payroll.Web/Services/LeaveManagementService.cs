using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class LeaveManagementService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly AuditService _auditService;
        private readonly IEmailSender _emailSender;
        private readonly ILogger<LeaveManagementService> _logger;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly AttendanceCalculatorService _attendanceCalculator;
        private readonly AttendanceRefreshService _refreshService;
        private readonly NotificationService _notificationService;
        private readonly FirebaseAttendanceCalendarMutationService _firebaseCalendar;

        public LeaveManagementService(
            IDbContextFactory<AppDbContext> dbFactory,
            AuditService auditService,
            IEmailSender emailSender,
            ILogger<LeaveManagementService> logger,
            UserManager<IdentityUser> userManager,
            AttendanceCalculatorService attendanceCalculator,
            AttendanceRefreshService refreshService,
            NotificationService notificationService,
            FirebaseAttendanceCalendarMutationService firebaseCalendar)
        {
            _dbFactory = dbFactory;
            _auditService = auditService;
            _emailSender = emailSender;
            _logger = logger;
            _userManager = userManager;
            _attendanceCalculator = attendanceCalculator;
            _refreshService = refreshService;
            _notificationService = notificationService;
            _firebaseCalendar = firebaseCalendar;
        }

        // --- 1. LOAD DATA ---
        public async Task<List<LeaveRequest>> LoadLeaveRequestsAsync(
            int selectedEmployeeId, string filterStatus, DateTime? startDate, DateTime? endDate)
        {
            await using var dbContext = await _dbFactory.CreateDbContextAsync();
            var query = dbContext.LeaveRequests.Where(lr => lr.EmployeeID > 0).AsQueryable();

            if (selectedEmployeeId > 0)
                query = query.Where(lr => lr.EmployeeID == selectedEmployeeId);

            if (startDate.HasValue)
                query = query.Where(lr => lr.LeaveDate >= startDate.Value.Date);

            if (endDate.HasValue)
                query = query.Where(lr => lr.LeaveDate < endDate.Value.Date.AddDays(1));

            if (filterStatus == "Pending")
                query = query.Where(lr => (!lr.IsApproved || lr.Status == "Pending") && lr.Status != "Rejected" && lr.LeaveType != "Loss of Pay (Auto)");
            else if (filterStatus == "Approved")
                query = query.Where(lr => lr.IsApproved || lr.Status == "Approved");
            else if (filterStatus == "Rejected")
                query = query.Where(lr => lr.Status == "Rejected");

            var list = await query
                .OrderByDescending(lr => lr.LeaveDate)
                .ThenBy(lr => lr.EmployeeID)
                .ToListAsync();

            return list
                .GroupBy(lr => !string.IsNullOrEmpty(lr.FirebaseLeaveId)
                    ? lr.FirebaseLeaveId
                    : lr.LeaveRequestID > 0
                        ? lr.LeaveRequestID.ToString()
                        : $"{lr.EmployeeID}_{lr.LeaveDate:yyyyMMdd}_{lr.LeaveType}")
                .Select(g => g.First())
                .ToList();
        }

        // --- 2. SAVE NEW REQUEST (Admin Entry) ---
        public async Task SaveNewLeaveRequestAsync(LeaveRequest newRequest)
        {
            await using var dbContext = await _dbFactory.CreateDbContextAsync();

            var requestedDate = newRequest.LeaveDate?.Date;
            if (!requestedDate.HasValue)
                throw new InvalidOperationException("Leave date is required.");

            bool exists = await dbContext.LeaveRequests.AnyAsync(l =>
                l.EmployeeID == newRequest.EmployeeID &&
                l.LeaveDate.HasValue &&
                l.LeaveDate.Value.Date == requestedDate.Value);

            if (exists) throw new InvalidOperationException("Request already exists for this date.");

            // Admin entry is auto-approved, exactly as in the existing flow.
            newRequest.IsApproved = true;
            newRequest.Status = "Approved";
            newRequest.LeaveDate = requestedDate.Value;

            dbContext.LeaveRequests.Add(newRequest);
            await dbContext.SaveChangesAsync();

            var emp = await dbContext.Employees.FindAsync(newRequest.EmployeeID);
            await _firebaseCalendar.UpsertLeaveAsync(newRequest, emp?.Name);

            // IMPORTANT: Keep DailySummary synchronized immediately.
            // This makes an approved admin leave appear in Attendance Log Summary
            // without requiring a manual punch or waiting for the nightly job.
            await RefreshDailySummaryAsync(dbContext, newRequest.EmployeeID, requestedDate.Value);

            await _auditService.LogAsync(
                "CREATE",
                "LeaveRequest",
                newRequest.LeaveRequestID.ToString(),
                $"Admin added approved leave ({newRequest.LeaveType}) for EmpID: {newRequest.EmployeeID}");

            // ================================================================
            // BROADCAST REAL-TIME LEAVE UPDATE TO ALL CONNECTED CLIENTS
            // ================================================================
            try
            {
                await _refreshService.NotifyLeaveChangedAsync(
                    newRequest.EmployeeID,
                    newRequest.LeaveDate,
                    "CREATED");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to broadcast leave creation for EmployeeId={EmployeeId}",
                    newRequest.EmployeeID);
            }
        }

        // --- 3. UPDATE STATUS (Approve/Revoke) ---
        public async Task UpdateLeaveStatusAsync(int requestId, bool approved, string? explicitStatus = null)
        {
            await using var dbContext = await _dbFactory.CreateDbContextAsync();
            var dbReq = await dbContext.LeaveRequests.FindAsync(requestId);
            if (dbReq == null) throw new KeyNotFoundException("Leave request not found.");

            var emp = await dbContext.Employees.FindAsync(dbReq.EmployeeID);

            // Logic preserved: deduct balance only when changing Unapproved -> Approved.
            if (approved && !dbReq.IsApproved && emp != null)
            {
                decimal daysToDeduct = dbReq.IsHalfDay ? 0.5m : 1.0m;

                if (dbReq.LeaveType == "Paid Leave")
                    emp.PaidLeaveBalance = Math.Max(0, emp.PaidLeaveBalance - daysToDeduct);
                else if (dbReq.LeaveType == "Sick Leave")
                    emp.SickLeaveBalance = Math.Max(0, emp.SickLeaveBalance - daysToDeduct);

                dbContext.Employees.Update(emp);
            }

            dbReq.IsApproved = approved;
            dbReq.Status = !string.IsNullOrWhiteSpace(explicitStatus)
                ? explicitStatus
                : (approved ? "Approved" : "Pending");

            await dbContext.SaveChangesAsync();

            await _firebaseCalendar.UpsertLeaveAsync(dbReq, emp?.Name);

            // IMPORTANT: Recalculate the affected attendance day immediately.
            // Approval -> Leave status in DailySummary.
            // Revocation -> normal attendance calculation again.
            if (dbReq.LeaveDate.HasValue)
            {
                await RefreshDailySummaryAsync(
                    dbContext,
                    dbReq.EmployeeID,
                    dbReq.LeaveDate.Value.Date);
            }

            await _auditService.LogAsync(
                "UPDATE",
                "LeaveRequest",
                dbReq.LeaveRequestID.ToString(),
                $"Leave status set to {(approved ? "Approved" : "Pending")} for EmpID: {dbReq.EmployeeID}");

            if (emp != null)
                await SendStatusEmailAsync(dbReq, emp, approved ? "Approved" : "Pending (Revoked)");

            await _notificationService.NotifyEmployeeAsync(
                dbReq.EmployeeID,
                approved ? "Leave Approved" : "Leave Rejected / Revoked",
                approved
                    ? $"Your leave request for {dbReq.LeaveDate:dd-MMM-yyyy} was approved."
                    : $"Your leave request for {dbReq.LeaveDate:dd-MMM-yyyy} was rejected or revoked.",
                "/my-leave-history");

            // ================================================================
            // BROADCAST REAL-TIME LEAVE STATUS UPDATE TO ALL CONNECTED CLIENTS
            // ================================================================
            try
            {
                await _refreshService.NotifyLeaveChangedAsync(
                    dbReq.EmployeeID,
                    dbReq.LeaveDate,
                    approved ? "APPROVED" : "REVOKED");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to broadcast leave status update for EmployeeId={EmployeeId}",
                    dbReq.EmployeeID);
            }
        }

        // --- 4. DELETE REQUEST ---
        public async Task DeleteLeaveRequestAsync(int requestId)
        {
            await using var dbContext = await _dbFactory.CreateDbContextAsync();
            var req = await dbContext.LeaveRequests.FindAsync(requestId);
            if (req == null) return;

            var emp = await dbContext.Employees.FindAsync(req.EmployeeID);
            var affectedDate = req.LeaveDate?.Date;
            var employeeId = req.EmployeeID;

            var firebaseLeaveId = req.FirebaseLeaveId;

            var reqCopy = new LeaveRequest
            {
                EmployeeID = req.EmployeeID,
                LeaveDate = req.LeaveDate,
                LeaveType = req.LeaveType,
                IsHalfDay = req.IsHalfDay,
                IsApproved = req.IsApproved,
                FirebaseLeaveId = req.FirebaseLeaveId
            };

            dbContext.LeaveRequests.Remove(req);
            await dbContext.SaveChangesAsync();

            await _firebaseCalendar.DeleteLeaveAsync(requestId, firebaseLeaveId);

            // Recalculate after deletion so DailySummary does not keep stale leave status.
            if (affectedDate.HasValue)
            {
                await RefreshDailySummaryAsync(dbContext, reqCopy.EmployeeID, affectedDate.Value);
            }

            await _auditService.LogAsync(
                "DELETE",
                "LeaveRequest",
                requestId.ToString(),
                $"Leave ({reqCopy.LeaveType}) deleted for EmpID: {req.EmployeeID}");

            if (emp != null)
                await SendStatusEmailAsync(reqCopy, emp, "Denied/Deleted");

            await _notificationService.NotifyEmployeeAsync(
                employeeId,
                "Leave Request Denied",
                $"Your leave request for {affectedDate:dd-MMM-yyyy} was denied/deleted by the administrator.",
                "/my-leave-history");

            // ================================================================
            // BROADCAST REAL-TIME LEAVE DELETION TO ALL CONNECTED CLIENTS
            // ================================================================
            try
            {
                await _refreshService.NotifyLeaveChangedAsync(
                    employeeId,
                    affectedDate,
                    "DELETED");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to broadcast leave deletion for EmployeeId={EmployeeId}",
                    employeeId);
            }
        }

        // --- 5. DAILY ATTENDANCE SYNCHRONIZATION ---
        // This does NOT change the attendance calculation rules.
        // It simply runs the existing AttendanceCalculatorService against the
        // affected employee/day and stores the resulting DailySummary.
        private async Task RefreshDailySummaryAsync(
            AppDbContext dbContext,
            int employeeId,
            DateTime day)
        {
            var date = day.Date;
            var dateOnly = DateOnly.FromDateTime(date);
            var nextDate = date.AddDays(1);

            var emp = await dbContext.Employees
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EmployeeID == employeeId);

            if (emp == null)
            {
                _logger.LogWarning(
                    "Cannot refresh DailySummary. Employee {EmployeeId} was not found.",
                    employeeId);
                return;
            }

            var settings = await dbContext.CompanySettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingID == 1)
                ?? new CompanySetting();

            var featureSettings = await dbContext.FeatureSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == 1)
                ?? new FeatureSettings();

            var holidays = await dbContext.CompanyHolidays
                .AsNoTracking()
                .ToListAsync();

            var punches = await dbContext.AttendanceLogs
                .AsNoTracking()
                .Where(l =>
                    l.EmployeeID == employeeId &&
                    l.PunchTime >= date &&
                    l.PunchTime < nextDate)
                .OrderBy(l => l.PunchTime)
                .ToListAsync();

            var schedule = await dbContext.ShiftSchedules
                .AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.EmployeeID == employeeId &&
                    s.ShiftDate == dateOnly);

            // Only APPROVED leave participates in attendance calculation.
            // If multiple records somehow exist for the same date, prefer a real
            // approved leave over an automatically-created LOP record.
            var leave = await dbContext.LeaveRequests
                .AsNoTracking()
                .Where(l =>
                    l.EmployeeID == employeeId &&
                    l.IsApproved &&
                    l.LeaveDate.HasValue &&
                    l.LeaveDate.Value >= date &&
                    l.LeaveDate.Value < nextDate)
                .OrderBy(l => l.LeaveType == "Loss of Pay (Auto)" ? 1 : 0)
                .ThenByDescending(l => l.LeaveRequestID)
                .FirstOrDefaultAsync();

            var result = _attendanceCalculator.CalculateDailyResult(
                emp,
                date,
                punches,
                leave,
                schedule,
                settings,
                holidays,
                featureSettings);

            var summary = await dbContext.DailySummaries
                .FirstOrDefaultAsync(s =>
                    s.EmployeeID == employeeId &&
                    s.ShiftDate == dateOnly);

            if (summary == null)
            {
                summary = new DailySummary
                {
                    EmployeeID = employeeId,
                    ShiftDate = dateOnly
                };
                dbContext.DailySummaries.Add(summary);
            }

            summary.Status = result.Status;
            summary.EarnedStandardHours = result.EarnedStandardDuration.TotalHours < 0
                ? 0
                : (decimal)result.EarnedStandardDuration.TotalHours;
            summary.TotalOvertimeDuration = result.TotalOvertimeDuration;
            summary.TotalPenaltyDuration = result.TotalPenalty;
            summary.TotalLateness = result.TotalLateness;
            summary.TotalBreakPenalty = result.TotalBreakPenalty;
            summary.ScheduledShiftDuration = result.ScheduledShiftDuration;
            summary.ShiftAllowanceEarned = result.ShiftAllowanceEarned;

            // This result is now generated from the approved leave + attendance data,
            // not from a manual attendance override.
            summary.IsManualOverride = false;

            await dbContext.SaveChangesAsync();

            await _firebaseCalendar.UpsertDailySummaryAsync(summary);

            _logger.LogInformation(
                "DailySummary synchronized for EmpID {EmployeeId} on {Date}. Status={Status}, Leave={LeaveType}",
                employeeId,
                dateOnly,
                result.Status,
                leave?.LeaveType ?? "None");
        }

        // --- INTERNAL EMAIL HELPER ---
        private async Task SendStatusEmailAsync(LeaveRequest req, Employee emp, string status)
        {
            if (string.IsNullOrEmpty(emp.Email)) return;

            try
            {
                string subject = $"Leave Request Update: {status}";
                string body = $@"
                    <h3>Leave Request Update</h3>
                    <p>Hello {emp.Name},</p>
                    <p>Your leave request details:</p>
                    <ul>
                        <li><strong>Date:</strong> {req.LeaveDate:dd-MMM-yyyy}</li>
                        <li><strong>Type:</strong> {req.LeaveType}</li>
                        <li><strong>Status:</strong> {status}</li>
                    </ul>
                    <p>Please log in to the portal for more details.</p>";

                await _emailSender.SendEmailAsync(emp.Email, subject, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email notification.");
            }
        }
    }
}