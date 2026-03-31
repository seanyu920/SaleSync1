using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System.Diagnostics;

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
            string connectionString = "Server=IANPC;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
            SELECT user_id, full_name, username
            FROM users
            WHERE username = @Username
              AND password_hash = @Password
              AND status = 'active'";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", username);
                    cmd.Parameters.AddWithValue("@Password", password);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            HttpContext.Session.SetString("UserName", reader["username"].ToString());
                            HttpContext.Session.SetString("FullName", reader["full_name"].ToString());

                            return RedirectToAction("Dashboard", "Admin");
                        }
                    }
                }
            }

            ViewBag.Message = "Invalid email or password.";
            ViewBag.Success = false;
            return View("Index");
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }
    }
}