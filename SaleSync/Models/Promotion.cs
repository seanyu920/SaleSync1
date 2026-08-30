using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SaleSync.Models
{
    public enum DiscountType
    {
        Percentage = 0,
        FixedAmount = 1
    }


public class Promotion
    {
        [Key]
        [Column("promotion_id")]
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        [Column("promotion_name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        [Column("promo_code")]
        public string Code { get; set; } = string.Empty;

        [Required]
        [Column("discount_type")]
        public DiscountType Type { get; set; }

        [Required]
        [Column("discount_value", TypeName = "decimal(18,2)")]
        [Range(0.01, 100000.00, ErrorMessage = "Discount value must be greater than zero.")]
        public decimal DiscountValue { get; set; }

        [Column("minimum_order", TypeName = "decimal(18,2)")]
        public decimal MinimumSpend { get; set; } = 0;

        [Required]
        [Column("start_date")]
        public DateTime StartDate { get; set; }

        [Required]
        [Column("end_date")]
        public DateTime EndDate { get; set; }

        [Required]
        [Column("max_usage")]
        public int MaxUsage { get; set; }

        [Required]
        [Column("current_usage")]
        public int UsageCount { get; set; } = 0;

        [Required]
        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [StringLength(500)]
        [Column("description")]
        public string? Description { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class PromotionValidationResult
    {
        public bool IsValid { get; set; }

        public string Message { get; set; } = string.Empty;

        public decimal CalculatedDiscount { get; set; }

        public Promotion? Promotion { get; set; }
    }


}
