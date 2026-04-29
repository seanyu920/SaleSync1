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
using Microsoft.AspNetCore.Authentication;

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
                var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, "SuperAdmin"),
            new Claim(ClaimTypes.NameIdentifier, "0"), // Identify as ID 0
            new Claim(ClaimTypes.Role, "Admin")
        };

                var claimsIdentity = new ClaimsIdentity(claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity));

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
                            // 1. Grab the raw role from the database and remove any hidden spaces
                            string rawRole = reader["role_name"].ToString().Trim();

                            // ⭐ THE FIX: Force it to Title Case (e.g. "admin" becomes "Admin", "CUSTOMER" becomes "Customer")
                            // This guarantees it perfectly matches your [Authorize] padlocks!
                            string role = char.ToUpper(rawRole[0]) + rawRole.Substring(1).ToLower();

                            string fullName = reader["full_name"].ToString();
                            string userId = reader["user_id"].ToString();

                            var claims = new List<Claim>
    {
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim(ClaimTypes.Name, fullName),
        new Claim(ClaimTypes.Role, role) // This is now perfectly formatted!
    };

                            // ⭐ THE FIX: We explicitly tell ASP.NET which claim represents the Name and the Role
                            var claimsIdentity = new ClaimsIdentity(
    claims,
    CookieAuthenticationDefaults.AuthenticationScheme,
    ClaimTypes.Name,
    ClaimTypes.Role    // <--- This is the golden ticket!
);

                            await HttpContext.SignInAsync(
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                new ClaimsPrincipal(claimsIdentity)
                            );

                            // We can confidently route based on the cleanly formatted role
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
            // ⭐ THE SHIELD: Prevent NULLs from reaching the database!
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            {
                ViewBag.Message = "System Error: One or more fields were blank. Please check your form!";
                return View();
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // 1. Check if the user already exists
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

                // 2. Insert the new Customer
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

        // ⭐ 3. THE ASYNC LOGOUT FIX ⭐
        public async Task<IActionResult> Logout()
        {
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