using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using SaleSync.Services;
using System;
using System.Security.Cryptography;
using System.Net;
using System.Net.Mail;

namespace SaleSync.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString; 

        public HomeController(IConfiguration configuration)
        {
            _configuration = configuration;
            connectionString = _configuration.GetConnectionString("DefaultConnection");
        }
        

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

  
        private void LogActivity(int userId, string action, string details)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "INSERT INTO ActivityLogs (UserId, ActionType, Details) VALUES (@uid, @action, @details)";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@uid", userId);
                    cmd.Parameters.AddWithValue("@action", action);
                    cmd.Parameters.AddWithValue("@details", details);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
        }


        [HttpPost]
        public async Task<IActionResult> Login(string username, string password)
        {
       
            var ghostUser = _configuration["SuperAdminConfig:Username"];
            var ghostPassHash = _configuration["SuperAdminConfig:PasswordHash"];

            )
            if (!string.IsNullOrEmpty(ghostUser) && !string.IsNullOrEmpty(ghostPassHash) &&
                username == ghostUser && PasswordHasher.Verify(password, ghostPassHash, out _))
            {
                var ghostClaims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "SuperAdmin"),
                    new Claim(ClaimTypes.NameIdentifier, "0"),
                    new Claim(ClaimTypes.Role, "Admin"),
            
                    new Claim("Username", ghostUser)
                };

                var ghostIdentity = new ClaimsIdentity(ghostClaims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(ghostIdentity));

                LogActivity(0, "System Access", "SuperAdmin Ghost Account logged in.");

                return RedirectToAction("Dashboard", "Admin");
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                    SELECT u.user_id, u.full_name, u.username, u.password_hash, r.role_name
                    FROM users u
                    INNER JOIN roles r ON u.role_id = r.role_id
                    WHERE (u.username = @Username OR u.email = @Username)
                      AND u.is_active = 1"; 

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", username);

                    string storedHash = null;
                    string rawRole = null, fullName = null, userId = null, dbUsername = null;
                    bool found = false;

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            storedHash = reader["password_hash"]?.ToString();
                            rawRole = reader["role_name"].ToString().Trim();
                            fullName = reader["full_name"].ToString();
                            userId = reader["user_id"].ToString();
                            dbUsername = reader["username"].ToString();
                            found = true;
                        }
                    }

                    if (found && PasswordHasher.Verify(password, storedHash, out bool needsUpgrade))
                    {
                        if (needsUpgrade)
                        {
                            using (SqlCommand upgradeCmd = new SqlCommand(
                                "UPDATE users SET password_hash = @hash WHERE user_id = @id", conn))
                            {
                                upgradeCmd.Parameters.AddWithValue("@hash", PasswordHasher.Hash(password));
                                upgradeCmd.Parameters.AddWithValue("@id", userId);
                                upgradeCmd.ExecuteNonQuery();
                            }
                        }

                        {
                            string role = char.ToUpper(rawRole[0]) + rawRole.Substring(1).ToLower();

                            var claims = new List<Claim>
                            {
                                new Claim(ClaimTypes.NameIdentifier, userId),
                                new Claim(ClaimTypes.Name, fullName),
                                new Claim(ClaimTypes.Role, role),
                                
                                new Claim("Username", dbUsername)
                            };

                            var claimsIdentity = new ClaimsIdentity(
                                claims,
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                ClaimTypes.Name,
                                ClaimTypes.Role
                            );

                            await HttpContext.SignInAsync(
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                new ClaimsPrincipal(claimsIdentity)
                            );

                            LogActivity(Convert.ToInt32(userId), "System Access", $"Logged in successfully as {role}.");

                            if (role == "Customer") return RedirectToAction("CustomerOrdering", "Customer");
                            if (role == "Admin") return RedirectToAction("Dashboard", "Admin");
                            if (role == "Manager") return RedirectToAction("Dashboard", "Manager");
                            if (role == "Cashier") return RedirectToAction("Dashboard", "Cashier");
                        }
                    }
                }
            }

            ViewBag.Message = "Invalid username or password, or account is unverified.";
            return View("LogIn");
        }

   
        [HttpPost]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest();

            int userId = 0;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                string checkSql = "SELECT user_id FROM users WHERE email = @Email AND is_active = 1";
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@Email", email);
                    var result = await checkCmd.ExecuteScalarAsync();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                
                if (userId != 0)
                {
                    string token = GenerateResetToken();
                    DateTime expiry = DateTime.Now.AddMinutes(30);

                    string updateSql = @"
                        UPDATE users 
                        SET PasswordResetToken = @Token, PasswordResetExpiry = @Expiry 
                        WHERE user_id = @UserId";

                    using (SqlCommand updateCmd = new SqlCommand(updateSql, conn))
                    {
                        updateCmd.Parameters.AddWithValue("@Token", token);
                        updateCmd.Parameters.AddWithValue("@Expiry", expiry);
                        updateCmd.Parameters.AddWithValue("@UserId", userId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    string resetLink = Url.Action("ResetPassword", "Home",
                        new { email = email, token = token }, Request.Scheme);

                    try
                    {
                        await SendPasswordResetEmailAsync(email, resetLink);
                    }
                    catch (Exception ex)
                    {
                        
                        LogActivity(userId, "Password Reset Email Failed", ex.Message);
                    }
                }
            }

            return Ok();
        }

       
        [HttpGet]
        public async Task<IActionResult> ResetPassword(string email, string token)
        {
            bool isValid = await IsResetTokenValidAsync(email, token);
            if (!isValid)
            {
                ViewBag.Message = "This reset link is invalid or has expired. Please request a new one.";
                return View("LogIn");
            }

            ViewBag.Email = email;
            ViewBag.Token = token;
            return View();
        }

        
        [HttpPost]
        public async Task<IActionResult> ResetPassword(string email, string token, string password, string confirmPassword)
        {
            bool isValid = await IsResetTokenValidAsync(email, token);
            if (!isValid)
            {
                ViewBag.Message = "This reset link is invalid or has expired. Please request a new one.";
                return View("LogIn");
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                ViewBag.Email = email;
                ViewBag.Token = token;
                ViewBag.Message = "Password must be at least 8 characters long.";
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.Email = email;
                ViewBag.Token = token;
                ViewBag.Message = "Passwords do not match. Please try again.";
                return View();
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string updateSql = @"
                    UPDATE users 
                    SET password_hash = @Hash, PasswordResetToken = NULL, PasswordResetExpiry = NULL 
                    WHERE email = @Email";

                using (SqlCommand cmd = new SqlCommand(updateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@Hash", PasswordHasher.Hash(password));
                    cmd.Parameters.AddWithValue("@Email", email);
                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            TempData["SuccessMessage"] = "Your password has been reset. You can now log in.";
            return RedirectToAction("LogIn");
        }

        private async Task<bool> IsResetTokenValidAsync(string email, string token)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
                return false;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT COUNT(1) FROM users 
                    WHERE email = @Email 
                      AND PasswordResetToken = @Token 
                      AND PasswordResetExpiry > @Now";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@Token", token);
                    cmd.Parameters.AddWithValue("@Now", DateTime.Now);
                    await conn.OpenAsync();
                    int count = (int)await cmd.ExecuteScalarAsync();
                    return count > 0;
                }
            }
        }

        private static string GenerateResetToken()
        {
            byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);


            return Convert.ToBase64String(tokenBytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
        }

        private async Task SendPasswordResetEmailAsync(string targetEmail, string resetLink)
        {
            var smtpServer = _configuration["EmailSettings:SmtpServer"];
            var port = int.Parse(_configuration["EmailSettings:Port"]);
            var senderEmail = _configuration["EmailSettings:SenderEmail"];
            var senderName = _configuration["EmailSettings:SenderName"];
            var smtpPassword = _configuration["EmailSettings:Password"];

            using (var message = new MailMessage())
            {
                message.From = new MailAddress(senderEmail, senderName);
                message.To.Add(new MailAddress(targetEmail));
                message.Subject = "Reset your Cafero password";

                message.Body = $@"
                    <div style='font-family: sans-serif; padding: 20px; color: #4a2511; background-color: #fdfaf6; border-radius: 8px;'>
                        <h2>Password Reset Request</h2>
                        <p>We received a request to reset the password on your Cafero account. Click the button below to choose a new one:</p>
                        <p style='margin: 24px 0;'>
                            <a href='{resetLink}' style='background-color:#f46a05; color:#ffffff; padding:12px 24px; border-radius:6px; text-decoration:none; font-weight:bold; display:inline-block;'>Reset Password</a>
                        </p>
                        <p style='font-size: 12px; color: #a39081;'>This link is valid for 30 minutes. If you didn't request this, you can safely ignore this email — your password will not be changed.</p>
                    </div>";
                message.IsBodyHtml = true;

                using (var client = new SmtpClient(smtpServer, port))
                {
                    client.Credentials = new NetworkCredential(senderEmail, smtpPassword);
                    client.EnableSsl = true;
                    await client.SendMailAsync(message);
                }
            }
        }


        public async Task<IActionResult> Logout()
        {
            string userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userIdClaim))
            {
                int currentUserId = int.Parse(userIdClaim);
                LogActivity(currentUserId, "System Access", "Logged out of the system.");
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }

        public IActionResult LogInSelection()
        {
            return View();
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View();
        }

        [HttpGet]
        public IActionResult CustomerLogIn()
        {
            return View();
        }
    }
}