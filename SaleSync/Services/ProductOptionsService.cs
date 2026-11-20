using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SaleSync.Services
{
    /// <summary>What a customer can pick for one menu item.</summary>
    public class ProductOptions
    {
        /// <summary>When true the customer/cashier must pick a sugar level (25/50/75/100%).</summary>
        public bool RequiresSugarLevel { get; set; }

        /// <summary>Optional extras for this item only. Empty = the item has no add-ons.</summary>
        public List<string> Addons { get; set; } = new List<string>();
    }

    /// <summary>Request body for saving the options of one item.</summary>
    public class SaveProductOptionsRequest
    {
        public int ProductId { get; set; }
        public bool RequiresSugarLevel { get; set; }
        public List<string> Addons { get; set; } = new List<string>();
    }

    /// <summary>
    /// Per-item add-ons and the "sugar level required" flag.
    ///
    /// Add-ons live in the product_addons table (one row per add-on per product) and the
    /// sugar flag is products.requires_sugar_level. A brand-new item has neither, so it
    /// shows no add-ons until an Admin/Manager sets them up on the Products page.
    ///
    /// Size variants ("Latte - Small" / "Latte - Large") are separate product rows, so
    /// saving options for one of them is applied to its sibling sizes too.
    ///
    /// The schema is created automatically the first time this service is used, so no
    /// manual SQL step is needed (SQL/add_product_addons.sql does the same thing by hand).
    /// </summary>
    public class ProductOptionsService
    {
        public const int MaxAddonsPerItem = 20;
        public const int MaxAddonLength = 50;

        private static readonly Regex SizeSuffix =
            new Regex(@"\s*-\s*(Small|Large)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly object SchemaLock = new object();
        private static bool _schemaReady;

        private readonly string _connectionString;

        public ProductOptionsService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // ---------------------------------------------------------------------------------
        // Keys shared with the views: "<category lowercase, no spaces>|<base name lowercase>"
        // ---------------------------------------------------------------------------------

        public static string BaseName(string productName) =>
            SizeSuffix.Replace(productName ?? "", "").Trim();

        public static string NormalizeCategory(string category) =>
            (category ?? "Uncategorized").Replace(" ", "").ToLowerInvariant();

        public static string MakeKey(string category, string productName) =>
            NormalizeCategory(category) + "|" + BaseName(productName).ToLowerInvariant();

        // ---------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------

        /// <summary>Options for every active menu item, keyed by MakeKey(category, name). Used by the customer page and the POS.</summary>
        public Dictionary<string, ProductOptions> GetOptionsMap()
        {
            var map = new Dictionary<string, ProductOptions>();

            try
            {
                EnsureSchema();

                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var byProduct = new Dictionary<int, List<string>>();
                using (var cmd = new SqlCommand(
                    "SELECT product_id, addon_name FROM product_addons WHERE is_active = 1 ORDER BY addon_id", conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int pid = Convert.ToInt32(r["product_id"]);
                        if (!byProduct.TryGetValue(pid, out var list))
                            byProduct[pid] = list = new List<string>();
                        list.Add(r["addon_name"].ToString());
                    }
                }

                const string sql = @"
                    SELECT p.product_id, p.product_name, c.category_name,
                           ISNULL(p.requires_sugar_level, 0) AS requires_sugar_level
                    FROM products p
                    LEFT JOIN categories c ON p.category_id = c.category_id
                    WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
                      AND p.is_archived = 0";

                using (var cmd = new SqlCommand(sql, conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string key = MakeKey(r["category_name"]?.ToString(), r["product_name"]?.ToString());

                        if (!map.TryGetValue(key, out var opts))
                            map[key] = opts = new ProductOptions();

                        if (Convert.ToBoolean(r["requires_sugar_level"]))
                            opts.RequiresSugarLevel = true;

                        if (byProduct.TryGetValue(Convert.ToInt32(r["product_id"]), out var addons))
                        {
                            foreach (var a in addons)
                                if (!opts.Addons.Contains(a, StringComparer.OrdinalIgnoreCase))
                                    opts.Addons.Add(a);
                        }
                    }
                }
            }
            catch
            {
                // If the options can't be read the menu still works, just without add-ons.
                return new Dictionary<string, ProductOptions>();
            }

            return map;
        }

        /// <summary>Options of one product (used by the Products page editor).</summary>
        public ProductOptions GetOptions(int productId)
        {
            EnsureSchema();

            var opts = new ProductOptions();

            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            using (var cmd = new SqlCommand(
                "SELECT ISNULL(requires_sugar_level, 0) FROM products WHERE product_id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@id", productId);
                var v = cmd.ExecuteScalar();
                opts.RequiresSugarLevel = v != null && v != DBNull.Value && Convert.ToBoolean(v);
            }

            using (var cmd = new SqlCommand(
                "SELECT addon_name FROM product_addons WHERE product_id = @id AND is_active = 1 ORDER BY addon_id", conn))
            {
                cmd.Parameters.AddWithValue("@id", productId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) opts.Addons.Add(r["addon_name"].ToString());
            }

            return opts;
        }

        // ---------------------------------------------------------------------------------
        // Writing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Replaces the add-ons and sugar flag of a product and of its other size variants.
        /// Returns the number of products updated (0 = product not found).
        /// </summary>
        public int SaveOptions(int productId, bool requiresSugarLevel, IEnumerable<string> addons)
        {
            EnsureSchema();

            var clean = (addons ?? Enumerable.Empty<string>())
                .Select(a => (a ?? "").Trim())
                .Where(a => a.Length > 0)
                .Select(a => a.Length > MaxAddonLength ? a.Substring(0, MaxAddonLength) : a)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxAddonsPerItem)
                .ToList();

            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            // The product plus its other sizes (same category, same name without "- Small/Large").
            var targets = new List<int>();

            string name = null;
            int? categoryId = null;

            using (var cmd = new SqlCommand(
                "SELECT product_name, category_id FROM products WHERE product_id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@id", productId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return 0;
                name = r["product_name"].ToString();
                categoryId = r["category_id"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["category_id"]);
            }

            targets.Add(productId);

            using (var cmd = new SqlCommand(@"
                SELECT product_id, product_name FROM products
                WHERE product_id <> @id
                  AND (is_ingredient = 0 OR is_ingredient IS NULL)
                  AND ((@cat IS NULL AND category_id IS NULL) OR category_id = @cat)", conn))
            {
                cmd.Parameters.AddWithValue("@id", productId);
                cmd.Parameters.AddWithValue("@cat", (object)categoryId ?? DBNull.Value);
                using var r = cmd.ExecuteReader();
                string baseName = BaseName(name);
                while (r.Read())
                {
                    if (string.Equals(BaseName(r["product_name"].ToString()), baseName, StringComparison.OrdinalIgnoreCase))
                        targets.Add(Convert.ToInt32(r["product_id"]));
                }
            }

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (int pid in targets)
                {
                    using (var cmd = new SqlCommand(
                        "UPDATE products SET requires_sugar_level = @sugar WHERE product_id = @id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@sugar", requiresSugarLevel);
                        cmd.Parameters.AddWithValue("@id", pid);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SqlCommand("DELETE FROM product_addons WHERE product_id = @id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", pid);
                        cmd.ExecuteNonQuery();
                    }

                    foreach (var addon in clean)
                    {
                        using var cmd = new SqlCommand(
                            "INSERT INTO product_addons (product_id, addon_name) VALUES (@id, @name)", conn, tx);
                        cmd.Parameters.AddWithValue("@id", pid);
                        cmd.Parameters.AddWithValue("@name", addon);
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return targets.Count;
        }

        // ---------------------------------------------------------------------------------
        // Schema
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Creates product_addons and products.requires_sugar_level when they are missing.
        /// When the table is created for the first time, the add-ons that used to be hardcoded
        /// in the ordering pages are copied onto the existing items once, so nothing disappears.
        /// After that, the Products page is the only place add-ons are managed.
        /// </summary>
        private void EnsureSchema()
        {
            if (_schemaReady) return;

            lock (SchemaLock)
            {
                if (_schemaReady) return;

                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                bool tableExisted;
                using (var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID('product_addons', 'U') IS NULL THEN 0 ELSE 1 END", conn))
                    tableExisted = Convert.ToInt32(cmd.ExecuteScalar()) == 1;

                bool columnExisted;
                using (var cmd = new SqlCommand("SELECT CASE WHEN COL_LENGTH('products', 'requires_sugar_level') IS NULL THEN 0 ELSE 1 END", conn))
                    columnExisted = Convert.ToInt32(cmd.ExecuteScalar()) == 1;

                if (!columnExisted)
                {
                    Run(conn, "ALTER TABLE products ADD requires_sugar_level BIT NOT NULL CONSTRAINT DF_products_requires_sugar_level DEFAULT 0");
                }

                if (!tableExisted)
                {
                    Run(conn, @"
                        CREATE TABLE product_addons (
                            addon_id   INT IDENTITY(1,1) PRIMARY KEY,
                            product_id INT NOT NULL,
                            addon_name NVARCHAR(100) NOT NULL,
                            is_active  BIT NOT NULL CONSTRAINT DF_product_addons_is_active DEFAULT 1
                        )");

                    Run(conn, "CREATE INDEX IX_product_addons_product ON product_addons (product_id)");
                }

                if (!tableExisted || !columnExisted)
                    SeedFromLegacyDefaults(conn, seedAddons: !tableExisted, seedSugar: !columnExisted);

                _schemaReady = true;
            }
        }

        private static void Run(SqlConnection conn, string sql)
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        // The lists that used to be hardcoded in CustomerOrdering.cshtml / CashierMenu.cshtml.
        private static readonly Dictionary<string, string[]> LegacyAddonsByCategory = new Dictionary<string, string[]>
        {
            ["coffee"] = new[] { "Extra Sugar", "Oat Milk", "Extra Ice" },
            ["milktea"] = new[] { "Extra Pearls", "Extra Ice" },
            ["fruittea"] = new[] { "Extra Sugar", "Extra Ice" },
            ["ricemeal"] = new[] { "Extra Rice" },
            ["snacks&pasta"] = new[] { "Extra Sauce", "Extra Cheese" }
        };

        private void SeedFromLegacyDefaults(SqlConnection conn, bool seedAddons, bool seedSugar)
        {
            var products = new List<(int Id, string Name, string Category)>();

            using (var cmd = new SqlCommand(@"
                SELECT p.product_id, p.product_name, c.category_name
                FROM products p
                LEFT JOIN categories c ON p.category_id = c.category_id
                WHERE (p.is_ingredient = 0 OR p.is_ingredient IS NULL)", conn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    products.Add((Convert.ToInt32(r["product_id"]), r["product_name"].ToString(), r["category_name"]?.ToString()));
            }

            foreach (var p in products)
            {
                string cat = NormalizeCategory(p.Category);
                string lowerName = p.Name.ToLowerInvariant();

                if (seedSugar && cat == "milktea")
                    Run(conn, $"UPDATE products SET requires_sugar_level = 1 WHERE product_id = {p.Id}");

                if (!seedAddons) continue;

                string[] addons;
                if (lowerName.Contains("flavored fries")) addons = new[] { "Barbeque", "Cheese", "Sour Cream" };
                else if (lowerName.Contains("siomai")) addons = Array.Empty<string>();
                else addons = LegacyAddonsByCategory.TryGetValue(cat, out var list) ? list : Array.Empty<string>();

                foreach (var a in addons)
                {
                    using var cmd = new SqlCommand(
                        "INSERT INTO product_addons (product_id, addon_name) VALUES (@id, @name)", conn);
                    cmd.Parameters.AddWithValue("@id", p.Id);
                    cmd.Parameters.AddWithValue("@name", a);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
