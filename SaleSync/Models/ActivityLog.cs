namespace SaleSync.Models
{
    public class ActivityLog
    {
        public int LogId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Username { get; set; }
        public string Role { get; set; }
        public string ActionType { get; set; }
        public string Details { get; set; }


        public string OldValues { get; set; }
        public string NewValues { get; set; }
    }
}