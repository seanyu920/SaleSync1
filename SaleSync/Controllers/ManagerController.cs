using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using SaleSync.Models;
using System;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class ManagerController : Controller
    {
        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Manager")
                return RedirectToAction("Index", "Home");

            return View("ManagerDashboard");
        }

        public IActionResult Analytics()
        {
            // Security check: Only let Managers in
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Manager")
                return RedirectToAction("Index", "Home");

            // EXACT FIX: Tell it to load the Admin's Analytics page
            return View("~/Views/Admin/Analytics.cshtml");
        }

        [HttpGet]
        public IActionResult Inventory()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Manager")
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

            return View("~/Views/Admin/Inventory.cshtml", items);
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
    }
}