namespace SaleSync.Models
{
    public class WebCustomization
    {

        public string StoreName { get; set; } = "Cafero";
        public string StoreTagline { get; set; } = "Coffee & Pastries";
        public string BusinessAddress { get; set; } = "";
        public string ContactNumber { get; set; } = "";
        public string TaxIdNumber { get; set; } = "";
        public string ReceiptFooter { get; set; } = "";
        public string? LogoPath { get; set; }


        public string CurrencySymbol { get; set; } = "₱";
        public decimal VatRate { get; set; } = 12.00m;
        public int LowStockThreshold { get; set; } = 10;


        public string OpeningTime { get; set; } = "07:00";
        public string ClosingTime { get; set; } = "21:00";

        public bool IsTemporarilyClosed { get; set; } = false;


        public bool StaffOnBreak { get; set; } = false;


        public string PrimaryColor { get; set; } = "#C9A84C";
        public string SidebarColor { get; set; } = "#141A14";
        public string AccentColor { get; set; } = "#C9A84C";
        public string BackgroundColor { get; set; } = "#0D110D";
        public string SidebarPosition { get; set; } = "left";
    }


    public class StoreStatus
    {
        public bool IsOpen { get; set; }
        public string Label { get; set; } = "";
        public string HoursLabel { get; set; } = "";
        public string OpeningTimeLabel { get; set; } = "";
        public string ClosingTimeLabel { get; set; } = "";
    }
}