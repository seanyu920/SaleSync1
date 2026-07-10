namespace SaleSync.Models
{
    // 1. The Customer-Facing Model (Lean and clean)
    public class MenuItemModel
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public string CategoryName { get; set; }
        public decimal Price { get; set; }
        public string ImagePath { get; set; }

        // Optional: Include image or description for the frontend
        public string Description { get; set; }
        public string ImageUrl { get; set; }
    }

    // 2. The Admin-Facing Recipe Models (For your inventory management)
    public class RecipeItem
    {
        public int IngredientId { get; set; }
        public decimal Quantity { get; set; }
    }

    public class RecipeSaveRequest
    {
        public int ProductId { get; set; }
        public List<RecipeItem> Ingredients { get; set; }
    }

    // 3. The Admin Comprehensive View (When creating a brand new item + recipe)
    public class ComprehensiveItemModel
    {
        public string ProductName { get; set; }
        public string Size { get; set; }
        public decimal Price { get; set; }
        public string CategoryName { get; set; }

        // This holds the recipe text/instructions for now
        public string RecipeDetails { get; set; }

        // This holds the list of ingredients they filled out in the boxes!
        public List<RecipeItem> Ingredients { get; set; }
    }
}