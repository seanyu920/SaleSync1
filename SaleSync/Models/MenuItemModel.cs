namespace SaleSync.Models
{

    public class MenuItemModel
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public string CategoryName { get; set; }
        public decimal Price { get; set; }
        public string ImagePath { get; set; }


        public string Description { get; set; }
        public string ImageUrl { get; set; }
    }

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

 
    public class ComprehensiveItemModel
    {
        public string ProductName { get; set; }
        public string Size { get; set; }
        public decimal Price { get; set; }
        public string CategoryName { get; set; }


        public string RecipeDetails { get; set; }

        public List<RecipeItem> Ingredients { get; set; }
    }
}