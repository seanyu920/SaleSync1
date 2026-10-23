using System;
using System.Collections.Generic;
using System.Linq;

namespace SaleSync.Models
{
    public class DailyReportViewModel
    {
        // Which day this report covers (defaults to today)
        public DateTime ReportDate { get; set; } = DateTime.Today;
        public bool IsToday => ReportDate.Date == DateTime.Today;

        // Headline numbers
        public decimal TotalSales { get; set; }
        public decimal TotalCost { get; set; }
        public decimal GrossProfit => TotalSales - TotalCost;
        public decimal ProfitMarginPercent => TotalSales > 0 ? Math.Round(GrossProfit / TotalSales * 100m, 1) : 0m;

        public int TransactionCount { get; set; }
        public decimal AverageSale => TransactionCount > 0 ? TotalSales / TransactionCount : 0m;
        public int TotalUnitsSold => ProductsSold.Sum(p => p.QuantitySold);

        // Comparison with the day before
        public decimal PreviousDaySales { get; set; }
        public decimal? SalesChangePercent => PreviousDaySales > 0
            ? Math.Round((TotalSales - PreviousDaySales) / PreviousDaySales * 100m, 1)
            : (decimal?)null;

        // Breakdowns
        public List<ProductSalesItem> ProductsSold { get; set; } = new List<ProductSalesItem>();
        public List<PaymentBreakdownItem> PaymentBreakdown { get; set; } = new List<PaymentBreakdownItem>();

        // Restocking
        public List<InventoryPurchaseItem> IngredientsBought { get; set; } = new List<InventoryPurchaseItem>();
        public decimal TotalInventorySpend { get; set; }
        public decimal SalesLessRestocking => TotalSales - TotalInventorySpend;
    }

    public class ProductSalesItem
    {
        public string ProductName { get; set; }
        public int QuantitySold { get; set; }
        public decimal TotalRevenue { get; set; }
    }

    public class PaymentBreakdownItem
    {
        public string Method { get; set; }
        public int TransactionCount { get; set; }
        public decimal Total { get; set; }
    }

    public class InventoryPurchaseItem
    {
        public string ItemName { get; set; }
        public decimal QuantityBought { get; set; }   // decimal so 0.5 kg etc. isn't rounded
        public decimal TotalCost { get; set; }
    }
}
