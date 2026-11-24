using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SaleSync.Models;

namespace SaleSync.Services
{
    /// <summary>The kinds of stock movement the ledger knows about.</summary>
    public static class MovementTypes
    {
        public const string InitialStock = "Initial Stock";    // new item added with a starting quantity
        public const string Restock = "Restock";               // stock added to an existing item
        public const string SaleUsage = "Sale Usage";          // ingredients used when an order is completed
        public const string AdjustmentOut = "Adjustment Out";  // stock reduced by hand on the Inventory page
    }

    /// <summary>Everything stored about a single stock movement.</summary>
    public class InventoryMovementEntry
    {
        public int ProductId { get; set; }
        public string ItemName { get; set; } = "";
        public string? Unit { get; set; }
        public string MovementType { get; set; } = "";

        /// <summary>Positive = stock in, negative = stock out.</summary>
        public decimal QuantityChange { get; set; }
        public decimal? QuantityBefore { get; set; }
        public decimal? QuantityAfter { get; set; }

        public decimal? UnitCost { get; set; }
        public decimal? TotalValue { get; set; }

        public string? Supplier { get; set; }
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }
        public int? UserId { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Keeps the stock movement ledger (the inventory_movements table) and builds the
    /// Inventory Stock Report from it.
    ///
    /// The table is created automatically the first time it is needed, and the existing
    /// inventory_purchases history is copied into it once, so no manual SQL has to be run.
    /// A problem with the ledger never blocks an order or an inventory edit: failures are
    /// logged and the main action carries on.
    /// </summary>
    public class InventoryMovementService
    {
        private readonly string _connectionString;
        private readonly ILogger<InventoryMovementService> _logger;

        private static readonly object SchemaLock = new object();
        private static bool _schemaReady;

        public InventoryMovementService(IConfiguration configuration, ILogger<InventoryMovementService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
        }

        // ---------------------------------------------------------------------------------
        // Schema
        // ---------------------------------------------------------------------------------

        /// <summary>Makes sure the ledger table exists. Returns false when it could not be created.</summary>
        public bool EnsureSchema()
        {
            if (_schemaReady) return true;

            lock (SchemaLock)
            {
                if (_schemaReady) return true;

                try
                {
                    using var conn = new SqlConnection(_connectionString);
                    conn.Open();

                    bool existed;
                    using (var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID('inventory_movements', 'U') IS NULL THEN 0 ELSE 1 END", conn))
                        existed = Convert.ToInt32(cmd.ExecuteScalar()) == 1;

                    if (!existed)
                    {
                        Run(conn, @"
                            CREATE TABLE inventory_movements (
                                movement_id     INT IDENTITY(1,1) PRIMARY KEY,
                                product_id      INT           NOT NULL,
                                item_name       NVARCHAR(255) NOT NULL,
                                unit            NVARCHAR(30)  NULL,
                                movement_type   NVARCHAR(30)  NOT NULL,
                                quantity_change DECIMAL(18,4) NOT NULL,
                                quantity_before DECIMAL(18,4) NULL,
                                quantity_after  DECIMAL(18,4) NULL,
                                unit_cost       DECIMAL(18,4) NULL,
                                total_value     DECIMAL(18,2) NULL,
                                supplier        NVARCHAR(255) NULL,
                                reference_type  NVARCHAR(30)  NULL,
                                reference_id    INT           NULL,
                                user_id         INT           NULL,
                                notes           NVARCHAR(400) NULL,
                                created_at      DATETIME      NOT NULL CONSTRAINT DF_inventory_movements_created_at DEFAULT GETDATE()
                            )");

                        Run(conn, "CREATE INDEX IX_inventory_movements_created ON inventory_movements (created_at)");
                        Run(conn, "CREATE INDEX IX_inventory_movements_product ON inventory_movements (product_id, created_at)");

                        BackfillFromPurchases(conn);
                    }

                    _schemaReady = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not prepare the inventory_movements table. Stock movements will not be recorded until this is fixed.");
                    return false;
                }
            }
        }

        private static void Run(SqlConnection conn, string sql)
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// One-time copy of the old purchase history into the ledger, so past restocks show up
        /// in the report. Stock that went out before the ledger existed was never recorded anywhere,
        /// so it cannot be recreated.
        /// </summary>
        private void BackfillFromPurchases(SqlConnection conn)
        {
            try
            {
                using var check = new SqlCommand("SELECT CASE WHEN OBJECT_ID('inventory_purchases', 'U') IS NULL THEN 0 ELSE 1 END", conn);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0) return;

                Run(conn, @"
                    INSERT INTO inventory_movements
                        (product_id, item_name, unit, movement_type, quantity_change, unit_cost, total_value,
                         reference_type, notes, created_at)
                    SELECT p.product_id, p.product_name, p.unit, 'Restock',
                           CAST(ip.quantity_bought AS DECIMAL(18,4)),
                           CASE WHEN ip.quantity_bought <> 0
                                THEN CAST(ip.total_cost AS DECIMAL(18,4)) / CAST(ip.quantity_bought AS DECIMAL(18,4))
                           END,
                           CAST(ip.total_cost AS DECIMAL(18,2)),
                           'Import', 'Imported from purchase history', ISNULL(ip.purchase_date, GETDATE())
                    FROM inventory_purchases ip WITH (NOLOCK)
                    CROSS APPLY (SELECT TOP 1 product_id, product_name, unit
                                 FROM products WITH (NOLOCK)
                                 WHERE product_name = ip.item_name AND is_ingredient = 1
                                 ORDER BY product_id) p
                    WHERE ip.quantity_bought > 0");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not copy the old purchase history into inventory_movements. The ledger will start empty.");
            }
        }

        // ---------------------------------------------------------------------------------
        // Recording movements
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes one movement. Pass the caller's connection and transaction so the ledger row
        /// commits or rolls back together with the stock change it describes.
        /// </summary>
        public void Record(SqlConnection conn, SqlTransaction? transaction, InventoryMovementEntry e)
        {
            if (!EnsureSchema()) return;

            try
            {
                const string sql = @"
                    INSERT INTO inventory_movements
                        (product_id, item_name, unit, movement_type, quantity_change, quantity_before, quantity_after,
                         unit_cost, total_value, supplier, reference_type, reference_id, user_id, notes)
                    VALUES
                        (@pid, @name, @unit, @type, @change, @before, @after,
                         @cost, @value, @supplier, @reftype, @refid, @uid, @notes)";

                using var cmd = new SqlCommand(sql, conn, transaction);
                cmd.Parameters.Add("@pid", SqlDbType.Int).Value = e.ProductId;
                cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = Cut(e.ItemName, 255) ?? "Unnamed Item";
                cmd.Parameters.Add("@unit", SqlDbType.NVarChar, 30).Value = (object?)Cut(e.Unit, 30) ?? DBNull.Value;
                cmd.Parameters.Add("@type", SqlDbType.NVarChar, 30).Value = e.MovementType;
                cmd.Parameters.Add(Dec("@change", 18, 4, e.QuantityChange));
                cmd.Parameters.Add(Dec("@before", 18, 4, e.QuantityBefore));
                cmd.Parameters.Add(Dec("@after", 18, 4, e.QuantityAfter));
                cmd.Parameters.Add(Dec("@cost", 18, 4, e.UnitCost));
                cmd.Parameters.Add(Dec("@value", 18, 2, e.TotalValue));
                cmd.Parameters.Add("@supplier", SqlDbType.NVarChar, 255).Value = (object?)Cut(e.Supplier, 255) ?? DBNull.Value;
                cmd.Parameters.Add("@reftype", SqlDbType.NVarChar, 30).Value = (object?)Cut(e.ReferenceType, 30) ?? DBNull.Value;
                cmd.Parameters.Add("@refid", SqlDbType.Int).Value = (object?)e.ReferenceId ?? DBNull.Value;
                cmd.Parameters.Add("@uid", SqlDbType.Int).Value = e.UserId.HasValue && e.UserId.Value > 0 ? (object)e.UserId.Value : DBNull.Value;
                cmd.Parameters.Add("@notes", SqlDbType.NVarChar, 400).Value = (object?)Cut(e.Notes, 400) ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record a stock movement for product {ProductId}.", e.ProductId);
            }
        }

        /// <summary>
        /// Called after an item is edited on the Inventory page. Logs a Restock when the quantity went
        /// up and an Adjustment Out when it was lowered by hand. Does nothing if the quantity did not change.
        /// </summary>
        public void RecordManualChange(int productId, string itemName, string? unit,
                                       decimal quantityBefore, decimal quantityAfter,
                                       decimal unitCost, string? supplier, int userId)
        {
            decimal delta = Math.Round(quantityAfter - quantityBefore, 4);
            if (delta == 0) return;
            if (!EnsureSchema()) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                bool isIn = delta > 0;
                Record(conn, null, new InventoryMovementEntry
                {
                    ProductId = productId,
                    ItemName = itemName,
                    Unit = unit,
                    MovementType = isIn ? MovementTypes.Restock : MovementTypes.AdjustmentOut,
                    QuantityChange = delta,
                    QuantityBefore = quantityBefore,
                    QuantityAfter = quantityAfter,
                    UnitCost = unitCost,
                    TotalValue = Math.Round(Math.Abs(delta) * unitCost, 2),
                    Supplier = supplier,
                    ReferenceType = "Manual",
                    UserId = userId,
                    Notes = isIn ? "Stock added on the Inventory page" : "Stock reduced by hand on the Inventory page"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record the stock change for product {ProductId}.", productId);
            }
        }

        /// <summary>Called after a brand new inventory item is created with a starting quantity.</summary>
        public void RecordInitialStock(string itemName, string? unit, decimal quantity,
                                       decimal unitCost, string? supplier, int userId)
        {
            if (quantity <= 0) return;
            if (!EnsureSchema()) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                object? found;
                using (var cmd = new SqlCommand(
                    "SELECT TOP 1 product_id FROM products WHERE product_name = @name AND is_ingredient = 1 ORDER BY product_id DESC", conn))
                {
                    cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = itemName;
                    found = cmd.ExecuteScalar();
                }

                if (found == null || found == DBNull.Value) return;

                Record(conn, null, new InventoryMovementEntry
                {
                    ProductId = Convert.ToInt32(found),
                    ItemName = itemName,
                    Unit = unit,
                    MovementType = MovementTypes.InitialStock,
                    QuantityChange = quantity,
                    QuantityBefore = 0m,
                    QuantityAfter = quantity,
                    UnitCost = unitCost,
                    TotalValue = Math.Round(quantity * unitCost, 2),
                    Supplier = supplier,
                    ReferenceType = "Manual",
                    UserId = userId,
                    Notes = "New item added to the inventory"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record the starting stock for '{ItemName}'.", itemName);
            }
        }

        // ---------------------------------------------------------------------------------
        // Report
        // ---------------------------------------------------------------------------------

        private const string FilterWhere = @"
            WHERE m.created_at >= @from AND m.created_at < @toExcl
              AND (@pid IS NULL OR m.product_id = @pid)
              AND (@mtype IS NULL OR m.movement_type = @mtype)
              AND (@dir = 0 OR (@dir = 1 AND m.quantity_change > 0) OR (@dir = -1 AND m.quantity_change < 0))";

        private static (int Direction, string? MovementType) ParseType(string? type)
        {
            switch ((type ?? "all").Trim().ToLowerInvariant())
            {
                case "in": return (1, null);
                case "out": return (-1, null);
                case "restock": return (0, MovementTypes.Restock);
                case "initial": return (0, MovementTypes.InitialStock);
                case "sale": return (0, MovementTypes.SaleUsage);
                case "adjustment": return (0, MovementTypes.AdjustmentOut);
                default: return (0, null);
            }
        }

        private static void AddFilterParams(SqlCommand cmd, InventoryReportFilter f)
        {
            var (direction, movementType) = ParseType(f.Type);

            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = f.From.Date;
            cmd.Parameters.Add("@toExcl", SqlDbType.DateTime).Value = f.To.Date.AddDays(1);
            cmd.Parameters.Add("@pid", SqlDbType.Int).Value = (object?)f.ProductId ?? DBNull.Value;
            cmd.Parameters.Add("@mtype", SqlDbType.NVarChar, 30).Value = (object?)movementType ?? DBNull.Value;
            cmd.Parameters.Add("@dir", SqlDbType.Int).Value = direction;
        }

        /// <summary>The full (uncapped) movement list for the CSV download.</summary>
        public List<InventoryMovementRow> GetMovementsForExport(InventoryReportFilter f)
        {
            if (!EnsureSchema()) return new List<InventoryMovementRow>();

            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            return LoadMovements(conn, f, 100000);
        }

        private static List<InventoryMovementRow> LoadMovements(SqlConnection conn, InventoryReportFilter f, int top)
        {
            string sql = @"
                SELECT TOP (@top)
                    m.movement_id, m.created_at, m.product_id, m.item_name, m.unit, m.movement_type,
                    m.quantity_change, m.quantity_before, m.quantity_after, m.unit_cost, m.total_value,
                    m.supplier, m.reference_type, m.reference_id, u.username, m.notes
                FROM inventory_movements m
                LEFT JOIN users u ON m.user_id = u.user_id" + FilterWhere + @"
                ORDER BY m.created_at DESC, m.movement_id DESC";

            var rows = new List<InventoryMovementRow>();

            using var cmd = new SqlCommand(sql, conn);
            AddFilterParams(cmd, f);
            cmd.Parameters.Add("@top", SqlDbType.Int).Value = top;

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new InventoryMovementRow
                {
                    MovementId = Convert.ToInt32(r["movement_id"]),
                    Date = Convert.ToDateTime(r["created_at"]),
                    ProductId = Convert.ToInt32(r["product_id"]),
                    ItemName = r["item_name"]?.ToString() ?? "",
                    Unit = r["unit"] == DBNull.Value ? "" : r["unit"].ToString() ?? "",
                    MovementType = r["movement_type"]?.ToString() ?? "",
                    QuantityChange = Convert.ToDecimal(r["quantity_change"]),
                    QuantityBefore = NullableDecimal(r["quantity_before"]),
                    QuantityAfter = NullableDecimal(r["quantity_after"]),
                    UnitCost = NullableDecimal(r["unit_cost"]),
                    TotalValue = NullableDecimal(r["total_value"]),
                    Supplier = r["supplier"] == DBNull.Value ? "" : r["supplier"].ToString() ?? "",
                    ReferenceType = r["reference_type"] == DBNull.Value ? "" : r["reference_type"].ToString() ?? "",
                    ReferenceId = r["reference_id"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["reference_id"]),
                    PerformedBy = r["username"] == DBNull.Value ? "System" : r["username"].ToString() ?? "System",
                    Notes = r["notes"] == DBNull.Value ? "" : r["notes"].ToString() ?? ""
                });
            }

            return rows;
        }

        /// <summary>Builds everything the Inventory Stock Report page shows.</summary>
        public InventoryReportViewModel BuildReport(string range, InventoryReportFilter f, int lowStockThreshold)
        {
            var vm = new InventoryReportViewModel
            {
                Range = range,
                From = f.From.Date,
                To = f.To.Date,
                Type = f.Type,
                ProductId = f.ProductId,
                LowStockThreshold = lowStockThreshold
            };

            bool ledgerReady = EnsureSchema();
            vm.LedgerUnavailable = !ledgerReady;

            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            // 1) Items on hand right now (used for the dropdown, alerts and "current stock" column)
            var current = LoadCurrentItems(conn);
            vm.ItemOptions = current
                .Select(c => new InventoryItemOption { ProductId = c.Id, ItemName = c.Name })
                .ToList();
            vm.Alerts = BuildAlerts(current, lowStockThreshold);

            if (!ledgerReady)
            {
                return vm;
            }

            // 2) Per-item totals for the period
            var summaries = LoadItemTotals(conn, f);

            // 3) Usage rate (units used in sales per day) for the "days left" estimate
            var usagePerDay = LoadUsagePerDay(conn, f);

            // 4) When live tracking began
            vm.LiveTrackingSince = LoadLiveTrackingStart(conn);

            // 5) Movement log
            int cap = f.MaxRows > 0 ? f.MaxRows : 100000;
            vm.Movements = LoadMovements(conn, f, cap);

            // 6) Breakdown by type + chart data
            LoadTypeBreakdown(conn, f, vm);
            vm.DailyPoints = LoadDailyPoints(conn, f);

            // Merge totals with live stock
            bool showIdleItems = string.Equals(f.Type, "all", StringComparison.OrdinalIgnoreCase) && !f.ProductId.HasValue;
            var currentById = current.ToDictionary(c => c.Id);

            foreach (var s in summaries.Values)
            {
                if (currentById.TryGetValue(s.ProductId, out var c))
                {
                    s.ItemName = c.Name;
                    s.Category = c.Category;
                    s.Unit = c.Unit;
                    s.CurrentStock = c.Quantity;
                }
                else
                {
                    s.Category = "Archived / removed";
                }
            }

            if (showIdleItems)
            {
                foreach (var c in current)
                {
                    if (summaries.ContainsKey(c.Id)) continue;
                    summaries[c.Id] = new InventoryItemSummary
                    {
                        ProductId = c.Id,
                        ItemName = c.Name,
                        Category = c.Category,
                        Unit = c.Unit,
                        CurrentStock = c.Quantity
                    };
                }
            }

            foreach (var s in summaries.Values)
            {
                if (usagePerDay.TryGetValue(s.ProductId, out var perDay)) s.AvgDailyUse = Math.Round(perDay, 4);

                if (s.CurrentStock.HasValue)
                {
                    decimal stock = s.CurrentStock.Value;
                    s.StockLevel = stock <= 0 ? "Out" : (stock <= lowStockThreshold ? "Low" : "OK");

                    if (stock <= 0) s.DaysLeft = 0m;
                    else if (s.AvgDailyUse > 0) s.DaysLeft = Math.Round(stock / s.AvgDailyUse, 1);
                }
            }

            vm.ItemSummaries = summaries.Values
                .OrderByDescending(s => s.ValueOut)
                .ThenByDescending(s => s.ValueIn)
                .ThenBy(s => s.ItemName)
                .ToList();

            // Headline numbers
            var moved = vm.ItemSummaries.Where(s => s.MovementCount > 0).ToList();
            vm.TotalValueIn = moved.Sum(s => s.ValueIn);
            vm.TotalValueOut = moved.Sum(s => s.ValueOut);
            vm.MovementCount = moved.Sum(s => s.MovementCount);
            vm.ItemsAffected = moved.Count;
            vm.TotalMatchingMovements = vm.MovementCount;
            vm.MovementsTruncated = vm.Movements.Count < vm.MovementCount;

            var top = moved.OrderByDescending(s => s.ValueOut).FirstOrDefault();
            if (top != null && top.ValueOut > 0)
            {
                vm.TopUsedItem = top.ItemName;
                vm.TopUsedItemValue = top.ValueOut;
            }

            return vm;
        }

        // ---- current stock ----

        private class CurrentItem
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Category { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal Quantity { get; set; }
            public DateTime? Expiry { get; set; }
        }

        private static List<CurrentItem> LoadCurrentItems(SqlConnection conn)
        {
            const string sql = @"
                SELECT p.product_id, p.product_name, ISNULL(c.category_name, 'Raw Materials') AS category_name,
                       ISNULL(p.unit, 'pcs') AS unit, ISNULL(p.stock_quantity, 0) AS stock_quantity, p.expiration_date
                FROM products p
                LEFT JOIN categories c ON p.category_id = c.category_id
                WHERE p.is_ingredient = 1 AND (p.is_archived = 0 OR p.is_archived IS NULL)
                ORDER BY p.product_name";

            var list = new List<CurrentItem>();

            using var cmd = new SqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CurrentItem
                {
                    Id = Convert.ToInt32(r["product_id"]),
                    Name = r["product_name"]?.ToString() ?? "Unnamed Item",
                    Category = r["category_name"]?.ToString() ?? "",
                    Unit = r["unit"]?.ToString() ?? "",
                    Quantity = Convert.ToDecimal(r["stock_quantity"]),
                    Expiry = r["expiration_date"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["expiration_date"])
                });
            }

            return list;
        }

        private static List<InventoryAlertItem> BuildAlerts(List<CurrentItem> items, int lowStockThreshold)
        {
            const int expiringSoonDays = 14;
            DateTime today = DateTime.Today;
            var alerts = new List<InventoryAlertItem>();

            foreach (var i in items)
            {
                if (i.Quantity <= 0)
                {
                    alerts.Add(new InventoryAlertItem { Kind = "Out of stock", ItemName = i.Name, Unit = i.Unit, Quantity = i.Quantity });
                }
                else if (i.Quantity <= lowStockThreshold)
                {
                    alerts.Add(new InventoryAlertItem { Kind = "Low stock", ItemName = i.Name, Unit = i.Unit, Quantity = i.Quantity });
                }

                // Expiry only matters while there is still stock on the shelf.
                if (i.Quantity > 0 && i.Expiry.HasValue)
                {
                    int days = (int)(i.Expiry.Value.Date - today).TotalDays;
                    if (days < 0)
                    {
                        alerts.Add(new InventoryAlertItem
                        {
                            Kind = "Expired", ItemName = i.Name, Unit = i.Unit, Quantity = i.Quantity,
                            ExpirationDate = i.Expiry.Value.Date, DaysToExpiry = days
                        });
                    }
                    else if (days <= expiringSoonDays)
                    {
                        alerts.Add(new InventoryAlertItem
                        {
                            Kind = "Expiring soon", ItemName = i.Name, Unit = i.Unit, Quantity = i.Quantity,
                            ExpirationDate = i.Expiry.Value.Date, DaysToExpiry = days
                        });
                    }
                }
            }

            int Rank(string kind) => kind == "Out of stock" ? 0 : kind == "Expired" ? 1 : kind == "Low stock" ? 2 : 3;

            return alerts.OrderBy(a => Rank(a.Kind)).ThenBy(a => a.ItemName).ToList();
        }

        // ---- period totals ----

        private static Dictionary<int, InventoryItemSummary> LoadItemTotals(SqlConnection conn, InventoryReportFilter f)
        {
            string sql = @"
                SELECT m.product_id,
                       MAX(m.item_name) AS item_name,
                       MAX(m.unit) AS unit,
                       SUM(CASE WHEN m.quantity_change > 0 THEN m.quantity_change ELSE 0 END)  AS qty_in,
                       SUM(CASE WHEN m.quantity_change < 0 THEN -m.quantity_change ELSE 0 END) AS qty_out,
                       SUM(CASE WHEN m.quantity_change > 0 THEN ISNULL(m.total_value, 0) ELSE 0 END) AS value_in,
                       SUM(CASE WHEN m.quantity_change < 0 THEN ISNULL(m.total_value, 0) ELSE 0 END) AS value_out,
                       COUNT(*) AS cnt,
                       MAX(m.created_at) AS last_move
                FROM inventory_movements m" + FilterWhere + @"
                GROUP BY m.product_id";

            var result = new Dictionary<int, InventoryItemSummary>();

            using var cmd = new SqlCommand(sql, conn);
            AddFilterParams(cmd, f);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = Convert.ToInt32(r["product_id"]);
                result[id] = new InventoryItemSummary
                {
                    ProductId = id,
                    ItemName = r["item_name"]?.ToString() ?? "",
                    Unit = r["unit"] == DBNull.Value ? "" : r["unit"].ToString() ?? "",
                    QtyIn = Convert.ToDecimal(r["qty_in"]),
                    QtyOut = Convert.ToDecimal(r["qty_out"]),
                    ValueIn = Convert.ToDecimal(r["value_in"]),
                    ValueOut = Convert.ToDecimal(r["value_out"]),
                    MovementCount = Convert.ToInt32(r["cnt"]),
                    LastMovement = r["last_move"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["last_move"])
                };
            }

            return result;
        }

