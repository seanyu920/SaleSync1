namespace SaleSync.Models
{
    public class SaleHistoryItem
    {
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public string CashierName { get; set; }
        public string ItemsSummary { get; set; }
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = "Completed";

        // ⭐ NEW: Properties for Online & Delivery Orders
        public string CustomerName { get; set; }
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
    }

    public class CashierDashboardViewModel
    {
        public decimal TodayTotalSales { get; set; }
        public int TodayTransactionCount { get; set; }
        public List<SaleHistoryItem> RecentSales { get; set; }
    }
}