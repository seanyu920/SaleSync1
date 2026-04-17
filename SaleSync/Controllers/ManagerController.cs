using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using System;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class ManagerController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

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
                // ⭐ FIXED: Now only counts 'Completed' sales in the Dashboard math
                string totalSql = @"SELECT ISNULL(SUM(total_amount), 0) as Total, COUNT(sale_id) as Count 
                    FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE) 
                    AND status = 'Completed'";

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

        // ==========================================
        // ⭐ UPGRADED: The Checkmark / Status Logic
        // ==========================================
        [HttpPost]
        public IActionResult UpdateSaleStatus([FromBody] StatusUpdateModel request)
        {
            if (!IsManager()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string currentStatus = "";
                using (SqlCommand checkCmd = new SqlCommand("SELECT status FROM sales WHERE sale_id = @id", conn))
                {
                    checkCmd.Parameters.AddWithValue("@id", request.SaleId);
                    var result = checkCmd.ExecuteScalar();
                    if (result != null) currentStatus = result.ToString();
                }

                // If it's already marked as Completed/Voided, do nothing.
                if (currentStatus == request.Status) return Ok();

                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string sql = "UPDATE sales SET status = @status WHERE sale_id = @id";
                        using (SqlCommand cmd = new SqlCommand(sql, conn, transaction))
                        {
                            cmd.Parameters.AddWithValue("@status", request.Status);
                            cmd.Parameters.AddWithValue("@id", request.SaleId);
                            cmd.ExecuteNonQuery();
                        }

                        // ⭐ THE MAGIC: If marking as 'Completed', deduct the ingredients NOW
                        if (request.Status == "Completed" && currentStatus != "Completed")
                        {
                            string getItemsSql = "SELECT product_id, quantity FROM sale_items WHERE sale_id = @id";
                            var itemsList = new List<(int pId, int qty)>();

                            using (SqlCommand getCmd = new SqlCommand(getItemsSql, conn, transaction))
                            {
                                getCmd.Parameters.AddWithValue("@id", request.SaleId);
                                using (SqlDataReader r = getCmd.ExecuteReader())
                                {
                                    while (r.Read()) itemsList.Add((Convert.ToInt32(r["product_id"]), Convert.ToInt32(r["quantity"])));
                                }
                            }

                            // Loop through the items and do the ingredient math
                            foreach (var item in itemsList)
                            {
                                DeductIngredients(conn, transaction, item.pId, item.qty);
                            }
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        // Protects database if stock falls below zero
                        transaction.Rollback();
                        return BadRequest(new { message = ex.Message });
                    }
                }
            }
            return Ok();
        }

        // ==========================================
        // ⭐ ADDED: The Math Helper Formula
        // ==========================================
        private void DeductIngredients(SqlConnection conn, SqlTransaction transaction, int productId, int qty)
        {
            string recipeQuery = @"
                SELECT ingredient_id, quantity_required
                FROM   product_ingredients
                WHERE  product_id = @product_id";

            using SqlCommand cmd = new SqlCommand(recipeQuery, conn, transaction);
            cmd.Parameters.AddWithValue("@product_id", productId);

            var ingredients = new List<(int id, double qtyReq)>();

            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    ingredients.Add((
                        Convert.ToInt32(reader["ingredient_id"]),
                        Convert.ToDouble(reader["quantity_required"])
                    ));
                }
            }

            foreach (var ing in ingredients)
            {
                double totalDeduct = ing.qtyReq * qty;
                string updateQuery = @"
                    UPDATE products
                    SET    stock_quantity = stock_quantity - @deduct
                    WHERE  product_id     = @ingredient_id
                      AND  stock_quantity >= @deduct";

                using SqlCommand updateCmd = new SqlCommand(updateQuery, conn, transaction);
                updateCmd.Parameters.AddWithValue("@deduct", totalDeduct);
                updateCmd.Parameters.AddWithValue("@ingredient_id", ing.id);

                int rows = updateCmd.ExecuteNonQuery();
                if (rows == 0)
                {
                    throw new Exception($"Stock for ingredient ID {ing.id} became insufficient.");
                }
            }
        }

        // Inside ManagerController.cs
        public IActionResult Analytics()
        {
            // This tells the Manager controller to go look in the Admin folder for the view
            return View("~/Views/Admin/Analytics.cshtml");
        }

        [HttpGet]
        public IActionResult Products()
        {
            if (HttpContext.Session.GetString("Role") != "Manager")
                return RedirectToAction("Index", "Home");

            var menuList = new List<SaleSync.Models.MenuItemModel>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT p.product_id, p.product_name, c.category_name, p.selling_price
                    FROM products p
                    LEFT JOIN categories c ON p.category_id = c.category_id
                    WHERE p.is_ingredient = 0 OR p.is_ingredient IS NULL";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            menuList.Add(new SaleSync.Models.MenuItemModel
                            {
                                ProductId = Convert.ToInt32(r["product_id"]),
                                ProductName = r["product_name"].ToString(),
                                CategoryName = r["category_name"]?.ToString() ?? "Uncategorized",
                                Price = r["selling_price"] != DBNull.Value ? Convert.ToDecimal(r["selling_price"]) : 0
                            });
                        }
                    }
                }
            }

            return View("~/Views/Admin/Products.cshtml", menuList);
        }

    } // End of ManagerController

    // ⭐ ADDED: Support class placed securely outside the controller braces
    public class StatusUpdateModel
    {
        public int SaleId { get; set; }
        public string Status { get; set; }
    }
}