        /// <summary>
        /// Average quantity used in sales per day, per item. Only the days since sale tracking began
        /// are counted, so a period that starts before tracking does not water the average down.
        /// </summary>
        private static Dictionary<int, decimal> LoadUsagePerDay(SqlConnection conn, InventoryReportFilter f)
        {
            var perDay = new Dictionary<int, decimal>();

            DateTime? firstUsage;
            using (var cmd = new SqlCommand("SELECT MIN(created_at) FROM inventory_movements WHERE movement_type = 'Sale Usage'", conn))
            {
                object? v = cmd.ExecuteScalar();
                firstUsage = v == null || v == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(v);
            }

            if (!firstUsage.HasValue) return perDay;

            DateTime today = DateTime.Today;
            DateTime windowStart = f.From.Date;
            if (firstUsage.Value.Date > windowStart) windowStart = firstUsage.Value.Date;
            DateTime windowEnd = f.To.Date > today ? today : f.To.Date;

            double days = Math.Max(1d, (windowEnd - windowStart).TotalDays + 1d);

            const string sql = @"
                SELECT product_id, SUM(-quantity_change) AS used
                FROM inventory_movements
                WHERE movement_type = 'Sale Usage'
                  AND created_at >= @from AND created_at < @toExcl
                GROUP BY product_id";

            using var usageCmd = new SqlCommand(sql, conn);
            usageCmd.Parameters.Add("@from", SqlDbType.DateTime).Value = windowStart;
            usageCmd.Parameters.Add("@toExcl", SqlDbType.DateTime).Value = f.To.Date.AddDays(1);

            using var r = usageCmd.ExecuteReader();
            while (r.Read())
            {
                decimal used = r["used"] == DBNull.Value ? 0m : Convert.ToDecimal(r["used"]);
                perDay[Convert.ToInt32(r["product_id"])] = used / (decimal)days;
            }

            return perDay;
        }

