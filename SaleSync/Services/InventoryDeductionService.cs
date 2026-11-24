using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;

namespace SaleSync.Services
{

    public class InventoryDeductionService
    {
        private readonly InventoryMovementService _movements;

        public InventoryDeductionService(InventoryMovementService movements)
        {
            _movements = movements;
        }

        /// <summary>
        /// Takes the ingredients of a menu item out of stock. Every ingredient that is deducted is also
        /// written to the stock movement ledger (shown on the Inventory Stock Report).
        /// </summary>
        /// <param name="saleId">The order this deduction belongs to, so the report can name it.</param>
        /// <param name="userId">The staff member who completed the order.</param>
        public void DeductIngredients(SqlConnection conn, SqlTransaction transaction, int productId, int qty,
                                      int? saleId = null, int? userId = null)
        {
            string recipeQuery = @"
                SELECT pi.ingredient_id,
                       pi.quantity_required,
                       ISNULL(p.conversion_factor, 1) as conversion_factor,
                       p.product_name as ingredient_name,
                       p.unit as ingredient_unit,
                       ISNULL(p.cost_price, 0) as cost_price,
                       m.product_name as menu_name
                FROM product_ingredients pi
                JOIN products p ON pi.ingredient_id = p.product_id
                LEFT JOIN products m ON pi.product_id = m.product_id
                WHERE pi.product_id = @product_id";

            var ingredients = new List<(int id, decimal qtyReq, decimal conv, string name, string unit, decimal cost, string menuName)>();

            using (SqlCommand cmd = new SqlCommand(recipeQuery, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@product_id", productId);

                using SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ingredients.Add((
                        Convert.ToInt32(reader["ingredient_id"]),
                        Convert.ToDecimal(reader["quantity_required"]),
                        Convert.ToDecimal(reader["conversion_factor"]),
                        reader["ingredient_name"]?.ToString() ?? "Unnamed Item",
                        reader["ingredient_unit"] == DBNull.Value ? "" : reader["ingredient_unit"].ToString() ?? "",
                        Convert.ToDecimal(reader["cost_price"]),
                        reader["menu_name"] == DBNull.Value ? "" : reader["menu_name"].ToString() ?? ""
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

                RecordUsage(conn, transaction, ing.id, ing.name, ing.unit, ing.cost, ing.menuName, qty, totalDeduct, saleId, userId);
            }
        }

        /// <summary>Writes the "Sale Usage" ledger row for one ingredient. Never throws.</summary>
        private void RecordUsage(SqlConnection conn, SqlTransaction transaction, int ingredientId, string name, string unit,
                                 decimal unitCost, string menuName, int menuQty, decimal totalDeduct, int? saleId, int? userId)
        {
            try
            {
                decimal? after = null;
                using (SqlCommand stockCmd = new SqlCommand("SELECT stock_quantity FROM products WHERE product_id = @id", conn, transaction))
                {
                    stockCmd.Parameters.AddWithValue("@id", ingredientId);
                    object? value = stockCmd.ExecuteScalar();
                    if (value != null && value != DBNull.Value) after = Convert.ToDecimal(value);
                }

                string note = string.IsNullOrWhiteSpace(menuName) ? "Used for an order" : $"{menuQty}x {menuName}";

                _movements.Record(conn, transaction, new InventoryMovementEntry
                {
                    ProductId = ingredientId,
                    ItemName = name,
                    Unit = unit,
                    MovementType = MovementTypes.SaleUsage,
                    QuantityChange = -totalDeduct,
                    QuantityBefore = after.HasValue ? after.Value + totalDeduct : (decimal?)null,
                    QuantityAfter = after,
                    UnitCost = unitCost,
                    TotalValue = Math.Round(totalDeduct * unitCost, 2),
                    ReferenceType = saleId.HasValue ? "Sale" : null,
                    ReferenceId = saleId,
                    UserId = userId,
                    Notes = note
                });
            }
            catch
            {
                // The ledger must never stop an order from being completed.
            }
        }
    }
}
