using System;
using System.Collections.Generic;

namespace SaleSync.Models
{

    public class OnlineOrderRequest
    {
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public DateTime? PickupTime { get; set; }
        public List<OnlineOrderItem> Items { get; set; }
    }

    public class OnlineOrderItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }


    public class CustomerOrderModel
    {
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } 
        public string OrderType { get; set; } 
    }
}