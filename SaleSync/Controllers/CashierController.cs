using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using SaleSync.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.NetworkInformation;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Cashier,Admin,Manager")]
    public class CashierController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly StoreSettingsService _storeSettingsService;
        private readonly InventoryDeductionService _inventoryDeductionService;
        private readonly string connectionString;

        public CashierController(
            IConfiguration configuration,
            StoreSettingsService storeSettingsService,
            InventoryDeductionService inventoryDeductionService)
        {
            _configuration = configuration;
            _storeSettingsService = storeSettingsService;
            _inventoryDeductionService = inventoryDeductionService;

            connectionString = _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "Connection string 'DefaultConnection' was not found.");
        }

        // =========================================================
        // DASHBOARD
        // =========================================================

        public IActionResult Dashboard()
        {
            var storeSettings = _storeSettingsService.GetSettings();

            ViewBag.StoreStatus =
                _storeSettingsService.GetStoreStatus(storeSettings);

            var model = new CashierDashboardViewModel
            {
                RecentSales = new List<SaleHistoryItem>()
            };

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string totalSql = @"
                    SELECT
                        ISNULL(SUM(total_amount), 0) AS Total,
                        COUNT(sale_id) AS Count
                    FROM sales
                    WHERE CAST(sale_date AS DATE) = CAST(GETDATE() AS DATE)
                    AND status = 'Completed'";

                string historySql = @"
                    SELECT TOP 10
                        s.sale_id,
                        s.sale_date,
                        s.total_amount,
                        s.status,
                        u.username,
                        (
                            SELECT STRING_AGG(
                                CAST(si.quantity AS VARCHAR) + 'x ' + p.product_name,
                                ', '
                            )
                            FROM sale_items si
                            JOIN products p
                                ON si.product_id = p.product_id
                            WHERE si.sale_id = s.sale_id
                        ) AS ItemsSummary
                    FROM sales s
                    JOIN users u
                        ON s.user_id = u.user_id
                    ORDER BY s.sale_date DESC";

                conn.Open();

                using (SqlCommand cmd = new SqlCommand(totalSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        model.TodayTotalSales =
                            Convert.ToDecimal(r["Total"]);

                        model.TodayTransactionCount =
                            Convert.ToInt32(r["Count"]);
                    }
                }

                using (SqlCommand cmd = new SqlCommand(historySql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        model.RecentSales.Add(
                            new SaleHistoryItem
                            {
                                SaleId =
    Convert.ToInt32(r["sale_id"]),

                                SaleDate =
    Convert.ToDateTime(r["sale_date"]),

                                TotalAmount =
    Convert.ToDecimal(r["total_amount"]),

                                CashierName =
    r["username"]?.ToString(),

                                ItemsSummary =
    r["ItemsSummary"]?.ToString()
    ?? "No items",

                                Status =
    r["status"]?.ToString()
    ?? "Pending"
                            });
                    }
                }

                return View("CashierDashboard", model);
            }
        }

        // =========================================================
        // MENU
        // =========================================================

        public IActionResult CashierMenu()
        {
            var menuList = new List<MenuItemModel>();

            using (SqlConnection conn =
                   new SqlConnection(connectionString))
            {
                string sql = @"
                    SELECT
                        p.product_id,
                        p.product_name,
                        c.category_name,
                        p.selling_price
                    FROM products p
                    LEFT JOIN categories c
                        ON p.category_id = c.category_id
                    WHERE
                        (
                            p.is_ingredient = 0
                            OR p.is_ingredient IS NULL
                        )
                        AND
                        (
                            p.is_archived = 0
                            OR p.is_archived IS NULL
                        )
                    ORDER BY p.product_name";

                using (SqlCommand cmd =
                       new SqlCommand(sql, conn))
                {
                    conn.Open();

                    using (SqlDataReader r =
                           cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            menuList.Add(
                                new MenuItemModel
                                {
                                    ProductId =
                                        Convert.ToInt32(
                                            r["product_id"]),

                                    ProductName =
                                        r["product_name"]?.ToString(),

                                    CategoryName =
                                        r["category_name"]?.ToString()
                                        ?? "Uncategorized",

                                    Price =
                                        r["selling_price"] != DBNull.Value
                                            ? Convert.ToDecimal(
                                                r["selling_price"])
                                            : 0m
                                });
                        }
                    }
                }
            }

            return View(menuList);
        }

        // =========================================================
        // PROMOTION VALIDATION
        // =========================================================

        [HttpPost]
        public IActionResult ValidatePromo(
            [FromBody] PromoValidationRequest request)
        {
            try
            {
                // -------------------------------------------------
                // BASIC REQUEST VALIDATION
                // -------------------------------------------------

                if (request == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid promotion request."
                    });
                }

                string promoCode =
                    request.PromoCode?
                        .Trim()
                        .ToUpperInvariant()
                        ?? string.Empty;

                decimal orderTotal =
                    Math.Round(request.TotalAmount, 2);

                if (string.IsNullOrWhiteSpace(promoCode))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Please enter a promo code."
                    });
                }

                if (orderTotal < 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid order total."
                    });
                }

                // -------------------------------------------------
                // DATABASE
                // -------------------------------------------------

                using SqlConnection conn =
                    new SqlConnection(connectionString);

                conn.Open();

                /*
                 * IMPORTANT:
                 *
                 * Do NOT convert discount_type to INT here.
                 *
                 * Your database may contain values such as:
                 *
                 * 0
                 * 1
                 * percentage
                 * percent
                 * fixed
                 * amount
                 *
                 * Therefore we read it as text and normalize it
                 * below.
                 */

                const string query = @"
                    SELECT TOP 1
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
                        description
                    FROM promotions
                    WHERE
                        UPPER(LTRIM(RTRIM(CAST(promo_code AS NVARCHAR(100)))))
                        = @PromoCode";

                using SqlCommand cmd =
                    new SqlCommand(query, conn);

                cmd.Parameters.Add(
                    "@PromoCode",
                    SqlDbType.NVarChar,
                    100
                ).Value = promoCode;

                using SqlDataReader reader =
                    cmd.ExecuteReader();

                if (!reader.Read())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Promo code not found."
                    });
                }

                // -------------------------------------------------
                // SAFELY READ DATABASE VALUES
                // -------------------------------------------------

                int promotionId = 0;

                if (reader["promotion_id"] != DBNull.Value)
                {
                    int.TryParse(
                        reader["promotion_id"].ToString(),
                        out promotionId);
                }

                string promotionName =
                    reader["promotion_name"]?.ToString()?.Trim()
                    ?? "Promotion";

                string databasePromoCode =
                    reader["promo_code"]?.ToString()?.Trim()
                    ?? promoCode;

                string discountType =
                    reader["discount_type"]?.ToString()
                        ?.Trim()
                        .ToLowerInvariant()
                    ?? string.Empty;

                decimal discountValue = 0m;

                if (reader["discount_value"] != DBNull.Value)
                {
                    decimal.TryParse(
                        reader["discount_value"].ToString(),
                        out discountValue);
                }

                decimal minimumOrder = 0m;

                if (reader["minimum_order"] != DBNull.Value)
                {
                    decimal.TryParse(
                        reader["minimum_order"].ToString(),
                        out minimumOrder);
                }

                DateTime startDate = DateTime.MinValue;

                if (reader["start_date"] != DBNull.Value)
                {
                    DateTime.TryParse(
                        reader["start_date"].ToString(),
                        out startDate);
                }

                DateTime endDate = DateTime.MaxValue;

                if (reader["end_date"] != DBNull.Value)
                {
                    DateTime parsedEndDate;

                    if (DateTime.TryParse(
                        reader["end_date"].ToString(),
                        out parsedEndDate))
                    {
                        endDate = parsedEndDate;
                    }
                }

                int maxUsage = 0;

                if (reader["max_usage"] != DBNull.Value)
                {
                    int.TryParse(
                        reader["max_usage"].ToString(),
                        out maxUsage);
                }

                int currentUsage = 0;

                if (reader["current_usage"] != DBNull.Value)
                {
                    int.TryParse(
                        reader["current_usage"].ToString(),
                        out currentUsage);
                }

                bool isActive = false;

                if (reader["is_active"] != DBNull.Value)
                {
                    bool.TryParse(
                        reader["is_active"].ToString(),
                        out isActive);

                    // Handles databases storing 0 / 1.
                    if (!isActive)
                    {
                        int activeInt;

                        if (int.TryParse(
                            reader["is_active"].ToString(),
                            out activeInt))
                        {
                            isActive = activeInt == 1;
                        }
                    }
                }

                // -------------------------------------------------
                // VALIDATE PROMOTION
                // -------------------------------------------------

                if (!isActive)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "This promotion is currently inactive."
                    });
                }

                DateTime now = DateTime.Now;

                if (now < startDate)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"This promotion starts on {startDate:MMM dd, yyyy hh:mm tt}."
                    });
                }

                if (now > endDate)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"This promotion expired on {endDate:MMM dd, yyyy hh:mm tt}."
                    });
                }

                if (maxUsage > 0 &&
                    currentUsage >= maxUsage)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "This promotion has reached its maximum usage limit."
                    });
                }

                if (orderTotal < minimumOrder)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"Minimum order of ₱{minimumOrder:N2} is required for this promotion."
                    });
                }

                if (discountValue < 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "This promotion has an invalid discount value."
                    });
                }

                // -------------------------------------------------
                // NORMALIZE DISCOUNT TYPE
                // -------------------------------------------------

                bool isPercentage =
                    discountType == "0" ||
                    discountType == "percentage" ||
                    discountType == "percent" ||
                    discountType == "percentage discount";

                bool isFixed =
                    discountType == "1" ||
                    discountType == "fixed" ||
                    discountType == "amount" ||
                    discountType == "fixed amount" ||
                    discountType == "fixed discount";

                // -------------------------------------------------
                // CALCULATE DISCOUNT
                // -------------------------------------------------

                decimal discountAmount = 0m;

                if (isPercentage)
                {
                    // Prevent invalid percentages from producing
                    // unexpected results.

                    if (discountValue > 100m)
                    {
                        discountValue = 100m;
                    }

                    discountAmount =
                        orderTotal *
                        (discountValue / 100m);
                }
                else if (isFixed)
                {
                    discountAmount =
                        discountValue;
                }
                else
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"Invalid discount type '{discountType}' in the database."
                    });
                }

                // Never discount below zero.
                if (discountAmount < 0m)
                {
                    discountAmount = 0m;
                }

                // Never discount more than the order total.
                if (discountAmount > orderTotal)
                {
                    discountAmount = orderTotal;
                }

                discountAmount =
                    Math.Round(
                        discountAmount,
                        2,
                        MidpointRounding.AwayFromZero);

                decimal finalAmount =
                    orderTotal - discountAmount;

                if (finalAmount < 0m)
                {
                    finalAmount = 0m;
                }

                finalAmount =
                    Math.Round(
                        finalAmount,
                        2,
                        MidpointRounding.AwayFromZero);

                // -------------------------------------------------
                // SUCCESS
                // -------------------------------------------------

                return Ok(new
                {
                    success = true,

                    message = "Promotion is valid.",

                    promotionId = promotionId,

                    promotionName = promotionName,

                    promoCode = databasePromoCode,

                    discountType = discountType,

                    discountValue =
                        Math.Round(
                            discountValue,
                            2),

                    discountAmount =
                        discountAmount,

                    originalTotal =
                        Math.Round(
                            orderTotal,
                            2),

                    finalAmount =
                        finalAmount,

                    minimumOrder =
                        Math.Round(
                            minimumOrder,
                            2),

                    remainingUsage =
                        maxUsage > 0
                            ? Math.Max(
                                0,
                                maxUsage - currentUsage)
                            : -1
                });
            }
            catch (SqlException ex)
            {
                Console.WriteLine(
                    "PROMOTION SQL ERROR:");
                Console.WriteLine(ex.ToString());

                return StatusCode(500, new
                {
                    success = false,
                    message =
                        "Database error while validating promotion."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "PROMOTION VALIDATION ERROR:");
                Console.WriteLine(ex.ToString());

                return StatusCode(500, new
                {
                    success = false,
                    message =
                        "Unable to validate promotion."
                });
            }
        }

        // =========================================================
        // STATUS UPDATES & INVENTORY DEDUCTION
        // =========================================================

        [HttpPost]
        public IActionResult UpdateSaleStatus(
            [FromBody] StatusUpdateModel request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid status request."
                });
            }

            using SqlConnection conn =
                new SqlConnection(connectionString);

            conn.Open();

            string currentStatus = "";

            using (SqlCommand checkCmd =
                   new SqlCommand(
                       "SELECT status FROM sales WHERE sale_id = @id",
                       conn))
            {
                checkCmd.Parameters.Add(
                    "@id",
                    SqlDbType.Int).Value =
                    request.SaleId;

                currentStatus =
                    checkCmd.ExecuteScalar()
                    ?.ToString()
                    ?? "";
            }

            if (currentStatus == request.Status)
            {
                return Ok(new
                {
                    success = true
                });
            }

            using SqlTransaction transaction =
                conn.BeginTransaction();

            try
            {
                string sql = @"
                    UPDATE sales
                    SET status = @status
                    WHERE sale_id = @id";

                using (SqlCommand cmd =
                       new SqlCommand(
                           sql,
                           conn,
                           transaction))
                {
                    cmd.Parameters.Add(
                        "@status",
                        SqlDbType.NVarChar,
                        50).Value =
                        request.Status ?? "";

                    cmd.Parameters.Add(
                        "@id",
                        SqlDbType.Int).Value =
                        request.SaleId;

                    cmd.ExecuteNonQuery();
                }

                // Deduct ingredients only when the order
                // changes INTO Completed.
                if (request.Status == "Completed" &&
                    currentStatus != "Completed")
                {
                    string getItemsSql = @"
                        SELECT
                            product_id,
                            quantity
                        FROM sale_items
                        WHERE sale_id = @id";

                    var itemsList =
                        new List<(int pId, int qty)>();

                    using (SqlCommand getCmd =
                           new SqlCommand(
                               getItemsSql,
                               conn,
                               transaction))
                    {
                        getCmd.Parameters.Add(
                            "@id",
                            SqlDbType.Int).Value =
                            request.SaleId;

                        using SqlDataReader r =
                            getCmd.ExecuteReader();

                        while (r.Read())
                        {
                            itemsList.Add(
                                (
                                    Convert.ToInt32(
                                        r["product_id"]),

                                    Convert.ToInt32(
                                        r["quantity"])
                                ));
                        }
                    }

                    foreach (var item in itemsList)
                    {
                        _inventoryDeductionService
                            .DeductIngredients(
                                conn,
                                transaction,
                                item.pId,
                                item.qty);
                    }
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                }

                return BadRequest(new
                {
                    success = false,
                    message = ex.Message
                });
            }

            return Ok(new
            {
                success = true
            });
        }

        // =========================================================
        // WEB CUSTOMIZATION
        // =========================================================

        public IActionResult WebCustomization()
        {
            var settings =
                _storeSettingsService.GetSettings();

            return View(
                "~/Views/Admin/WebCustomization.cshtml",
                settings);
        }

        // =========================================================
        // VERIFY AND VOID
        // =========================================================

        [HttpPost]
        public IActionResult VerifyAndVoid(
            [FromBody] VoidRequestModel request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid void request."
                });
            }

            using SqlConnection conn =
                new SqlConnection(connectionString);

            conn.Open();

            string checkAuth = @"
                SELECT
                    u.user_id,
                    u.password_hash
                FROM users u
                JOIN roles r
                    ON u.role_id = r.role_id
                WHERE u.username = @user
                AND (
                    r.role_name = 'Admin'
                    OR r.role_name = 'Manager'
                )";

            string overrideUserId = null;
            string overrideHash = null;

            using (SqlCommand cmd =
                   new SqlCommand(
                       checkAuth,
                       conn))
            {
                cmd.Parameters.Add(
                    "@user",
                    SqlDbType.NVarChar,
                    100).Value =
                    request.AdminUser ?? "";

                using SqlDataReader r =
                    cmd.ExecuteReader();

                if (r.Read())
                {
                    overrideUserId =
                        r["user_id"]?.ToString();

                    overrideHash =
                        r["password_hash"]?.ToString();
                }
            }

            if (overrideHash == null ||
                !PasswordHasher.Verify(
                    request.AdminPass ?? "",
                    overrideHash,
                    out bool needsUpgrade))
            {
                return Unauthorized(new
                {
                    success = false,
                    message =
                        "Invalid Admin credentials"
                });
            }

            if (needsUpgrade)
            {
                using SqlCommand upgradeCmd =
                    new SqlCommand(
                        @"UPDATE users
                          SET password_hash = @hash
                          WHERE user_id = @id",
                        conn);

                upgradeCmd.Parameters.Add(
                    "@hash",
                    SqlDbType.NVarChar,
                    500).Value =
                    PasswordHasher.Hash(
                        request.AdminPass ?? "");

                upgradeCmd.Parameters.Add(
                    "@id",
                    SqlDbType.Int).Value =
                    Convert.ToInt32(
                        overrideUserId);

                upgradeCmd.ExecuteNonQuery();
            }

            using SqlCommand vCmd =
                new SqlCommand(
                    @"UPDATE sales
                      SET status = 'Voided'
                      WHERE sale_id = @id",
                    conn);

            vCmd.Parameters.Add(
                "@id",
                SqlDbType.Int).Value =
                request.SaleId;

            vCmd.ExecuteNonQuery();

            return Ok(new
            {
                success = true
            });
        }

        // =========================================================
        // ORDER DETAILS
        // =========================================================

        [HttpGet]
        public IActionResult GetOrderDetails(
            int saleId)
        {
            var itemsSold =
                new List<string>();

            var ingredientsDeducted =
                new List<string>();

            using SqlConnection conn =
                new SqlConnection(connectionString);

            conn.Open();

            string itemsSql = @"
                SELECT
                    si.quantity,
                    p.product_name
                FROM sale_items si
                JOIN products p
                    ON si.product_id = p.product_id
                WHERE si.sale_id = @id";

            using (SqlCommand cmd =
                   new SqlCommand(
                       itemsSql,
                       conn))
            {
                cmd.Parameters.Add(
                    "@id",
                    SqlDbType.Int).Value =
                    saleId;

                using SqlDataReader r =
                    cmd.ExecuteReader();

                while (r.Read())
                {
                    itemsSold.Add(
                        $"{r["quantity"]}x " +
                        $"{r["product_name"]}");
                }
            }

            string ingSql = @"
                SELECT
                    SUM(
                        (
                            si.quantity *
                            pi.quantity_required
                        ) /
                        ISNULL(
                            pi.conversion_factor,
                            1
                        )
                    ) AS Total,
                    ing.product_name,
                    ing.unit
                FROM sale_items si
                JOIN product_ingredients pi
                    ON si.product_id = pi.product_id
                JOIN products ing
                    ON pi.ingredient_id = ing.product_id
                WHERE si.sale_id = @id
                GROUP BY
                    ing.product_name,
                    ing.unit";

            using (SqlCommand cmd =
                   new SqlCommand(
                       ingSql,
                       conn))
            {
                cmd.Parameters.Add(
                    "@id",
                    SqlDbType.Int).Value =
                    saleId;

                using SqlDataReader r =
                    cmd.ExecuteReader();

                while (r.Read())
                {
                    decimal total =
                        r["Total"] != DBNull.Value
                            ? Convert.ToDecimal(r["Total"])
                            : 0m;

                    ingredientsDeducted.Add(
                        $"{total:F2} " +
                        $"{r["unit"]} " +
                        $"{r["product_name"]}");
                }
            }

            return Json(
                new
                {
                    items = itemsSold,
                    ingredients = ingredientsDeducted
                });
        }

        // =========================================================
        // GLOBAL SEARCH
        // =========================================================

        [HttpGet]
        public IActionResult GlobalSearch(
            string q)
        {
            if (string.IsNullOrWhiteSpace(q) ||
                q.Length < 2)
            {
                return Json(
                    new
                    {
                        products = new object[0],
                        inventory = new object[0],
                        transactions = new object[0]
                    });
            }

            var products =
                new List<object>();

            var inventory =
                new List<object>();

            var transactions =
                new List<object>();

            using SqlConnection conn =
                new SqlConnection(connectionString);

            conn.Open();

            // -------------------------------------------------
            // PRODUCTS
            // -------------------------------------------------

            string prodSql = @"
                SELECT TOP 8
                    p.product_id,
                    p.product_name,
                    p.selling_price,
                    p.sku
                FROM products p
                LEFT JOIN categories c
                    ON p.category_id = c.category_id
                WHERE
                    (
                        p.is_archived = 0
                        OR p.is_archived IS NULL
                    )
                    AND
                    (
                        p.is_ingredient = 0
                        OR p.is_ingredient IS NULL
                    )
                    AND
                    (
                        p.product_name LIKE @q
                        OR c.category_name LIKE @q
                    )
                ORDER BY p.product_name";

            using (SqlCommand cmd =
                   new SqlCommand(
                       prodSql,
                       conn))
            {
                cmd.Parameters.Add(
                    "@q",
                    SqlDbType.NVarChar,
                    200).Value =
                    "%" + q.Trim() + "%";

                using SqlDataReader reader =
                    cmd.ExecuteReader();

                while (reader.Read())
                {
                    products.Add(
                        new
                        {
                            id =
                                reader["product_id"],

                            name =
                                reader["product_name"]
                                    ?.ToString(),

                            price =
                                reader["selling_price"]
                                != DBNull.Value
                                    ? Convert.ToDecimal(
                                        reader["selling_price"])
                                    : 0m,

                            sku =
                                reader["sku"]
                                ?.ToString(),

                            url =
                                "/Cashier/CashierMenu"
                        });
                }
            }

            // -------------------------------------------------
            // CASHIER'S TRANSACTIONS
            // -------------------------------------------------

            string transSql = @"
                SELECT TOP 8
                    s.sale_id,
                    s.customer_name,
                    s.total_amount,
                    s.status,
                    s.payment_method,
                    s.sale_date
                FROM sales s
                WHERE
                    (
                        s.customer_name LIKE @q
                        OR CAST(
                            s.sale_id AS VARCHAR
                        ) LIKE @q
                    )
                    AND s.user_id = @uid
                ORDER BY s.sale_date DESC";

            using (SqlCommand cmd =
                   new SqlCommand(
                       transSql,
                       conn))
            {
                cmd.Parameters.Add(
                    "@q",
                    SqlDbType.NVarChar,
                    200).Value =
                    "%" + q.Trim() + "%";

                var uidClaim =
                    User.FindFirst(
                        System.Security.Claims.ClaimTypes.NameIdentifier);

                int uid = 0;

                if (uidClaim != null)
                {
                    int.TryParse(
                        uidClaim.Value,
                        out uid);
                }

                cmd.Parameters.Add(
                    "@uid",
                    SqlDbType.Int).Value =
                    uid;

                using SqlDataReader reader =
                    cmd.ExecuteReader();

                while (reader.Read())
                {
                    transactions.Add(
                        new
                        {
                            id =
                                reader["sale_id"],

                            name =
                                (
                                    reader["customer_name"]
                                        ?.ToString()
                                    ?? "Walk-in"
                                )
                                +
                                " (#" +
                                reader["sale_id"] +
                                ")",

                            amount =
                                reader["total_amount"]
                                != DBNull.Value
                                    ? Convert.ToDecimal(
                                        reader["total_amount"])
                                    : 0m,

                            status =
                                reader["status"]
                                    ?.ToString(),

                            method =
                                reader["payment_method"]
                                    ?.ToString(),

                            url =
                                "/Cashier/CashierMenu"
                        });
                }
            }

            return Json(
                new
                {
                    products,
                    inventory,
                    transactions
                });
        }

        // =========================================================
        // REQUEST MODELS
        // =========================================================

        public class PromoValidationRequest
        {
            public string PromoCode { get; set; }
            public decimal TotalAmount { get; set; }
        }

        public class StatusUpdateModel
        {
            public int SaleId { get; set; }
            public string Status { get; set; }
        }

        public class VoidRequestModel
        {
            public int SaleId { get; set; }
            public string AdminUser { get; set; }
            public string AdminPass { get; set; }
        }
    }
}


