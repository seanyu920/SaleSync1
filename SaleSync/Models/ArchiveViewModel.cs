using System.Collections.Generic;
using SaleSync.Models;

namespace SaleSync.Models
{
    public class ArchiveViewModel
    {
        public List<MenuItemModel> ArchivedProducts { get; set; } = new List<MenuItemModel>();
        public List<InventoryItems> ArchivedIngredients { get; set; } = new List<InventoryItems>();
    }
}