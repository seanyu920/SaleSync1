using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SaleSync.Models;

namespace SaleSync.Services
{
    /// <summary>Thrown when a receipt submission is refused; the message is safe to show the customer.</summary>
    public class PaymentProofException : Exception
    {
        public PaymentProofException(string message) : base(message) { }
    }

    /// <summary>
    /// Manual-assisted payment verification for GCash / Maya orders.
    ///
    /// How it works:
    ///  1. The customer attaches a receipt photo and types the reference number at checkout.
    ///  2. The order is saved with status "Verifying Payment", so it does NOT appear in the
    ///     kitchen queue yet.
    ///  3. A cashier compares the receipt with the café's own GCash/Maya account and taps
    ///     Confirm (order becomes "Pending" and enters the kitchen queue) or Reject with a reason
    ///     (order becomes "Cancelled" and the customer sees the reason).
    ///
    /// Protections: a reference number or a receipt image can't be used twice, a customer can only
    /// have a few unverified orders open at once, the image type is checked from its real bytes,
    /// and every confirm/reject is written to the Activity Log with who did it.
    ///
    /// The payment_proofs table is created automatically the first time it is needed.
    /// </summary>
    public class PaymentVerificationService
    {
        // NOTE: sales.status is only 20 characters wide, so this value must stay at 20 characters or fewer.
        public const string AwaitingStatus = "Verifying Payment";
        public const string ConfirmedOrderStatus = "Pending";
        public const string RejectedOrderStatus = "Cancelled";

        public const int MaxAwaitingPerCustomer = 3;
        public const int MaxReceiptBytes = 5 * 1024 * 1024;

        private readonly string _connectionString;
        private readonly ILogger<PaymentVerificationService> _logger;

        private static readonly object SchemaLock = new object();
        private static bool _schemaReady;

        public PaymentVerificationService(IConfiguration configuration, ILogger<PaymentVerificationService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
        }

        // ---------------------------------------------------------------------------------
        // Input rules (static so the controller can use them before touching the database)
        // ---------------------------------------------------------------------------------

