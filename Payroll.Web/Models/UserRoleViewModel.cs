using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Payroll.Web.Models
{
    public class NewUserFormModel
    {
        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Invalid email format.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
        public string Password { get; set; } = string.Empty;

        public int? EmployeeId { get; set; } // Link to Employee
        public string InitialRole { get; set; } = "Employee";
    }
    // --- END NEW CLASS ---
    public class UserRoleViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string? FirebaseUid { get; set; }
        public string Email { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;

        // Custom properties
        public int? EmployeeId { get; set; } // Link to custom employee data
        public string EmployeeName { get; set; } = "N/A (Unlinked)";
        public string CurrentRole { get; set; } = "Employee"; // Admin or Employee
        public bool IsEmployeeActive { get; set; } // Based on TerminationDate
        public bool IsDisabled { get; set; }
    }
}