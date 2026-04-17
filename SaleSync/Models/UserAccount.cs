namespace SaleSync.Models
{
    public class UserAccount
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = ""; // <-- Added this for your HTML table!
        public string Role { get; set; } = "";
    }
}