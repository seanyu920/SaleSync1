using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SaleSync.Models;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class PromotionsController : Controller
    {
        private readonly string _connectionString;

        public PromotionsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "Connection string 'DefaultConnection' was not found."
                );
        }

        // =========================================================
        // GET: /Promotions
        // =========================================================
        [HttpGet]
        public IActionResult Index()
        {
            var promotions = new List<Promotion>();

            try
            {
                using SqlConnection connection = new SqlConnection(_connectionString);
                connection.Open();

                const string query = @"
                    SELECT
                        promotion_id,
                        promotion_name,
                        promo_code,
                        discount_type,
                        discount_value,
                        minimum_order,
                        start_date,
                        end_date,
                        max_usage,
                        current_usage,
                        is_active,
                        description,
                        created_at
                    FROM promotions
                    ORDER BY created_at DESC";

                using SqlCommand command = new SqlCommand(query, connection);

                using SqlDataReader reader = command.ExecuteReader();

                while (reader.Read())
                {
                    promotions.Add(new Promotion
                    {
                        Id = Convert.ToInt32(reader["promotion_id"]),

                        Name = reader["promotion_name"]?.ToString() ?? "",

                        Code = reader["promo_code"]?.ToString() ?? "",

                        Type = Convert.ToInt32(reader["discount_type"]) == 0
                            ? DiscountType.Percentage
                            : DiscountType.FixedAmount,

                        DiscountValue = reader["discount_value"] != DBNull.Value
                            ? Convert.ToDecimal(reader["discount_value"])
                            : 0,

                        MinimumSpend = reader["minimum_order"] != DBNull.Value
                            ? Convert.ToDecimal(reader["minimum_order"])
                            : 0,

                        StartDate = Convert.ToDateTime(reader["start_date"]),

                        EndDate = Convert.ToDateTime(reader["end_date"]),

                        MaxUsage = reader["max_usage"] != DBNull.Value
                            ? Convert.ToInt32(reader["max_usage"])
                            : 0,

                        UsageCount = reader["current_usage"] != DBNull.Value
                            ? Convert.ToInt32(reader["current_usage"])
                            : 0,

                        IsActive = reader["is_active"] != DBNull.Value &&
                                   Convert.ToBoolean(reader["is_active"]),

                        Description = reader["description"] != DBNull.Value
                            ? reader["description"].ToString()
                            : "",

                        CreatedAt = reader["created_at"] != DBNull.Value
                            ? Convert.ToDateTime(reader["created_at"])
                            : DateTime.Now
                    });
                }

                // Calculate status dynamically based on current date/time.
                DateTime now = DateTime.Now;

                foreach (var promotion in promotions)
                {
                    if (promotion.EndDate < now)
                    {
                        promotion.IsActive = false;
                    }
                }

                ViewBag.TotalPromotions = promotions.Count;

                ViewBag.ActivePromotions = promotions.Count(p =>
                    p.IsActive &&
                    p.StartDate <= now &&
                    p.EndDate >= now &&
                    p.UsageCount < p.MaxUsage
                );

                ViewBag.ExpiredPromotions = promotions.Count(p =>
                    p.EndDate < now
                );

                return View(promotions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Promotion loading error: {ex.Message}");

                ViewBag.TotalPromotions = 0;
                ViewBag.ActivePromotions = 0;
                ViewBag.ExpiredPromotions = 0;

                TempData["PromotionError"] =
                    "Unable to load promotions from the database.";

                return View(promotions);
            }
        }

        // =========================================================
        // POST: /Promotions/CreatePromotion
        // =========================================================
        [HttpPost]
        public IActionResult CreatePromotion([FromBody] Promotion promotion)
        {
            try
            {
                if (promotion == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid promotion data."
                    });
                }

                // Clean input
                promotion.Name = promotion.Name?.Trim() ?? string.Empty;
                promotion.Code = promotion.Code?.Trim().ToUpper() ?? string.Empty;
                promotion.Description = promotion.Description?.Trim() ?? string.Empty;

                // -------------------------------------------------
                // Validation
                // -------------------------------------------------

                if (string.IsNullOrWhiteSpace(promotion.Name))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promotion name is required."
                    });
                }

                if (string.IsNullOrWhiteSpace(promotion.Code))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promo code is required."
                    });
                }

                if (promotion.DiscountValue <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Discount value must be greater than zero."
                    });
                }

                if (promotion.Type == DiscountType.Percentage &&
                    promotion.DiscountValue > 100)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Percentage discounts cannot exceed 100%."
                    });
                }

                if (promotion.Type != DiscountType.Percentage &&
                    promotion.Type != DiscountType.FixedAmount)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid discount type."
                    });
                }

                if (promotion.MinimumSpend < 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Minimum order cannot be negative."
                    });
                }

                if (promotion.MaxUsage <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Maximum usage must be at least 1."
                    });
                }

                if (promotion.StartDate >= promotion.EndDate)
                {
                    return Json(new
                    {
                        success = false,
                        message = "End date and time must be after the start date and time."
                    });
                }

                using SqlConnection connection =
                    new SqlConnection(_connectionString);

                connection.Open();

                // -------------------------------------------------
                // Check duplicate promo code
                // -------------------------------------------------

                const string checkQuery = @"
                    SELECT COUNT(*)
                    FROM promotions
                    WHERE UPPER(promo_code) = UPPER(@PromoCode)";

                using (SqlCommand checkCommand =
                       new SqlCommand(checkQuery, connection))
                {
                    checkCommand.Parameters.AddWithValue(
                        "@PromoCode",
                        promotion.Code
                    );

                    int existingPromo =
                        Convert.ToInt32(checkCommand.ExecuteScalar());

                    if (existingPromo > 0)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "This promo code already exists."
                        });
                    }
                }

                // -------------------------------------------------
                // Insert promotion
                // -------------------------------------------------

                const string insertQuery = @"
                    INSERT INTO promotions
                    (
                        promotion_name,
                        promo_code,
                        discount_type,
                        discount_value,
                        minimum_order,
                        start_date,
                        end_date,
                        max_usage,
                        current_usage,
                        is_active,
                        description,
                        created_at
                    )
                    VALUES
                    (
                        @PromotionName,
                        @PromoCode,
                        @DiscountType,
                        @DiscountValue,
                        @MinimumOrder,
                        @StartDate,
                        @EndDate,
                        @MaxUsage,
                        0,
                        1,
                        @Description,
                        GETDATE()
                    )";

                using SqlCommand command =
                    new SqlCommand(insertQuery, connection);

                command.Parameters.AddWithValue(
                    "@PromotionName",
                    promotion.Name
                );

                command.Parameters.AddWithValue(
                    "@PromoCode",
                    promotion.Code
                );

                command.Parameters.AddWithValue(
                    "@DiscountType",
                    (int)promotion.Type
                );

                command.Parameters.AddWithValue(
                    "@DiscountValue",
                    promotion.DiscountValue
                );

                command.Parameters.AddWithValue(
                    "@MinimumOrder",
                    promotion.MinimumSpend
                );

                command.Parameters.AddWithValue(
                    "@StartDate",
                    promotion.StartDate
                );

                command.Parameters.AddWithValue(
                    "@EndDate",
                    promotion.EndDate
                );

                command.Parameters.AddWithValue(
                    "@MaxUsage",
                    promotion.MaxUsage
                );

                command.Parameters.AddWithValue(
                    "@Description",
                    string.IsNullOrWhiteSpace(promotion.Description)
                        ? DBNull.Value
                        : promotion.Description
                );

                command.ExecuteNonQuery();

                return Json(new
                {
                    success = true,
                    message = "Promotion created successfully."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Promotion creation error: {ex.Message}"
                );

                return Json(new
                {
                    success = false,
                    message = "An error occurred while creating the promotion."
                });
            }
        }

        // =========================================================
        // POST: /Promotions/UpdatePromotion
        // =========================================================
        [HttpPost]
        public IActionResult UpdatePromotion([FromBody] Promotion promotion)
        {
            try
            {
                if (promotion == null || promotion.Id <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid promotion data."
                    });
                }

                promotion.Name = promotion.Name?.Trim() ?? "";
                promotion.Code = promotion.Code?.Trim().ToUpper() ?? "";
                promotion.Description = promotion.Description?.Trim() ?? "";

                // -------------------------------------------------
                // Validation
                // -------------------------------------------------

                if (string.IsNullOrWhiteSpace(promotion.Name))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promotion name is required."
                    });
                }

                if (string.IsNullOrWhiteSpace(promotion.Code))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promo code is required."
                    });
                }

                if (promotion.DiscountValue <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Discount value must be greater than zero."
                    });
                }

                if (promotion.Type == DiscountType.Percentage &&
                    promotion.DiscountValue > 100)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Percentage discounts cannot exceed 100%."
                    });
                }

                if (promotion.MinimumSpend < 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Minimum order cannot be negative."
                    });
                }

                if (promotion.MaxUsage <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Maximum usage must be at least 1."
                    });
                }

                if (promotion.StartDate >= promotion.EndDate)
                {
                    return Json(new
                    {
                        success = false,
                        message = "End date and time must be after the start date and time."
                    });
                }

                using SqlConnection connection =
                    new SqlConnection(_connectionString);

                connection.Open();

                // -------------------------------------------------
                // Check duplicate promo code
                // -------------------------------------------------

                const string duplicateQuery = @"
                    SELECT COUNT(*)
                    FROM promotions
                    WHERE UPPER(promo_code) = UPPER(@PromoCode)
                    AND promotion_id <> @PromotionId";

                using (SqlCommand duplicateCommand =
                       new SqlCommand(duplicateQuery, connection))
                {
                    duplicateCommand.Parameters.AddWithValue(
                        "@PromoCode",
                        promotion.Code
                    );

                    duplicateCommand.Parameters.AddWithValue(
                        "@PromotionId",
                        promotion.Id
                    );

                    int duplicateCount =
                        Convert.ToInt32(
                            duplicateCommand.ExecuteScalar()
                        );

                    if (duplicateCount > 0)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "This promo code already exists."
                        });
                    }
                }

                // -------------------------------------------------
                // Update
                // -------------------------------------------------

                const string updateQuery = @"
                    UPDATE promotions
                    SET
                        promotion_name = @PromotionName,
                        promo_code = @PromoCode,
                        discount_type = @DiscountType,
                        discount_value = @DiscountValue,
                        minimum_order = @MinimumOrder,
                        start_date = @StartDate,
                        end_date = @EndDate,
                        max_usage = @MaxUsage,
                        description = @Description
                    WHERE promotion_id = @PromotionId";

                using SqlCommand command =
                    new SqlCommand(updateQuery, connection);

                command.Parameters.AddWithValue(
                    "@PromotionName",
                    promotion.Name
                );

                command.Parameters.AddWithValue(
                    "@PromoCode",
                    promotion.Code
                );

                command.Parameters.AddWithValue(
                    "@DiscountType",
                    (int)promotion.Type
                );

                command.Parameters.AddWithValue(
                    "@DiscountValue",
                    promotion.DiscountValue
                );

                command.Parameters.AddWithValue(
                    "@MinimumOrder",
                    promotion.MinimumSpend
                );

                command.Parameters.AddWithValue(
                    "@StartDate",
                    promotion.StartDate
                );

                command.Parameters.AddWithValue(
                    "@EndDate",
                    promotion.EndDate
                );

                command.Parameters.AddWithValue(
                    "@MaxUsage",
                    promotion.MaxUsage
                );

                command.Parameters.AddWithValue(
                    "@Description",
                    string.IsNullOrWhiteSpace(promotion.Description)
                        ? DBNull.Value
                        : promotion.Description
                );

                command.Parameters.AddWithValue(
                    "@PromotionId",
                    promotion.Id
                );

                int rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected == 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promotion was not found."
                    });
                }

                return Json(new
                {
                    success = true,
                    message = "Promotion updated successfully."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Promotion update error: {ex.Message}"
                );

                return Json(new
                {
                    success = false,
                    message = "An error occurred while updating the promotion."
                });
            }
        }

        // =========================================================
        // POST: /Promotions/DeletePromotion
        // =========================================================
        [HttpPost]
        public IActionResult DeletePromotion([FromBody] DeletePromotionRequest request)
        {
            try
            {
                if (request == null || request.Id <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid promotion ID."
                    });
                }

                using SqlConnection connection =
                    new SqlConnection(_connectionString);

                connection.Open();

                const string deleteQuery = @"
                    DELETE FROM promotions
                    WHERE promotion_id = @PromotionId";

                using SqlCommand command =
                    new SqlCommand(deleteQuery, connection);

                command.Parameters.AddWithValue(
                    "@PromotionId",
                    request.Id
                );

                int rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected == 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promotion was not found."
                    });
                }

                return Json(new
                {
                    success = true,
                    message = "Promotion deleted successfully."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Promotion deletion error: {ex.Message}"
                );

                return Json(new
                {
                    success = false,
                    message = "An error occurred while deleting the promotion."
                });
            }
        }

        // =========================================================
        // POST: /Promotions/TogglePromotion
        // =========================================================
        [HttpPost]
        public IActionResult TogglePromotion([FromBody] TogglePromotionRequest request)
        {
            try
            {
                if (request == null || request.Id <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Invalid promotion ID."
                    });
                }

                using SqlConnection connection =
                    new SqlConnection(_connectionString);

                connection.Open();

                const string query = @"
                    UPDATE promotions
                    SET is_active = @IsActive
                    WHERE promotion_id = @PromotionId";

                using SqlCommand command =
                    new SqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@IsActive",
                    request.IsActive
                );

                command.Parameters.AddWithValue(
                    "@PromotionId",
                    request.Id
                );

                int rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected == 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Promotion was not found."
                    });
                }

                return Json(new
                {
                    success = true,
                    message = request.IsActive
                        ? "Promotion activated."
                        : "Promotion deactivated."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Promotion toggle error: {ex.Message}"
                );

                return Json(new
                {
                    success = false,
                    message = "An error occurred while changing promotion status."
                });
            }
        }

        // =========================================================
        // Request Models
        // =========================================================

        public class DeletePromotionRequest
        {
            public int Id { get; set; }
        }

        public class TogglePromotionRequest
        {
            public int Id { get; set; }
            public bool IsActive { get; set; }
        }
    }
}