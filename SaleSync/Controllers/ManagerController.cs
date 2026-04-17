using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using System;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    // ⭐ CRITICAL: This must say ManagerController, NOT AdminController
    public class ManagerController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=IANPC;Database=SaleSync;Trusted_Connection=True;Encrypt=False;";

        public ManagerController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // Authorization helper
        private bool IsManager()
        {
            var role = HttpContext.Session.GetString("Role");
            return role == "Manager" || role == "Admin";
        }

        public IActionResult Dashboard()
        {
            if (!IsManager()) return RedirectToAction("Index", "Home");

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
            // Ensure you have a ManagerDashboard.cshtml view, or route them to a shared one
            return View("ManagerDashboard", model);
        }

        [HttpGet]
        public IActionResult Inventory()
        {
            if (!IsManager()) return RedirectToAction("Index", "Home");

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
            return View("~/Views/Admin/Inventory.cshtml", inventoryList);
            // Note: I routed this to use your existing Admin Inventory view so you don't have to duplicate the HTML file!
        }

        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems model)
        {
            if (!IsManager()) return RedirectToAction("Index", "Home");

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
        [HttpPost]
        public IActionResult UpdateOrderStatus(int saleId, string status)
        {
            // 1. Security Check: Ensure only Managers/Admins can do this
            if (!IsManager()) return RedirectToAction("Index", "Home");

            // 2. Optional but recommended: Validate the status string
            if (status != "Completed" && status != "Void" && status != "Voided")
            {
                TempData["Error"] = "Invalid status update.";
                return RedirectToAction("Dashboard");
            }

            // 3. Update the database
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE sales SET status = @status WHERE sale_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@status", status);
                    cmd.Parameters.AddWithValue("@id", saleId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }

            // 4. Refresh the page to show the updated log
            return RedirectToAction("Dashboard");
        }

        public IActionResult Analytics() => View();
        public IActionResult Products() => View();
    }
}