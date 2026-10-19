using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;
using System.Collections.Generic;
using System;
using System.IO;
using System.Security.Claims;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SaleSync.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly SaleSync.Services.StoreSettingsService _storeSettingsService;
        private readonly IWebHostEnvironment _env;
        private readonly string connectionString;

        // ── PROFILE STORAGE (no database needed) ───────────────────────────────
        // Profile details are kept in  <project>/App_Data/profiles.json  (one entry per
        // customer id) and profile pictures in  <project>/wwwroot/images/profiles/ .
        private const string ProfilePictureFolder = "images/profiles";
        private const long MaxProfilePictureBytes = 5 * 1024 * 1024;

        private static readonly object ProfileFileLock = new object();

        private static readonly JsonSerializerOptions ProfileJsonOptions =
            new JsonSerializerOptions { WriteIndented = true };

        public CustomerController(
            IConfiguration configuration,
            SaleSync.Services.StoreSettingsService storeSettingsService,
            IWebHostEnvironment env)
        {
            _configuration = configuration;
            _storeSettingsService = storeSettingsService;
            _env = env;
            connectionString = _configuration.GetConnectionString("DefaultConnection");
        }

        [HttpGet]
        public IActionResult CustomerOrdering()
        {
            var storeSettings = _storeSettingsService.GetSettings();
            ViewBag.StoreSettings = storeSettings;
            ViewBag.StoreStatus = _storeSettingsService.GetStoreStatus(storeSettings);

            string userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int currentUserId = string.IsNullOrEmpty(userIdClaim) ? 0 : int.Parse(userIdClaim);

            var dashboardData = new CustomerDashboardViewModel();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();


                string menuSql = @"
            SELECT p.product_id, p.product_name, c.category_name, p.selling_price, p.image_path
            FROM products p
            LEFT JOIN categories c ON p.category_id = c.category_id
            WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
            AND p.is_archived = 0
            ORDER BY c.category_name, p.product_name";

                using (SqlCommand cmd = new SqlCommand(menuSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        dashboardData.MenuItems.Add(new MenuItemModel
                        {
                            ProductId = Convert.ToInt32(r["product_id"]),
                            ProductName = r["product_name"].ToString(),
                            CategoryName = r["category_name"]?.ToString() ?? "Uncategorized",
                            Price = r["selling_price"] != DBNull.Value ? Convert.ToDecimal(r["selling_price"]) : 0,


                            ImagePath = r["image_path"] != DBNull.Value ? r["image_path"].ToString() : null
                        });
                    }
                }


                if (currentUserId != 0)
                {
                    string orderSql = @"
                SELECT sale_id, sale_date, total_amount, status, order_type 
                FROM sales 
                WHERE user_id = @userId 
                ORDER BY sale_date DESC";

                    using (SqlCommand cmd = new SqlCommand(orderSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@userId", currentUserId);
                        using (SqlDataReader r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                dashboardData.OrderHistory.Add(new CustomerOrderModel
                                {
                                    SaleId = Convert.ToInt32(r["sale_id"]),
                                    SaleDate = Convert.ToDateTime(r["sale_date"]),
                                    TotalAmount = Convert.ToDecimal(r["total_amount"]),
                                    Status = r["status"]?.ToString() ?? "Unknown",
                                    OrderType = r["order_type"]?.ToString() ?? "Pick-up"
                                });
                            }
                        }
                    }
                }
            }

            // Saved profile (display name + picture) so the page shows them on first load.
            if (currentUserId != 0)
            {
                try
                {
                    var profile = LoadProfile(currentUserId);

                    if (!string.IsNullOrWhiteSpace(profile.DisplayName))
                        ViewBag.CustomerName = profile.DisplayName;

                    if (!string.IsNullOrWhiteSpace(profile.ProfilePicture))
                        ViewBag.ProfilePicture = profile.ProfilePicture;
                }
                catch (Exception)
                {
                    // If the profile file can't be read, fall back to the login username.
                }
            }

            return View(dashboardData);
        }

        [HttpPost]
        public IActionResult PlaceOnlineOrder([FromBody] OnlineOrderRequest request)
        {
            if (request == null || request.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "Your cart is empty." });

            var storeSettings = _storeSettingsService.GetSettings();
            var storeStatus = _storeSettingsService.GetStoreStatus(storeSettings);

            if (!storeStatus.IsOpen)
            {
                string closedMessage = storeSettings.IsTemporarilyClosed
                    ? "We're currently closed. Please check back later."
                    : $"We're closed right now. Ordering opens at {storeStatus.OpeningTimeLabel}.";

                return BadRequest(new { message = closedMessage });
            }

            string customerName = User.Identity?.Name ?? "Online Guest";
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction transaction = conn.BeginTransaction())
                {
                    try
                    {
                        decimal totalAmount = 0;
                        foreach (var item in request.Items)
                        {
                            totalAmount += item.Quantity * item.Price;
                        }


                        string insertSale = @"
                            INSERT INTO sales (sale_date, total_amount, status, customer_name, order_type, payment_method, delivery_address, pickup_datetime, user_id) 
                            OUTPUT INSERTED.sale_id
                            VALUES (GETDATE(), @total, 'Pending', @custName, @orderType, @paymentMethod, @address, @pickupTime, @userId)";

                        int newSaleId;
                        using (SqlCommand cmd = new SqlCommand(insertSale, conn, transaction))
                        {
                            cmd.Parameters.AddWithValue("@total", totalAmount);
                            cmd.Parameters.AddWithValue("@custName", customerName);
                            cmd.Parameters.AddWithValue("@orderType", request.OrderType ?? "Pick-up");
                            cmd.Parameters.AddWithValue("@paymentMethod", request.PaymentMethod ?? "Cash");
                            cmd.Parameters.AddWithValue("@address", string.IsNullOrEmpty(request.DeliveryAddress) ? (object)DBNull.Value : request.DeliveryAddress);
                            cmd.Parameters.AddWithValue("@pickupTime", request.PickupTime.HasValue ? (object)request.PickupTime.Value : DBNull.Value);
                            cmd.Parameters.AddWithValue("@userId", currentUserId);

                            newSaleId = (int)cmd.ExecuteScalar();
                        }


                        string insertItem = @"
                            INSERT INTO sale_items (sale_id, product_id, quantity, price, subtotal, size, special_instructions) 
                            VALUES (@sid, @pid, @qty, @price, @sub, @size, @instructions)";

                        foreach (var item in request.Items)
                        {
                            using (SqlCommand cmd = new SqlCommand(insertItem, conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", newSaleId);
                                cmd.Parameters.AddWithValue("@pid", item.ProductId);
                                cmd.Parameters.AddWithValue("@qty", item.Quantity);
                                cmd.Parameters.AddWithValue("@price", item.Price);
                                cmd.Parameters.AddWithValue("@sub", item.Quantity * item.Price);


                                cmd.Parameters.AddWithValue("@size", string.IsNullOrEmpty(item.Size) ? "Regular" : item.Size);
                                cmd.Parameters.AddWithValue("@instructions", string.IsNullOrEmpty(item.SpecialInstructions) ? (object)DBNull.Value : item.SpecialInstructions);

                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                        return Ok(new { message = "Order sent to the kitchen!", orderId = newSaleId });
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        return BadRequest(new { message = "Failed to process order: " + ex.Message });
                    }
                }
            }
        }
        [HttpGet]
        public IActionResult GetMyOrders()
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            var orders = new List<object>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                const string sql = @"
            SELECT sale_id, sale_date, total_amount, status, order_type
            FROM sales
            WHERE user_id = @userId
            ORDER BY sale_date DESC";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@userId", currentUserId);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            orders.Add(new
                            {
                                saleId = Convert.ToInt32(r["sale_id"]),
                                saleDate = Convert.ToDateTime(r["sale_date"]).ToString("MMM dd, hh:mm tt"),
                                totalAmount = Convert.ToDecimal(r["total_amount"]),
                                status = r["status"]?.ToString() ?? "Pending",
                                orderType = r["order_type"]?.ToString() ?? "Pick-up"
                            });
                        }
                    }
                }
            }
            return Json(orders);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PROFILE SETTINGS (display name, contact number, profile picture)
        //  Saved in App_Data/profiles.json, so no database changes are needed.
        // ══════════════════════════════════════════════════════════════════════

        private int GetCurrentUserId()
        {
            return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;
        }

        private string ProfileFilePath =>
            Path.Combine(_env.ContentRootPath, "App_Data", "profiles.json");

        private Dictionary<string, StoredProfile> ReadAllProfiles()
        {
            if (!System.IO.File.Exists(ProfileFilePath))
                return new Dictionary<string, StoredProfile>();

            string json = System.IO.File.ReadAllText(ProfileFilePath);

            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, StoredProfile>();

            return JsonSerializer.Deserialize<Dictionary<string, StoredProfile>>(json)
                   ?? new Dictionary<string, StoredProfile>();
        }

        private void WriteAllProfiles(Dictionary<string, StoredProfile> all)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileFilePath));

            // Write to a temp file first so a crash can never leave a half-written profiles.json.
            string tempPath = ProfileFilePath + ".tmp";

            System.IO.File.WriteAllText(tempPath, JsonSerializer.Serialize(all, ProfileJsonOptions));
            System.IO.File.Move(tempPath, ProfileFilePath, true);
        }

        private StoredProfile LoadProfile(int userId)
        {
            lock (ProfileFileLock)
            {
                var all = ReadAllProfiles();

                return all.TryGetValue(userId.ToString(), out var profile)
                    ? profile
                    : new StoredProfile();
            }
        }

        private void SaveProfile(int userId, Action<StoredProfile> change)
        {
            lock (ProfileFileLock)
            {
                var all = ReadAllProfiles();

                string key = userId.ToString();

                if (!all.TryGetValue(key, out var profile))
                {
                    profile = new StoredProfile();
                    all[key] = profile;
                }

                change(profile);

                WriteAllProfiles(all);
            }
        }

        // GET /Customer/GetProfile
        [HttpGet]
        public IActionResult GetProfile()
        {
            int userId = GetCurrentUserId();

            if (userId == 0)
                return Unauthorized();

            StoredProfile profile;

            try
            {
                profile = LoadProfile(userId);
            }
            catch (Exception)
            {
                profile = new StoredProfile();
            }

            return Json(new
            {
                displayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? (User.FindFirstValue("Username") ?? User.Identity?.Name ?? "Customer")
                    : profile.DisplayName,
                contactNumber = profile.ContactNumber ?? "",
                profilePicture = profile.ProfilePicture ?? ""
            });
        }

        // POST /Customer/UploadProfilePhoto   (multipart form, field name: profileImageFile)
        [HttpPost]
        public async Task<IActionResult> UploadProfilePhoto(IFormFile profileImageFile)
        {
            int userId = GetCurrentUserId();

            if (userId == 0)
                return Unauthorized();

            if (profileImageFile == null || profileImageFile.Length == 0)
                return BadRequest("Please choose an image.");

            if (profileImageFile.Length > MaxProfilePictureBytes)
                return BadRequest("Please choose an image under 5 MB.");

            var allowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "image/jpeg", ".jpg" },
                { "image/png", ".png" },
                { "image/gif", ".gif" },
                { "image/webp", ".webp" }
            };

            if (!allowedTypes.TryGetValue(profileImageFile.ContentType ?? "", out string extension))
                return BadRequest("Only JPG, PNG, GIF or WEBP images are allowed.");

            // The content type comes from the browser, so also check the real file header.
            byte[] header = new byte[12];

            using (var peek = profileImageFile.OpenReadStream())
            {
                int read = await peek.ReadAsync(header, 0, header.Length);

                if (read < 12 || !LooksLikeImage(header))
                    return BadRequest("That file doesn't look like a valid image.");
            }

            string folder = Path.Combine(_env.WebRootPath, ProfilePictureFolder.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(folder);

            string fileName = $"user-{userId}-{Guid.NewGuid():N}{extension}";
            string fullPath = Path.Combine(folder, fileName);

            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await profileImageFile.CopyToAsync(stream);
            }

            string publicPath = "/" + ProfilePictureFolder + "/" + fileName;
            string oldPath = null;

            try
            {
                SaveProfile(userId, profile =>
                {
                    oldPath = profile.ProfilePicture;
                    profile.ProfilePicture = publicPath;
                });
            }
            catch (Exception ex)
            {
                // Don't leave an orphaned file behind if saving failed.
                TryDeleteProfileFile(publicPath);

                return BadRequest("Could not save your photo: " + ex.Message);
            }

            TryDeleteProfileFile(oldPath);

            return Json(new { imagePath = publicPath });
        }

        // POST /Customer/UpdateProfile   (JSON: { DisplayName, ContactNumber, RemovePhoto })
        [HttpPost]
        public IActionResult UpdateProfile([FromBody] ProfileUpdateRequest request)
        {
            int userId = GetCurrentUserId();

            if (userId == 0)
                return Unauthorized();

            if (request == null)
                return BadRequest(new { message = "Nothing to save." });

            string displayName = (request.DisplayName ?? "").Trim();
            string contactNumber = (request.ContactNumber ?? "").Trim();

            if (displayName.Length == 0)
                return BadRequest(new { message = "Please enter a display name." });

            if (displayName.Length > 40)
                return BadRequest(new { message = "Display name must be 40 characters or fewer." });

            if (contactNumber.Length > 20)
                return BadRequest(new { message = "Contact number must be 20 characters or fewer." });

            if (contactNumber.Any(c => !(char.IsDigit(c) || c == '+' || c == ' ' || c == '-' || c == '(' || c == ')')))
                return BadRequest(new { message = "Contact number can only contain digits, spaces and + - ( )." });

            string oldPath = null;

            try
            {
                SaveProfile(userId, profile =>
                {
                    profile.DisplayName = displayName;
                    profile.ContactNumber = contactNumber;

                    if (request.RemovePhoto)
                    {
                        oldPath = profile.ProfilePicture;
                        profile.ProfilePicture = null;
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Could not save your profile: " + ex.Message });
            }

            if (request.RemovePhoto)
                TryDeleteProfileFile(oldPath);

            return Ok(new { message = "Profile updated." });
        }

        // Real image files start with known bytes, whatever the browser says they are.
        private static bool LooksLikeImage(byte[] h)
        {
            bool jpeg = h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;
            bool png = h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47;
            bool gif = h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38;
            bool webp = h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
                     && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50;

            return jpeg || png || gif || webp;
        }

        // Only ever deletes files inside the profile-pictures folder.
        private void TryDeleteProfileFile(string publicPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(publicPath))
                    return;

                string prefix = "/" + ProfilePictureFolder + "/";

                if (!publicPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return;

                string fileName = Path.GetFileName(publicPath);

                string fullPath = Path.Combine(
                    _env.WebRootPath,
                    ProfilePictureFolder.Replace('/', Path.DirectorySeparatorChar),
                    fileName);

                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch
            {
                // A leftover file is harmless; never fail the request over it.
            }
        }
    }


    public class OnlineOrderRequest
    {
        public string OrderType { get; set; }
        public string PaymentMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public DateTime? PickupTime { get; set; }
        public List<OnlineOrderItemRequest> Items { get; set; }
    }

    public class OnlineOrderItemRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Size { get; set; }
        public string SpecialInstructions { get; set; }
    }

    public class ProfileUpdateRequest
    {
        public string DisplayName { get; set; }
        public string ContactNumber { get; set; }
        public bool RemovePhoto { get; set; }
    }

    // What is saved for one customer in App_Data/profiles.json
    public class StoredProfile
    {
        public string DisplayName { get; set; }
        public string ContactNumber { get; set; }
        public string ProfilePicture { get; set; }
    }
}
