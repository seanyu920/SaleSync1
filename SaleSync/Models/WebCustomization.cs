namespace SaleSync.Models
{
    public class WebCustomization
    {
        // --- Store Branding & Receipts ---
        public string StoreName { get; set; } = "Cafero";
        public string StoreTagline { get; set; } = "Coffee & Pastries";
        public string BusinessAddress { get; set; } = "";
        public string ContactNumber { get; set; } = "";
        public string TaxIdNumber { get; set; } = "";
        public string ReceiptFooter { get; set; } = "";
        public string? LogoPath { get; set; }

        // --- POS & System Defaults ---
        public string CurrencySymbol { get; set; } = "₱";
        public decimal VatRate { get; set; } = 12.00m;
        public int LowStockThreshold { get; set; } = 10;

        // --- Business Hours ---
        // Stored as "HH:mm" (24-hour) to work directly with <input type="time">.
        public string OpeningTime { get; set; } = "07:00";
        public string ClosingTime { get; set; } = "21:00";
        // Manual override: when true, the store shows as closed regardless of the schedule above.
        public bool IsTemporarilyClosed { get; set; } = false;

        // --- UI Web Theme Colors ---
        public string PrimaryColor { get; set; } = "#4a2511";   // Default Warm Coffee Brown
        public string SidebarColor { get; set; } = "#2b1b17";   // Default Deep Dark Charcoal
        public string AccentColor { get; set; } = "#b58361";    // Default Caramel Accent
        public string BackgroundColor { get; set; } = "#fdfbf7"; // Default Cream Background
        public string SidebarPosition { get; set; } = "left";   // Sidebar alignment: left or right
    }

    // Lightweight result describing whether the store is currently open, for use on dashboards.
    public class StoreStatus
    {
        public bool IsOpen { get; set; }
        public string Label { get; set; } = "";              // e.g. "Open" or "Closed"
        public string HoursLabel { get; set; } = "";          // e.g. "7:00 AM – 9:00 PM"
        public string OpeningTimeLabel { get; set; } = "";     // e.g. "7:00 AM"
        public string ClosingTimeLabel { get; set; } = "";     // e.g. "9:00 PM"
    }
}