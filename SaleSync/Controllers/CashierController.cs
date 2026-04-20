using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SaleSync.Controllers
{
    public class CashierController : Controller
    {
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

        // DASHBOARD
        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || role != "Cashier")
                return RedirectToAction("Index", "Home");

            var model = new CashierDashboardViewModel { RecentSales = new List<SaleHistoryItem>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {

                string totalSql = @"SELECT ISNULL(SUM(total_amount), 0) as Total, COUNT(sale_id) as Count 
                    FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE) 
                    AND status = 'Completed'";


                string historySql = @"
    SELECT TOP 10 
        s.sale_id, s.sale_date, s.total_amount, s.status, u.username,
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
                            Status = r["status"]?.ToString() ?? "Pending" // <--- Add this line
                        });
                    }
                }
            }
            return View("CashierDashboard", model);
        }

        // MENU 
        public IActionResult CashierMenu()
        {
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || role != "Cashier")
                return RedirectToAction("Index", "Home");

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
        // CHECKOUT

        [HttpPost]
        public IActionResult Checkout([FromBody] CheckoutRequest request)
        {
            if (request?.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "No items were submitted." });

            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Unauthorized(new { message = "Session expired. Please log in again." });

            decimal totalAmount = request.Items.Sum(i => i.Quantity * i.Price);

            using SqlConnection conn = new SqlConnection(connectionString);
            conn.Open();
            SqlTransaction transaction = conn.BeginTransaction();

            try
            {
                var requiredDeductions = new Dictionary<int, double>();

                foreach (var item in request.Items)
                {
                    string recipeQuery = @"
                SELECT ingredient_id, quantity_required
                FROM   product_ingredients
                WHERE  product_id = @product_id";

                    using SqlCommand recipeCmd = new SqlCommand(recipeQuery, conn, transaction);
                    recipeCmd.Parameters.AddWithValue("@product_id", item.ProductId);

                    using SqlDataReader reader = recipeCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int ingId = Convert.ToInt32(reader["ingredient_id"]);
                        double qtyReq = Convert.ToDouble(reader["quantity_required"]);
                        double deduct = qtyReq * item.Quantity;

                        if (requiredDeductions.ContainsKey(ingId))
                            requiredDeductions[ingId] += deduct;
                        else
                            requiredDeductions[ingId] = deduct;
                    }
                }

                foreach (var kv in requiredDeductions)
                {
                    string stockCheckSql = @"
                SELECT stock_quantity
                FROM   products WITH (UPDLOCK, ROWLOCK)
                WHERE  product_id = @ingredient_id";

                    using SqlCommand checkCmd = new SqlCommand(stockCheckSql, conn, transaction);
                    checkCmd.Parameters.AddWithValue("@ingredient_id", kv.Key);

                    var result = checkCmd.ExecuteScalar();
                    if (result == null)
                    {
                        transaction.Rollback();
                        return NotFound(new { message = $"Ingredient ID {kv.Key} not found." });
                    }

                    double currentStock = Convert.ToDouble(result);
                    if (currentStock < kv.Value)
                    {
                        transaction.Rollback();
                        return Conflict(new
                        {
                            message = $"Insufficient stock for ingredient ID {kv.Key}. " +
                                      $"Available: {currentStock:F2}, Required: {kv.Value:F2}."
                        });
                    }
                }


                int saleId;
                string saleQuery = @"
    INSERT INTO sales
        (user_id, sale_date, total_amount, discount, tax,
         final_amount, payment_method, amount_paid, change_amount, status)
    VALUES
        (@user_id, GETDATE(), @total_amount, 0, 0,
         @total_amount, @payment_method, @amount_paid, @change_amount, 'Pending');
    SELECT SCOPE_IDENTITY();";

                using (SqlCommand cmd = new SqlCommand(saleQuery, conn, transaction))
                {
                    decimal amountPaid = request.AmountPaid > 0 ? request.AmountPaid : totalAmount;
                    decimal changeAmount = amountPaid - totalAmount;

                    cmd.Parameters.AddWithValue("@user_id", userId.Value);
                    cmd.Parameters.AddWithValue("@total_amount", totalAmount);
                    cmd.Parameters.AddWithValue("@payment_method", request.PaymentMethod ?? "cash");
                    cmd.Parameters.AddWithValue("@amount_paid", amountPaid);
                    cmd.Parameters.AddWithValue("@change_amount", changeAmount < 0 ? 0 : changeAmount);

                    saleId = Convert.ToInt32(cmd.ExecuteScalar());
                }

                foreach (var item in request.Items)
                {
                    string insertItem = @"
                INSERT INTO sale_items (sale_id, product_id, quantity, price, subtotal)
                VALUES (@sale_id, @product_id, @quantity, @price, @subtotal)";

                    using (SqlCommand cmd = new SqlCommand(insertItem, conn, transaction))
                    {
                        cmd.Parameters.AddWithValue("@sale_id", saleId);
                        cmd.Parameters.AddWithValue("@product_id", item.ProductId);
                        cmd.Parameters.AddWithValue("@quantity", item.Quantity);
                        cmd.Parameters.AddWithValue("@price", item.Price);
                        cmd.Parameters.AddWithValue("@subtotal", item.Quantity * item.Price);
                        cmd.ExecuteNonQuery();
                    }


                }

                transaction.Commit();
                return Ok(new { success = true, message = "Checkout complete." });
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                return BadRequest(new { message = ex.Message });
            }
        }
        // DEDUCTION LOGICV
        private void DeductIngredients(SqlConnection conn, SqlTransaction transaction,
                                       int productId, int qty)
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
        //UPDATESALESTATUS
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
        //VERIFY N VOID

        [HttpPost]
        public IActionResult VerifyAndVoid([FromBody] VoidRequestModel request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string checkAuth = @"
            SELECT r.role_name 
            FROM users u 
            JOIN roles r ON u.role_id = r.role_id 
            WHERE u.username = @user AND u.password_hash = @pass 
            AND (r.role_name = 'Admin' OR r.role_name = 'Manager')";

                using (SqlCommand cmd = new SqlCommand(checkAuth, conn))
                {
                    cmd.Parameters.AddWithValue("@user", request.AdminUser);
                    cmd.Parameters.AddWithValue("@pass", request.AdminPass);
                    var role = cmd.ExecuteScalar();

                    if (role == null)
                        return Unauthorized(new { message = "Invalid Admin/Manager credentials" });

 
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
        // ORDER DETAILS
        [HttpGet]
        public IActionResult GetOrderDetails(int saleId)
        {
            var itemsSold = new List<string>();
            var ingredientsDeducted = new List<string>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string itemsSql = @"
                    SELECT si.quantity, p.product_name 
                    FROM sale_items si 
                    JOIN products p ON si.product_id = p.product_id 
                    WHERE si.sale_id = @id";

                using (SqlCommand cmd = new SqlCommand(itemsSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            itemsSold.Add($"{r["quantity"]}x {r["product_name"]}");
                    }
                }

                string ingSql = @"
                    SELECT 
                        SUM(si.quantity * pi.quantity_required) as TotalQty,
                        ing.product_name,
                        ing.sku
                    FROM sale_items si
                    JOIN product_ingredients pi ON si.product_id = pi.product_id
                    JOIN products ing ON pi.ingredient_id = ing.product_id
                    WHERE si.sale_id = @id
                    GROUP BY ing.product_name, ing.sku";

                using (SqlCommand cmd = new SqlCommand(ingSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            ingredientsDeducted.Add($"{r["TotalQty"]} of {r["product_name"]} (ID: {r["sku"]})");
                    }
                }
            }

            return Json(new { items = itemsSold, ingredients = ingredientsDeducted });
        }


        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }
        public class VoidRequestModel
        {
            public int SaleId { get; set; }
            public string AdminUser { get; set; }
            public string AdminPass { get; set; }
        }
    }
}