namespace SaleSync.Models
{
    /// <summary>What a customer sends along with a GCash / Maya order.</summary>
    public class PaymentProofSubmission
    {
        /// <summary>"GCash" or "Maya".</summary>
        public string PaymentMethod { get; set; } = "";

        /// <summary>Reference number with spaces/dashes removed and upper-cased.</summary>
        public string ReferenceNumber { get; set; } = "";

        public byte[] ImageBytes { get; set; } = Array.Empty<byte>();

        /// <summary>Detected from the file's real bytes, never taken from the browser.</summary>
        public string ImageType { get; set; } = "image/jpeg";

        /// <summary>SHA-256 of the image, used to catch the same screenshot being reused.</summary>
        public string ImageHash { get; set; } = "";
    }

    /// <summary>One order waiting for a cashier to check its receipt.</summary>
    public class PendingPaymentItem
    {
        public int ProofId { get; set; }
        public int SaleId { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string ReferenceNumber { get; set; } = "";
        public decimal AmountDue { get; set; }
        public DateTime SubmittedAt { get; set; }
        public string CustomerName { get; set; } = "";
        public string OrderType { get; set; } = "";
        public string ItemsSummary { get; set; } = "";

        /// <summary>The reference number grouped the way the GCash app shows it (1234 567 890123).</summary>
        public string DisplayReference
        {
            get
            {
                string r = ReferenceNumber ?? "";
                bool allDigits = r.Length == 13 && r.All(char.IsDigit);
                return allDigits ? $"{r.Substring(0, 4)} {r.Substring(4, 3)} {r.Substring(7)}" : r;
            }
        }
    }

    public class PaymentReviewResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";

        public static PaymentReviewResult Success(string message) => new PaymentReviewResult { Ok = true, Message = message };
        public static PaymentReviewResult Fail(string message) => new PaymentReviewResult { Ok = false, Message = message };
    }

    public class PaymentReviewRequest
    {
        public int ProofId { get; set; }
        public string? Reason { get; set; }
    }
}
