using System;
using System.Collections.Generic;

namespace SaleSync.Models
{
    public class TransactionHistoryViewModel
    {
        public List<SaleHistoryItem> Sales { get; set; } = new List<SaleHistoryItem>();
        public List<string> PaymentMethods { get; set; } = new List<string>();

      
        public string? Search { get; set; }
        public string? Status { get; set; }
        public string? Method { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }

   
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

  
        public decimal CompletedTotal { get; set; }

        public bool HasFilters =>
            !string.IsNullOrWhiteSpace(Search) ||
            !string.IsNullOrWhiteSpace(Status) ||
            !string.IsNullOrWhiteSpace(Method) ||
            From.HasValue || To.HasValue;
    }
}