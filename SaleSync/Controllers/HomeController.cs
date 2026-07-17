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

namespace SaleSync.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString; // Remove the hardcoded string here!

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

        // ==========================================
        // ⭐ THE ENTERPRISE LOGGER HELPER
        // ==========================================
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

        // ⭐ 1. THE ASYNC LOGIN FIX ⭐
        [HttpPost]
        public async Task<IActionResult> Login(string username, string password)
        {
            // 1. Read the secret "Ghost" credentials from configuration.
            // These must come from user-secrets/environment variables in every
            // environment — see appsettings.json for setup notes. The password
            // is stored as a bcrypt hash, never in plaintext.
            var ghostUser = _configuration["SuperAdminConfig:Username"];
            var ghostPassHash = _configuration["SuperAdminConfig:PasswordHash"];

            // 2. The Ghost Check (Invisible to the Database)
            if (!string.IsNullOrEmpty(ghostUser) && !string.IsNullOrEmpty(ghostPassHash) &&
                username == ghostUser && PasswordHasher.Verify(password, ghostPassHash, out _))
            {
                var ghostClaims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "SuperAdmin"),
                    new Claim(ClaimTypes.NameIdentifier, "0"),
                    new Claim(ClaimTypes.Role, "Admin"),
                    // Chat (and anything else keyed off the DB "username" column) needs a
                    // stable username claim distinct from the display name below.
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
                      AND u.is_active = 1"; // Keeps unverified (is_active = 0) users locked out!

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
                                // ClaimTypes.Name holds the display (full) name used throughout the
                                // UI. Chat needs the actual users.username value, so keep it separate.
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

        // ⭐ 2. REGISTER NOW GENERATES AND SAVES OTP (UPDATED TO ASYNC) ⭐
        [HttpPost]
        public async Task<IActionResult> Register(string fullName, string username, string email, string password, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            {
                ViewBag.Message = "One or more fields were blank. Please check your form!";
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.Message = "Passwords do not match. Please try again.";
                return View();
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();
                string checkSql = "SELECT COUNT(*) FROM users WHERE username = @Username OR email = @Email";

                int exists = 0;
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@Username", username);
                    checkCmd.Parameters.AddWithValue("@Email", email);
                    exists = (int)await checkCmd.ExecuteScalarAsync();
                }

                if (exists > 0)
                {
                    ViewBag.Message = "An account with that Username or Email already exists.";
                    return View();
                }

                // Generate OTP Code and 10-minute expiry window
                string generatedOtp = Random.Shared.Next(100000, 999999).ToString();
                DateTime expiryTime = DateTime.Now.AddMinutes(10);

                // Insert user with status 'pending', is_active = 0, and new OTP attributes
                string insertSql = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id, status, is_active, OtpCode, OtpExpiry, IsEmailConfirmed)
                    VALUES (@FullName, @Username, @Email, @Password, 
                           (SELECT TOP 1 role_id FROM roles WHERE role_name = 'Customer'), 
                           'pending', 0, @OtpCode, @OtpExpiry, 0)";

                using (SqlCommand cmd = new SqlCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", fullName);
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@Password", PasswordHasher.Hash(password));
                    cmd.Parameters.AddWithValue("@OtpCode", generatedOtp);
                    cmd.Parameters.AddWithValue("@OtpExpiry", expiryTime);

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // ─── STAGE EMAIL ACTIVATION HERE ───
            // await _emailService.SendOtpEmailAsync(email, generatedOtp);

            TempData["SuccessMessage"] = "Account created! An OTP verification code has been sent to your email.";
            return RedirectToAction("VerifyOtp", new { email = email });
        }

        // ⭐ 3. NEW OTP VERIFICATION ROUTINES ⭐
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
                    // Activate account by updating status, is_active, and purging temporary credentials
                    string activateQuery = @"UPDATE users 
                                             SET status = 'active', is_active = 1, IsEmailConfirmed = 1, OtpCode = NULL, OtpExpiry = NULL 
                                             WHERE email = @Email";

                    using (SqlCommand cmd = new SqlCommand(activateQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@Email", email);
                        await conn.OpenAsync();
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                TempData["SuccessMessage"] = "Account verified successfully! You can now log in.";
                return RedirectToAction("LogIn");
            }

            ViewBag.UserEmail = email;
            ViewBag.Message = "Invalid or expired OTP code. Please try again.";
            return View();
        }

        // ⭐ 4. THE ASYNC LOGOUT FIX WITH AUDIT LOGGING ⭐
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

        [HttpGet]
        public IActionResult Register()
        {
            return View();
        }
    }
}