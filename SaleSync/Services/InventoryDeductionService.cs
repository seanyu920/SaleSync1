using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;

namespace SaleSync.Services
{
    /// <summary>
    /// Deducts ingredient stock when a sale is completed, based on each product's
    /// recipe (product_ingredients) and each ingredient's conversion_factor
    /// (recipes can be written in a different unit than the ingredient is
    /// stocked in, e.g. recipe in ml, inventory tracked in gallons).
    ///
    /// This used to be copy-pasted separately into AdminController, CashierController,
    /// and ManagerController. ManagerController's copy had drifted out of date (double
    /// math, no conversion_factor), so sales completed through the Manager POS were
    /// deducting stock incorrectly. Consolidating here fixes that and makes sure any
    /// future fix to this logic only needs to happen once.
    /// </summary>
    public class InventoryDeductionService
    {
        /// <summary>
        /// Deducts stock for one sold product line within an existing connection/transaction.
        /// Throws if any ingredient doesn't have enough stock, so the caller's transaction
        /// can be rolled back.
        /// </summary>
        public void DeductIngredients(SqlConnection conn, SqlTransaction transaction, int productId, int qty)
        {
            string recipeQuery = @"
                SELECT pi.ingredient_id, pi.quantity_required, ISNULL(p.conversion_factor, 1) as conversion_factor
                FROM product_ingredients pi
                JOIN products p ON pi.ingredient_id = p.product_id
                WHERE pi.product_id = @product_id";

            var ingredients = new List<(int id, decimal qtyReq, decimal conv)>();

            using (SqlCommand cmd = new SqlCommand(recipeQuery, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@product_id", productId);

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ingredients.Add((
                        Convert.ToInt32(reader["ingredient_id"]),
                        Convert.ToDecimal(reader["quantity_required"]),
                        Convert.ToDecimal(reader["conversion_factor"])
                    ));
                }
            }

            foreach (var ing in ingredients)
            {
                // Fixed-point decimal math to avoid floating-point precision mismatches
                // against the database's decimal columns.
                decimal totalDeduct = (ing.qtyReq * qty) / ing.conv;

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
                    // Explicit error string the SweetAlert on the front end can display.
                    throw new Exception($"Stock Shortage: Ingredient ID {ing.id} has insufficient stock to fulfill this order (Required deduction: {totalDeduct}).");
                }
            }
        }
    }
}
