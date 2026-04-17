using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using System;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private bool IsAdmin() => HttpContext.Session.GetString("Role") == "Admin";
        private bool CanAccessInventory() => IsAdmin() || HttpContext.Session.GetString("Role") == "Manager";

        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || (role != "Admin" && role != "Manager"))
                return RedirectToAction("Index", "Home");

            var model = new CashierDashboardViewModel { RecentSales = new List<SaleHistoryItem>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string totalSql = @"SELECT ISNULL(SUM(total_amount), 0) as Total, COUNT(sale_id) as Count 
                                    FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE)";

                string historySql = @"
                    SELECT TOP 10 s.sale_id, s.sale_date, s.total_amount, s.status, u.username,
                    (SELECT STRING_AGG(CAST(si.quantity AS VARCHAR) + 'x ' + p.product_name, ', ') 
                     FROM sale_items si JOIN products p ON si.product_id = p.product_id 
                     WHERE si.sale_id = s.sale_id) as ItemsSummary
                    FROM sales s JOIN users u ON s.user_id = u.user_id
                    ORDER BY s.sale_date DESC";

                conn.Open();
                using (SqlCommand cmd = new SqlCommand(totalSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        model.TodayTotalSales = Convert.ToDecimal(r["Total"]);
                        model.TodayTransactionCount = Convert.ToInt32(r["Count"]);
                    }
                }

                using (SqlCommand cmd = new SqlCommand(historySql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        model.RecentSales.Add(new SaleHistoryItem
                        {
                            SaleId = Convert.ToInt32(r["sale_id"]),
                            SaleDate = Convert.ToDateTime(r["sale_date"]),
                            TotalAmount = Convert.ToDecimal(r["total_amount"]),
                            CashierName = r["username"].ToString(),
                            ItemsSummary = r["ItemsSummary"]?.ToString() ?? "No items",
                            Status = r["status"]?.ToString() ?? "Pending"
                        });
                    }
                }
            }
            return View("AdminDashboard", model);
        }

        [HttpPost]
        public IActionResult UpdateSaleStatus([FromBody] StatusUpdateModel request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE sales SET status = @status WHERE sale_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@status", request.Status);
                    cmd.Parameters.AddWithValue("@id", request.SaleId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return Ok();
        }

        [HttpPost]
        public IActionResult VerifyAndVoid([FromBody] AdminVoidRequest request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string checkAuth = @"
            SELECT COUNT(*) 
            FROM users u 
            JOIN roles r ON u.role_id = r.role_id 
            WHERE u.password_hash = @pass 
            AND (r.role_name = 'Admin' OR r.role_name = 'Manager')";

                using (SqlCommand cmd = new SqlCommand(checkAuth, conn))
                {
                    cmd.Parameters.AddWithValue("@pass", request.Pass ?? "");
                    int isValid = Convert.ToInt32(cmd.ExecuteScalar());

                    if (isValid == 0)
                        return Unauthorized(new { message = "Invalid credentials. Void denied." });

                    string voidSql = "UPDATE sales SET status = 'Voided' WHERE sale_id = @id";
                    using (SqlCommand voidCmd = new SqlCommand(voidSql, conn))
                    {
                        voidCmd.Parameters.AddWithValue("@id", request.SaleId);
                        voidCmd.ExecuteNonQuery();
                    }
                }
            }
            return Ok();
        }

        public IActionResult Analytics() => View();
        public IActionResult Products() => IsAdmin() ? View() : RedirectToAction("Index", "Home");

        [HttpGet]
        public IActionResult Inventory()
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            var inventoryList = new List<InventoryItems>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
    SELECT p.product_id, p.product_name, p.stock_quantity, p.cost_price, p.sku, c.category_name 
    FROM products p
    LEFT JOIN categories c ON p.category_id = c.category_id
    WHERE p.is_ingredient = 1";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            inventoryList.Add(new InventoryItems
                            {
                                ProductId = Convert.ToInt32(r["product_id"]),
                                ItemID = r["sku"]?.ToString() ?? "N/A",
                                ItemName = r["product_name"].ToString(),
                                Quantity = Convert.ToInt32(r["stock_quantity"]),
                                PurchasePrice = Convert.ToDecimal(r["cost_price"]),
                                ItemCategory = r["category_name"]?.ToString() ?? "Raw Materials"
                            });
                        }
                    }
                }
            }
            return View(inventoryList);
        }

        // ⭐ ADDED: This connects your HTML Update button to the Database
        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems model)
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE products SET stock_quantity = @qty, cost_price = @price WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);
                    cmd.Parameters.AddWithValue("@id", model.ProductId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("Inventory");
        }

        [HttpGet]
        public IActionResult ManageAccounts()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var accountList = new List<UserAccount>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // Added u.password_hash to match your database and HTML table
                string sql = @"
            SELECT u.user_id, u.username, u.full_name, u.email, u.password_hash, r.role_name 
            FROM users u
            LEFT JOIN roles r ON u.role_id = r.role_id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            accountList.Add(new UserAccount
                            {
                                UserId = Convert.ToInt32(r["user_id"]),
                                FullName = r["full_name"]?.ToString() ?? "",
                                Username = r["username"]?.ToString() ?? "",
                                Email = r["email"]?.ToString() ?? "",
                                Password = r["password_hash"]?.ToString() ?? "", // <-- Passing it to the View
                                Role = r["role_name"]?.ToString() ?? "Unknown"
                            });
                        }
                    }
                }
            }
            return View(accountList);
        }
        // ⭐ ADDED: This catches the data when you click "Update Selected"
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            // Security check: Only Admins can update accounts
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // This updates the user details. 
                // The sub-query handles converting the text Role (like "Cashier") back into the correct role_id for your database!
                string sql = @"
                    UPDATE users 
                    SET full_name = @fullName, 
                        username = @username, 
                        email = @email, 
                        password_hash = @password,
                        role_id = ISNULL((SELECT TOP 1 role_id FROM roles WHERE role_name = @role), role_id)
                    WHERE user_id = @id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@fullName", model.FullName ?? "");
                    cmd.Parameters.AddWithValue("@username", model.Username ?? "");
                    cmd.Parameters.AddWithValue("@email", model.Email ?? "");
                    cmd.Parameters.AddWithValue("@password", model.Password ?? "");
                    cmd.Parameters.AddWithValue("@role", model.Role ?? "");
                    cmd.Parameters.AddWithValue("@id", model.UserId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            // Refreshes the page so you can instantly see your updates in the table
            return RedirectToAction("ManageAccounts");
        }
        // ⭐ ADDED: This catches the data when you click "+ Add Account"
        [HttpPost]
        public IActionResult AddAccount(UserAccount model)
        {
            // Security check
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id)
                    VALUES (@fullName, @username, @email, @password, 
                            ISNULL((SELECT TOP 1 role_id FROM roles WHERE role_name = @role), 2))";
                // Note: Defaults to role_id 2 (Cashier) if they forget to select a role

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@fullName", model.FullName ?? "");
                    cmd.Parameters.AddWithValue("@username", model.Username ?? "");
                    cmd.Parameters.AddWithValue("@email", model.Email ?? "");
                    cmd.Parameters.AddWithValue("@password", model.Password ?? "");
                    cmd.Parameters.AddWithValue("@role", model.Role ?? "Cashier");

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }
        // ⭐ ADDED: This catches the data when you click "Delete Selected"
        [HttpPost]
        public IActionResult DeleteAccount(int UserId)
        {
            // Security check
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            // Prevent the Admin from accidentally deleting themselves!
            var currentUserId = HttpContext.Session.GetInt32("UserId");
            if (currentUserId == UserId)
            {
                // Optionally return a warning, but for now, just cancel the delete
                return RedirectToAction("ManageAccounts");
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "DELETE FROM users WHERE user_id = @id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }

        public class AdminVoidRequest { public int SaleId { get; set; } public string Pass { get; set; } }
        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }
    }
}