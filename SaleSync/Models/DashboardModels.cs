namespace SaleSync.Models
{
    public class DashboardBestSeller
    {
        public string ProductName { get; set; } = "";
        public int UnitsSold { get; set; }
        public decimal Revenue { get; set; }
    }

    public class DashboardLowStockItem
    {
        public string ItemName { get; set; } = "";
        public double Quantity { get; set; }
        public string Unit { get; set; } = "";

        // True when stock is at or below half of the low-stock threshold.
        public bool IsCritical { get; set; }
    }
}
