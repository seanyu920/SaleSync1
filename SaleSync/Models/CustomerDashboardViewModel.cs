using System.Collections.Generic;

namespace SaleSync.Models
{
    public class CustomerDashboardViewModel
    {
        public List<MenuItemModel> MenuItems { get; set; } = new List<MenuItemModel>();
        public List<CustomerOrderModel> OrderHistory { get; set; } = new List<CustomerOrderModel>();
    }
}