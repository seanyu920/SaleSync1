using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private readonly IConfiguration _configuration;

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private bool IsAdmin()
        {
            var role = HttpContext.Session.GetString("Role");
            return role == "Admin";
        }

        // ================= DASHBOARD =================
        public IActionResult Dashboard()
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            return View("AdminDashboard");
        }

        // ================= INVENTORY (RESTORED) =================
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        [HttpGet]
        public IActionResult Inventory()
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            return View(inventoryItems);
        }

        [HttpPost]
        public IActionResult AddInventory(InventoryItems item)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            if (item != null)
            {
                inventoryItems.Add(item);
            }

            return RedirectToAction("Inventory");
        }

        // ================= MANAGE ACCOUNTS =================
        [HttpGet]
        public IActionResult ManageAccounts()
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            List<UserAccount> accounts = new List<UserAccount>();
            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                    SELECT 
                        u.user_id,
                        u.full_name,
                        u.username,
                        u.email,
                        u.password_hash,
                        u.status,
                        r.role_name
                    FROM users u
                    INNER JOIN roles r ON u.role_id = r.role_id
                    ORDER BY u.user_id";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        accounts.Add(new UserAccount
                        {
                            UserId = Convert.ToInt32(reader["user_id"]),
                            FullName = reader["full_name"]?.ToString() ?? "",
                            Username = reader["username"]?.ToString() ?? "",
                            Email = reader["email"]?.ToString() ?? "",
                            Password = reader["password_hash"]?.ToString() ?? "",
                            Role = reader["role_name"]?.ToString() ?? "",
                            Status = reader["status"]?.ToString() ?? ""
                        });
                    }
                }
            }

            return View(accounts);
        }

        // ================= SAVE ACCOUNT =================
        [HttpPost]
        public IActionResult SaveAccount(UserAccount model)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // CHECK DUPLICATE
                string checkQuery = "SELECT COUNT(*) FROM users WHERE email = @Email OR username = @Username";

                using (SqlCommand checkCmd = new SqlCommand(checkQuery, conn))
                {
                    checkCmd.Parameters.AddWithValue("@Email", model.Email);
                    checkCmd.Parameters.AddWithValue("@Username", model.Username);

                    int exists = (int)checkCmd.ExecuteScalar();

                    if (exists > 0)
                    {
                        TempData["Error"] = "Account already exists. Use Update instead.";
                        return RedirectToAction("ManageAccounts");
                    }
                }

                // GET ROLE ID
                string getRoleQuery = "SELECT role_id FROM roles WHERE role_name = @Role";
                int roleId;

                using (SqlCommand roleCmd = new SqlCommand(getRoleQuery, conn))
                {
                    roleCmd.Parameters.AddWithValue("@Role", model.Role);
                    object result = roleCmd.ExecuteScalar();

                    if (result == null)
                    {
                        TempData["Error"] = "Invalid role selected.";
                        return RedirectToAction("ManageAccounts");
                    }

                    roleId = Convert.ToInt32(result);
                }

                // INSERT
                string insertQuery = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id, status)
                    VALUES (@FullName, @Username, @Email, @Password, @RoleId, 'active')";

                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", model.FullName);
                    cmd.Parameters.AddWithValue("@Username", model.Username);
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    cmd.Parameters.AddWithValue("@Password", model.Password);
                    cmd.Parameters.AddWithValue("@RoleId", roleId);

                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account saved successfully.";
            return RedirectToAction("ManageAccounts");
        }

        // ================= UPDATE ACCOUNT =================
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string getRoleQuery = "SELECT role_id FROM roles WHERE role_name = @Role";
                int roleId;

                using (SqlCommand roleCmd = new SqlCommand(getRoleQuery, conn))
                {
                    roleCmd.Parameters.AddWithValue("@Role", model.Role);
                    object result = roleCmd.ExecuteScalar();

                    if (result == null)
                    {
                        TempData["Error"] = "Invalid role selected.";
                        return RedirectToAction("ManageAccounts");
                    }

                    roleId = Convert.ToInt32(result);
                }

                string updateQuery = @"
                    UPDATE users
                    SET full_name = @FullName,
                        username = @Username,
                        email = @Email,
                        password_hash = @Password,
                        role_id = @RoleId
                    WHERE user_id = @UserId";

                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", model.FullName);
                    cmd.Parameters.AddWithValue("@Username", model.Username);
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    cmd.Parameters.AddWithValue("@Password", model.Password);
                    cmd.Parameters.AddWithValue("@RoleId", roleId);
                    cmd.Parameters.AddWithValue("@UserId", model.UserId);

                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account updated successfully.";
            return RedirectToAction("ManageAccounts");
        }

        // ================= DELETE ACCOUNT =================
        [HttpPost]
        public IActionResult DeleteAccount(int userId)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string deleteQuery = "DELETE FROM users WHERE user_id = @UserId";

                using (SqlCommand cmd = new SqlCommand(deleteQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account deleted successfully.";
            return RedirectToAction("ManageAccounts");
        }
    }
}