        /// <summary>True for the payment methods that need a receipt (GCash and Maya).</summary>
        public static bool RequiresProof(string? method)
        {
            string m = (method ?? "").Trim();
            return m.Equals("GCash", StringComparison.OrdinalIgnoreCase)
                || m.Equals("Maya", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Returns "GCash" or "Maya" with the standard spelling.</summary>
        public static string CleanMethodName(string? method)
        {
            return (method ?? "").Trim().Equals("Maya", StringComparison.OrdinalIgnoreCase) ? "Maya" : "GCash";
        }

        /// <summary>Removes spaces and dashes and upper-cases, so "1234 567 890123" and "1234567890123" match.</summary>
        public static string NormalizeReference(string? raw)
        {
            return Regex.Replace(raw ?? "", @"[\s\-]", "").ToUpperInvariant();
        }

        /// <summary>
        /// Returns an error message when the reference number looks wrong, or null when it is fine.
        /// A GCash reference number is 13 digits. If GCash ever changes that, change it here.
        /// </summary>
        public static string? ValidateReference(string method, string normalizedReference)
        {
            if (CleanMethodName(method) == "GCash")
            {
                return Regex.IsMatch(normalizedReference, @"^\d{13}$")
                    ? null
                    : "A GCash reference number has 13 digits. Copy it from your receipt (it may be shown as 1234 567 890123).";
            }

            return Regex.IsMatch(normalizedReference, @"^[A-Z0-9]{6,32}$")
                ? null
                : "Please enter the reference number exactly as shown on your Maya receipt.";
        }

        /// <summary>Works out the real image type from the first bytes. Returns null when it is not JPEG, PNG or WebP.</summary>
        public static string? DetectImageType(byte[] h)
        {
            if (h == null || h.Length < 12) return null;

            if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
            if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "image/png";
            if (h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
                && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return "image/webp";

            return null;
        }

        public static string HashImage(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        // ---------------------------------------------------------------------------------
        // Schema
        // ---------------------------------------------------------------------------------

        /// <summary>Makes sure the payment_proofs table exists. Returns false when it could not be created.</summary>
        public bool EnsureSchema()
        {
            if (_schemaReady) return true;

            lock (SchemaLock)
            {
                if (_schemaReady) return true;

                try
                {
                    using var conn = new SqlConnection(_connectionString);
                    conn.Open();

                    bool existed;
                    using (var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID('payment_proofs', 'U') IS NULL THEN 0 ELSE 1 END", conn))
                        existed = Convert.ToInt32(cmd.ExecuteScalar()) == 1;

                    if (!existed)
                    {
                        Run(conn, @"
                            CREATE TABLE payment_proofs (
                                proof_id         INT IDENTITY(1,1) PRIMARY KEY,
                                sale_id          INT            NOT NULL,
                                payment_method   NVARCHAR(20)   NOT NULL,
                                reference_number NVARCHAR(40)   NOT NULL,
                                amount_due       DECIMAL(18,2)  NOT NULL,
                                image_data       VARBINARY(MAX) NOT NULL,
                                image_type       NVARCHAR(30)   NOT NULL,
                                image_hash       CHAR(64)       NOT NULL,
                                status           NVARCHAR(20)   NOT NULL CONSTRAINT DF_payment_proofs_status DEFAULT 'Pending',
                                submitted_at     DATETIME       NOT NULL CONSTRAINT DF_payment_proofs_submitted DEFAULT GETDATE(),
                                reviewed_by      INT            NULL,
                                reviewed_at      DATETIME       NULL,
                                review_note      NVARCHAR(300)  NULL
                            )");

                        Run(conn, "CREATE INDEX IX_payment_proofs_sale ON payment_proofs (sale_id)");
                        Run(conn, "CREATE INDEX IX_payment_proofs_ref ON payment_proofs (reference_number)");
                        Run(conn, "CREATE INDEX IX_payment_proofs_hash ON payment_proofs (image_hash)");
                        Run(conn, "CREATE INDEX IX_payment_proofs_status ON payment_proofs (status, submitted_at)");
                    }

                    _schemaReady = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not create or check the payment_proofs table.");
                    return false;
                }
            }
        }

        private static void Run(SqlConnection conn, string sql)
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        // ---------------------------------------------------------------------------------
        // Checkout side
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Runs inside the order transaction, before the order is saved. Returns a message to show
        /// the customer when the submission must be refused, or null when it is fine.
        /// The locking hints make sure two orders sent at the same moment can't both use one reference.
        /// </summary>
        public string? CheckBeforeSubmit(SqlConnection conn, SqlTransaction tx, int customerUserId, PaymentProofSubmission proof)
        {
            if (!EnsureSchema())
                return "Payment verification isn't available right now. Please try again in a moment, or choose Pay at Counter.";

            if (customerUserId > 0)
            {
                using var limitCmd = new SqlCommand(
                    "SELECT COUNT(*) FROM sales WHERE user_id = @uid AND status = @awaiting", conn, tx);
                limitCmd.Parameters.Add("@uid", SqlDbType.Int).Value = customerUserId;
                limitCmd.Parameters.Add("@awaiting", SqlDbType.NVarChar, 50).Value = AwaitingStatus;

                int open = Convert.ToInt32(limitCmd.ExecuteScalar());
                if (open >= MaxAwaitingPerCustomer)
                    return $"You already have {open} orders waiting for payment verification. Please wait for staff to confirm them before placing another.";
            }

            using (var refCmd = new SqlCommand(
                "SELECT TOP 1 1 FROM payment_proofs WITH (UPDLOCK, HOLDLOCK) WHERE reference_number = @ref AND status <> 'Rejected'", conn, tx))
            {
                refCmd.Parameters.Add("@ref", SqlDbType.NVarChar, 40).Value = proof.ReferenceNumber;
                if (refCmd.ExecuteScalar() != null)
                    return "This reference number has already been used for another order. Please check the number on your receipt.";
            }

            using (var hashCmd = new SqlCommand(
                "SELECT TOP 1 1 FROM payment_proofs WITH (UPDLOCK, HOLDLOCK) WHERE image_hash = @hash AND status <> 'Rejected'", conn, tx))
            {
                hashCmd.Parameters.Add("@hash", SqlDbType.Char, 64).Value = proof.ImageHash;
                if (hashCmd.ExecuteScalar() != null)
                    return "This receipt image has already been submitted for another order. Please attach the receipt for this payment.";
            }

            return null;
        }

        /// <summary>Saves the receipt for an order. Call it in the same transaction as the order itself.</summary>
        public void SaveProof(SqlConnection conn, SqlTransaction tx, int saleId, decimal amountDue, PaymentProofSubmission proof)
        {
            const string sql = @"
                INSERT INTO payment_proofs
                    (sale_id, payment_method, reference_number, amount_due, image_data, image_type, image_hash)
                VALUES
                    (@sid, @method, @ref, @amount, @img, @type, @hash)";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@sid", SqlDbType.Int).Value = saleId;
            cmd.Parameters.Add("@method", SqlDbType.NVarChar, 20).Value = proof.PaymentMethod;
            cmd.Parameters.Add("@ref", SqlDbType.NVarChar, 40).Value = proof.ReferenceNumber;

            var amount = cmd.Parameters.Add("@amount", SqlDbType.Decimal);
            amount.Precision = 18;
            amount.Scale = 2;
            amount.Value = amountDue;

            cmd.Parameters.Add("@img", SqlDbType.VarBinary, -1).Value = proof.ImageBytes;
            cmd.Parameters.Add("@type", SqlDbType.NVarChar, 30).Value = proof.ImageType;
            cmd.Parameters.Add("@hash", SqlDbType.Char, 64).Value = proof.ImageHash;
            cmd.ExecuteNonQuery();
        }

        /// <summary>For the customer's order history: why each of their payments was not accepted (sale id -> reason).</summary>
        public Dictionary<int, string> GetRejectionNotes(int customerUserId)
        {
            var notes = new Dictionary<int, string>();
            if (customerUserId <= 0 || !EnsureSchema()) return notes;

            try
            {
                const string sql = @"
                    SELECT p.sale_id, p.review_note
                    FROM payment_proofs p
                    JOIN sales s ON s.sale_id = p.sale_id
                    WHERE s.user_id = @uid AND p.status = 'Rejected'
                    ORDER BY p.proof_id";

                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@uid", SqlDbType.Int).Value = customerUserId;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string note = r["review_note"] as string ?? "";
                    if (!string.IsNullOrWhiteSpace(note))
                        notes[Convert.ToInt32(r["sale_id"])] = note;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load payment rejection notes.");
            }

            return notes;
        }

        // ---------------------------------------------------------------------------------
        // Cashier side
        // ---------------------------------------------------------------------------------

        /// <summary>Every order waiting for a receipt check, oldest first.</summary>
        public List<PendingPaymentItem> GetAwaiting()
        {
            var list = new List<PendingPaymentItem>();
            if (!EnsureSchema()) return list;

            try
            {
                const string sql = @"
                    SELECT p.proof_id, p.sale_id, p.payment_method, p.reference_number, p.amount_due, p.submitted_at,
                           s.customer_name, s.order_type,
                           (SELECT STRING_AGG(CAST(si.quantity AS VARCHAR(10)) + 'x ' + pr.product_name, ', ')
                            FROM sale_items si
                            JOIN products pr ON si.product_id = pr.product_id
                            WHERE si.sale_id = s.sale_id) AS items_summary
                    FROM payment_proofs p
                    JOIN sales s ON s.sale_id = p.sale_id
                    WHERE p.status = 'Pending' AND s.status = @awaiting
                    ORDER BY p.submitted_at ASC";

                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@awaiting", SqlDbType.NVarChar, 50).Value = AwaitingStatus;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new PendingPaymentItem
                    {
                        ProofId = Convert.ToInt32(r["proof_id"]),
                        SaleId = Convert.ToInt32(r["sale_id"]),
                        PaymentMethod = r["payment_method"]?.ToString() ?? "",
                        ReferenceNumber = r["reference_number"]?.ToString() ?? "",
                        AmountDue = Convert.ToDecimal(r["amount_due"]),
                        SubmittedAt = Convert.ToDateTime(r["submitted_at"]),
                        CustomerName = r["customer_name"] as string ?? "Online Guest",
                        OrderType = r["order_type"] as string ?? "Pick-up",
                        ItemsSummary = r["items_summary"] as string ?? "No items"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load orders awaiting payment verification.");
            }

            return list;
        }

        /// <summary>How many orders are waiting. Used by the Queue page to notice new receipts.</summary>
        public int CountAwaiting()
        {
            if (!EnsureSchema()) return 0;

            try
            {
                const string sql = @"
                    SELECT COUNT(*)
                    FROM payment_proofs p
                    JOIN sales s ON s.sale_id = p.sale_id
                    WHERE p.status = 'Pending' AND s.status = @awaiting";

                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@awaiting", SqlDbType.NVarChar, 50).Value = AwaitingStatus;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not count orders awaiting payment verification.");
                return 0;
            }
        }

        /// <summary>The receipt picture for one payment, or null if there is none.</summary>
        public (byte[] Data, string Type)? GetReceipt(int proofId)
        {
            if (!EnsureSchema()) return null;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = new SqlCommand("SELECT image_data, image_type FROM payment_proofs WHERE proof_id = @id", conn);
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = proofId;
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                byte[] data = (byte[])r["image_data"];
                string type = r["image_type"] as string ?? "image/jpeg";
                return (data, type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load receipt {ProofId}.", proofId);
                return null;
            }
        }

        /// <summary>Cashier confirmed the payment: the order enters the kitchen queue.</summary>
        public PaymentReviewResult Confirm(int proofId, int staffUserId)
        {
            return Review(proofId, staffUserId, approve: true, reason: null);
        }

        /// <summary>Cashier rejected the payment: the order is cancelled and the customer sees the reason.</summary>
        public PaymentReviewResult Reject(int proofId, int staffUserId, string reason)
        {
            return Review(proofId, staffUserId, approve: false, reason: reason);
        }

        private PaymentReviewResult Review(int proofId, int staffUserId, bool approve, string? reason)
        {
            if (!EnsureSchema())
                return PaymentReviewResult.Fail("Payment verification isn't available right now.");

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var tx = conn.BeginTransaction();

                int saleId;
                string status, method, reference;
                decimal amount;

                using (var cmd = new SqlCommand(@"
                    SELECT sale_id, status, payment_method, reference_number, amount_due
                    FROM payment_proofs WITH (UPDLOCK, ROWLOCK)
                    WHERE proof_id = @id", conn, tx))
                {
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = proofId;
                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                        return PaymentReviewResult.Fail("That payment could not be found.");

                    saleId = Convert.ToInt32(r["sale_id"]);
                    status = r["status"]?.ToString() ?? "";
                    method = r["payment_method"]?.ToString() ?? "";
                    reference = r["reference_number"]?.ToString() ?? "";
                    amount = Convert.ToDecimal(r["amount_due"]);
                }

                if (status != "Pending")
                    return PaymentReviewResult.Fail("This payment was already reviewed by someone else.");

                using (var cmd = new SqlCommand(@"
                    UPDATE payment_proofs
                    SET status = @newStatus, reviewed_by = @staff, reviewed_at = GETDATE(), review_note = @note
                    WHERE proof_id = @id", conn, tx))
                {
                    cmd.Parameters.Add("@newStatus", SqlDbType.NVarChar, 20).Value = approve ? "Verified" : "Rejected";
                    cmd.Parameters.Add("@staff", SqlDbType.Int).Value = staffUserId > 0 ? (object)staffUserId : DBNull.Value;
                    cmd.Parameters.Add("@note", SqlDbType.NVarChar, 300).Value =
                        approve || string.IsNullOrWhiteSpace(reason) ? DBNull.Value : (object)Cut(reason!.Trim(), 300);
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = proofId;
                    cmd.ExecuteNonQuery();
                }

                int changed;
                using (var cmd = new SqlCommand(
                    "UPDATE sales SET status = @newStatus WHERE sale_id = @sid AND status = @awaiting", conn, tx))
                {
                    cmd.Parameters.Add("@newStatus", SqlDbType.NVarChar, 50).Value = approve ? ConfirmedOrderStatus : RejectedOrderStatus;
                    cmd.Parameters.Add("@sid", SqlDbType.Int).Value = saleId;
                    cmd.Parameters.Add("@awaiting", SqlDbType.NVarChar, 50).Value = AwaitingStatus;
                    changed = cmd.ExecuteNonQuery();
                }

                if (changed == 0)
                    return PaymentReviewResult.Fail("This order is no longer waiting for verification (it may have been voided).");

                tx.Commit();

                string who = approve ? "Payment Verified" : "Payment Rejected";
                string details = approve
                    ? $"Confirmed {method} payment for Order #ORD-{saleId} (ref {reference}, PHP {amount:0.00})."
                    : $"Rejected {method} payment for Order #ORD-{saleId} (ref {reference}, PHP {amount:0.00}). Reason: {reason?.Trim()}";
                WriteActivityLog(staffUserId, who, details);

                return PaymentReviewResult.Success(approve
                    ? $"Payment confirmed. Order #ORD-{saleId} is now in the kitchen queue."
                    : $"Payment rejected. Order #ORD-{saleId} was cancelled and the customer will see your reason.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment review failed for proof {ProofId}.", proofId);
                return PaymentReviewResult.Fail("Something went wrong while saving. Please try again.");
            }
        }

        private void WriteActivityLog(int userId, string action, string details)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = new SqlCommand(
                    "INSERT INTO ActivityLogs (UserId, ActionType, Details) VALUES (@uid, @action, @details)", conn);
                cmd.Parameters.Add("@uid", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@action", SqlDbType.NVarChar, 100).Value = action;
                cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // The payment was already saved; a missing log line must not undo it.
                _logger.LogWarning(ex, "Could not write the activity log entry '{Action}'.", action);
            }
        }

        private static string Cut(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
