using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using SaleSync.Models;
using System.Collections.Generic;
using System;

namespace SaleSync.Controllers
{
    // ⭐ THE PADLOCK: Only logged-in customers can access the storefront
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        // ⭐ 1. UPDATED TO YOUR ACTUAL PC DATABASE ⭐
        private readonly string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=SaleSync;Trusted_Connection=True;TrustServerCertificate=True;";

        // ⭐ 2. RENAMED TO MATCH YOUR HTML FILE ⭐
        [HttpGet]
        public IActionResult CustomerOrdering()
        {
            var menuList = new List<MenuItemModel>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // We only want to select sellable items (is_ingredient = 0)
                string sql = @"
                    SELECT p.product_id, p.product_name, c.category_name, p.selling_price
                    FROM products p
                    LEFT JOIN categories c ON p.category_id = c.category_id
                    WHERE p.is_ingredient = 0 OR p.is_ingredient IS NULL
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
    }
}