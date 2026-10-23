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


        public string CustomerName { get; set; }
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public DateTime? PickupTime { get; set; }
    }

    public class CashierDashboardViewModel
    {
        public decimal TodayTotalSales { get; set; }
        public int TodayTransactionCount { get; set; }
        public List<SaleHistoryItem> RecentSales { get; set; }

 
        public int TodayAllTransactionCount { get; set; }
        public int PendingOrderCount { get; set; }

        public decimal WeeklySalesTotal { get; set; }
        public List<string> WeeklyLabels { get; set; } = new List<string>();
        public List<decimal> WeeklyValues { get; set; } = new List<decimal>();

        public List<DashboardBestSeller> BestSellers { get; set; } = new List<DashboardBestSeller>();

        public List<string> PaymentLabels { get; set; } = new List<string>();
        public List<int> PaymentValues { get; set; } = new List<int>();

        public int LowStockCount { get; set; }
        public List<DashboardLowStockItem> LowStockItems { get; set; } = new List<DashboardLowStockItem>();
    }
}