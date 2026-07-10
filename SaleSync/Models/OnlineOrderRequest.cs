using System;
using System.Collections.Generic;

namespace SaleSync.Models
{
    // ==========================================
    // MODELS FOR PLACING ORDERS (Frontend -> Backend)
    // ==========================================

    // This acts as a blueprint for the incoming JSON from the checkout cart
    public class OnlineOrderRequest
    {
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public List<OnlineOrderItem> Items { get; set; }
    }

    public class OnlineOrderItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }

    // ==========================================
    // MODEL FOR VIEWING ORDERS (Backend -> Frontend)
    // ==========================================

    // This acts as a blueprint for displaying order history back to the customer
    public class CustomerOrderModel
    {
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } // e.g., Pending, Preparing, Completed
        public string OrderType { get; set; } // Pick-up or Delivery
    }
}