        private static DateTime? LoadLiveTrackingStart(SqlConnection conn)
        {
            using var cmd = new SqlCommand(
                "SELECT MIN(created_at) FROM inventory_movements WHERE ISNULL(reference_type, '') <> 'Import'", conn);
            object? v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(v);
        }

        private static void LoadTypeBreakdown(SqlConnection conn, InventoryReportFilter f, InventoryReportViewModel vm)
        {
            string sql = @"
                SELECT m.movement_type, COUNT(*) AS cnt, SUM(ISNULL(m.total_value, 0)) AS val
                FROM inventory_movements m" + FilterWhere + @"
                GROUP BY m.movement_type";

            using var cmd = new SqlCommand(sql, conn);
            AddFilterParams(cmd, f);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string type = r["movement_type"]?.ToString() ?? "";
                int cnt = Convert.ToInt32(r["cnt"]);
                decimal val = r["val"] == DBNull.Value ? 0m : Convert.ToDecimal(r["val"]);

                switch (type)
                {
                    case MovementTypes.Restock:
                    case MovementTypes.InitialStock:
                        vm.RestockCount += cnt;
                        break;
                    case MovementTypes.SaleUsage:
                        vm.SaleUsageCount += cnt;
                        vm.SaleUsageValue += val;
                        break;
                    case MovementTypes.AdjustmentOut:
                        vm.ManualOutCount += cnt;
                        vm.ManualOutValue += val;
                        break;
                }
            }
        }

        private static List<InventoryDailyPoint> LoadDailyPoints(SqlConnection conn, InventoryReportFilter f)
        {
            string sql = @"
                SELECT CAST(m.created_at AS DATE) AS d,
                       SUM(CASE WHEN m.quantity_change > 0 THEN ISNULL(m.total_value, 0) ELSE 0 END) AS v_in,
                       SUM(CASE WHEN m.quantity_change < 0 THEN ISNULL(m.total_value, 0) ELSE 0 END) AS v_out
                FROM inventory_movements m" + FilterWhere + @"
                GROUP BY CAST(m.created_at AS DATE)
                ORDER BY d";

            var byDay = new Dictionary<DateTime, InventoryDailyPoint>();

            using (var cmd = new SqlCommand(sql, conn))
            {
                AddFilterParams(cmd, f);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    DateTime day = Convert.ToDateTime(r["d"]).Date;
                    byDay[day] = new InventoryDailyPoint
                    {
                        Day = day,
                        ValueIn = Convert.ToDecimal(r["v_in"]),
                        ValueOut = Convert.ToDecimal(r["v_out"])
                    };
                }
            }

            // Short periods show every day (including quiet ones); long periods show only days with activity.
            var points = new List<InventoryDailyPoint>();
            int span = (int)(f.To.Date - f.From.Date).TotalDays + 1;

            if (span > 0 && span <= 62)
            {
                for (DateTime d = f.From.Date; d <= f.To.Date; d = d.AddDays(1))
                    points.Add(byDay.TryGetValue(d, out var p) ? p : new InventoryDailyPoint { Day = d });
            }
            else
            {
                points.AddRange(byDay.Values.OrderBy(p => p.Day));
            }

            return points;
        }

        // ---------------------------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------------------------

        private static SqlParameter Dec(string name, byte precision, byte scale, decimal? value)
        {
            return new SqlParameter(name, SqlDbType.Decimal)
            {
                Precision = precision,
                Scale = scale,
                Value = value.HasValue ? (object)value.Value : DBNull.Value
            };
        }

        private static decimal? NullableDecimal(object value)
        {
            return value == DBNull.Value ? (decimal?)null : Convert.ToDecimal(value);
        }

        private static string? Cut(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
