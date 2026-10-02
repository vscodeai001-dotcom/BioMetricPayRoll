using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payroll.Shared.Data;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class EmailSender : IEmailSender
    {
        private readonly ILogger<EmailSender> _logger;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly IConfiguration _configuration;

        public EmailSender(
            ILogger<EmailSender> logger,
            IDbContextFactory<AppDbContext> dbFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _dbFactory = dbFactory;
            _configuration = configuration;
        }

        private async Task<(string host, int port, string user, string pass, bool ssl, string fromEmail, string fromName)?> GetSmtpConfigAsync()
        {
            using var dbContext = await _dbFactory.CreateDbContextAsync();
            var settings = await dbContext.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingID == 1);

            var host = settings?.SmtpHost;
            var port = settings?.SmtpPort ?? 0;
            var user = settings?.SmtpUser;
            var pass = settings?.SmtpPass;
            var ssl = settings?.EnableSsl ?? true;
            var fromEmail = settings?.SmtpFromEmail;
            var fromName = settings?.CompanyName ?? "BioMetric+Payroll System";

            // Fallback to appsettings.json or environment variables if DB fields are unconfigured
            if (string.IsNullOrWhiteSpace(host))
                host = _configuration["Smtp:Host"] ?? _configuration["SMTP_HOST"];
            if (port <= 0)
                port = int.TryParse(_configuration["Smtp:Port"], out var p) ? p : 587;
            if (string.IsNullOrWhiteSpace(user))
                user = _configuration["Smtp:User"] ?? _configuration["SMTP_USER"] ?? _configuration["Firebase:SuperAdminEmail"] ?? "prakashshiva368@gmail.com";
            if (string.IsNullOrWhiteSpace(pass))
                pass = _configuration["Smtp:Pass"] ?? _configuration["SMTP_PASS"];
            if (string.IsNullOrWhiteSpace(fromEmail))
                fromEmail = _configuration["Smtp:FromEmail"] ?? user;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                return null;
            }

            return (host, port, user, pass, ssl, fromEmail, fromName);
        }

        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            try
            {
                var smtp = await GetSmtpConfigAsync();
                if (smtp == null)
                {
                    _logger.LogWarning("⚠️ Email not sent. SMTP settings are not configured in Company Settings or appsettings.json.");
                    return;
                }

                using var client = new SmtpClient(smtp.Value.host, smtp.Value.port)
                {
                    Credentials = new NetworkCredential(smtp.Value.user, smtp.Value.pass),
                    EnableSsl = smtp.Value.ssl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(smtp.Value.fromEmail, smtp.Value.fromName),
                    Subject = subject,
                    Body = htmlMessage,
                    IsBodyHtml = true
                };

                mailMessage.To.Add(email);

                await client.SendMailAsync(mailMessage);
                _logger.LogInformation("✅ Email sent successfully to {Email}", email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send email to {Email}", email);
            }
        }

        public async Task<bool> SendEmailWithAttachmentAsync(
            string email,
            string subject,
            string htmlMessage,
            string attachmentFilePath,
            string? attachmentDisplayName = null,
            string? fromEmail = null,
            string? fromDisplayName = null)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            if (!File.Exists(attachmentFilePath))
            {
                _logger.LogWarning("⚠️ Attachment file does not exist: {Path}", attachmentFilePath);
                return false;
            }

            try
            {
                var smtp = await GetSmtpConfigAsync();
                if (smtp == null)
                {
                    _logger.LogWarning("⚠️ Email backup not sent. SMTP settings are not configured in Company Settings or appsettings.json.");
                    return false;
                }

                using var client = new SmtpClient(smtp.Value.host, smtp.Value.port)
                {
                    Credentials = new NetworkCredential(smtp.Value.user, smtp.Value.pass),
                    EnableSsl = smtp.Value.ssl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };

                var resolvedSenderEmail = !string.IsNullOrWhiteSpace(fromEmail) ? fromEmail : smtp.Value.fromEmail;
                var resolvedSenderName = !string.IsNullOrWhiteSpace(fromDisplayName) ? fromDisplayName : smtp.Value.fromName;

                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(resolvedSenderEmail, resolvedSenderName),
                    Subject = subject,
                    Body = htmlMessage,
                    IsBodyHtml = true
                };

                mailMessage.To.Add(email);

                var attachment = new Attachment(attachmentFilePath)
                {
                    Name = attachmentDisplayName ?? Path.GetFileName(attachmentFilePath)
                };
                mailMessage.Attachments.Add(attachment);

                await client.SendMailAsync(mailMessage);
                _logger.LogInformation("✅ Backup email sent from {From} to {To} with attachment '{File}'", resolvedSenderEmail, email, attachment.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send backup email with attachment to {Email}", email);
                return false;
            }
        }
    }
}