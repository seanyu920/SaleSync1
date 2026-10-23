using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SaleSync.Services
{
    public class AddCategoryRequest
    {
        public string CategoryName { get; set; }
    }

    public enum CategoryAddResult
    {
        Added,
        AlreadyExists
    }

    /// <summary>
    /// Menu categories (the cards on the Products page and the POS).
    ///
    /// The "categories" table is shared with inventory ingredients, so menu categories
    /// are marked with categories.is_menu_category = 1. A category also counts as a menu
    /// category if any non-archived menu product (non-ingredient) is already using it,
    /// so existing data keeps working even before the flag is filled in.
    /// </summary>
    public class CategoryService
    {
        public const int MaxNameLength = 50;

        private readonly string _connectionString;

        public CategoryService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>Names of all categories that should appear as cards on the Products page and in the POS.</summary>
        public List<string> GetMenuCategoryNames()
        {
            var names = new List<string>();

            const string sql = @"
                SELECT DISTINCT c.category_name
                FROM categories c
                WHERE c.category_name IS NOT NULL
                  AND LTRIM(RTRIM(c.category_name)) <> ''
                  AND (
                        c.is_menu_category = 1
                        OR EXISTS (
                            SELECT 1
                            FROM products p
                            WHERE p.category_id = c.category_id
                              AND (p.is_ingredient = 0 OR p.is_ingredient IS NULL)
                              AND (p.is_archived = 0 OR p.is_archived IS NULL)
                        )
                      )
                ORDER BY c.category_name";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var name = r["category_name"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Adds a menu category. If a category with that name already exists but is not yet a
        /// menu category (for example an old ingredient category), it is switched on as a menu
        /// category instead of creating a duplicate row.
        /// </summary>
        public CategoryAddResult AddMenuCategory(string name)
        {
            name = (name ?? string.Empty).Trim();

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    int? existingId = null;
                    bool existingIsMenu = false;

                    const string findSql = @"
                        SELECT TOP 1 c.category_id,
                               CASE WHEN c.is_menu_category = 1 THEN 1 ELSE 0 END AS is_menu
                        FROM categories c WITH (UPDLOCK, HOLDLOCK)
                        WHERE UPPER(LTRIM(RTRIM(c.category_name))) = UPPER(@name)";

                    using (var find = new SqlCommand(findSql, conn, tx))
                    {
                        find.Parameters.AddWithValue("@name", name);
                        using (var r = find.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                existingId = Convert.ToInt32(r["category_id"]);
                                existingIsMenu = Convert.ToInt32(r["is_menu"]) == 1;
                            }
                        }
                    }

                    if (existingId.HasValue)
                    {
                        if (existingIsMenu)
                        {
                            tx.Rollback();
                            return CategoryAddResult.AlreadyExists;
                        }

                        using (var upd = new SqlCommand(
                            "UPDATE categories SET is_menu_category = 1 WHERE category_id = @id", conn, tx))
                        {
                            upd.Parameters.AddWithValue("@id", existingId.Value);
                            upd.ExecuteNonQuery();
                        }

                        tx.Commit();
                        return CategoryAddResult.Added;
                    }

                    // Works whether category_id is an IDENTITY column or a plain int column.
                    const string insertSql = @"
                        IF COLUMNPROPERTY(OBJECT_ID('categories'), 'category_id', 'IsIdentity') = 1
                        BEGIN
                            INSERT INTO categories (category_name, is_menu_category)
                            VALUES (@name, 1);
                        END
                        ELSE
                        BEGIN
                            INSERT INTO categories (category_id, category_name, is_menu_category)
                            SELECT ISNULL(MAX(category_id), 0) + 1, @name, 1
                            FROM categories WITH (TABLOCKX);
                        END";

                    using (var ins = new SqlCommand(insertSql, conn, tx))
                    {
                        ins.Parameters.AddWithValue("@name", name);
                        ins.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return CategoryAddResult.Added;
                }
            }
        }
    }
}
