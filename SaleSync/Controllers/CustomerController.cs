using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using SaleSync.Models;
using System.Collections.Generic;
using System;
using System.Security.Claims;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        private readonly string connectionString = "Server=IANPC;Database=SaleSync;Trusted_Connection=True;Encrypt=False;";

        [HttpGet]
        public IActionResult CustomerOrdering()
        {
            var menuList = new List<MenuItemModel>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT p.product_id, p.product_name, c.category_name, p.selling_price
                    FROM products p
                    LEFT JOIN categories c ON p.category_id = c.category_id
                    WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
                    AND p.is_archived = 0
                    ORDER BY c.category_name, p.product_name";

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

        [HttpPost]
        public IActionResult PlaceOnlineOrder([FromBody] OnlineOrderRequest request)
        {
            if (request == null || request.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "Your cart is empty." });

            string customerName = User.Identity?.Name ?? "Online Guest";

            // ⭐ THE FIX: Grab the logged-in customer's ID
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        decimal totalAmount = 0;
                        foreach (var item in request.Items)
                        {
                            totalAmount += item.Quantity * item.Price;
                        }

                        // ⭐ THE FIX: Added user_id to the SQL columns and VALUES
                        string insertSale = @"
                            INSERT INTO sales (sale_date, total_amount, status, customer_name, order_type, payment_method, delivery_address, user_id) 
                            OUTPUT INSERTED.sale_id
                            VALUES (GETDATE(), @total, 'Pending', @custName, @orderType, @paymentMethod, @address, @userId)";

                        int newSaleId;
                        using (SqlCommand cmd = new SqlCommand(insertSale, conn, transaction))
                        {
                            cmd.Parameters.AddWithValue("@total", totalAmount);
                            cmd.Parameters.AddWithValue("@custName", customerName);
                            cmd.Parameters.AddWithValue("@orderType", request.OrderType ?? "Pick-up");
                            cmd.Parameters.AddWithValue("@paymentMethod", request.PaymentMethod ?? "Cash");

                            // Save the Delivery Address (handles null automatically)
                            cmd.Parameters.AddWithValue("@address", string.IsNullOrEmpty(request.DeliveryAddress) ? (object)DBNull.Value : request.DeliveryAddress);

                            // ⭐ THE FIX: Save the User ID to the database
                            cmd.Parameters.AddWithValue("@userId", currentUserId);

                            newSaleId = (int)cmd.ExecuteScalar();
                        }

                        string insertItem = @"
                            INSERT INTO sale_items (sale_id, product_id, quantity, price, subtotal) 
                            VALUES (@sid, @pid, @qty, @price, @sub)";

                        foreach (var item in request.Items)
                        {
                            using (SqlCommand cmd = new SqlCommand(insertItem, conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", newSaleId);
                                cmd.Parameters.AddWithValue("@pid", item.ProductId);
                                cmd.Parameters.AddWithValue("@qty", item.Quantity);
                                cmd.Parameters.AddWithValue("@price", item.Price);
                                cmd.Parameters.AddWithValue("@sub", item.Quantity * item.Price);
                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                        return Ok(new { message = "Order sent to the kitchen!", orderId = newSaleId });
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        return BadRequest(new { message = "Failed to process order: " + ex.Message });
                    }
                }
            }
        }
    }
}