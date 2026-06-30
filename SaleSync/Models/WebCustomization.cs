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

        // --- POS & System Defaults ---
        public string CurrencySymbol { get; set; } = "₱";
        public decimal VatRate { get; set; } = 12.00m;
        public int LowStockThreshold { get; set; } = 10;

        // --- UI Web Theme Colors ---
        public string PrimaryColor { get; set; } = "#4a2511";   // Default Warm Coffee Brown
        public string SidebarColor { get; set; } = "#2b1b17";   // Default Deep Dark Charcoal
        public string AccentColor { get; set; } = "#b58361";    // Default Caramel Accent
        public string BackgroundColor { get; set; } = "#fdfbf7"; // Default Cream Background
        public string SidebarPosition { get; set; } = "left";   // Sidebar alignment: left or right
    }
}