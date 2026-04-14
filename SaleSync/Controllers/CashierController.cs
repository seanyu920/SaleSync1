using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;

namespace SaleSync.Controllers
{
    public class CashierController : Controller
    {
        private readonly string connectionString =
            "Server=IANPC;Database=SaleSync;Trusted_Connection=True;Encrypt=False;";

        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Cashier")
                return RedirectToAction("Index", "Home");

            return View("CashierDashboard");
        }


        [HttpPost]
        public IActionResult Checkout([FromBody] CheckoutRequest request)
        {
            if (request?.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "No items were submitted." });

            // Pull user_id from session (set during login)
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Unauthorized(new { message = "Session expired. Please log in again." });

            decimal totalAmount = request.Items.Sum(i => i.Quantity * i.Price);

            using SqlConnection conn = new SqlConnection(connectionString);
            conn.Open();
            SqlTransaction transaction = conn.BeginTransaction();

            try
            {
                // --- Pre-flight: aggregate & validate all ingredient stock ---
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

                // --- INSERT sales row with all required columns ---
                int saleId;
                string saleQuery = @"
            INSERT INTO sales
                (user_id, sale_date, total_amount, discount, tax,
                 final_amount, payment_method, amount_paid, change_amount)
            VALUES
                (@user_id, GETDATE(), @total_amount, 0, 0,
                 @total_amount, @payment_method, @amount_paid, @change_amount);
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

                // --- INSERT sale_items & deduct ingredients ---
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

                    DeductIngredients(conn, transaction, item.ProductId, item.Quantity);
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

        // -----------------------------------------------------------------------
        //  INGREDIENT DEDUCTION — called only AFTER stock validation has passed
        // -----------------------------------------------------------------------
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
            } // reader closed before UPDATE commands run

            foreach (var ing in ingredients)
            {
                double totalDeduct = ing.qtyReq * qty;

                // Double-safety: WHERE clause ensures stock never goes negative
                string updateQuery = @"
                    UPDATE products
                    SET    stock_quantity = stock_quantity - @deduct
                    WHERE  product_id     = @ingredient_id
                      AND  stock_quantity >= @deduct";

                using SqlCommand updateCmd = new SqlCommand(updateQuery, conn, transaction);
                updateCmd.Parameters.AddWithValue("@deduct", totalDeduct);
                updateCmd.Parameters.AddWithValue("@ingredient_id", ing.id);

                int rows = updateCmd.ExecuteNonQuery();

                // Concurrency fallback: another request snuck in between our check and update
                if (rows == 0)
                {
                    throw new Exception(
                        $"Stock for ingredient ID {ing.id} became insufficient during processing. " +
                        "The sale has been rolled back. Please retry."
                    );
                }
            }
        }
    }
}