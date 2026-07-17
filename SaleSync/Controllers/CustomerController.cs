using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using System.Collections.Generic;
using System;
using System.Security.Claims;
using System.Linq;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString;

        public CustomerController(IConfiguration configuration)
        {
            _configuration = configuration;
            connectionString = _configuration.GetConnectionString("DefaultConnection");
        }

        [HttpGet]
        public IActionResult CustomerOrdering()
        {
            string userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int currentUserId = string.IsNullOrEmpty(userIdClaim) ? 0 : int.Parse(userIdClaim);

            var dashboardData = new CustomerDashboardViewModel();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // 1. Fetch live menu data (ADDED p.image_path to SELECT)
                string menuSql = @"
            SELECT p.product_id, p.product_name, c.category_name, p.selling_price, p.image_path
            FROM products p
            LEFT JOIN categories c ON p.category_id = c.category_id
            WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
            AND p.is_archived = 0
            ORDER BY c.category_name, p.product_name";

                using (SqlCommand cmd = new SqlCommand(menuSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        dashboardData.MenuItems.Add(new MenuItemModel
                        {
                            ProductId = Convert.ToInt32(r["product_id"]),
                            ProductName = r["product_name"].ToString(),
                            CategoryName = r["category_name"]?.ToString() ?? "Uncategorized",
                            Price = r["selling_price"] != DBNull.Value ? Convert.ToDecimal(r["selling_price"]) : 0,

                            // ADDED: Map the image path column from the database reader
                            ImagePath = r["image_path"] != DBNull.Value ? r["image_path"].ToString() : null
                        });
                    }
                }

                // 2. Fetch live order tracking records for this customer
                if (currentUserId != 0)
                {
                    string orderSql = @"
                SELECT sale_id, sale_date, total_amount, status, order_type 
                FROM sales 
                WHERE user_id = @userId 
                ORDER BY sale_date DESC";

                    using (SqlCommand cmd = new SqlCommand(orderSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@userId", currentUserId);
                        using (SqlDataReader r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                dashboardData.OrderHistory.Add(new CustomerOrderModel
                                {
                                    SaleId = Convert.ToInt32(r["sale_id"]),
                                    SaleDate = Convert.ToDateTime(r["sale_date"]),
                                    TotalAmount = Convert.ToDecimal(r["total_amount"]),
                                    Status = r["status"]?.ToString() ?? "Unknown",
                                    OrderType = r["order_type"]?.ToString() ?? "Pick-up"
                                });
                            }
                        }
                    }
                }
            }

            return View(dashboardData);
        }

        [HttpPost]
        public IActionResult PlaceOnlineOrder([FromBody] OnlineOrderRequest request)
        {
            if (request == null || request.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "Your cart is empty." });

            string customerName = User.Identity?.Name ?? "Online Guest";
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

                        // Insert Sale Head Record
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
                            cmd.Parameters.AddWithValue("@address", string.IsNullOrEmpty(request.DeliveryAddress) ? (object)DBNull.Value : request.DeliveryAddress);
                            cmd.Parameters.AddWithValue("@userId", currentUserId);

                            newSaleId = (int)cmd.ExecuteScalar();
                        }

                        // Connected Customization: size and special_instructions added to columns
                        string insertItem = @"
                            INSERT INTO sale_items (sale_id, product_id, quantity, price, subtotal, size, special_instructions) 
                            VALUES (@sid, @pid, @qty, @price, @sub, @size, @instructions)";

                        foreach (var item in request.Items)
                        {
                            using (SqlCommand cmd = new SqlCommand(insertItem, conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", newSaleId);
                                cmd.Parameters.AddWithValue("@pid", item.ProductId);
                                cmd.Parameters.AddWithValue("@qty", item.Quantity);
                                cmd.Parameters.AddWithValue("@price", item.Price);
                                cmd.Parameters.AddWithValue("@sub", item.Quantity * item.Price);

                                // Direct custom bindings safely fall back to avoiding schema validation issues
                                cmd.Parameters.AddWithValue("@size", string.IsNullOrEmpty(item.Size) ? "Regular" : item.Size);
                                cmd.Parameters.AddWithValue("@instructions", string.IsNullOrEmpty(item.SpecialInstructions) ? (object)DBNull.Value : item.SpecialInstructions);

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

    // --- Data Transfer Objects supporting matching Frontend Property structures ---
    public class OnlineOrderRequest
    {
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public List<OnlineOrderItemRequest> Items { get; set; }
    }

    public class OnlineOrderItemRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Size { get; set; }
        public string SpecialInstructions { get; set; }
    }
}