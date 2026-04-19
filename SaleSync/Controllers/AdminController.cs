using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using static SaleSync.Models.MenuItemModel;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly string connectionString = "Server=IANPC;Database=SaleSync;Trusted_Connection=True;Encrypt=False;";

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private bool IsAdmin() => HttpContext.Session.GetString("Role") == "Admin";
        private bool CanAccessInventory() => IsAdmin() || HttpContext.Session.GetString("Role") == "Manager";

        // Dashboard
        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || (role != "Admin" && role != "Manager"))
                return RedirectToAction("Index", "Home");

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
            return View("AdminDashboard", model);
        }

        // SALES UPDATE
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

        // ORDER DETAILS
        [HttpGet]
        public IActionResult GetOrderDetails(int saleId)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "Admin" && role != "Manager") return Unauthorized();

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
                        while (r.Read()) itemsSold.Add($"{r["quantity"]}x {r["product_name"]}");
                    }
                }

                string ingSql = @"
                    SELECT SUM(si.quantity * pi.quantity_required) as TotalQty, ing.product_name, ing.sku, ing.unit
                    FROM sale_items si
                    JOIN product_ingredients pi ON si.product_id = pi.product_id
                    JOIN products ing ON pi.ingredient_id = ing.product_id
                    WHERE si.sale_id = @id
                    GROUP BY ing.product_name, ing.sku, ing.unit";

                using (SqlCommand cmd = new SqlCommand(ingSql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", saleId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read()) ingredientsDeducted.Add($"{r["TotalQty"]}{r["unit"]} of {r["product_name"]} (ID: {r["sku"]})");
                    }
                }
            }

            return Json(new { items = itemsSold, ingredients = ingredientsDeducted });
        }

        // VOID FUNCTION
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

        // PRODUCTS MANAGEMENT
        [HttpGet]
        public IActionResult Products()
        {
            if (!IsAdmin() && HttpContext.Session.GetString("Role") != "Manager")
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

        // ADD MENU ITEM 
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

        // ⭐ INVENTORY MANAGEMENT (Updated with Unit)
        [HttpGet]
        public IActionResult Inventory()
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            var inventoryList = new List<InventoryItems>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                      SELECT p.product_id, p.product_name, p.stock_quantity, p.cost_price, p.sku, c.category_name, p.unit 
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
                                Quantity = Convert.ToInt32(r["stock_quantity"]),
                                Unit = r["unit"]?.ToString() ?? "pcs", // ⭐ Catch the unit from the DB!
                                PurchasePrice = Convert.ToDecimal(r["cost_price"]),
                                ItemCategory = r["category_name"]?.ToString() ?? "Raw Materials"
                            });
                        }
                    }
                }
            }
            return View(inventoryList);
        }

        // ⭐ UPDATE INVENTORY (Updated with Unit)
        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems model)
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // Figure out exactly how much NEW stock was added
                int currentStock = 0;
                string checkSql = "SELECT stock_quantity FROM products WHERE product_id = @id";
                using (SqlCommand checkCmd = new SqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@id", model.ProductId);
                    var result = checkCmd.ExecuteScalar();
                    if (result != DBNull.Value) currentStock = Convert.ToInt32(result);
                }

                int addedQuantity = model.Quantity - currentStock;

                // ⭐ Update the main database (Now includes the Unit update!)
                string sql = "UPDATE products SET stock_quantity = @qty, cost_price = @price, unit = @unit WHERE product_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);
                    cmd.Parameters.AddWithValue("@unit", model.Unit ?? "pcs"); // ⭐
                    cmd.Parameters.AddWithValue("@id", model.ProductId);
                    cmd.ExecuteNonQuery();
                }

                // If we ADDED stock, log the purchase!
                if (addedQuantity > 0)
                {
                    decimal totalCost = addedQuantity * model.PurchasePrice;
                    string logSql = "INSERT INTO inventory_purchases (item_name, quantity_bought, total_cost, purchase_date) VALUES ((SELECT product_name FROM products WHERE product_id = @id), @qty, @cost, GETDATE())";
                    using (SqlCommand logCmd = new SqlCommand(logSql, conn))
                    {
                        logCmd.Parameters.AddWithValue("@id", model.ProductId);
                        logCmd.Parameters.AddWithValue("@qty", addedQuantity);
                        logCmd.Parameters.AddWithValue("@cost", totalCost);
                        logCmd.ExecuteNonQuery();
                    }
                }
            }
            return RedirectToAction("Inventory");
        }

        // ⭐ ADD INVENTORY (Updated with Unit)
        [HttpPost]
        public IActionResult AddInventory(InventoryItems model)
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string itemName = model.ItemName ?? "New Ingredient";

                // ⭐ Added Unit to the Insert Statement
                string sql = @"
                    INSERT INTO products (product_name, stock_quantity, cost_price, is_ingredient, category_id, sku, barcode, unit)
                    VALUES (@name, @qty, @price, 1, 99, 
                            'ING-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8),
                            'BC-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8), @unit)";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", itemName);
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);
                    cmd.Parameters.AddWithValue("@unit", model.Unit ?? "pcs"); // ⭐
                    cmd.ExecuteNonQuery();
                }

                // LOG THE PURCHASE!
                if (model.Quantity > 0)
                {
                    decimal totalCost = model.Quantity * model.PurchasePrice;
                    string logSql = "INSERT INTO inventory_purchases (item_name, quantity_bought, total_cost, purchase_date) VALUES (@name, @qty, @cost, GETDATE())";
                    using (SqlCommand logCmd = new SqlCommand(logSql, conn))
                    {
                        logCmd.Parameters.AddWithValue("@name", itemName);
                        logCmd.Parameters.AddWithValue("@qty", model.Quantity);
                        logCmd.Parameters.AddWithValue("@cost", totalCost);
                        logCmd.ExecuteNonQuery();
                    }
                }
            }
            return RedirectToAction("Inventory");
        }

        // DELETE INVENTORY
        [HttpPost]
        public IActionResult DeleteInventory(int ProductId)
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string deleteFromRecipes = "DELETE FROM product_ingredients WHERE ingredient_id = @id";
                        using (SqlCommand cmdRecipe = new SqlCommand(deleteFromRecipes, conn, transaction))
                        {
                            cmdRecipe.Parameters.AddWithValue("@id", ProductId);
                            cmdRecipe.ExecuteNonQuery();
                        }

                        string deleteProduct = "DELETE FROM products WHERE product_id = @id";
                        using (SqlCommand cmdProduct = new SqlCommand(deleteProduct, conn, transaction))
                        {
                            cmdProduct.Parameters.AddWithValue("@id", ProductId);
                            cmdProduct.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                    catch (SqlException)
                    {
                        transaction.Rollback();
                        TempData["ErrorMessage"] = "Cannot delete item: It is already linked to past sales records.";
                    }
                }
            }
            return RedirectToAction("Inventory");
        }

        // ACCOUNT MANAGEMENT
        [HttpGet]
        public IActionResult ManageAccounts()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var accountList = new List<UserAccount>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {

                string sql = @"
            SELECT u.user_id, u.username, u.full_name, u.email, u.password_hash, r.role_name, u.is_active 
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

        // UPDATE ACCOUNT
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
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
            return RedirectToAction("ManageAccounts");
        }

        // ADD ACCOUNT
        [HttpPost]
        public IActionResult AddAccount(UserAccount model)
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

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

                TempData["SuccessMessage"] = "Account successfully created!";
            }
            return RedirectToAction("ManageAccounts");
        }

        // DEACTIVATE ACCOUNT
        [HttpPost]
        public IActionResult DeactivateAccount(int UserId)
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var currentUserId = HttpContext.Session.GetInt32("UserId");
            if (currentUserId == UserId) return RedirectToAction("ManageAccounts");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE users SET is_active = 0 WHERE user_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }

        // Reactivate Account
        [HttpPost]
        public IActionResult ReactivateAccount(int UserId)
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE users SET is_active = 1 WHERE user_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }

        [HttpGet]
        public IActionResult GetIngredients()
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "SELECT product_id, product_name, unit FROM products WHERE is_ingredient = 1 ORDER BY product_name";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            list.Add(new { id = r["product_id"], name = r["product_name"], unit = r["unit"] });
                    }
                }
            }
            return Json(list);
        }

        // GET RECIPE DETAILS FOR A PRODUCT
        [HttpGet]
        public IActionResult GetRecipe(int productId)
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT pi.ingredient_id, p.product_name, pi.quantity_required, p.unit 
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
                            list.Add(new { id = r["ingredient_id"], name = r["product_name"], qty = r["quantity_required"], unit = r["unit"] });
                    }
                }
            }
            return Json(list);
        }

        // SAVE RECIPE DETAILS FOR A PRODUCT
        [HttpPost]
        public IActionResult SaveRecipe([FromBody] RecipeSaveRequest request)
        {
            if (!IsAdmin()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string delSql = "DELETE FROM product_ingredients WHERE product_id = @id";
                using (SqlCommand delCmd = new SqlCommand(delSql, conn))
                {
                    delCmd.Parameters.AddWithValue("@id", request.ProductId);
                    delCmd.ExecuteNonQuery();
                }

                if (request.Ingredients != null && request.Ingredients.Count > 0)
                {
                    string insSql = "INSERT INTO product_ingredients (product_id, ingredient_id, quantity_required) VALUES (@pid, @iid, @qty)";
                    foreach (var item in request.Ingredients)
                    {
                        using (SqlCommand insCmd = new SqlCommand(insSql, conn))
                        {
                            insCmd.Parameters.AddWithValue("@pid", request.ProductId);
                            insCmd.Parameters.AddWithValue("@iid", item.IngredientId);
                            insCmd.Parameters.AddWithValue("@qty", item.Quantity);
                            insCmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            return Ok();
        }

        // ADD A NEW PRODUCT WITH ITS RECIPE IN ONE GO
        [HttpPost]
        public IActionResult AddFullProduct([FromBody] ComprehensiveItemModel model)
        {
            if (!IsAdmin()) return Unauthorized();

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

        // DELETE A PRODUCT AND ITS RECIPE
        [HttpPost]
        public IActionResult DeleteMenuItem([FromBody] int productId)
        {
            if (!IsAdmin()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string deleteRecipeSql = "DELETE FROM product_ingredients WHERE product_id = @id";
                        using (SqlCommand recipeCmd = new SqlCommand(deleteRecipeSql, conn, transaction))
                        {
                            recipeCmd.Parameters.AddWithValue("@id", productId);
                            recipeCmd.ExecuteNonQuery();
                        }

                        string deleteProductSql = "DELETE FROM products WHERE product_id = @id";
                        using (SqlCommand productCmd = new SqlCommand(deleteProductSql, conn, transaction))
                        {
                            productCmd.Parameters.AddWithValue("@id", productId);
                            productCmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                        return Ok();
                    }
                    catch (SqlException)
                    {
                        transaction.Rollback();
                        return BadRequest("Cannot delete item used in sales.");
                    }
                }
            }
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

        // SALES REPORT
        [HttpGet]
        public IActionResult DailyReport()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "Admin" && role != "Manager") return RedirectToAction("Index", "Home");

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

                // Ingredients Bought Today
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

        public class AdminVoidRequest { public int SaleId { get; set; } public string Pass { get; set; } }
        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }
    }
}