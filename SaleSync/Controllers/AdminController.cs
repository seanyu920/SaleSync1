using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private readonly IConfiguration _configuration;

        public AdminController(IConfiguration configuration)
        {
            _configuration = configuration;
        }
        public IActionResult Analytics()
        {
            return View();
        }

        private bool IsAdmin()
        {
            var role = HttpContext.Session.GetString("Role");
            return role == "Admin";
        }

        private bool CanAccessInventory()
        {
            var role = HttpContext.Session.GetString("Role");
            return role == "Admin" || role == "Manager";
        }

        // DASHBOARD
        public IActionResult Dashboard()
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            return View("AdminDashboard");
        }

        // INVENTORY FUNCTION
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        [HttpGet]
        public IActionResult Inventory()
        {
            if (!CanAccessInventory())
                return RedirectToAction("Index", "Home");

            List<InventoryItems> items = new List<InventoryItems>();
            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = @"
                    SELECT p.product_id, c.category_name, p.product_name, p.sku, p.cost_price, p.stock_quantity, p.description
                    FROM dbo.products p
                    INNER JOIN dbo.categories c ON p.category_id = c.category_id
                    ORDER BY p.product_id";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string description = reader["description"]?.ToString() ?? "";

                        items.Add(new InventoryItems
                        {
                            ProductId = Convert.ToInt32(reader["product_id"]),
                            ItemCategory = reader["category_name"]?.ToString() ?? "",
                            ItemName = reader["product_name"]?.ToString() ?? "",
                            ItemID = reader["sku"]?.ToString() ?? "",
                            PurchasePrice = reader["cost_price"] == DBNull.Value ? 0 : Convert.ToDecimal(reader["cost_price"]),
                            Quantity = reader["stock_quantity"] == DBNull.Value ? 0 : Convert.ToInt32(reader["stock_quantity"]),
                            StockLevel = reader["stock_quantity"] == DBNull.Value
                                ? "In Stock"
                                : Convert.ToInt32(reader["stock_quantity"]) <= 10 ? "Low Stock" : "In Stock",
                            StockSupplier = ExtractValue(description, "Supplier:"),
                            DateAcquired = ExtractValue(description, "Date Acquired:"),
                            ExpirationDate = ExtractValue(description, "Expiration Date:")
                        });
                    }
                }
            }

            return View(items);
        }

        [HttpPost]
        public IActionResult AddInventory(InventoryItems item)
        {
            if (!CanAccessInventory())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                int categoryId = GetCategoryId(item.ItemCategory, conn);

                string insertQuery = @"
                    INSERT INTO dbo.products
                    (category_id, supplier_id, product_name, barcode, sku, description, cost_price, selling_price, stock_quantity, reorder_level, unit)
                    VALUES
                    (@CategoryId, NULL, @ProductName, @Barcode, @SKU, @Description, @CostPrice, 0, @StockQuantity, 10, NULL)";

                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@CategoryId", categoryId);
                    cmd.Parameters.AddWithValue("@ProductName", item.ItemName);
                    cmd.Parameters.AddWithValue("@Barcode", item.ItemID);
                    cmd.Parameters.AddWithValue("@SKU", item.ItemID);
                    cmd.Parameters.AddWithValue("@Description", $"Supplier: {item.StockSupplier}; Date Acquired: {item.DateAcquired}; Expiration Date: {item.ExpirationDate}");
                    cmd.Parameters.AddWithValue("@CostPrice", item.PurchasePrice);
                    cmd.Parameters.AddWithValue("@StockQuantity", item.Quantity);
                    cmd.ExecuteNonQuery();
                }
            }

            return RedirectToAction("Inventory");
        }

        [HttpGet]
        public IActionResult EditInventory(string id)
        {
            if (!CanAccessInventory())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";
            InventoryItems item = new InventoryItems();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = @"
                    SELECT TOP 1 p.product_id, c.category_name, p.product_name, p.sku, p.cost_price, p.stock_quantity, p.description
                    FROM dbo.products p
                    INNER JOIN dbo.categories c ON p.category_id = c.category_id
                    WHERE p.sku = @Id OR p.barcode = @Id";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", id);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string desc = reader["description"]?.ToString() ?? "";

                            item.ProductId = Convert.ToInt32(reader["product_id"]);
                            item.ItemCategory = reader["category_name"]?.ToString() ?? "";
                            item.ItemName = reader["product_name"]?.ToString() ?? "";
                            item.ItemID = reader["sku"]?.ToString() ?? "";
                            item.PurchasePrice = reader["cost_price"] == DBNull.Value ? 0 : Convert.ToDecimal(reader["cost_price"]);
                            item.Quantity = reader["stock_quantity"] == DBNull.Value ? 0 : Convert.ToInt32(reader["stock_quantity"]);
                            item.StockLevel = item.Quantity <= 10 ? "Low Stock" : "In Stock";
                            item.StockSupplier = ExtractValue(desc, "Supplier:");
                            item.DateAcquired = ExtractValue(desc, "Date Acquired:");
                            item.ExpirationDate = ExtractValue(desc, "Expiration Date:");
                        }
                    }
                }
            }

            return View("EditInventory", item);
        }

        public IActionResult Products()
        {
            // Security Check (Optional, but good practice since you have roles!)
            var role = HttpContext.Session.GetString("Role");
            if (string.IsNullOrEmpty(role) || role != "Admin")
            {
                return RedirectToAction("Index", "Home");
            }

            // Tells the server to show the Products.cshtml page
            return View();
        }

        [HttpPost]
        public IActionResult UpdateInventory(InventoryItems item)
        {
            if (!CanAccessInventory())
                return RedirectToAction("Index", "Home");
                
            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                int categoryId = GetCategoryId(item.ItemCategory, conn);

                string updateQuery = @"
                    UPDATE dbo.products
                    SET category_id = @CategoryId,
                        product_name = @ProductName,
                        barcode = @Barcode,
                        sku = @SKU,
                        description = @Description,
                        cost_price = @CostPrice,
                        stock_quantity = @StockQuantity
                    WHERE product_id = @ProductId";

                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@CategoryId", categoryId);
                    cmd.Parameters.AddWithValue("@ProductName", item.ItemName);
                    cmd.Parameters.AddWithValue("@Barcode", item.ItemID);
                    cmd.Parameters.AddWithValue("@SKU", item.ItemID);
                    cmd.Parameters.AddWithValue("@Description", $"Supplier: {item.StockSupplier}; Date Acquired: {item.DateAcquired}; Expiration Date: {item.ExpirationDate}");
                    cmd.Parameters.AddWithValue("@CostPrice", item.PurchasePrice);
                    cmd.Parameters.AddWithValue("@StockQuantity", item.Quantity);
                    cmd.Parameters.AddWithValue("@ProductId", item.ProductId);
                    cmd.ExecuteNonQuery();
                }
            }

            return RedirectToAction("Inventory");
        }

        [HttpPost]
        public IActionResult DeleteInventory(string id)
        {
            if (!CanAccessInventory())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string deleteQuery = "DELETE FROM dbo.products WHERE sku = @Id OR barcode = @Id";

                using (SqlCommand cmd = new SqlCommand(deleteQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.ExecuteNonQuery();
                }
            }

            return RedirectToAction("Inventory");
        }

        private int GetCategoryId(string categoryName, SqlConnection conn)
        {
            string query = "SELECT category_id FROM dbo.categories WHERE category_name = @CategoryName";

            using (SqlCommand cmd = new SqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("@CategoryName", categoryName);
                object result = cmd.ExecuteScalar();

                if (result != null)
                    return Convert.ToInt32(result);
            }

            string insertQuery = "INSERT INTO dbo.categories (category_name) VALUES (@CategoryName); SELECT SCOPE_IDENTITY();";

            using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
            {
                cmd.Parameters.AddWithValue("@CategoryName", categoryName);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private string ExtractValue(string description, string key)
        {
            if (string.IsNullOrEmpty(description) || !description.Contains(key))
                return "";

            int start = description.IndexOf(key) + key.Length;
            int end = description.IndexOf(";", start);

            if (end == -1)
                end = description.Length;

            return description.Substring(start, end - start).Trim();
        }

        // SAVE ACCOUNT
        [HttpPost]
        public IActionResult SaveAccount(UserAccount model)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string checkQuery = "SELECT COUNT(*) FROM users WHERE email = @Email OR username = @Username";
                using (SqlCommand checkCmd = new SqlCommand(checkQuery, conn))
                {
                    checkCmd.Parameters.AddWithValue("@Email", model.Email);
                    checkCmd.Parameters.AddWithValue("@Username", model.Username);

                    int exists = (int)checkCmd.ExecuteScalar();
                    if (exists > 0)
                    {
                        TempData["Error"] = "Account already exists. Use Update instead.";
                        return RedirectToAction("ManageAccounts");
                    }
                }

                string getRoleQuery = "SELECT role_id FROM roles WHERE role_name = @Role";
                int roleId;

                using (SqlCommand roleCmd = new SqlCommand(getRoleQuery, conn))
                {
                    roleCmd.Parameters.AddWithValue("@Role", model.Role);
                    object result = roleCmd.ExecuteScalar();

                    if (result == null)
                    {
                        TempData["Error"] = "Invalid role selected.";
                        return RedirectToAction("ManageAccounts");
                    }

                    roleId = Convert.ToInt32(result);
                }

                string insertQuery = @"
                    INSERT INTO users (full_name, username, email, password_hash, role_id, status)
                    VALUES (@FullName, @Username, @Email, @Password, @RoleId, 'active')";

                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", model.FullName);
                    cmd.Parameters.AddWithValue("@Username", model.Username);
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    cmd.Parameters.AddWithValue("@Password", model.Password);
                    cmd.Parameters.AddWithValue("@RoleId", roleId);
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account saved successfully.";
            return RedirectToAction("ManageAccounts");
        }

        // UPDATE ACCOUNT
        [HttpPost]
        public IActionResult UpdateAccount(UserAccount model)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string getRoleQuery = "SELECT role_id FROM roles WHERE role_name = @Role";
                int roleId;

                using (SqlCommand roleCmd = new SqlCommand(getRoleQuery, conn))
                {
                    roleCmd.Parameters.AddWithValue("@Role", model.Role);
                    object result = roleCmd.ExecuteScalar();

                    if (result == null)
                    {
                        TempData["Error"] = "Invalid role selected.";
                        return RedirectToAction("ManageAccounts");
                    }

                    roleId = Convert.ToInt32(result);
                }

                string updateQuery = @"
                    UPDATE users
                    SET full_name = @FullName,
                        username = @Username,
                        email = @Email,
                        password_hash = @Password,
                        role_id = @RoleId
                    WHERE user_id = @UserId";

                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@FullName", model.FullName);
                    cmd.Parameters.AddWithValue("@Username", model.Username);
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    cmd.Parameters.AddWithValue("@Password", model.Password);
                    cmd.Parameters.AddWithValue("@RoleId", roleId);
                    cmd.Parameters.AddWithValue("@UserId", model.UserId);
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account updated successfully.";
            return RedirectToAction("ManageAccounts");
        }

        // DELETE ACCOUNT
        [HttpPost]
        public IActionResult DeleteAccount(int userId)
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string deleteQuery = "DELETE FROM users WHERE user_id = @UserId";

                using (SqlCommand cmd = new SqlCommand(deleteQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = "Account deleted successfully.";
            return RedirectToAction("ManageAccounts");
        }

        [HttpGet]
        public IActionResult ManageAccounts()
        {
            if (!IsAdmin())
                return RedirectToAction("Index", "Home");

            List<UserAccount> accounts = new List<UserAccount>();
            string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = @"
                    SELECT u.user_id, u.full_name, u.username, u.email, u.password_hash, u.status, r.role_name
                    FROM users u
                    INNER JOIN roles r ON u.role_id = r.role_id
                    ORDER BY u.user_id";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        accounts.Add(new UserAccount
                        {
                            UserId = Convert.ToInt32(reader["user_id"]),
                            FullName = reader["full_name"]?.ToString() ?? "",
                            Username = reader["username"]?.ToString() ?? "",
                            Email = reader["email"]?.ToString() ?? "",
                            Password = reader["password_hash"]?.ToString() ?? "",
                            Role = reader["role_name"]?.ToString() ?? "",
                            Status = reader["status"]?.ToString() ?? ""
                        });
                    }
                }
            }

            return View(accounts);
        }
    }
}