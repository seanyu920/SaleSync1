using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Net;        // Added for NetworkCredential
using System.Net.Mail;   // Added for MailMessage and SmtpClient
using SaleSync.Services;

namespace SaleSync.Controllers
{
    public class AccountController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString;

        public AccountController(IConfiguration configuration)
        {
            _configuration = configuration;
            connectionString = _configuration.GetConnectionString("DefaultConnection");
        }

        // 1. Displays the empty Registration Page
        [HttpGet]
        public IActionResult Register()
        {
            return View();
        }

        // 2. Handles the form submission
        [HttpPost]
        public async Task<IActionResult> Register(string fullName, string username, string email, string password, string confirmPassword)
        {
            // Validation: Ensure no fields are empty
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            {
                ViewBag.Message = "One or more fields were blank. Please check your form!";
                return View();
            }

            // Validation: Password match check
            if (password != confirmPassword)
            {
                ViewBag.Message = "Passwords do not match. Please try again.";
                return View();
            }

            bool accountExists = false;

            // ─── CHECK IF USERNAME OR EMAIL EXISTS ──────────────────────────
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string checkQuery = "SELECT COUNT(1) FROM users WHERE username = @Username OR email = @Email";
                using (SqlCommand cmd = new SqlCommand(checkQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Email", email);

                    await conn.OpenAsync();
                    int count = (int)await cmd.ExecuteScalarAsync();
                    if (count > 0)
                    {
                        accountExists = true;
                    }
                }
            }

            if (accountExists)
            {
                ViewBag.Message = "An account with that Username or Email already exists.";
                return View();
            }

            // ─── GENERATE OTP LOGIC ─────────────────────────────────────────────
            string generatedOtp = Random.Shared.Next(100000, 999999).ToString();
            DateTime expiryTime = DateTime.Now.AddMinutes(10);

            // ─── SAVE USER TO DATABASE (RAW SQL INSERT) ─────────────────────────
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // UPDATED: 'pending' changed to 'Pending' to pass database constraints
                string insertQuery = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id, status, is_active, OtpCode, OtpExpiry, IsEmailConfirmed) 
                    VALUES (@FullName, @Username, @Email, @Password, 
                           (SELECT TOP 1 role_id FROM roles WHERE role_name = 'Customer'), 
                           'Pending', 0, @OtpCode, @OtpExpiry, 0)";

                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", fullName);
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@Password", PasswordHasher.Hash(password));
                    cmd.Parameters.AddWithValue("@OtpCode", generatedOtp);
                    cmd.Parameters.AddWithValue("@OtpExpiry", expiryTime);

                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // ─── ⭐ LIVE EMAIL ACTIVATION ROUTINE ⭐ ──────────────────────────
            await SendOtpEmailNativeAsync(email, generatedOtp);

            TempData["SuccessMessage"] = "Account created! An OTP verification code has been sent to your email.";

            // Redirect to the verification screen inside this same controller
            return RedirectToAction("VerifyOtp", new { email = email });
        }

        // ─── OTP VERIFICATION ACTIONS ────────────────────────────────────────
        [HttpGet]
        public IActionResult VerifyOtp(string email)
        {
            ViewBag.UserEmail = email;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> VerifyOtp(string email, string enteredOtp)
        {
            bool isValid = false;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string verifyQuery = @"SELECT COUNT(1) FROM users 
                                       WHERE email = @Email AND OtpCode = @OtpCode AND OtpExpiry > @CurrentTime";

                using (SqlCommand cmd = new SqlCommand(verifyQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@OtpCode", enteredOtp);
                    cmd.Parameters.AddWithValue("@CurrentTime", DateTime.Now);

                    await conn.OpenAsync();
                    int count = (int)await cmd.ExecuteScalarAsync();
                    if (count > 0) isValid = true;
                }
            }

            if (isValid)
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    // UPDATED: 'active' changed to 'Active' to pass database constraints
                    string activateQuery = @"UPDATE users 
                                             SET status = 'Active', is_active = 1, IsEmailConfirmed = 1, OtpCode = NULL, OtpExpiry = NULL 
                                             WHERE email = @Email";

                    using (SqlCommand cmd = new SqlCommand(activateQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@Email", email);
                        await conn.OpenAsync();
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                TempData["SuccessMessage"] = "Account verified successfully! You can now log in.";
                return RedirectToAction("Login", "Home");
            }

            ViewBag.UserEmail = email;
            ViewBag.Message = "Invalid or expired OTP code. Please try again.";
            return View();
        }

        // ─── ⭐ PRIVATE NATIVE EMAIL SENDER HELPER ⭐ ────────────────────────
        private async Task SendOtpEmailNativeAsync(string targetEmail, string otpCode)
        {
            var smtpServer = _configuration["EmailSettings:SmtpServer"];
            var port = int.Parse(_configuration["EmailSettings:Port"]);
            var senderEmail = _configuration["EmailSettings:SenderEmail"];
            var senderName = _configuration["EmailSettings:SenderName"];
            var password = _configuration["EmailSettings:Password"];

            using (var message = new MailMessage())
            {
                message.From = new MailAddress(senderEmail, senderName);
                message.To.Add(new MailAddress(targetEmail));
                message.Subject = $"{otpCode} is your Cafero Verification Code";

                // Custom styled HTML body matching your coffee theme
                message.Body = $@"
                    <div style='font-family: sans-serif; padding: 20px; color: #4a2511; background-color: #fdfaf6; border-radius: 8px;'>
                        <h2>Welcome to Cafero!</h2>
                        <p>Thank you for signing up with SaleSync. Use the verification code below to activate your account:</p>
                        <div style='font-size: 28px; font-weight: bold; letter-spacing: 4px; padding: 15px; background-color: #ffffff; border: 1px solid #dcbca5; display: inline-block; border-radius: 6px; color: #f46a05;'>
                            {otpCode}
                        </div>
                        <p style='font-size: 12px; color: #a39081; margin-top: 20px;'>This code is valid for 10 minutes. If you didn't request this, you can safely ignore this email.</p>
                    </div>";
                message.IsBodyHtml = true;

                using (var client = new SmtpClient(smtpServer, port))
                {
                    client.Credentials = new NetworkCredential(senderEmail, password);
                    client.EnableSsl = true; // Essential for Gmail SMTP authorization
                    await client.SendMailAsync(message);
                }
            }
        }
    }
}