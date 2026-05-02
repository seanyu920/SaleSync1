using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using SaleSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Cashier,Admin,Manager")]
    public class CashierController : Controller
    {
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

        // --- DASHBOARD ---
        public IActionResult Dashboard()
        {
            var model = new CashierDashboardViewModel { RecentSales = new List<SaleHistoryItem>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string totalSql = @"SELECT ISNULL(SUM(total_amount), 0) as Total, COUNT(sale_id) as Count 
                    FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE) 
                    AND status = 'Completed'";

                string historySql = @"
                    SELECT TOP 10 s.sale_id, s.sale_date, s.total_amount, s.status, u.username,
                    (SELECT STRING_AGG(CAST(si.quantity AS VARCHAR) + 'x ' + p.product_name, ', ') 
                     FROM sale_items si 
                     JOIN products p ON si.product_id = p.product_id 
                     WHERE si.sale_id = s.sale_id) as ItemsSummary
                    FROM sales s
                    JOIN users u ON s.user_id = u.user_id
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
            return View("CashierDashboard", model);
        }

        // --- MENU ---
        public IActionResult CashierMenu()
        {
            var menuList = new List<MenuItemModel>();

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
                            menuList.Add(new MenuItemModel
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
            return View(menuList);
        }

        // --- CHECKOUT (The Inventory Logic Engine) ---
        [HttpPost]
        public IActionResult Checkout([FromBody] CheckoutRequest request)
        {
            if (request?.Items == null || request.Items.Count == 0) return BadRequest(new { message = "No items were submitted." });

            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized(new { message = "Session expired." });

            int userId = int.Parse(userIdStr);
            decimal totalAmount = request.Items.Sum(i => i.Quantity * i.Price);

            using SqlConnection conn = new SqlConnection(connectionString);
            conn.Open();
            SqlTransaction transaction = conn.BeginTransaction();

            try
            {
                // Structure: AmountToDeduct, RecipeDisplayAmount, IngredientName, InventoryUnit, RecipeUnit
                var requiredDeductions = new Dictionary<int, (double GallonAmount, double RawRecipeAmount, string Name, string InvUnit, string RecUnit)>();

                // 1. Pre-Check Inventory & Gather Recipes
                foreach (var item in request.Items)
                {
                    string recipeQuery = @"
                        SELECT pi.ingredient_id, pi.quantity_required, ISNULL(pi.conversion_factor, 1) as conversion_factor,
                               pi.unit_of_measure as recipe_unit, ing.product_name, ing.unit as inv_unit
                        FROM product_ingredients pi
                        JOIN products ing ON pi.ingredient_id = ing.product_id
                        WHERE pi.product_id = @product_id";

                    using SqlCommand recipeCmd = new SqlCommand(recipeQuery, conn, transaction);
                    recipeCmd.Parameters.AddWithValue("@product_id", item.ProductId);

                    using SqlDataReader reader = recipeCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int ingId = Convert.ToInt32(reader["ingredient_id"]);
                        double recipeQty = Convert.ToDouble(reader["quantity_required"]);
                        double convFactor = Convert.ToDouble(reader["conversion_factor"]);

                        double rawTotal = recipeQty * item.Quantity; // e.g. 350ml
                        double gallonTotal = rawTotal / convFactor; // e.g. 0.09 gallons

                        if (requiredDeductions.ContainsKey(ingId))
                        {
                            var existing = requiredDeductions[ingId];
                            requiredDeductions[ingId] = (existing.GallonAmount + gallonTotal, existing.RawRecipeAmount + rawTotal, existing.Name, existing.InvUnit, existing.RecUnit);
                        }
                        else
                        {
                            requiredDeductions[ingId] = (gallonTotal, rawTotal, reader["product_name"].ToString(), reader["inv_unit"].ToString(), reader["recipe_unit"]?.ToString() ?? "");
                        }
                    }
                }

                // 2. Validate Stock Levels
                foreach (var kv in requiredDeductions)
                {
                    string stockCheckSql = "SELECT stock_quantity FROM products WITH (UPDLOCK, ROWLOCK) WHERE product_id = @ingredient_id";
                    using SqlCommand checkCmd = new SqlCommand(stockCheckSql, conn, transaction);
                    checkCmd.Parameters.AddWithValue("@ingredient_id", kv.Key);
                    var currentStock = Convert.ToDouble(checkCmd.ExecuteScalar() ?? 0);

                    if (currentStock < kv.Value.GallonAmount)
                    {
                        transaction.Rollback();
                        return Conflict(new { message = $"Insufficient stock for {kv.Value.Name}. Available: {currentStock:F2} {kv.Value.InvUnit}, Required: {kv.Value.GallonAmount:F4} {kv.Value.InvUnit}." });
                    }
                }

                // 3. Insert Sale Record
                int saleId;
                string saleQuery = @"
                    INSERT INTO sales (user_id, sale_date, total_amount, discount, tax, final_amount, payment_method, amount_paid, change_amount, status)
                    VALUES (@user_id, GETDATE(), @total_amount, 0, 0, @total_amount, @payment_method, @amount_paid, @change_amount, 'Pending');
                    SELECT SCOPE_IDENTITY();";

                using (SqlCommand cmd = new SqlCommand(saleQuery, conn, transaction))
                {
                    decimal amountPaid = request.AmountPaid > 0 ? request.AmountPaid : totalAmount;
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@total_amount", totalAmount);
                    cmd.Parameters.AddWithValue("@payment_method", request.PaymentMethod ?? "cash");
                    cmd.Parameters.AddWithValue("@amount_paid", amountPaid);
                    cmd.Parameters.AddWithValue("@change_amount", (amountPaid - totalAmount) < 0 ? 0 : (amountPaid - totalAmount));
                    saleId = Convert.ToInt32(cmd.ExecuteScalar());
                }

                // 4. Save Items & Deduct Stock
                foreach (var item in request.Items)
                {
                    string insertItem = "INSERT INTO sale_items (sale_id, product_id, quantity, price, subtotal) VALUES (@sale_id, @product_id, @quantity, @price, @subtotal)";
                    using (SqlCommand cmd = new SqlCommand(insertItem, conn, transaction))
                    {
                        cmd.Parameters.AddWithValue("@sale_id", saleId);
                        cmd.Parameters.AddWithValue("@product_id", item.ProductId);
                        cmd.Parameters.AddWithValue("@quantity", item.Quantity);
                        cmd.Parameters.AddWithValue("@price", item.Price);
                        cmd.Parameters.AddWithValue("@subtotal", item.Quantity * item.Price);
                        cmd.ExecuteNonQuery();
                    }
                    DeductIngredients(conn, transaction, item.ProductId, item.Quantity);
                }

                transaction.Commit();

                // ⭐ POPUP SUMMARY: Shows raw ml/g to the user, but database is already updated with gallons!
                var deductionSummary = requiredDeductions.Select(kv => $"- {kv.Value.RawRecipeAmount:F0} {kv.Value.RecUnit} {kv.Value.Name}").ToList();

                return Ok(new { success = true, message = "Checkout complete.", deductions = deductionSummary });
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                return BadRequest(new { message = ex.Message });
            }
        }

        private void DeductIngredients(SqlConnection conn, SqlTransaction transaction, int productId, int qty)
        {
            string recipeQuery = "SELECT ingredient_id, quantity_required, ISNULL(conversion_factor, 1) as conv FROM product_ingredients WHERE product_id = @p_id";
            var ingredients = new List<(int id, double req, double conv)>();

            using (SqlCommand cmd = new SqlCommand(recipeQuery, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@p_id", productId);
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read()) ingredients.Add((Convert.ToInt32(r["ingredient_id"]), Convert.ToDouble(r["quantity_required"]), Convert.ToDouble(r["conv"])));
                }
            }

            foreach (var ing in ingredients)
            {
                string updateSql = "UPDATE products SET stock_quantity = stock_quantity - ((@qty * @req) / @conv) WHERE product_id = @id";
                using (SqlCommand upCmd = new SqlCommand(updateSql, conn, transaction))
                {
                    upCmd.Parameters.AddWithValue("@qty", qty);
                    upCmd.Parameters.AddWithValue("@req", ing.req);
                    upCmd.Parameters.AddWithValue("@conv", ing.conv);
                    upCmd.Parameters.AddWithValue("@id", ing.id);
                    upCmd.ExecuteNonQuery();
                }
            }
        }

        // --- STATUS UPDATES & VOIDING ---
        [HttpPost]
        public IActionResult UpdateSaleStatus([FromBody] StatusUpdateModel request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string currentStatus = "";
                using (SqlCommand checkCmd = new SqlCommand("SELECT status FROM sales WHERE sale_id = @id", conn))
                {
                    checkCmd.Parameters.AddWithValue("@id", request.SaleId);
                    currentStatus = checkCmd.ExecuteScalar()?.ToString() ?? "";
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
                        transaction.Commit();
                    }
                    catch { transaction.Rollback(); throw; }
                }
            }
            return Ok();
        }

        [HttpPost]
        public IActionResult VerifyAndVoid([FromBody] VoidRequestModel request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string checkAuth = @"SELECT r.role_name FROM users u JOIN roles r ON u.role_id = r.role_id 
                                    WHERE u.username = @user AND u.password_hash = @pass AND (r.role_name = 'Admin' OR r.role_name = 'Manager')";

                using (SqlCommand cmd = new SqlCommand(checkAuth, conn))
                {
                    cmd.Parameters.AddWithValue("@user", request.AdminUser);
                    cmd.Parameters.AddWithValue("@pass", request.AdminPass);
                    if (cmd.ExecuteScalar() == null) return Unauthorized(new { message = "Invalid Admin credentials" });

                    using (SqlCommand vCmd = new SqlCommand("UPDATE sales SET status = 'Voided' WHERE sale_id = @id", conn))
                    {
                        vCmd.Parameters.AddWithValue("@id", request.SaleId);
                        vCmd.ExecuteNonQuery();
                    }
                }
            }
            return Ok();
        }

        [HttpGet]
        public IActionResult GetOrderDetails(int saleId)
        {
            var itemsSold = new List<string>();
            var ingredientsDeducted = new List<string>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string itemsSql = "SELECT si.quantity, p.product_name FROM sale_items si JOIN products p ON si.product_id = p.product_id WHERE si.sale_id = @id";
                using (SqlCommand cmd = new SqlCommand(itemsSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader()) { while (r.Read()) itemsSold.Add($"{r["quantity"]}x {r["product_name"]}"); }
                }

                string ingSql = @"SELECT SUM((si.quantity * pi.quantity_required) / ISNULL(pi.conversion_factor, 1)) as Total, ing.product_name, ing.unit 
                                  FROM sale_items si JOIN product_ingredients pi ON si.product_id = pi.product_id 
                                  JOIN products ing ON pi.ingredient_id = ing.product_id WHERE si.sale_id = @id GROUP BY ing.product_name, ing.unit";
                using (SqlCommand cmd = new SqlCommand(ingSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader()) { while (r.Read()) ingredientsDeducted.Add($"{r["Total"]:F2} {r["unit"]} {r["product_name"]}"); }
                }
            }
            return Json(new { items = itemsSold, ingredients = ingredientsDeducted });
        }

        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }
        public class VoidRequestModel { public int SaleId { get; set; } public string AdminUser { get; set; } public string AdminPass { get; set; } }
    }
}