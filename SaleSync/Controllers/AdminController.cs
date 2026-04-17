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

        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || (role != "Admin" && role != "Manager"))
                return RedirectToAction("Index", "Home");

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
            return View("AdminDashboard", model);
        }

        [HttpPost]
        public IActionResult UpdateSaleStatus([FromBody] StatusUpdateModel request)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "UPDATE sales SET status = @status WHERE sale_id = @id";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@status", request.Status);
                    cmd.Parameters.AddWithValue("@id", request.SaleId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return Ok();
        }

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
        [HttpGet]
        public IActionResult Products()
        {
            if (!IsAdmin() && HttpContext.Session.GetString("Role") != "Manager")
                return RedirectToAction("Index", "Home");

            var menuList = new List<MenuItemModel>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // Fetch only finished products (is_ingredient is 0 or NULL)
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
        [HttpPost]
        public IActionResult AddMenuItem([FromBody] MenuItemModel model)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // ADDED: sku and barcode generation using NEWID() so they never duplicate!
                // Using 'PRD-' to stand for Product, so you can tell them apart from your 'ING-' ingredients.
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
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            var inventoryList = new List<InventoryItems>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
    SELECT p.product_id, p.product_name, p.stock_quantity, p.cost_price, p.sku, c.category_name 
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
                                PurchasePrice = Convert.ToDecimal(r["cost_price"]),
                                ItemCategory = r["category_name"]?.ToString() ?? "Raw Materials"
                            });
                        }
                    }
                }
            }
            return View(inventoryList);
        }

        // ⭐ ADDED: This connects your HTML Update button to the Database
        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems model)
        {
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

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
        // ⭐ ADDED: This catches the data when you add a new item to inventory
        [HttpPost]
        public IActionResult AddInventory(InventoryItems model)
        {
            // 1. Security Check
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // 2. Insert into the database. 
                // Note: Defaults to category 99 (Raw Materials) and sets is_ingredient = 1
                string sql = @"
                    INSERT INTO products (product_name, stock_quantity, cost_price, is_ingredient, category_id, sku, barcode)
                    VALUES (@name, @qty, @price, 1, 99, 
                            'ING-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8),
                            'BC-' + LEFT(CAST(NEWID() AS VARCHAR(36)), 8))";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", model.ItemName ?? "New Ingredient");
                    cmd.Parameters.AddWithValue("@qty", model.Quantity);
                    cmd.Parameters.AddWithValue("@price", model.PurchasePrice);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }

            // 3. Refresh the inventory page to show the new item
            return RedirectToAction("Inventory");
        }
        // ⭐ ADDED: This catches the data when you click "Delete" on an item
        [HttpPost]
        public IActionResult DeleteInventory(int ProductId)
        {
            // 1. Security Check
            if (!CanAccessInventory()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // 2. SQL to delete the specific item
                string sql = "DELETE FROM products WHERE product_id = @id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", ProductId);

                    conn.Open();
                    try
                    {
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqlException)
                    {
                        // Safely catches the error if the item is tied to an existing sale
                        TempData["ErrorMessage"] = "Cannot delete item: It is already linked to past sales records.";
                    }
                }
            }

            // 3. Refresh the inventory page
            return RedirectToAction("Inventory");
        }

        [HttpGet]
        public IActionResult ManageAccounts()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var accountList = new List<UserAccount>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // Added u.password_hash to match your database and HTML table
                string sql = @"
            SELECT u.user_id, u.username, u.full_name, u.email, u.password_hash, r.role_name 
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
                                Password = r["password_hash"]?.ToString() ?? "", // <-- Passing it to the View
                                Role = r["role_name"]?.ToString() ?? "Unknown"
                            });
                        }
                    }
                }
            }
            return View(accountList);
        }
        // ⭐ ADDED: This catches the data when you click "Update Selected"
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            // Security check: Only Admins can update accounts
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // This updates the user details. 
                // The sub-query handles converting the text Role (like "Cashier") back into the correct role_id for your database!
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
            // Refreshes the page so you can instantly see your updates in the table
            return RedirectToAction("ManageAccounts");
        }
        // ⭐ ADDED: This catches the data when you click "+ Add Account"
        [HttpPost]
        public IActionResult AddAccount(UserAccount model)
        {
            // Security check
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id)
                    VALUES (@fullName, @username, @email, @password, 
                            ISNULL((SELECT TOP 1 role_id FROM roles WHERE role_name = @role), 2))";
                // Note: Defaults to role_id 2 (Cashier) if they forget to select a role

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@fullName", model.FullName ?? "");
                    cmd.Parameters.AddWithValue("@username", model.Username ?? "");
                    cmd.Parameters.AddWithValue("@email", model.Email ?? "");
                    cmd.Parameters.AddWithValue("@password", model.Password ?? "");
                    cmd.Parameters.AddWithValue("@role", model.Role ?? "Cashier");

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }
        // ⭐ ADDED: This catches the data when you click "Delete Selected"
        [HttpPost]
        public IActionResult DeleteAccount(int UserId)
        {
            // Security check
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            // Prevent the Admin from accidentally deleting themselves!
            var currentUserId = HttpContext.Session.GetInt32("UserId");
            if (currentUserId == UserId)
            {
                // Optionally return a warning, but for now, just cancel the delete
                return RedirectToAction("ManageAccounts");
            }

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "DELETE FROM users WHERE user_id = @id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", UserId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            return RedirectToAction("ManageAccounts");
        }
        // 1. Fetches all raw materials for the dropdown
        [HttpGet]
        public IActionResult GetIngredients()
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = "SELECT product_id, product_name FROM products WHERE is_ingredient = 1 ORDER BY product_name";
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    conn.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            list.Add(new { id = r["product_id"], name = r["product_name"] });
                    }
                }
            }
            return Json(list);
        }

        // 2. Fetches an existing recipe so you can edit it
        [HttpGet]
        public IActionResult GetRecipe(int productId)
        {
            var list = new List<object>();
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT pi.ingredient_id, p.product_name, pi.quantity_required 
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
                            list.Add(new { id = r["ingredient_id"], name = r["product_name"], qty = r["quantity_required"] });
                    }
                }
            }
            return Json(list);
        }

        // 3. Saves the recipe to the database
        [HttpPost]
        public IActionResult SaveRecipe([FromBody] RecipeSaveRequest request)
        {
            if (!IsAdmin()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                // Step A: Delete the old recipe so we don't get duplicates
                string delSql = "DELETE FROM product_ingredients WHERE product_id = @id";
                using (SqlCommand delCmd = new SqlCommand(delSql, conn))
                {
                    delCmd.Parameters.AddWithValue("@id", request.ProductId);
                    delCmd.ExecuteNonQuery();
                }

                // Step B: Insert the new recipe ingredients
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
        [HttpPost]
        public IActionResult AddFullProduct([FromBody] ComprehensiveItemModel model)
        {
            if (!IsAdmin()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                // Start a transaction so it all saves at the exact same time
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // 1. Save the Product details first
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
                            newProductId = (int)cmd.ExecuteScalar(); // Grabs the new ID instantly
                        }

                        // 2. Route the ingredients straight to the ingredients table
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

                        // 3. Save everything permanently
                        transaction.Commit();
                        return Ok();
                    }
                    catch (Exception)
                    {
                        // If anything goes wrong, undo the whole thing to protect the database!
                        transaction.Rollback();
                        return BadRequest("Failed to save comprehensive item.");
                    }
                }
            }
        }
        [HttpPost]
        public IActionResult DeleteMenuItem([FromBody] int productId)
        {
            // 1. Security check
            if (!IsAdmin()) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // Step A: Safely wipe out any attached recipe ingredients FIRST
                        string deleteRecipeSql = "DELETE FROM product_ingredients WHERE product_id = @id";
                        using (SqlCommand recipeCmd = new SqlCommand(deleteRecipeSql, conn, transaction))
                        {
                            recipeCmd.Parameters.AddWithValue("@id", productId);
                            recipeCmd.ExecuteNonQuery();
                        }

                        // Step B: Now that it's unlinked, delete the actual product
                        string deleteProductSql = "DELETE FROM products WHERE product_id = @id";
                        using (SqlCommand productCmd = new SqlCommand(deleteProductSql, conn, transaction))
                        {
                            productCmd.Parameters.AddWithValue("@id", productId);
                            productCmd.ExecuteNonQuery();
                        }

                        // Save the changes permanently
                        transaction.Commit();
                        return Ok();
                    }
                    catch (SqlException)
                    {
                        // If it fails here, it means the item has already been sold to a customer!
                        transaction.Rollback();
                        return BadRequest("Cannot delete item used in sales.");
                    }
                }
            }
        }

        public class AdminVoidRequest { public int SaleId { get; set; } public string Pass { get; set; } }
        public class StatusUpdateModel { public int SaleId { get; set; } public string Status { get; set; } }
    }
}