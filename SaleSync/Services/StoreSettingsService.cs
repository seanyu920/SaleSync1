using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;

namespace SaleSync.Services
{
    // Reads and writes the single row of store-wide settings (branding, receipt
    // details, theme colors, business hours) and works out whether the store
    // should currently show as open or closed. Used by every dashboard and by
    // the Web Customization page, so there is one source of truth instead of
    // each controller running its own copy of this logic.
    public class StoreSettingsService
    {
        private readonly string _connectionString;

        public StoreSettingsService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public WebCustomization GetSettings()
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string selectSql = "SELECT TOP 1 * FROM store_settings WHERE id = 1";
                using (SqlCommand cmd = new SqlCommand(selectSql, conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return new WebCustomization
                        {
                            StoreName = r["store_name"] as string ?? "Cafero",
                            StoreTagline = r["store_tagline"] as string ?? "",
                            BusinessAddress = r["business_address"] as string ?? "",
                            ContactNumber = r["contact_number"] as string ?? "",
                            TaxIdNumber = r["tax_id_number"] as string ?? "",
                            ReceiptFooter = r["receipt_footer"] as string ?? "",
                            LogoPath = r["logo_path"] as string,
                            CurrencySymbol = r["currency_symbol"] as string ?? "₱",
                            VatRate = r["vat_rate"] != DBNull.Value ? Convert.ToDecimal(r["vat_rate"]) : 12.00m,
                            LowStockThreshold = r["low_stock_threshold"] != DBNull.Value ? Convert.ToInt32(r["low_stock_threshold"]) : 10,
                            OpeningTime = r["opening_time"] as string ?? "07:00",
                            ClosingTime = r["closing_time"] as string ?? "21:00",
                            IsTemporarilyClosed = r["is_temporarily_closed"] != DBNull.Value && Convert.ToBoolean(r["is_temporarily_closed"]),
                            PrimaryColor = r["primary_color"] as string ?? "#4a2511",
                            SidebarColor = r["sidebar_color"] as string ?? "#2b1b17",
                            AccentColor = r["accent_color"] as string ?? "#b58361",
                            BackgroundColor = r["background_color"] as string ?? "#fdfbf7",
                            SidebarPosition = r["sidebar_position"] as string ?? "left"
                        };
                    }
                }

