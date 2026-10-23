using System.Collections.Generic;
using SaleSync.Models;

namespace SaleSync.Models
{
    public class ArchivedCategoryModel
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; }

        // Menu items that were archived together with this category (they come back on Restore).
        public int ItemCount { get; set; }
    }

    public class ArchiveViewModel
    {
        public List<MenuItemModel> ArchivedProducts { get; set; } = new List<MenuItemModel>();
        public List<InventoryItems> ArchivedIngredients { get; set; } = new List<InventoryItems>();
        public List<ArchivedCategoryModel> ArchivedCategories { get; set; } = new List<ArchivedCategoryModel>();
    }
}
