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
using System; // Added for Convert.ToInt32

namespace SaleSync.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=IANPC;Database=SaleSync;Trusted_Connection=True;Encrypt=False;";

        public HomeController(IConfiguration configuration)
        {
            _configuration = configuration;
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
            // 1. Read the secret "Ghost" credentials from appsettings.json
            var ghostUser = _configuration["SuperAdminConfig:Username"];
            var ghostPass = _configuration["SuperAdminConfig:Password"];

            // 2. The Ghost Check (Invisible to the Database)
            if (username == ghostUser && password == ghostPass)
            {
                var ghostClaims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "SuperAdmin"),
                    new Claim(ClaimTypes.NameIdentifier, "0"), // Identify as ID 0
                    new Claim(ClaimTypes.Role, "Admin")
                };

                var ghostIdentity = new ClaimsIdentity(ghostClaims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(ghostIdentity));

                // ⭐ LOG THE GHOST LOGIN
                LogActivity(0, "System Access", "SuperAdmin Ghost Account logged in.");

                return RedirectToAction("Dashboard", "Admin");
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                    SELECT u.user_id, u.full_name, u.username, r.role_name
                    FROM users u
                    INNER JOIN roles r ON u.role_id = r.role_id
                    WHERE (u.username = @Username OR u.email = @Username)
                      AND u.password_hash = @Password
                      AND u.is_active = 1";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Password", password);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string rawRole = reader["role_name"].ToString().Trim();
                            string role = char.ToUpper(rawRole[0]) + rawRole.Substring(1).ToLower();
                            string fullName = reader["full_name"].ToString();
                            string userId = reader["user_id"].ToString();

                            var claims = new List<Claim>
                            {
                                new Claim(ClaimTypes.NameIdentifier, userId),
                                new Claim(ClaimTypes.Name, fullName),
                                new Claim(ClaimTypes.Role, role)
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

                            // ⭐ LOG THE SUCCESSFUL LOGIN
                            LogActivity(Convert.ToInt32(userId), "System Access", $"Logged in successfully as {role}.");

                            if (role == "Customer") return RedirectToAction("CustomerOrdering", "Customer");
                            if (role == "Admin") return RedirectToAction("Dashboard", "Admin");
                            if (role == "Manager") return RedirectToAction("Dashboard", "Manager");
                            if (role == "Cashier") return RedirectToAction("Dashboard", "Cashier");
                        }
                    }
                }
            }

            ViewBag.Message = "Invalid username or password.";
            return View("LogIn");
        }

        // ⭐ 2. REGISTER NOW USES IANPC DATABASE ⭐
        [HttpPost]
        public IActionResult Register(string fullName, string username, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            {
                ViewBag.Message = "System Error: One or more fields were blank. Please check your form!";
                return View();
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string checkSql = "SELECT COUNT(*) FROM users WHERE username = @Username OR email = @Email";

                int exists = 0;
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@Username", username);
                    checkCmd.Parameters.AddWithValue("@Email", email);
                    exists = (int)checkCmd.ExecuteScalar();
                }

                if (exists > 0)
                {
                    ViewBag.Message = "An account with that Username or Email already exists.";
                    return View();
                }

                string insertSql = @"
             INSERT INTO users (full_name, username, email, password_hash, role_id, status, is_active)
             VALUES (@FullName, @Username, @Email, @Password, 
                    (SELECT TOP 1 role_id FROM roles WHERE role_name = 'Customer'), 
                    'active', 1)";

                using (SqlCommand cmd = new SqlCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", fullName);
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@Password", password);
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["SuccessMessage"] = "Account created successfully! Please log in.";
            return View("Views/Home/LogIn.cshtml");
        }

        // ⭐ 3. THE ASYNC LOGOUT FIX WITH AUDIT LOGGING ⭐
        public async Task<IActionResult> Logout()
        {
            // ⭐ LOG THE LOGOUT BEFORE WE DESTROY THE SESSION
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