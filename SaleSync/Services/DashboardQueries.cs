using System.Globalization;
using Microsoft.Data.SqlClient;
using SaleSync.Models;

namespace SaleSync.Services
{
    /// <summary>
    /// Loads the live numbers shown on the Admin, Manager and Cashier dashboards,
    /// so all three controllers share one set of queries.
    /// </summary>
    public static class DashboardQueries
    {
        // "Past 7 days" = today plus the 6 days before it.
        private const string SinceSql = "CAST(DATEADD(day, -6, GETDATE()) AS DATE)";

        /// <param name="conn">An already-open connection with no active reader.</param>
        /// <param name="lowStockThreshold">Items at or below this quantity count as low stock.</param>
        public static void Load(CashierDashboardViewModel model, SqlConnection conn, int lowStockThreshold)
        {
            LoadCounts(model, conn);
            LoadWeeklySales(model, conn);
            LoadBestSellers(model, conn);
            LoadPaymentMix(model, conn);
            LoadLowStock(model, conn, lowStockThreshold);
        }

        private static void LoadCounts(CashierDashboardViewModel model, SqlConnection conn)
        {
            const string sql = @"
                SELECT
                    (SELECT COUNT(*) FROM sales
                      WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE)
                        AND ISNULL(status, '') NOT IN ('Voided', 'Cancelled')) AS AllToday,
                    (SELECT COUNT(*) FROM sales
                      WHERE status IN ('Pending', 'Preparing', 'Processing')) AS ActiveQueue";

            using var cmd = new SqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                model.TodayAllTransactionCount = Convert.ToInt32(r["AllToday"]);
                model.PendingOrderCount = Convert.ToInt32(r["ActiveQueue"]);
            }
        }

        private static void LoadWeeklySales(CashierDashboardViewModel model, SqlConnection conn)
        {
            string sql = $@"
                SELECT CAST(sale_date AS DATE) AS SaleDay,
                       SUM(ISNULL(final_amount, total_amount)) AS Total
                FROM sales
                WHERE status = 'Completed'
                  AND sale_date >= {SinceSql}
                GROUP BY CAST(sale_date AS DATE)";

            var totals = new Dictionary<DateTime, decimal>();
            using (var cmd = new SqlCommand(sql, conn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    totals[Convert.ToDateTime(r["SaleDay"]).Date] =
                        r["Total"] == DBNull.Value ? 0m : Convert.ToDecimal(r["Total"]);
                }
            }

            model.WeeklyLabels.Clear();
            model.WeeklyValues.Clear();
            model.WeeklySalesTotal = 0m;

            // One point per day, including days with no sales, oldest first.
            for (int i = 6; i >= 0; i--)
            {
                DateTime day = DateTime.Today.AddDays(-i);
                decimal value = totals.TryGetValue(day, out var t) ? t : 0m;

                model.WeeklyLabels.Add(day.ToString("ddd", CultureInfo.InvariantCulture));
                model.WeeklyValues.Add(value);
                model.WeeklySalesTotal += value;
            }
        }

        private static void LoadBestSellers(CashierDashboardViewModel model, SqlConnection conn)
        {
            string sql = $@"
                SELECT TOP 5
                    p.product_name,
                    SUM(si.quantity) AS Units,
                    SUM(si.subtotal) AS Revenue
                FROM sale_items si
                JOIN sales s    ON si.sale_id = s.sale_id
                JOIN products p ON si.product_id = p.product_id
                WHERE s.status = 'Completed'
                  AND s.sale_date >= {SinceSql}
                GROUP BY p.product_name
                ORDER BY Units DESC, Revenue DESC";

            model.BestSellers.Clear();
            using var cmd = new SqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                model.BestSellers.Add(new DashboardBestSeller
                {
                    ProductName = r["product_name"]?.ToString() ?? "",
                    UnitsSold = r["Units"] == DBNull.Value ? 0 : Convert.ToInt32(r["Units"]),
                    Revenue = r["Revenue"] == DBNull.Value ? 0m : Convert.ToDecimal(r["Revenue"])
                });
            }
        }

        private static void LoadPaymentMix(CashierDashboardViewModel model, SqlConnection conn)
        {
            string sql = $@"
                SELECT LOWER(LTRIM(RTRIM(ISNULL(payment_method, '')))) AS Method,
                       COUNT(*) AS Cnt
                FROM sales
                WHERE status = 'Completed'
                  AND sale_date >= {SinceSql}
                GROUP BY LOWER(LTRIM(RTRIM(ISNULL(payment_method, ''))))
                ORDER BY Cnt DESC";

            model.PaymentLabels.Clear();
            model.PaymentValues.Clear();

            using var cmd = new SqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                model.PaymentLabels.Add(FormatPaymentMethod(r["Method"]?.ToString()));
                model.PaymentValues.Add(Convert.ToInt32(r["Cnt"]));
            }
        }

        private static string FormatPaymentMethod(string? method)
        {
            switch ((method ?? "").Trim().ToLowerInvariant())
            {
                case "": return "Unspecified";
                case "cash": return "Cash";
                case "gcash": return "GCash";
                case "card": return "Card";
                default:
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(method!.Trim().ToLowerInvariant());
            }
        }

        private static void LoadLowStock(CashierDashboardViewModel model, SqlConnection conn, int threshold)
        {
            // Same definition of "inventory item" as the Inventory page:
            // ingredients that are not archived.
            const string whereSql = @"
                WHERE is_ingredient = 1
                  AND (is_archived = 0 OR is_archived IS NULL)
                  AND ISNULL(stock_quantity, 0) <= @threshold";

            model.LowStockItems.Clear();
            model.LowStockCount = 0;

            using (var cmd = new SqlCommand("SELECT COUNT(*) FROM products" + whereSql, conn))
            {
                cmd.Parameters.AddWithValue("@threshold", threshold);
                model.LowStockCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            const string listSql = @"
                SELECT TOP 10 product_name, ISNULL(stock_quantity, 0) AS Qty, unit
                FROM products" + whereSql + @"
                ORDER BY ISNULL(stock_quantity, 0) ASC, product_name ASC";

            using (var cmd = new SqlCommand(listSql, conn))
            {
                cmd.Parameters.AddWithValue("@threshold", threshold);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    double qty = Convert.ToDouble(r["Qty"]);
                    model.LowStockItems.Add(new DashboardLowStockItem
                    {
                        ItemName = r["product_name"]?.ToString() ?? "",
                        Quantity = qty,
                        Unit = r["unit"] == DBNull.Value ? "" : r["unit"].ToString() ?? "",
                        IsCritical = qty <= threshold / 2.0
                    });
                }
            }
        }
    }
}
