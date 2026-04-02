namespace SaleSync.Models
{
    public class InventoryItems
    {
        public int ProductId { get; set; }

        public string ItemCategory { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string ItemID { get; set; } = "";
        public decimal PurchasePrice { get; set; }
        public int Quantity { get; set; }
        public string StockLevel { get; set; } = "";
        public string StockSupplier { get; set; } = "";
        public string DateAcquired { get; set; } = "";
        public string ExpirationDate { get; set; } = "";
    }
}