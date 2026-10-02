using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class FBPService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly NotificationService _notificationService;
        private readonly FirebaseEmployeeManagementService _firebaseEmployees;
        private readonly FirebaseRealtimeService _firebase;
        private readonly AttendanceRefreshService _refreshService;

        public FBPService(
            IDbContextFactory<AppDbContext> dbFactory,
            NotificationService notificationService,
            FirebaseEmployeeManagementService firebaseEmployees,
            FirebaseRealtimeService firebase,
            AttendanceRefreshService refreshService)
        {
            _dbFactory = dbFactory;
            _notificationService = notificationService;
            _firebaseEmployees = firebaseEmployees;
            _firebase = firebase;
            _refreshService = refreshService;
        }

        // --- ADMIN: MANAGE COMPONENTS ---

        public async Task<List<FBPComponent>> GetActiveComponentsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.FBPComponents.Where(c => c.IsActive).ToListAsync();
        }

        public async Task SaveComponentAsync(FBPComponent component)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (component.ComponentId == 0)
            {
                db.FBPComponents.Add(component);
            }
            else
            {
                db.FBPComponents.Update(component);
            }
            await db.SaveChangesAsync();

            // REQUIREMENT: Synchronize FBP components to Firebase SSOT so the
            // Android application has a real-time list of available benefits.
            try
            {
                var ownerUid = _firebaseEmployees.OwnerUid;
                await _firebase.SetOwnerRecordAsync(
                    ownerUid,
                    "fbp_components",
                    component.ComponentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    component);

                await _firebase.PublishLocalApplicationChangeAsync(ownerUid, "FBPComponent", "MODIFIED");
            }
            catch { /* Best effort only; SQL remains calculation authority. */ }
        }

        // --- EMPLOYEE: DECLARATION LOGIC ---

        public async Task<List<FlexibleBenefitDeclaration>> GetDeclarationsAsync(int employeeId, int financialYear)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.FlexibleBenefitDeclarations
                .Where(d => d.EmployeeId == employeeId && d.FinancialYear == financialYear)
                .ToListAsync();
        }

        public async Task SaveDeclarationsAsync(int employeeId, int financialYear, List<FlexibleBenefitDeclaration> declarations)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Delete existing drafts/submissions for the year to replace them
            var existing = await db.FlexibleBenefitDeclarations
                .Where(d => d.EmployeeId == employeeId && d.FinancialYear == financialYear && d.Status != "Locked")
                .ToListAsync();
            db.FlexibleBenefitDeclarations.RemoveRange(existing);

            // Calculate monthly amount for new submissions
            int remainingMonths = 12; // Simplified: Assume declaration is done at the start of the year

            foreach (var declaration in declarations)
            {
                if (declaration.AnnualAllocatedAmount > 0)
                {
                    declaration.EmployeeId = employeeId;
                    declaration.FinancialYear = financialYear;
                    declaration.Status = "Submitted";
                    declaration.SubmissionDate = DateTime.Now;

                    // CRITICAL CALCULATION: Pro-rata monthly distribution
                    declaration.MonthlyAllocatedAmount = Math.Round(declaration.AnnualAllocatedAmount / remainingMonths, 2);

                    db.FlexibleBenefitDeclarations.Add(declaration);
                }
            }
            await db.SaveChangesAsync();

            // REQUIREMENT: Synchronize FBP declarations to Firebase SSOT.
            // This ensures the Android employee dashboard reflects the
            // latest submitted benefits immediately.
            try
            {
                var ownerUid = _firebaseEmployees.OwnerUid;
                var updates = declarations
                    .Where(d => d.DeclarationId > 0)
                    .ToDictionary(
                        d => d.DeclarationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        d => (object?)d);

                if (updates.Count > 0)
                {
                    await _firebase.SetOwnerRecordsAsync(ownerUid, "fbp_declarations", updates);
                    await _firebase.PublishLocalApplicationChangeAsync(ownerUid, "FlexibleBenefitDeclaration", "MODIFIED");
                }
            }
            catch { /* Best effort */ }

            // Employee identity/name is a Firebase SSOT projection.
            // declaration calculation and persistence above remain SQL-backed.
            var employee = await _firebaseEmployees.GetEmployeeAsync(employeeId);
            if (employee != null)
            {
                await _notificationService.NotifyAdminsAsync(
                    "New FBP Declaration",
                    $"{employee.Name} submitted an FBP declaration for FY {financialYear}-{financialYear + 1}.",
                    "/admin/fbp-approval");
            }
        }


        // --- ADMIN: DECLARATION APPROVAL / REJECTION ---
        // These methods preserve the existing Web approval semantics for native Android callers.
        public async Task<bool> ApproveDeclarationsAsync(int employeeId, int financialYear)
        {
            var employee = await _firebaseEmployees.GetEmployeeAsync(employeeId);
            if (employee == null) return false;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.FlexibleBenefitDeclarations
                .Where(d => d.EmployeeId == employeeId && d.FinancialYear == financialYear && d.Status == "Submitted")
                .ToListAsync();
            if (rows.Count == 0) return false;

            var totalAllocated = rows.Sum(d => d.AnnualAllocatedAmount);
            var availableAllowance = employee.MonthlySalary * 12m * 0.10m;
            if (totalAllocated > availableAllowance) return false;

            foreach (var row in rows) row.Status = "Approved";
            await db.SaveChangesAsync();
            try
            {
                var ownerUid = _firebaseEmployees.OwnerUid;
                var updates = rows.ToDictionary(d => d.DeclarationId.ToString(System.Globalization.CultureInfo.InvariantCulture), d => (object?)d);
                await _firebase.SetOwnerRecordsAsync(ownerUid, "fbp_declarations", updates);
                await _firebase.PublishLocalApplicationChangeAsync(ownerUid, "FlexibleBenefitDeclaration", "MODIFIED");
            }
            catch { }

            await _notificationService.NotifyEmployeeAsync(
                employeeId,
                "FBP Declaration Approved",
                $"Your FBP declaration for FY {financialYear}-{financialYear + 1} was approved and locked.",
                "/my-fbp-declaration");
            await _refreshService.NotifyDataChangedAsync(employeeId, null, "FBP_APPROVAL");
            return true;
        }

        public async Task RejectDeclarationsAsync(int employeeId, int financialYear, string remarks)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.FlexibleBenefitDeclarations
                .Where(d => d.EmployeeId == employeeId && d.FinancialYear == financialYear && d.Status != "Locked")
                .ToListAsync();
            if (rows.Count == 0) return;
            db.FlexibleBenefitDeclarations.RemoveRange(rows);
            await db.SaveChangesAsync();
            try
            {
                var ownerUid = _firebaseEmployees.OwnerUid;
                foreach (var row in rows)
                    await _firebase.DeleteOwnerRecordAsync(ownerUid, "fbp_declarations", row.DeclarationId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await _firebase.PublishLocalApplicationChangeAsync(ownerUid, "FlexibleBenefitDeclaration", "DELETED");
            }
            catch { }

            await _notificationService.NotifyEmployeeAsync(
                employeeId,
                "FBP Declaration Rejected",
                $"Your FBP declaration for FY {financialYear}-{financialYear + 1} was rejected. Reason: {remarks}",
                "/my-fbp-declaration");
            await _refreshService.NotifyDataChangedAsync(employeeId, null, "FBP_REJECTION");
        }

        // --- PAYROLL INTEGRATION LOGIC ---

        /// <summary>
        /// Retrieves the active FBP monthly allocation for an employee.
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetActiveMonthlyAllocationsAsync(int employeeId, int financialYear)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Only consider declarations that have been APPROVED or are currently ACTIVE and Locked
            var declarations = await db.FlexibleBenefitDeclarations
                .Where(d => d.EmployeeId == employeeId &&
                            d.FinancialYear == financialYear &&
                            d.Status == "Approved" &&
                            d.IsActive)
                .ToDictionaryAsync(d => d.ComponentName, d => d.MonthlyAllocatedAmount);

            return declarations;
        }
    }
}