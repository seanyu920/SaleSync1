using System.Collections.Generic;

namespace SaleSync.Models
{
    // This acts as a blueprint for the incoming JSON from the checkout cart
    public class OnlineOrderRequest
    {
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; } // ⭐ NEW
        public List<OnlineOrderItem> Items { get; set; }
    }

    public class OnlineOrderItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }
}