namespace SaleSync.Models
{
    public class MenuItemModel
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public string CategoryName { get; set; }
        public decimal Price { get; set; }

        // This will hold the recipe text/instructions for now
        public string RecipeDetails { get; set; }
        public class RecipeSaveRequest
        {
            public int ProductId { get; set; }
            public List<RecipeItem> Ingredients { get; set; }
        }

        public class RecipeItem
        {
            public int IngredientId { get; set; }
            public decimal Quantity { get; set; }
        }
        public class ComprehensiveItemModel
        {
            public string ProductName { get; set; }
            public string Size { get; set; }
            public decimal Price { get; set; }
            public string CategoryName { get; set; }

            // This holds the list of ingredients they filled out in the boxes!
            public List<RecipeItem> Ingredients { get; set; }
        }
    }
}