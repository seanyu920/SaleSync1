using System.Collections.Generic;

namespace SaleSync.Models
{
    public class DailyReportViewModel
    {
        public decimal TotalSales { get; set; }
        public decimal TotalCost { get; set; }
        public decimal NetProfit => TotalSales - TotalCost;
        public int TransactionCount { get; set; }
        public List<ProductSalesItem> ProductsSold { get; set; } = new List<ProductSalesItem>();


        public List<InventoryPurchaseItem> IngredientsBought { get; set; } = new List<InventoryPurchaseItem>();
        public decimal TotalInventorySpend { get; set; }
    }

    public class ProductSalesItem
    {
        public string ProductName { get; set; }
        public int QuantitySold { get; set; }
        public decimal TotalRevenue { get; set; }
    }


    public class InventoryPurchaseItem
    {
        public string ItemName { get; set; }
        public int QuantityBought { get; set; }
        public decimal TotalCost { get; set; }
    }
}