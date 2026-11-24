namespace SaleSync.Models
{
    public class InventoryItems
    {
        public int ProductId { get; set; }
        public string ItemID { get; set; }
        public string ItemName { get; set; }
        public string RecipeUnit { get; set; }
        public double ConversionFactor { get; set; }

        // Our precise math variable
        public double Quantity { get; set; }

        public string Unit { get; set; }
        public decimal PurchasePrice { get; set; }
        public string ItemCategory { get; set; }

        public string StockSupplier { get; set; }
        public string DateAcquired { get; set; }
        public string ExpirationDate { get; set; }

        /// <summary>Why the item is being changed. Required when updating an existing item; saved to the stock report.</summary>
        public string ChangeReason { get; set; }
    }
}