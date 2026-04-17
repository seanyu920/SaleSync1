using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace SaleSync.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;

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

        public IActionResult AdminDashboard()
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index");
            }

            return View();
        }

        [HttpPost]
        public IActionResult Login(string username, string password)
        {
            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                    SELECT u.user_id, u.full_name, u.username, r.role_name
                    FROM users u
                    INNER JOIN roles r ON u.role_id = r.role_id
                    WHERE u.username = @Username
                      AND u.password_hash = @Password
                      AND u.status = 'active'";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Password", password);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string role = reader["role_name"].ToString();

                            HttpContext.Session.SetInt32("UserId", Convert.ToInt32(reader["user_id"])); // ← ADDED
                            HttpContext.Session.SetString("UserName", reader["username"].ToString());
                            HttpContext.Session.SetString("FullName", reader["full_name"].ToString());
                            HttpContext.Session.SetString("Role", role);

                            if (role == "Admin")
                                return RedirectToAction("Dashboard", "Admin");

                            if (role == "Manager")
                                return RedirectToAction("Dashboard", "Manager");

                            if (role == "Cashier")
                                return RedirectToAction("Dashboard", "Cashier");
                        }
                    }
                }
            }

            ViewBag.Message = "Invalid username or password.";
            return View("LogIn");
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View();
        }
    }
}