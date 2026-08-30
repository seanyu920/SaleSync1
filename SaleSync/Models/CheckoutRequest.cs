using System.Collections.Generic;

namespace SaleSync.Models
{
    public class CheckoutRequest
    {
        public List<SaleItemViewModel> Items { get; set; } = new();

        public string PaymentMethod { get; set; } = "cash";

        public decimal AmountPaid { get; set; }

        public string? PromoCode { get; set; }
    }
}