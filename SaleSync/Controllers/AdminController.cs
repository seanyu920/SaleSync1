using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using static SaleSync.Models.MenuItemModel;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class AdminController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB; Database=SaleSync; Trusted_Connection=True; TrustServerCertificate=True;";

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // ==========================================
        // ⭐ THE ENTERPRISE LOGGER HELPER
        // ==========================================
        private void LogActivity(int userId, string action, string details, object oldData = null, object newData = null)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    INSERT INTO ActivityLogs (UserId, ActionType, Details, OldValues, NewValues) 
                    VALUES (@uid, @action, @details, @oldVal, @newVal)";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@uid", userId);
                    cmd.Parameters.AddWithValue("@action", action);
                    cmd.Parameters.AddWithValue("@details", details);
                    cmd.Parameters.AddWithValue("@oldVal", oldData != null ? JsonSerializer.Serialize(oldData) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@newVal", newData != null ? JsonSerializer.Serialize(newData) : DBNull.Value);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public IActionResult Dashboard()
        {
            var model = new CashierDashboardViewModel { RecentSales = new List<SaleHistoryItem>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string totalSql = @"SELECT ISNULL(SUM(total_amount), 0) as Total, COUNT(sale_id) as Count 
                    FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE) 
                    AND status = 'Completed'";

                // ⭐ UPDATE: Grabbing the new columns and changed to LEFT JOIN just in case an online order has no user_id
                string historySql = @"
                    SELECT TOP 10 s.sale_id, s.sale_date, s.total_amount, s.status, u.username,
                    s.customer_name, s.order_type, s.payment_method, s.delivery_address,
                    (SELECT STRING_AGG(CAST(si.quantity AS VARCHAR) + 'x ' + p.product_name, ', ') 
                     FROM sale_items si JOIN products p ON si.product_id = p.product_id 
                     WHERE si.sale_id = s.sale_id) as ItemsSummary
                    FROM sales s LEFT JOIN users u ON s.user_id = u.user_id
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
                            CashierName = r["username"]?.ToString() ?? "Online",
                            ItemsSummary = r["ItemsSummary"]?.ToString() ?? "No items",
                            Status = r["status"]?.ToString() ?? "Pending",

                            // ⭐ NEW: Mapping the online order data to your model
                            CustomerName = r["customer_name"]?.ToString(),
                            OrderType = r["order_type"]?.ToString(),
                            PaymentMethod = r["payment_method"]?.ToString(),
                            DeliveryAddress = r["delivery_address"]?.ToString()
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

        [HttpGet]
        public IActionResult GetOrderDetails(int saleId)
        {
            var result = new { items = new List<string>(), ingredients = new List<string>() };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
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

        [HttpPost]
        public IActionResult VerifyAndVoid([FromBody] AdminVoidRequest request)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

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

            LogActivity(currentUserId, "Order Voided", $"Voided Order #ORD-{request.SaleId}");
            return Ok();
        }

        public IActionResult Analytics() => View();
        [HttpGet]
        public IActionResult Analytics(string timeframe = "week")
        {
            var model = new AnalyticsViewModel { SelectedTimeframe = timeframe.ToLower() };
            string connectionString = "Server=(localdb)\\MSSQLLocalDB; Database=SaleSync; Trusted_Connection=True; TrustServerCertificate=True;";

            // Define T-SQL filtering criteria based on your exact column parameters
            string dateCondition = "CAST(s.sale_date AS DATE) = CAST(GETDATE() AS DATE)";
            string salesGroupQuery = "";

            switch (model.SelectedTimeframe)
            {
                case "today":
                    dateCondition = "CAST(s.sale_date AS DATE) = CAST(GETDATE() AS DATE)";
                    salesGroupQuery = $@"
                SELECT FORMAT(sale_date, 'hh:mm tt') AS Label, SUM(final_amount) AS Total 
                FROM dbo.sales s 
                WHERE {dateCondition} 
                GROUP BY FORMAT(sale_date, 'hh:mm tt'), DATEPART(hour, sale_date), DATEPART(minute, sale_date)
                ORDER BY DATEPART(hour, sale_date), DATEPART(minute, sale_date) ASC;";
                    break;

                case "month":
                    dateCondition = "s.sale_date >= DATEADD(month, -1, GETDATE())";
                    salesGroupQuery = $@"
                SELECT FORMAT(sale_date, 'MMM dd') AS Label, SUM(final_amount) AS Total 
                FROM dbo.sales s 
                WHERE {dateCondition} 
                GROUP BY FORMAT(sale_date, 'MMM dd'), CAST(sale_date AS DATE)
                ORDER BY CAST(sale_date AS DATE) ASC;";
                    break;

                case "year":
                    dateCondition = "s.sale_date >= DATEADD(year, -1, GETDATE())";
                    salesGroupQuery = $@"
                SELECT FORMAT(sale_date, 'MMM') AS Label, SUM(final_amount) AS Total 
                FROM dbo.sales s 
                WHERE {dateCondition} 
                GROUP BY FORMAT(sale_date, 'MMM'), MONTH(sale_date)
                ORDER BY MONTH(sale_date) ASC;";
                    break;

                case "week":
                default:
                    model.SelectedTimeframe = "week";
                    dateCondition = "s.sale_date >= DATEADD(day, -7, GETDATE())";
                    salesGroupQuery = $@"
                SELECT DATENAME(weekday, sale_date) AS Label, SUM(final_amount) AS Total 
                FROM dbo.sales s 
                WHERE {dateCondition} 
                GROUP BY DATENAME(weekday, sale_date), DATEPART(weekday, sale_date)
                ORDER BY DATEPART(weekday, sale_date) ASC;";
                    break;
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // 💳 1. Top-Level KPI Summary Card Metrics
                string summarySql = $@"
            SELECT 
                ISNULL(SUM(final_amount), 0) AS TotalRev, 
                COUNT(*) AS TotalTrans,
                CASE WHEN COUNT(*) = 0 THEN 0 ELSE ISNULL(SUM(final_amount), 0) / COUNT(*) END AS AOV
            FROM dbo.sales s
            WHERE {dateCondition};";

                using (SqlCommand cmd = new SqlCommand(summarySql, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        model.TotalRevenue = Convert.ToDecimal(reader["TotalRev"]);
                        model.TotalTransactions = Convert.ToInt32(reader["TotalTrans"]);
                        model.AverageOrderValue = Convert.ToDecimal(reader["AOV"]);
                    }
                }

                // 📊 2. Gross Revenue Trend Line/Bar Data
                using (SqlCommand cmd = new SqlCommand(salesGroupQuery, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        model.SalesLabels.Add(reader["Label"].ToString());
                        model.SalesValues.Add(Convert.ToDecimal(reader["Total"]));
                    }
                }

                // ⏰ 3. Peak Business Hours Traffic Trend
                string peakHoursSql = $@"
            SELECT 
                DATEPART(HOUR, s.sale_date) AS RawHour,
                FORMAT(s.sale_date, 'hh tt') AS HourLabel, 
                COUNT(*) AS TotalOrders
            FROM dbo.sales s
            WHERE {dateCondition}
            GROUP BY DATEPART(HOUR, s.sale_date), FORMAT(s.sale_date, 'hh tt')
            ORDER BY RawHour ASC;";

                using (SqlCommand cmd = new SqlCommand(peakHoursSql, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        model.PeakHourLabels.Add(reader["HourLabel"].ToString());
                        model.PeakHourOrderCounts.Add(Convert.ToInt32(reader["TotalOrders"]));
                    }
                }

                // ☕ 4. Best Sellers List (Linked via sale_items cross-reference)
                string bestSellersSql = $@"
            SELECT TOP 5 p.product_name, SUM(si.quantity) AS TotalQty 
            FROM dbo.sale_items si
            JOIN dbo.products p ON si.product_id = p.product_id
            JOIN dbo.sales s ON si.sale_id = s.sale_id
            WHERE {dateCondition}
            GROUP BY p.product_name
            ORDER BY TotalQty DESC;";

                using (SqlCommand cmd = new SqlCommand(bestSellersSql, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        model.BestSellers.Add(new BestSellerItem
                        {
                            ProductName = reader["product_name"].ToString(),
                            TotalSold = Convert.ToInt32(reader["TotalQty"])
                        });
                    }
                }

                // 💳 5. Payment Option Distribution Check
                string paymentSql = $@"
            SELECT s.payment_method, COUNT(*) AS DistributionCount 
            FROM dbo.sales s 
            WHERE {dateCondition}
            GROUP BY s.payment_method;";

                using (SqlCommand cmd = new SqlCommand(paymentSql, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string method = reader["payment_method"]?.ToString();
                        model.PaymentLabels.Add(string.IsNullOrEmpty(method) ? "Unspecified" : method);
                        model.PaymentValues.Add(Convert.ToInt32(reader["DistributionCount"]));
                    }
                }
            }

            return View(model);
        }

        [HttpGet]
        public IActionResult ActivityLog()
        {
            var logs = new List<ActivityLog>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT TOP 100 l.LogId, l.Timestamp, u.username, r.role_name, l.ActionType, l.Details, l.OldValues, l.NewValues
                    FROM ActivityLogs l
                    LEFT JOIN users u ON l.UserId = u.user_id
                    LEFT JOIN roles r ON u.role_id = r.role_id
                    ORDER BY l.Timestamp DESC";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            logs.Add(new ActivityLog
                            {
                                LogId = Convert.ToInt32(r["LogId"]),
                                Timestamp = Convert.ToDateTime(r["Timestamp"]),
                                Username = r["username"]?.ToString() ?? "System",
                                Role = r["role_name"]?.ToString() ?? "N/A",
                                ActionType = r["ActionType"].ToString(),
                                Details = r["Details"].ToString(),
                                OldValues = r["OldValues"]?.ToString(),
                                NewValues = r["NewValues"]?.ToString()
                            });
                        }
                    }
                }
            }
            return View(logs);
        }

        // ⭐ UPDATE: Added Archive Filter to Products
        [HttpGet]
        public IActionResult Products()
        {
            var menuList = new List<MenuItemModel>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT p.product_id, p.product_name, c.category_name, p.selling_price
                    FROM products p
                    LEFT JOIN categories c ON p.category_id = c.category_id
                    WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
                    AND p.is_archived = 0";

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
        public IActionResult AddMenuItem([FromBody] MenuItemModel model)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    INSERT INTO products (product_name, selling_price, is_ingredient, category_id, sku, barcode)
                    VALUES (@name, @price, 0, 
                            (SELECT TOP 1 category_id FROM categories WHERE category_name = @catName),
                            'PRD-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8),
                            'BC-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8))";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", model.ProductName);
                    cmd.Parameters.AddWithValue("@price", model.Price);
                    cmd.Parameters.AddWithValue("@catName", model.CategoryName);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return Ok();
        }

        [HttpGet]
        public IActionResult Inventory()
        {
            var inventoryList = new List<InventoryItems>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                      SELECT p.product_id, p.product_name, p.stock_quantity, p.cost_price, p.sku, c.category_name, p.unit, p.recipe_unit, p.conversion_factor 
                      FROM products p
                      LEFT JOIN categories c ON p.category_id = c.category_id
                      WHERE p.is_ingredient = 1
                      ORDER BY c.category_name ASC, p.product_name ASC";

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
                                Quantity = Convert.ToDouble(r["stock_quantity"]),
                                Unit = r["unit"]?.ToString() ?? "pcs",
                                PurchasePrice = Convert.ToDecimal(r["cost_price"]),
                                ItemCategory = r["category_name"]?.ToString() ?? "Raw Materials",
                                RecipeUnit = r["recipe_unit"]?.ToString(),
                                ConversionFactor = r["conversion_factor"] != DBNull.Value ? Convert.ToDouble(r["conversion_factor"]) : 1
                            });
                        }
                    }
                }
            }
            return View(inventoryList);
        }

        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems model)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                InventoryItems oldItem = null;
                string getOldSql = "SELECT product_name, stock_quantity, cost_price, unit, recipe_unit, conversion_factor FROM products WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(getOldSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", model.ProductId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            oldItem = new InventoryItems
                            {
                                ItemName = r["product_name"].ToString(),
                                Quantity = Convert.ToDouble(r["stock_quantity"]),
                                PurchasePrice = Convert.ToDecimal(r["cost_price"]),
                                Unit = r["unit"]?.ToString(),
                                RecipeUnit = r["recipe_unit"]?.ToString(),
                                ConversionFactor = r["conversion_factor"] != DBNull.Value ? Convert.ToDouble(r["conversion_factor"]) : 1
                            };
                        }
                    }
                }

                double currentStock = oldItem?.Quantity ?? 0;
                double addedQuantity = model.Quantity - currentStock;

                string sql = "UPDATE products SET stock_quantity = @qty, cost_price = @price, unit = @unit, recipe_unit = @runit, conversion_factor = @conv WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);
                    cmd.Parameters.AddWithValue("@unit", model.Unit ?? "pcs");
                    cmd.Parameters.AddWithValue("@runit", string.IsNullOrEmpty(model.RecipeUnit) ? (object)DBNull.Value : model.RecipeUnit);
                    cmd.Parameters.AddWithValue("@conv", model.ConversionFactor > 0 ? model.ConversionFactor : 1);
                    cmd.Parameters.AddWithValue("@id", model.ProductId);
                    cmd.ExecuteNonQuery();
                }

                if (addedQuantity > 0)
                {
                    decimal totalCost = (decimal)addedQuantity * model.PurchasePrice;
                    string logSql = "INSERT INTO inventory_purchases (item_name, quantity_bought, total_cost, purchase_date) VALUES ((SELECT product_name FROM products WHERE product_id = @id), @qty, @cost, GETDATE())";
                    using (SqlCommand logCmd = new SqlCommand(logSql, conn))
                    {
                        logCmd.Parameters.AddWithValue("@id", model.ProductId);
                        logCmd.Parameters.AddWithValue("@qty", addedQuantity);
                        logCmd.Parameters.AddWithValue("@cost", totalCost);
                        logCmd.ExecuteNonQuery();
                    }
                }

                LogActivity(currentUserId, "Inventory Updated", $"Updated stock/details for {oldItem?.ItemName ?? "Item"}", oldData: oldItem, newData: model);
            }
            return RedirectToAction("Inventory");
        }

        [HttpPost]
        public IActionResult AddInventory(InventoryItems model)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string itemName = model.ItemName ?? "New Ingredient";

                string checkSql = "SELECT COUNT(*) FROM products WHERE product_name = @name AND is_ingredient = 1";
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@name", itemName);
                    int exists = (int)checkCmd.ExecuteScalar();
                    if (exists > 0)
                    {
                        TempData["ErrorMessage"] = $"'{itemName}' is already in your inventory! Please use the 'Update Item' button instead.";
                        return RedirectToAction("Inventory");
                    }
                }

                string sql = @"
            INSERT INTO products (product_name, stock_quantity, cost_price, is_ingredient, category_id, sku, barcode, unit, recipe_unit, conversion_factor)
            VALUES (@name, @qty, @price, 1, 99, 
                    'ING-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8),
                    'BC-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8), @unit, @runit, @conv)";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", itemName);
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);
                    cmd.Parameters.AddWithValue("@unit", model.Unit ?? "pcs");
                    cmd.Parameters.AddWithValue("@runit", string.IsNullOrEmpty(model.RecipeUnit) ? (object)DBNull.Value : model.RecipeUnit);
                    cmd.Parameters.AddWithValue("@conv", model.ConversionFactor > 0 ? model.ConversionFactor : 1);
                    cmd.ExecuteNonQuery();
                }

                if (model.Quantity > 0)
                {
                    decimal totalCost = (decimal)model.Quantity * model.PurchasePrice;
                    string logSql = "INSERT INTO inventory_purchases (item_name, quantity_bought, total_cost, purchase_date) VALUES (@name, @qty, @cost, GETDATE())";
                    using (SqlCommand logCmd = new SqlCommand(logSql, conn))
                    {
                        logCmd.Parameters.AddWithValue("@name", itemName);
                        logCmd.Parameters.AddWithValue("@qty", model.Quantity);
                        logCmd.Parameters.AddWithValue("@cost", totalCost);
                        logCmd.ExecuteNonQuery();
                    }
                }

                LogActivity(currentUserId, "Inventory Created", $"Added new ingredient: {itemName}", newData: model);
            }
            TempData["SuccessMessage"] = $"{model.ItemName} added successfully!";
            return RedirectToAction("Inventory");
        }
        [HttpPost]
        public IActionResult ArchiveInventory([FromForm] int productId)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                try
                {
                    string itemName = "Unknown Ingredient";

                    // 1. Fetch the ingredient details first for your activity log snapshot
                    string getOldSql = @"
                SELECT product_name 
                FROM products 
                WHERE product_id = @id AND is_ingredient = 1";

                    using (SqlCommand checkCmd = new SqlCommand(getOldSql, conn))
                    {
                        checkCmd.Parameters.AddWithValue("@id", productId);
                        using (SqlDataReader r = checkCmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                itemName = r["product_name"].ToString();
                            }
                        }
                    }

                    // 2. Soft-delete the ingredient by flipping the archive switch
                    string archiveSql = "UPDATE products SET is_archived = 1 WHERE product_id = @id AND is_ingredient = 1";
                    using (SqlCommand cmd = new SqlCommand(archiveSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", productId);
                        cmd.ExecuteNonQuery();
                    }

                    // 3. Log the activity specifically as an inventory action
                    LogActivity(
                        userId: currentUserId,
                        action: "Inventory Archived",
                        details: $"Archived Ingredient: {itemName} (ID: {productId})",
                        newData: null
                    );

                    // 4. Pass the success message to trigger your view's SweetAlert
                    TempData["SuccessMessage"] = $"'{itemName}' has been archived successfully!";

                    return RedirectToAction("Inventory");
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = $"Failed to archive ingredient: {ex.Message}";
                    return RedirectToAction("Inventory");
                }
            }
        }
        [HttpPost]
        public IActionResult RestoreInventoryItem([FromBody] int productId)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                try
                {
                    string itemName = "Unknown Ingredient";

                    // 1. Fetch the ingredient details first for your activity tracking data trail
                    string getNameSql = "SELECT product_name FROM products WHERE product_id = @id AND is_ingredient = 1";
                    using (SqlCommand checkCmd = new SqlCommand(getNameSql, conn))
                    {
                        checkCmd.Parameters.AddWithValue("@id", productId);
                        using (SqlDataReader r = checkCmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                itemName = r["product_name"].ToString();
                            }
                        }
                    }

                    // 2. Bring the row layer back by flipping the archive flag off (0)
                    string restoreSql = "UPDATE products SET is_archived = 0 WHERE product_id = @id AND is_ingredient = 1";
                    using (SqlCommand cmd = new SqlCommand(restoreSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", productId);
                        cmd.ExecuteNonQuery();
                    }

                    // 3. Log the action into your system activity tracking table
                    LogActivity(
                        userId: currentUserId,
                        action: "Inventory Restored",
                        details: $"Restored Ingredient: {itemName} (ID: {productId})",
                        newData: null
                    );

                    // 4. Return standard HTTP 200 to let SweetAlert reload the view context
                    return Ok();
                }
                catch (Exception ex)
                {
                    // If the query crashes, pass an explicit JSON message back to the client console
                    return BadRequest(new { message = $"Failed to restore ingredient: {ex.Message}" });
                }
            }
        }

        // ⭐ UPDATE: Soft Delete (Archive) with Enterprise Logging Support
        [HttpPost]
        public IActionResult ArchiveProduct([FromBody] int productId) // 👈 Renamed from DeleteMenuItem
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                try
                {
                    string productName = "Unknown Product";
                    MenuItemModel oldProductData = null;

                    // 1. Fetch the product details first for the activity log
                    string getOldSql = @"                 SELECT p.product_name, c.category_name, p.selling_price                  FROM products p                 LEFT JOIN categories c ON p.category_id = c.category_id                 WHERE p.product_id = @id";

                    using (SqlCommand checkCmd = new SqlCommand(getOldSql, conn))
                    {
                        checkCmd.Parameters.AddWithValue("@id", productId);
                        using (SqlDataReader r = checkCmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                productName = r["product_name"].ToString();
                                oldProductData = new MenuItemModel
                                {
                                    ProductId = productId,
                                    ProductName = productName,
                                    CategoryName = r["category_name"]?.ToString() ?? "Uncategorized",
                                    Price = r["selling_price"] != DBNull.Value ? Convert.ToDecimal(r["selling_price"]) : 0
                                };
                            }
                        }
                    }

                    // 2. Flip the archive switch
                    string archiveSql = "UPDATE products SET is_archived = 1 WHERE product_id = @id";
                    using (SqlCommand cmd = new SqlCommand(archiveSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", productId);
                        cmd.ExecuteNonQuery();
                    }

                    // 3. Log the activity with the complete historical snapshot
                    LogActivity(
                        userId: currentUserId,
                        action: "Product Archived",
                        details: $"Archived Product: {productName} (ID: {productId})",
                        oldData: oldProductData
                    );

                    return Ok();
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = $"Failed to archive item: {ex.Message}" });
                }
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public IActionResult ManageAccounts()
        {
            var accountList = new List<UserAccount>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {

                string sql = @"
            SELECT u.user_id, u.username, u.full_name, u.email, u.password_hash, r.role_name, u.is_active 
            FROM users u
            LEFT JOIN roles r ON u.role_id = r.role_id
            WHERE u.user_id <> 0 
            AND (r.role_name IS NULL OR r.role_name != 'Customer')";

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
                                Password = r["password_hash"]?.ToString() ?? "",
                                Role = r["role_name"]?.ToString() ?? "Unknown",
                                IsActive = r["is_active"] != DBNull.Value ? Convert.ToBoolean(r["is_active"]) : true
                            });
                        }
                    }
                }
            }
            return View(accountList);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                UserAccount oldAccountData = null;
                string getOldSql = "SELECT full_name, username, email, role_id FROM users WHERE user_id = @id";
                using (SqlCommand cmd = new SqlCommand(getOldSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", model.UserId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            oldAccountData = new UserAccount
                            {
                                FullName = r["full_name"].ToString(),
                                Username = r["username"].ToString(),
                                Email = r["email"].ToString(),
                                Role = r["role_id"].ToString()
                            };
                        }
                    }
                }

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
                    cmd.ExecuteNonQuery();
                }

                LogActivity(currentUserId, "Account Edited", $"Updated profile for {model.Username}", oldData: oldAccountData, newData: model);
            }
            return RedirectToAction("ManageAccounts");
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult AddAccount(UserAccount model)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string checkSql = "SELECT COUNT(*) FROM users WHERE username = @user OR email = @email";
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@user", model.Username ?? "");
                    checkCmd.Parameters.AddWithValue("@email", model.Email ?? "");

                    int exists = (int)checkCmd.ExecuteScalar();
                    if (exists > 0)
                    {
                        TempData["ErrorMessage"] = "An account with that Username or Email already exists!";
                        return RedirectToAction("ManageAccounts");
                    }
                }

                string sql = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id)
                    VALUES (@fullName, @username, @email, @password, 
                            ISNULL((SELECT TOP 1 role_id FROM roles WHERE role_name = @role), 2))";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@fullName", model.FullName ?? "");
                    cmd.Parameters.AddWithValue("@username", model.Username ?? "");
                    cmd.Parameters.AddWithValue("@email", model.Email ?? "");
                    cmd.Parameters.AddWithValue("@password", model.Password ?? "");
                    cmd.Parameters.AddWithValue("@role", model.Role ?? "Cashier");
                    cmd.ExecuteNonQuery();
                }

                LogActivity(currentUserId, "Account Created", $"Created new account: {model.Username}", newData: model);
                TempData["SuccessMessage"] = "Account successfully created!";
            }
            return RedirectToAction("ManageAccounts");
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult DeactivateAccount(int UserId)
        {
            string currentUserIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int currentUserId = string.IsNullOrEmpty(currentUserIdStr) ? 0 : int.Parse(currentUserIdStr);

            if (currentUserId == UserId)
            {
                TempData["ErrorMessage"] = "Self-deactivation is not allowed for security reasons.";
                return RedirectToAction("ManageAccounts");
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string checkLastRoleSql = @"
            SELECT COUNT(*) 
            FROM users 
            WHERE role_id = (SELECT role_id FROM users WHERE user_id = @id) 
            AND is_active = 1";

                using (SqlCommand checkCmd = new SqlCommand(checkLastRoleSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@id", UserId);
                    int activeCount = (int)checkCmd.ExecuteScalar();

                    if (activeCount <= 1)
                    {
                        TempData["ErrorMessage"] = "Trap Activated: You cannot deactivate the last active member of this role.";
                        return RedirectToAction("ManageAccounts");
                    }
                }

                string sql = "UPDATE users SET is_active = 0 WHERE user_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);
                    cmd.ExecuteNonQuery();
                }

                LogActivity(currentUserId, "Account Deactivated", $"Deactivated User ID: {UserId}");
            }
            return RedirectToAction("ManageAccounts");
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult ReactivateAccount(int UserId)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE users SET is_active = 1 WHERE user_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }

                LogActivity(currentUserId, "Account Reactivated", $"Reactivated User ID: {UserId}");
            }
            return RedirectToAction("ManageAccounts");
        }

        [HttpGet]
        public IActionResult GetIngredients()
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "SELECT product_id, product_name, ISNULL(recipe_unit, unit) as display_unit FROM products WHERE is_ingredient = 1 ORDER BY product_name";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            list.Add(new { id = r["product_id"], name = r["product_name"], unit = r["display_unit"] });
                    }
                }
            }
            return Json(list);
        }

        [HttpGet]
        public IActionResult GetRecipe(int productId)
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT pi.ingredient_id, p.product_name, pi.quantity_required, ISNULL(p.recipe_unit, p.unit) as display_unit 
                    FROM product_ingredients pi
                    JOIN products p ON pi.ingredient_id = p.product_id
                    WHERE pi.product_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", productId);
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            list.Add(new { id = r["ingredient_id"], name = r["product_name"], qty = r["quantity_required"], unit = r["display_unit"] });
                    }
                }
            }
            return Json(list);
        }

        [HttpPost]
        public IActionResult SaveRecipe([FromBody] RecipeSaveRequest request)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var oldRecipe = new Dictionary<string, double>();
                string getOldSql = @"
                    SELECT p.product_name, pi.quantity_required 
                    FROM product_ingredients pi
                    JOIN products p ON pi.ingredient_id = p.product_id
                    WHERE pi.product_id = @id";

                using (SqlCommand oldCmd = new SqlCommand(getOldSql, conn))
                {
                    oldCmd.Parameters.AddWithValue("@id", request.ProductId);
                    using (SqlDataReader r = oldCmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            oldRecipe[r["product_name"].ToString()] = Convert.ToDouble(r["quantity_required"]);
                        }
                    }
                }

                string delSql = "DELETE FROM product_ingredients WHERE product_id = @id";
                using (SqlCommand delCmd = new SqlCommand(delSql, conn))
                {
                    delCmd.Parameters.AddWithValue("@id", request.ProductId);
                    delCmd.ExecuteNonQuery();
                }

                var newRecipe = new Dictionary<string, double>();

                if (request.Ingredients != null && request.Ingredients.Count > 0)
                {
                    string insSql = "INSERT INTO product_ingredients (product_id, ingredient_id, quantity_required) VALUES (@pid, @iid, @qty)";
                    string nameSql = "SELECT product_name FROM products WHERE product_id = @id";

                    foreach (var item in request.Ingredients)
                    {
                        using (SqlCommand insCmd = new SqlCommand(insSql, conn))
                        {
                            insCmd.Parameters.AddWithValue("@pid", request.ProductId);
                            insCmd.Parameters.AddWithValue("@iid", item.IngredientId);
                            insCmd.Parameters.AddWithValue("@qty", item.Quantity);
                            insCmd.ExecuteNonQuery();
                        }

                        string ingName = "Unknown Ingredient";
                        using (SqlCommand nameCmd = new SqlCommand(nameSql, conn))
                        {
                            nameCmd.Parameters.AddWithValue("@id", item.IngredientId);
                            var nameResult = nameCmd.ExecuteScalar();
                            if (nameResult != null) ingName = nameResult.ToString();
                        }

                        newRecipe[ingName] = Convert.ToDouble(item.Quantity);
                    }
                }

                string prodName = "Unknown Product";
                using (SqlCommand pCmd = new SqlCommand("SELECT product_name FROM products WHERE product_id=@id", conn))
                {
                    pCmd.Parameters.AddWithValue("@id", request.ProductId);
                    var pRes = pCmd.ExecuteScalar();
                    if (pRes != null) prodName = pRes.ToString();
                }

                LogActivity(currentUserId, "Recipe Edited", $"Changed recipe for {prodName}", oldData: oldRecipe, newData: newRecipe);
            }
            return Ok();
        }

        [HttpPost]
        public IActionResult AddFullProduct([FromBody] ComprehensiveItemModel model)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string fullName = model.ProductName + (string.IsNullOrEmpty(model.Size) ? "" : model.Size);

                        string insertProd = @"
                            INSERT INTO products (product_name, selling_price, is_ingredient, category_id, sku, barcode) 
                            OUTPUT INSERTED.product_id 
                            VALUES (@name, @price, 0, 
                                   (SELECT TOP 1 category_id FROM categories WHERE category_name = @cat), 
                                   'PRD-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8), 
                                   'BC-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8))";

                        int newProductId;
                        using (SqlCommand cmd = new SqlCommand(insertProd, conn, transaction))
                        {
                            cmd.Parameters.AddWithValue("@name", fullName);
                            cmd.Parameters.AddWithValue("@price", model.Price);
                            cmd.Parameters.AddWithValue("@cat", model.CategoryName);
                            newProductId = (int)cmd.ExecuteScalar();
                        }

                        if (model.Ingredients != null && model.Ingredients.Count > 0)
                        {
                            string insertIng = "INSERT INTO product_ingredients (product_id, ingredient_id, quantity_required) VALUES (@pid, @iid, @qty)";
                            foreach (var ing in model.Ingredients)
                            {
                                using (SqlCommand ingCmd = new SqlCommand(insertIng, conn, transaction))
                                {
                                    ingCmd.Parameters.AddWithValue("@pid", newProductId);
                                    ingCmd.Parameters.AddWithValue("@iid", ing.IngredientId);
                                    ingCmd.Parameters.AddWithValue("@qty", ing.Quantity);
                                    ingCmd.ExecuteNonQuery();
                                }
                            }
                        }

                        transaction.Commit();

                        LogActivity(currentUserId, "Product Created", $"Added new product: {fullName}", newData: model);
                        return Ok();
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        return BadRequest("Failed to save comprehensive item.");
                    }
                }
            }
        }

        private void DeductIngredients(SqlConnection conn, SqlTransaction transaction, int productId, int qty)
        {
            string recipeQuery = @"
                SELECT pi.ingredient_id, pi.quantity_required, ISNULL(p.conversion_factor, 1) as conversion_factor
                FROM product_ingredients pi
                JOIN products p ON pi.ingredient_id = p.product_id
                WHERE pi.product_id = @product_id";

            using SqlCommand cmd = new SqlCommand(recipeQuery, conn, transaction);
            cmd.Parameters.AddWithValue("@product_id", productId);

            var ingredients = new List<(int id, double qtyReq, double conv)>();

            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    ingredients.Add((
                        Convert.ToInt32(reader["ingredient_id"]),
                        Convert.ToDouble(reader["quantity_required"]),
                        Convert.ToDouble(reader["conversion_factor"])
                    ));
                }
            }

            foreach (var ing in ingredients)
            {
                double totalDeduct = (ing.qtyReq * qty) / ing.conv;

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

        [HttpGet]
        public IActionResult DailyReport()
        {
            var report = new DailyReportViewModel();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string salesSql = "SELECT ISNULL(SUM(total_amount), 0) as TotalSales, COUNT(sale_id) as TxCount FROM sales WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE) AND status = 'Completed'";
                using (SqlCommand cmd = new SqlCommand(salesSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read()) { report.TotalSales = Convert.ToDecimal(r["TotalSales"]); report.TransactionCount = Convert.ToInt32(r["TxCount"]); }
                }

                string costSql = "SELECT ISNULL(SUM(si.quantity * pi.quantity_required * ing.cost_price), 0) FROM sale_items si JOIN sales s ON si.sale_id = s.sale_id JOIN product_ingredients pi ON si.product_id = pi.product_id JOIN products ing ON pi.ingredient_id = ing.product_id WHERE CAST(s.sale_date AS DATE) = CAST(GETDATE() AS DATE) AND s.status = 'Completed'";
                using (SqlCommand cmd = new SqlCommand(costSql, conn)) report.TotalCost = Convert.ToDecimal(cmd.ExecuteScalar());

                string productsSql = "SELECT p.product_name, SUM(si.quantity) as QtySold, SUM(si.subtotal) as TotalRevenue FROM sale_items si JOIN sales s ON si.sale_id = s.sale_id JOIN products p ON si.product_id = p.product_id WHERE CAST(s.sale_date AS DATE) = CAST(GETDATE() AS DATE) AND s.status = 'Completed' GROUP BY p.product_name ORDER BY QtySold DESC";
                using (SqlCommand cmd = new SqlCommand(productsSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read()) report.ProductsSold.Add(new ProductSalesItem { ProductName = r["product_name"].ToString(), QuantitySold = Convert.ToInt32(r["QtySold"]), TotalRevenue = Convert.ToDecimal(r["TotalRevenue"]) });
                }

                string boughtSql = "SELECT item_name, SUM(quantity_bought) as QtyBought, SUM(total_cost) as TotalCost FROM inventory_purchases WHERE CAST(purchase_date AS DATE) = CAST(GETDATE() AS DATE) GROUP BY item_name ORDER BY TotalCost DESC";
                using (SqlCommand cmd = new SqlCommand(boughtSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read()) report.IngredientsBought.Add(new InventoryPurchaseItem { ItemName = r["item_name"].ToString(), QuantityBought = Convert.ToInt32(r["QtyBought"]), TotalCost = Convert.ToDecimal(r["TotalCost"]) });
                }

                string spendSql = "SELECT ISNULL(SUM(total_cost), 0) FROM inventory_purchases WHERE CAST(purchase_date AS DATE) = CAST(GETDATE() AS DATE)";
                using (SqlCommand cmd = new SqlCommand(spendSql, conn)) report.TotalInventorySpend = Convert.ToDecimal(cmd.ExecuteScalar());
            }
            return View(report);
        }

        // ⭐ UPDATE: Added Archive Filter to POS View
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
                    WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
                    AND p.is_archived = 0";

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

        public class UpdatePriceModel { public int ProductId { get; set; } public decimal NewPrice { get; set; } }

        [HttpPost]
        public IActionResult UpdateProductPrice([FromBody] UpdatePriceModel request)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var oldData = new Dictionary<string, object>();
                string getOldSql = "SELECT product_name, selling_price FROM products WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(getOldSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", request.ProductId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            oldData["ProductName"] = r["product_name"].ToString();
                            oldData["Price"] = Convert.ToDecimal(r["selling_price"]);
                        }
                    }
                }

                string sql = "UPDATE products SET selling_price = @price WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@price", request.NewPrice);
                    cmd.Parameters.AddWithValue("@id", request.ProductId);
                    cmd.ExecuteNonQuery();
                }

                var newData = new Dictionary<string, object> {
                    { "ProductName", oldData["ProductName"] },
                    { "Price", request.NewPrice }
                };
                LogActivity(currentUserId, "Product Edited", $"Updated price for {oldData["ProductName"]}", oldData, newData);
            }
            return Ok();
        }
        // ==========================================
        // ⭐ THE ARCHIVE ROOM
        // ==========================================
        [HttpGet]
        public IActionResult ArchivedProducts()
        {
            var viewModel = new ArchiveViewModel();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // 🛍️ Query 1: Fetch Archived Finished Products (Menu Items)
                string productSql = @"
            SELECT p.product_id, p.product_name, c.category_name, p.selling_price 
            FROM products p
            LEFT JOIN categories c ON p.category_id = c.category_id
            WHERE p.is_archived = 1 AND p.is_ingredient = 0";

                using (SqlCommand cmd = new SqlCommand(productSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        viewModel.ArchivedProducts.Add(new MenuItemModel
                        {
                            ProductId = Convert.ToInt32(r["product_id"]),
                            ProductName = r["product_name"].ToString(),
                            CategoryName = r["category_name"]?.ToString() ?? "Uncategorized",
                            Price = r["selling_price"] != DBNull.Value ? Convert.ToDecimal(r["selling_price"]) : 0
                        });
                    }
                }

                // 📦 Query 2: Fetch Archived Raw Ingredients (Inventory)
                string ingredientSql = @"
            SELECT product_id, product_name, stock_quantity, cost_price, unit, recipe_unit, conversion_factor 
            FROM products 
            WHERE is_archived = 1 AND is_ingredient = 1";

                using (SqlCommand cmd = new SqlCommand(ingredientSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        viewModel.ArchivedIngredients.Add(new InventoryItems
                        {
                            ProductId = Convert.ToInt32(r["product_id"]),
                            ItemName = r["product_name"].ToString(),
                            ItemCategory = "Ingredients", // Hardcoded display category since category_id = 99
                            Quantity = r["stock_quantity"] != DBNull.Value ? Convert.ToDouble(r["stock_quantity"]) : 0,
                            PurchasePrice = r["cost_price"] != DBNull.Value ? Convert.ToDecimal(r["cost_price"]) : 0,
                            Unit = r["unit"]?.ToString() ?? "pcs",
                            RecipeUnit = r["recipe_unit"]?.ToString(),
                            ConversionFactor = r["conversion_factor"] != DBNull.Value ? Convert.ToDouble(r["conversion_factor"]) : 1
                        });
                    }
                }
            }

            return View(viewModel);
        }

        [HttpPost]
        public IActionResult RestoreMenuItem([FromBody] int productId)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                try
                {
                    // Flip the switch back to 0 (Active)
                    string restoreSql = "UPDATE products SET is_archived = 0 WHERE product_id = @id";
                    using (SqlCommand cmd = new SqlCommand(restoreSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", productId);
                        cmd.ExecuteNonQuery();
                    }

                    LogActivity(currentUserId, "Product Restored", $"Restored Product ID: {productId} from Archive");
                    return Ok();
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = $"Failed to restore item: {ex.Message}" });
                }
            }
        }

        // ⭐ NEW: REAL-TIME NOTIFICATION ENDPOINT ⭐
        [HttpGet]
        public IActionResult CheckNewOrders()
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "SELECT COUNT(*) FROM sales WHERE status = 'Pending'";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    int count = (int)cmd.ExecuteScalar();
                    return Json(new { pendingCount = count });
                }
            }
        }
        [HttpGet]
        public IActionResult WebCustomization()
        {
            // Initialize a completely new instance directly to prevent database errors for now
            var settings = new WebCustomization();

            return View(settings);
        }

        public class AdminVoidRequest { public int SaleId { get; set; } public string Pass { get; set; } }
        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }

        [HttpGet]
        public IActionResult QueueOrder()
        {
            // This tells ASP.NET to look for a view named QueueOrder.cshtml
            return View();
        }

    }
}