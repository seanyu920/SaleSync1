using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;

namespace SaleSync.Services
{

    public class InventoryDeductionService
    {
     
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
             
                    throw new Exception($"Stock Shortage: Ingredient ID {ing.id} has insufficient stock to fulfill this order (Required deduction: {totalDeduct}).");
                }
            }
        }
    }
}