                // No row yet (fresh install) — seed one with defaults so future saves have a row to UPDATE.
                var defaults = new WebCustomization();
                InsertDefaultRow(conn, defaults);
                return defaults;
            }
        }

        public void SaveSettings(WebCustomization settings)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string updateSql = @"
                    UPDATE store_settings SET
                        store_name = @storeName,
                        store_tagline = @storeTagline,
                        business_address = @businessAddress,
                        contact_number = @contactNumber,
                        tax_id_number = @taxIdNumber,
                        receipt_footer = @receiptFooter,
                        logo_path = @logoPath,
                        currency_symbol = @currencySymbol,
                        vat_rate = @vatRate,
                        low_stock_threshold = @lowStockThreshold,
                        opening_time = @openingTime,
                        closing_time = @closingTime,
                        is_temporarily_closed = @isTemporarilyClosed,
                        primary_color = @primaryColor,
                        sidebar_color = @sidebarColor,
                        accent_color = @accentColor,
                        background_color = @backgroundColor,
                        sidebar_position = @sidebarPosition
                    WHERE id = 1";

                using (SqlCommand cmd = new SqlCommand(updateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@storeName", settings.StoreName ?? "");
                    cmd.Parameters.AddWithValue("@storeTagline", settings.StoreTagline ?? "");
                    cmd.Parameters.AddWithValue("@businessAddress", settings.BusinessAddress ?? "");
                    cmd.Parameters.AddWithValue("@contactNumber", settings.ContactNumber ?? "");
                    cmd.Parameters.AddWithValue("@taxIdNumber", settings.TaxIdNumber ?? "");
                    cmd.Parameters.AddWithValue("@receiptFooter", settings.ReceiptFooter ?? "");
                    cmd.Parameters.AddWithValue("@logoPath", (object?)settings.LogoPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@currencySymbol", settings.CurrencySymbol ?? "₱");
                    cmd.Parameters.AddWithValue("@vatRate", settings.VatRate);
                    cmd.Parameters.AddWithValue("@lowStockThreshold", settings.LowStockThreshold);
                    cmd.Parameters.AddWithValue("@openingTime", settings.OpeningTime ?? "07:00");
                    cmd.Parameters.AddWithValue("@closingTime", settings.ClosingTime ?? "21:00");
                    cmd.Parameters.AddWithValue("@isTemporarilyClosed", settings.IsTemporarilyClosed);
                    cmd.Parameters.AddWithValue("@primaryColor", settings.PrimaryColor ?? "#4a2511");
                    cmd.Parameters.AddWithValue("@sidebarColor", settings.SidebarColor ?? "#2b1b17");
                    cmd.Parameters.AddWithValue("@accentColor", settings.AccentColor ?? "#b58361");
                    cmd.Parameters.AddWithValue("@backgroundColor", settings.BackgroundColor ?? "#fdfbf7");
                    cmd.Parameters.AddWithValue("@sidebarPosition", settings.SidebarPosition ?? "left");

                    int rowsAffected = cmd.ExecuteNonQuery();
                    if (rowsAffected == 0)
                    {
                        // No row existed yet — insert it directly with the values being saved.
                        InsertDefaultRow(conn, settings);
                    }
                }
            }
        }

        // Works out whether the store is currently open, honoring an overnight
        // schedule (e.g. 18:00–02:00) and the manual "temporarily closed" override.
        public StoreStatus GetStoreStatus(WebCustomization settings)
        {
            var status = new StoreStatus();

            bool parsedOpen = TimeSpan.TryParse(settings.OpeningTime, out var openTime);
            bool parsedClose = TimeSpan.TryParse(settings.ClosingTime, out var closeTime);

            status.HoursLabel = parsedOpen && parsedClose
                ? $"{FormatTime(openTime)} – {FormatTime(closeTime)}"
                : "Hours not set";
            status.OpeningTimeLabel = parsedOpen ? FormatTime(openTime) : "";
            status.ClosingTimeLabel = parsedClose ? FormatTime(closeTime) : "";

            if (settings.IsTemporarilyClosed)
            {
                status.IsOpen = false;
                status.Label = "Temporarily Closed";
                return status;
            }

            if (!parsedOpen || !parsedClose)
            {
                // No valid schedule configured — don't guess, just say so.
                status.IsOpen = false;
                status.Label = "Hours Not Set";
                return status;
            }

            var now = DateTime.Now.TimeOfDay;
            bool isOpen;

            if (closeTime > openTime)
            {
                // Normal same-day schedule, e.g. 07:00 - 21:00
                isOpen = now >= openTime && now < closeTime;
            }
            else
            {
                // Overnight schedule, e.g. 18:00 - 02:00
                isOpen = now >= openTime || now < closeTime;
            }

            status.IsOpen = isOpen;
            status.Label = isOpen ? "Open" : "Closed";
            return status;
        }

        private static string FormatTime(TimeSpan time)
        {
            return DateTime.Today.Add(time).ToString("h:mm tt");
        }

        private void InsertDefaultRow(SqlConnection conn, WebCustomization settings)
        {
            string insertSql = @"
                IF NOT EXISTS (SELECT 1 FROM store_settings WHERE id = 1)
                INSERT INTO store_settings (
                    id, store_name, store_tagline, business_address, contact_number, tax_id_number,
                    receipt_footer, logo_path, currency_symbol, vat_rate, low_stock_threshold,
                    opening_time, closing_time, is_temporarily_closed,
                    primary_color, sidebar_color, accent_color, background_color, sidebar_position
                ) VALUES (
                    1, @storeName, @storeTagline, @businessAddress, @contactNumber, @taxIdNumber,
                    @receiptFooter, @logoPath, @currencySymbol, @vatRate, @lowStockThreshold,
                    @openingTime, @closingTime, @isTemporarilyClosed,
                    @primaryColor, @sidebarColor, @accentColor, @backgroundColor, @sidebarPosition
                )";

            using (SqlCommand cmd = new SqlCommand(insertSql, conn))
            {
                cmd.Parameters.AddWithValue("@storeName", settings.StoreName ?? "Cafero");
                cmd.Parameters.AddWithValue("@storeTagline", settings.StoreTagline ?? "");
                cmd.Parameters.AddWithValue("@businessAddress", settings.BusinessAddress ?? "");
                cmd.Parameters.AddWithValue("@contactNumber", settings.ContactNumber ?? "");
                cmd.Parameters.AddWithValue("@taxIdNumber", settings.TaxIdNumber ?? "");
                cmd.Parameters.AddWithValue("@receiptFooter", settings.ReceiptFooter ?? "");
                cmd.Parameters.AddWithValue("@logoPath", (object?)settings.LogoPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@currencySymbol", settings.CurrencySymbol ?? "₱");
                cmd.Parameters.AddWithValue("@vatRate", settings.VatRate);
                cmd.Parameters.AddWithValue("@lowStockThreshold", settings.LowStockThreshold);
                cmd.Parameters.AddWithValue("@openingTime", settings.OpeningTime ?? "07:00");
                cmd.Parameters.AddWithValue("@closingTime", settings.ClosingTime ?? "21:00");
                cmd.Parameters.AddWithValue("@isTemporarilyClosed", settings.IsTemporarilyClosed);
                cmd.Parameters.AddWithValue("@primaryColor", settings.PrimaryColor ?? "#4a2511");
                cmd.Parameters.AddWithValue("@sidebarColor", settings.SidebarColor ?? "#2b1b17");
                cmd.Parameters.AddWithValue("@accentColor", settings.AccentColor ?? "#b58361");
                cmd.Parameters.AddWithValue("@backgroundColor", settings.BackgroundColor ?? "#fdfbf7");
                cmd.Parameters.AddWithValue("@sidebarPosition", settings.SidebarPosition ?? "left");
                cmd.ExecuteNonQuery();
            }
        }
    }
}
