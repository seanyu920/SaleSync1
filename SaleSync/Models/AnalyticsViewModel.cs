using System.Collections.Generic;

namespace SaleSync.Models
{
    public class AnalyticsViewModel
    {
        public string SelectedTimeframe { get; set; }

        // KPI Summary Cards
        public decimal TotalRevenue { get; set; }
        public int TotalTransactions { get; set; }
        public decimal AverageOrderValue { get; set; }

        // Chart Data
        public List<string> SalesLabels { get; set; } = new List<string>();
        public List<decimal> SalesValues { get; set; } = new List<decimal>();

        // New Peak Hours Chart Arrays
        public List<string> PeakHourLabels { get; set; } = new List<string>();
        public List<int> PeakHourOrderCounts { get; set; } = new List<int>();

        public List<BestSellerItem> BestSellers { get; set; } = new List<BestSellerItem>();
        public List<string> PaymentLabels { get; set; } = new List<string>();
        public List<int> PaymentValues { get; set; } = new List<int>();
    } // <-- This closes AnalyticsViewModel

    // 💡 Added right here so your List<BestSellerItem> recognizes it!
    public class BestSellerItem
    {
        public string ProductName { get; set; }
        public int TotalSold { get; set; }
    }
}