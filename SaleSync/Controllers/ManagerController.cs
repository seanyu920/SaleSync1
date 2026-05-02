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

        // ⭐ 1. THE SECURITY BOUNCER
        private bool IsManager()
        {
            return User.IsInRole("Manager") || User.IsInRole("Admin");
        }

        // ==========================================
        // ⭐ 2. MANAGER DASHBOARD DATA
        // ==========================================
        public IActionResult Dashboard()
        {
            if (!IsManager()) return RedirectToAction("Index", "Home");

            var model = new CashierDashboardViewModel { RecentSales = new List<SaleHistoryItem>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
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

        // ==========================================
        // ⭐ 3. FIXED: SECURE VOID ORDER LOGIC
        // ==========================================
        [HttpPost]
        public IActionResult VerifyAndVoid([FromBody] VoidRequest request)
        {
            if (!IsManager()) return Unauthorized(new { message = "Unauthorized access." });

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return BadRequest(new { message = "Session invalid." });

                if (userIdClaim == "0")
                {
                    var ghostPass = _configuration["SuperAdminConfig:Password"];
                    if (request.Pass != ghostPass) return BadRequest(new { message = "Incorrect password." });
                }
                else
                {
                    string checkPassSql = "SELECT password_hash FROM users WHERE user_id = @id";
                    using (SqlCommand cmd = new SqlCommand(checkPassSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", userIdClaim);
                        var dbPass = cmd.ExecuteScalar()?.ToString();
                        if (dbPass != request.Pass) return BadRequest(new { message = "Incorrect password." });
                    }
                }

                // FIX: Renamed SQL variable to @id to match the C# parameter
                string updateSql = "UPDATE sales SET status = 'Voided' WHERE sale_id = @id AND status != 'Completed'";
                using (SqlCommand cmd = new SqlCommand(updateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", request.SaleId);
                    int rows = cmd.ExecuteNonQuery();
                    if (rows == 0) return BadRequest(new { message = "Order not found or already completed/voided." });
                }
            }
            return Ok();
        }

        // ==========================================
        // ⭐ 4. FIXED: GET DROPDOWN ORDER DETAILS
        // ==========================================
        [HttpGet]
        public IActionResult GetOrderDetails(int saleId)
        {
            if (!IsManager()) return Unauthorized();

            var result = new { items = new List<string>(), ingredients = new List<string>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // FIX: Added cmd.Parameters to provide the @id value
                string itemSql = @"
                    SELECT si.quantity, p.product_name 
                    FROM sale_items si 
                    JOIN products p ON si.product_id = p.product_id 
                    WHERE si.sale_id = @id";

                using (SqlCommand cmd = new SqlCommand(itemSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read()) result.items.Add($"{r["quantity"]}x {r["product_name"]}");
                    }
                }

                // FIX: Added cmd.Parameters to provide the @id value
                string ingSql = @"
                    SELECT (pi.quantity_required * si.quantity) as total_req, p.product_name, ISNULL(p.recipe_unit, p.unit) as unit
                    FROM sale_items si
                    JOIN product_ingredients pi ON si.product_id = pi.product_id
                    JOIN products p ON pi.ingredient_id = p.product_id
                    WHERE si.sale_id = @id";

                using (SqlCommand cmd = new SqlCommand(ingSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read()) result.ingredients.Add($"{r["total_req"]} {r["unit"]} {r["product_name"]}");
                    }
                }
            }
            return Json(result);
        }

        // ==========================================
        // ⭐ 5. MARK ORDER COMPLETED & DEDUCT STOCK
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

                            foreach (var item in itemsList)
                            {
                                DeductIngredients(conn, transaction, item.pId, item.qty);
                            }
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        return BadRequest(new { message = ex.Message });
                    }
                }
            }
            return Ok();
        }

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

        // ==========================================
        // ⭐ 6. SHARED POS ACCESS
        // ==========================================
        [HttpGet]
        public IActionResult PointOfSale()
        {
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

            return View("~/Views/Cashier/CashierMenu.cshtml", menuList);
        }
    }

    // ==========================================
    // ⭐ REQUEST MODELS
    // ==========================================
    public class StatusUpdateModel
    {
        public int SaleId { get; set; }
        public string Status { get; set; }
    }

    public class VoidRequest
    {
        public int SaleId { get; set; }
        public string Pass { get; set; }
    }
}