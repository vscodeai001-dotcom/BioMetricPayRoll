using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services
{
    public class ResignationService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger<ResignationService> _logger;
        private readonly IEmailSender _emailSender;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly NotificationService _noteService;
        private readonly FirebaseRealtimeService _firebase;

        public ResignationService(
            IDbContextFactory<AppDbContext> dbFactory,
            ILogger<ResignationService> logger,
            IEmailSender emailSender,
            UserManager<IdentityUser> userManager,
            NotificationService noteService,
            FirebaseRealtimeService firebase
            )
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _emailSender = emailSender;
            _userManager = userManager;
            _noteService = noteService;
            _firebase = firebase;
        }

        private async Task UpsertFirebaseResignationAsync(ResignationRequest req)
        {
            try
            {
                var ownerUid = _firebase.ResolveOwnerUid("resignation-service", "Admin");
                var key = req.RequestId.ToString(CultureInfo.InvariantCulture);
                var row = new Dictionary<string, object?>
                {
                    ["requestId"] = req.RequestId,
                    ["id"] = req.RequestId,
                    ["employeeId"] = req.EmployeeId,
                    ["staffId"] = req.EmployeeId.ToString(CultureInfo.InvariantCulture),
                    ["submissionDate"] = new DateTimeOffset(req.SubmissionDate).ToUnixTimeMilliseconds(),
                    ["desiredLastWorkingDay"] = new DateTimeOffset(req.DesiredLastWorkingDay.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds(),
                    ["reason"] = req.Reason,
                    ["status"] = req.Status,
                    ["approvedLastWorkingDay"] = req.ApprovedLastWorkingDay.HasValue
                        ? (object)new DateTimeOffset(req.ApprovedLastWorkingDay.Value.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds()
                        : null,
                    ["adminRemarks"] = req.AdminRemarks,
                    ["isSettled"] = req.IsSettled,
                    ["_entity"] = "ResignationRequest",
                    ["_key"] = key,
                    ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                await _firebase.SetOwnerRecordAsync(ownerUid, "resignation_requests", key, row);
                await _firebase.PublishLocalApplicationChangeAsync(ownerUid, "ResignationRequest", "MODIFIED", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to project resignation request {RequestId} to Firebase.", req.RequestId);
            }
        }

        // --- 1. SUBMIT REQUEST (Employee) ---
        public async Task<bool> SubmitResignationAsync(int employeeId, DateOnly lastDay, string reason)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Gated Check
            var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == 1);
            if (features?.EnableResignationModule != true) return false;

            // Prevent duplicates ONLY if there is a Pending or Approved request
            bool pending = await db.ResignationRequests
                .AnyAsync(r => r.EmployeeId == employeeId && (r.Status == "Pending" || r.Status == "Approved"));

            if (pending) throw new InvalidOperationException("An active resignation request already exists.");

            var req = new ResignationRequest
            {
                EmployeeId = employeeId,
                DesiredLastWorkingDay = lastDay,
                Reason = reason,
                Status = "Pending",
                SubmissionDate = DateTime.Now
            };

            db.ResignationRequests.Add(req);
            await db.SaveChangesAsync();
            await UpsertFirebaseResignationAsync(req);

            // --- NOTIFY ADMINS ---
            var emp = await db.Employees.FindAsync(employeeId);
            if (emp != null)
            {
                await NotifyAdminsAsync($"Resignation: {emp.Name}",
                    $"<p>Employee <strong>{emp.Name}</strong> has submitted a resignation request.</p>" +
                    $"<p>Requested Last Day: {lastDay:dd-MMM-yyyy}</p>" +
                    $"<p>Reason: {reason}</p>" +
                    $"<p><a href='#'>Login to Admin Portal to review.</a></p>");

                await _noteService.NotifyAdminsAsync(
                    "New Resignation Request",
                    $"Resignation submitted by {emp.Name} for {lastDay:dd-MMM}.",
                    "/admin/exit-management");
            }

            return true;
        }

        // --- 2. APPROVE / REJECT (Admin) ---
        public async Task UpdateStatusAsync(int requestId, string status, DateOnly? approvedLastDay, string remarks)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var req = await db.ResignationRequests.FindAsync(requestId);
            if (req == null) return;

            req.Status = status;
            req.AdminRemarks = remarks;

            if (status == "Approved" && approvedLastDay.HasValue)
            {
                req.ApprovedLastWorkingDay = approvedLastDay.Value;
            }

            await db.SaveChangesAsync();
            await UpsertFirebaseResignationAsync(req);

            // --- NOTIFY EMPLOYEE ---
            var emp = await db.Employees.FindAsync(req.EmployeeId);
            if (emp != null && !string.IsNullOrEmpty(emp.Email))
            {
                string subject = $"Resignation Update: {status}";
                string body = $"<p>Your resignation request has been <strong>{status}</strong>.</p>" +
                              $"<p><strong>Remarks:</strong> {remarks}</p>";

                if (status == "Approved" && approvedLastDay.HasValue)
                {
                    body += $"<p><strong>Approved Last Working Day:</strong> {approvedLastDay:dd-MMM-yyyy}</p>";
                }

                await _emailSender.SendEmailAsync(emp.Email, subject, body);

                var user = await _userManager.FindByEmailAsync(emp.Email);
                if (user != null)
                {
                    string statusMsg = (status == "Approved") ? "Approved" : "Rejected";
                    await _noteService.SendNotificationAsync(
                        user.Id,
                        $"Resignation {statusMsg}",
                        $"Your resignation request was {statusMsg}.",
                        "my-resignation");
                }
            }
        }

        // --- 3. CALCULATE FNF (Core Logic) ---
        public async Task<FnFSettlement?> CalculateSettlementAsync(int requestId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Gated Check
            var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == 1);
            if (features?.EnableResignationModule != true) return null;

            var req = await db.ResignationRequests.FindAsync(requestId);
            if (req == null || req.Status != "Approved") return null;

            var emp = await db.Employees.FindAsync(req.EmployeeId);
            if (emp == null) return null;

            // Calculations
            DateOnly exitDate = req.ApprovedLastWorkingDay ?? req.DesiredLastWorkingDay;
            decimal dailyRate = emp.MonthlySalary / 30m;

            // Unpaid Salary
            int daysWorkedInMonth = exitDate.Day;
            decimal unpaidSalary = Math.Round(daysWorkedInMonth * dailyRate, 2);

            // Leave Encashment
            decimal leaveEncashment = 0;
            if (emp.PaidLeaveBalance > 0)
            {
                leaveEncashment = Math.Round(emp.PaidLeaveBalance * dailyRate, 2);
            }

            // Gratuity (5+ Years Rule)
            decimal gratuity = 0;
            if (emp.HireDate.HasValue)
            {
                var tenureYears = (exitDate.DayNumber - emp.HireDate.Value.DayNumber) / 365.25;
                if (tenureYears >= 5)
                {
                    gratuity = Math.Round((emp.MonthlySalary * 15 * (decimal)tenureYears) / 26, 2);
                }
            }

            // Deduct Pending Advances
            decimal pendingAdvances = await db.SalaryAdvances
                .Where(a => a.EmployeeID == emp.EmployeeID && a.PayrollID_Paid == null)
                .SumAsync(a => a.Amount);

            return new FnFSettlement
            {
                EmployeeId = emp.EmployeeID,
                ResignationRequestId = requestId,
                SettlementDate = DateTime.Now,
                UnpaidSalary = unpaidSalary,
                LeaveEncashment = leaveEncashment,
                Gratuity = gratuity,
                NoticePeriodRecovery = 0,
                OutstandingAdvances = pendingAdvances,
                AssetRecoveryCost = 0,
                BonusPayable = 0,
                NetPayable = (unpaidSalary + leaveEncashment + gratuity) - pendingAdvances
            };
        }

        // --- 4. FINALIZE & LOCK (The "Kill Switch") ---
        public async Task FinalizeSettlementAsync(FnFSettlement settlement)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            using var transaction = await db.Database.BeginTransactionAsync();

            try
            {
                // 1. Save Settlement
                settlement.IsFinalized = true;
                db.FnFSettlements.Add(settlement);

                // 2. Update Request
                var req = await db.ResignationRequests.FindAsync(settlement.ResignationRequestId);
                if (req != null) req.IsSettled = true;

                // 3. TERMINATE EMPLOYEE
                var emp = await db.Employees.FindAsync(settlement.EmployeeId);
                if (emp != null)
                {
                    emp.TerminationDate = req?.ApprovedLastWorkingDay ?? DateOnly.FromDateTime(DateTime.Now);
                    emp.PaidLeaveBalance = 0;
                    emp.SickLeaveBalance = 0;

                    // --- NOTIFY EMPLOYEE ---
                    if (!string.IsNullOrEmpty(emp.Email))
                    {
                        await _emailSender.SendEmailAsync(emp.Email,
                            "Full & Final Settlement Processed",
                            $"<p>Dear {emp.Name},</p><p>Your Full & Final settlement has been processed.</p>" +
                            $"<p><strong>Net Payable:</strong> ₹ {settlement.NetPayable:N2}</p>" +
                            $"<p>Your employment records have been closed. We wish you the best for your future endeavors.</p>");
                    }
                }

                // 4. Close Advances
                var advances = await db.SalaryAdvances
                    .Where(a => a.EmployeeID == settlement.EmployeeId && a.PayrollID_Paid == null)
                    .ToListAsync();
                db.SalaryAdvances.RemoveRange(advances);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                if (req != null) await UpsertFirebaseResignationAsync(req);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // --- HELPER: Email all Admins ---
        private async Task NotifyAdminsAsync(string subject, string htmlMessage)
        {
            try
            {
                // Find users with "Admin" or "SuperAdmin" role
                var admins = await _userManager.GetUsersInRoleAsync("Admin");
                var superAdmins = await _userManager.GetUsersInRoleAsync("SuperAdmin");

                var recipients = admins.Concat(superAdmins)
                                       .Where(u => !string.IsNullOrEmpty(u.Email))
                                       .Select(u => u.Email!)
                                       .Distinct()
                                       .ToList();

                foreach (var email in recipients)
                {
                    await _emailSender.SendEmailAsync(email, subject, htmlMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify admins about resignation.");
            }
        }
    }
}