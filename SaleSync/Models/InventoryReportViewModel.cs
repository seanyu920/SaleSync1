using System;
using System.Collections.Generic;
using System.Linq;

namespace SaleSync.Models
{
    /// <summary>One line in the stock movement log (stock going in or out of an item).</summary>
    public class InventoryMovementRow
    {
        public int MovementId { get; set; }
        public DateTime Date { get; set; }
        public int ProductId { get; set; }
        public string ItemName { get; set; } = "";
        public string Unit { get; set; } = "";
        public string MovementType { get; set; } = "";

        /// <summary>Positive = stock in, negative = stock out. Always in the item's stock unit.</summary>
        public decimal QuantityChange { get; set; }
        public decimal? QuantityBefore { get; set; }
        public decimal? QuantityAfter { get; set; }

        public decimal? UnitCost { get; set; }

        /// <summary>Peso value of the movement (always a positive amount).</summary>
        public decimal? TotalValue { get; set; }

        public string Supplier { get; set; } = "";
        public string ReferenceType { get; set; } = "";
        public int? ReferenceId { get; set; }
        public string PerformedBy { get; set; } = "";
        public string Notes { get; set; } = "";

        public bool IsIn => QuantityChange > 0;

        /// <summary>True for a detail-only edit (no stock moved).</summary>
        public bool IsEdit => QuantityChange == 0;

        /// <summary>Readable reference such as "Order #ORD-42"; empty when there is none.</summary>
        public string ReferenceLabel
        {
            get
            {
                if (ReferenceType == "Sale" && ReferenceId.HasValue) return $"Order #ORD-{ReferenceId.Value}";
                if (ReferenceType == "Import") return "Purchase history";
                return "";
            }
        }
    }

    /// <summary>Totals for one inventory item over the selected period.</summary>
    public class InventoryItemSummary
    {
        public int ProductId { get; set; }
        public string ItemName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Unit { get; set; } = "";

        /// <summary>Live stock on hand right now. Null when the item is archived or deleted.</summary>
        public decimal? CurrentStock { get; set; }

        public decimal QtyIn { get; set; }
        public decimal QtyOut { get; set; }
        public decimal NetChange => QtyIn - QtyOut;

        public decimal ValueIn { get; set; }
        public decimal ValueOut { get; set; }

        public int MovementCount { get; set; }
        public DateTime? LastMovement { get; set; }

        /// <summary>Average quantity used in sales per day, over the period tracked.</summary>
        public decimal AvgDailyUse { get; set; }

        /// <summary>Estimated days until the item runs out, based on AvgDailyUse. Null when unknown.</summary>
        public decimal? DaysLeft { get; set; }

        public string StockLevel { get; set; } = "";   // "Out", "Low", "OK" or "" when unknown
    }

    /// <summary>One item that needs attention right now (low, out, expiring, expired).</summary>
    public class InventoryAlertItem
    {
        public string Kind { get; set; } = "";         // "Out of stock", "Low stock", "Expired", "Expiring soon"
        public string ItemName { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Quantity { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public int? DaysToExpiry { get; set; }
    }

    /// <summary>One bar pair in the "stock value in vs out" chart.</summary>
    public class InventoryDailyPoint
    {
        public DateTime Day { get; set; }
        public decimal ValueIn { get; set; }
        public decimal ValueOut { get; set; }
    }

    /// <summary>What the person picked in the filter bar.</summary>
    public class InventoryReportFilter
    {
        public DateTime From { get; set; }

        /// <summary>Last day to include (the whole day is included).</summary>
        public DateTime To { get; set; }

        /// <summary>"all", "in", "out", "restock", "initial", "sale", "adjustment" or "edit".</summary>
        public string Type { get; set; } = "all";

        public int? ProductId { get; set; }

        /// <summary>Most rows to return in the on-screen log. 0 means no limit (used for CSV).</summary>
        public int MaxRows { get; set; } = 1000;
    }

    public class InventoryReportViewModel
    {
        // Filters as applied
        public string Range { get; set; } = "30d";
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Type { get; set; } = "all";
        public int? ProductId { get; set; }
        public int LowStockThreshold { get; set; } = 10;

        // Dropdown source
        public List<InventoryItemOption> ItemOptions { get; set; } = new List<InventoryItemOption>();

        // Headline numbers for the period
        public decimal TotalValueIn { get; set; }
        public decimal TotalValueOut { get; set; }
        public decimal NetValueChange => TotalValueIn - TotalValueOut;
        public int MovementCount { get; set; }
        public int ItemsAffected { get; set; }

        public int RestockCount { get; set; }
        public int SaleUsageCount { get; set; }
        public int ManualOutCount { get; set; }

        /// <summary>Edits that changed details (price, supplier, dates...) but not the quantity.</summary>
        public int DetailEditCount { get; set; }

        /// <summary>Value of what was used for sales, and what was reduced by hand.</summary>
        public decimal SaleUsageValue { get; set; }
        public decimal ManualOutValue { get; set; }

        public string TopUsedItem { get; set; } = "";
        public decimal TopUsedItemValue { get; set; }

        // Data coverage: stock-out tracking only exists from this moment onward.
        public DateTime? LiveTrackingSince { get; set; }

        /// <summary>True when the movement table could not be created or reached.</summary>
        public bool LedgerUnavailable { get; set; }

        // Detail
        public List<InventoryMovementRow> Movements { get; set; } = new List<InventoryMovementRow>();
        public bool MovementsTruncated { get; set; }
        public int TotalMatchingMovements { get; set; }

        public List<InventoryItemSummary> ItemSummaries { get; set; } = new List<InventoryItemSummary>();
        public List<InventoryAlertItem> Alerts { get; set; } = new List<InventoryAlertItem>();
        public List<InventoryDailyPoint> DailyPoints { get; set; } = new List<InventoryDailyPoint>();

        public int IdleItemCount => ItemSummaries.Count(i => i.MovementCount == 0);
    }

    public class InventoryItemOption
    {
        public int ProductId { get; set; }
        public string ItemName { get; set; } = "";
    }
}
