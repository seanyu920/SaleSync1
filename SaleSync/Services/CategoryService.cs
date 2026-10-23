using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SaleSync.Models;

namespace SaleSync.Services
{
    /// <summary>Request body used by AddCategory and ArchiveCategory.</summary>
    public class CategoryRequest
    {
        public string CategoryName { get; set; }
    }

    public enum CategoryAddResult
    {
        Added,
        AlreadyExists,
        ExistsInArchive
    }

    public class CategoryArchiveResult
    {
        public bool AlreadyArchived { get; set; }
        public string CategoryName { get; set; }
        public int ItemsArchived { get; set; }
    }

    public class CategoryRestoreResult
    {
        public bool Found { get; set; }
        public string CategoryName { get; set; }
        public int ItemsRestored { get; set; }
    }

    /// <summary>
    /// Menu categories (the cards on the Products page and the POS).
    ///
    /// The "categories" table is shared with inventory ingredients, so menu categories
    /// are marked with categories.is_menu_category = 1. A category also counts as a menu
    /// category if any non-archived menu product (non-ingredient) is already using it.
    ///
    /// Categories are never hard deleted. Archiving sets categories.is_archived = 1 and
    /// archives the menu items inside it (products.archived_by_category = 1 marks them so
    /// Restore only brings back the items that were archived with the category).
    /// </summary>
    public class CategoryService
    {
        public const int MaxNameLength = 50;

        private readonly string _connectionString;

        public CategoryService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // ---------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------

        /// <summary>Names of all active categories that should appear as cards on the Products page and in the POS.</summary>
        public List<string> GetMenuCategoryNames()
        {
            var names = new List<string>();

            const string sql = @"
                SELECT DISTINCT c.category_name
                FROM categories c
                WHERE c.category_name IS NOT NULL
                  AND LTRIM(RTRIM(c.category_name)) <> ''
                  AND c.is_archived = 0
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

        /// <summary>Names of archived categories. The views use this to hide the built-in default cards too.</summary>
        public List<string> GetArchivedCategoryNames()
        {
            var names = new List<string>();

            const string sql = @"
                SELECT c.category_name
                FROM categories c
                WHERE c.is_archived = 1
                  AND c.category_name IS NOT NULL
                  AND LTRIM(RTRIM(c.category_name)) <> ''";

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

        /// <summary>Archived categories for the Archive page, with how many items were archived along with each.</summary>
        public List<ArchivedCategoryModel> GetArchivedCategories()
        {
            var list = new List<ArchivedCategoryModel>();

            const string sql = @"
                SELECT c.category_id,
                       c.category_name,
                       (SELECT COUNT(*)
                        FROM products p
                        WHERE p.category_id = c.category_id
                          AND p.archived_by_category = 1
                          AND p.is_archived = 1) AS item_count
                FROM categories c
                WHERE c.is_archived = 1
                ORDER BY c.category_name";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ArchivedCategoryModel
                        {
                            CategoryId = Convert.ToInt32(r["category_id"]),
                            CategoryName = r["category_name"]?.ToString() ?? string.Empty,
                            ItemCount = r["item_count"] != DBNull.Value ? Convert.ToInt32(r["item_count"]) : 0
                        });
                    }
                }
            }

            return list;
        }

        // ---------------------------------------------------------------------------------
        // Adding
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Adds a menu category. If a category with that name already exists but is not yet a
        /// menu category (for example an old ingredient category), it is switched on as a menu
        /// category instead of creating a duplicate row. An archived category is never
        /// re-created: the caller is told to restore it from the Archive page instead.
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
                    bool existingIsArchived = false;

                    const string findSql = @"
                        SELECT TOP 1 c.category_id,
                               CASE WHEN c.is_menu_category = 1 THEN 1 ELSE 0 END AS is_menu,
                               CASE WHEN c.is_archived = 1 THEN 1 ELSE 0 END AS is_arch
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
                                existingIsArchived = Convert.ToInt32(r["is_arch"]) == 1;
                            }
                        }
                    }

                    if (existingId.HasValue)
                    {
                        if (existingIsArchived)
                        {
                            tx.Rollback();
                            return CategoryAddResult.ExistsInArchive;
                        }

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

                    InsertCategoryRow(conn, tx, name, archived: false);

                    tx.Commit();
                    return CategoryAddResult.Added;
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Archiving (soft delete) and restoring
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Archives a category and the active menu items inside it. Ingredients that happen to
        /// share the category row are left alone. Nothing is deleted.
        /// </summary>
        public CategoryArchiveResult ArchiveCategory(string name)
        {
            name = (name ?? string.Empty).Trim();

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    int? categoryId = null;
                    bool isArchived = false;
                    string storedName = name;

                    const string findSql = @"
                        SELECT TOP 1 c.category_id,
                               c.category_name,
                               CASE WHEN c.is_archived = 1 THEN 1 ELSE 0 END AS is_arch
                        FROM categories c WITH (UPDLOCK, HOLDLOCK)
                        WHERE UPPER(LTRIM(RTRIM(c.category_name))) = UPPER(@name)";

                    using (var find = new SqlCommand(findSql, conn, tx))
                    {
                        find.Parameters.AddWithValue("@name", name);
                        using (var r = find.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                categoryId = Convert.ToInt32(r["category_id"]);
                                storedName = r["category_name"]?.ToString()?.Trim() ?? name;
                                isArchived = Convert.ToInt32(r["is_arch"]) == 1;
                            }
                        }
                    }

                    // A built-in default card (Coffee, Milk Tea, ...) with no database row yet:
                    // create the row already archived so the card can still be hidden and later restored.
                    if (!categoryId.HasValue)
                    {
                        InsertCategoryRow(conn, tx, name, archived: true);
                        tx.Commit();

                        return new CategoryArchiveResult
                        {
                            CategoryName = name,
                            ItemsArchived = 0
                        };
                    }

                    if (isArchived)
                    {
                        tx.Rollback();
                        return new CategoryArchiveResult
                        {
                            AlreadyArchived = true,
                            CategoryName = storedName
                        };
                    }

                    using (var cat = new SqlCommand(
                        "UPDATE categories SET is_archived = 1 WHERE category_id = @id", conn, tx))
                    {
                        cat.Parameters.AddWithValue("@id", categoryId.Value);
                        cat.ExecuteNonQuery();
                    }

                    int itemsArchived;
                    const string archiveItemsSql = @"
                        UPDATE products
                        SET is_archived = 1,
                            archived_by_category = 1
                        WHERE category_id = @id
                          AND (is_ingredient = 0 OR is_ingredient IS NULL)
                          AND (is_archived = 0 OR is_archived IS NULL)";

                    using (var items = new SqlCommand(archiveItemsSql, conn, tx))
                    {
                        items.Parameters.AddWithValue("@id", categoryId.Value);
                        itemsArchived = items.ExecuteNonQuery();
                    }

                    tx.Commit();

                    return new CategoryArchiveResult
                    {
                        CategoryName = storedName,
                        ItemsArchived = itemsArchived
                    };
                }
            }
        }

        /// <summary>Restores an archived category and the items that were archived together with it.</summary>
        public CategoryRestoreResult RestoreCategory(int categoryId)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    string categoryName = null;

                    using (var find = new SqlCommand(
                        "SELECT category_name FROM categories WITH (UPDLOCK, HOLDLOCK) WHERE category_id = @id AND is_archived = 1",
                        conn, tx))
                    {
                        find.Parameters.AddWithValue("@id", categoryId);
                        categoryName = find.ExecuteScalar()?.ToString()?.Trim();
                    }

                    if (categoryName == null)
                    {
                        tx.Rollback();
                        return new CategoryRestoreResult { Found = false };
                    }

                    using (var cat = new SqlCommand(
                        "UPDATE categories SET is_archived = 0 WHERE category_id = @id", conn, tx))
                    {
                        cat.Parameters.AddWithValue("@id", categoryId);
                        cat.ExecuteNonQuery();
                    }

                    int itemsRestored;
                    const string restoreItemsSql = @"
                        UPDATE products
                        SET is_archived = 0,
                            archived_by_category = 0
                        WHERE category_id = @id
                          AND archived_by_category = 1";

                    using (var items = new SqlCommand(restoreItemsSql, conn, tx))
                    {
                        items.Parameters.AddWithValue("@id", categoryId);
                        itemsRestored = items.ExecuteNonQuery();
                    }

                    tx.Commit();

                    return new CategoryRestoreResult
                    {
                        Found = true,
                        CategoryName = categoryName,
                        ItemsRestored = itemsRestored
                    };
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        // Works whether category_id is an IDENTITY column or a plain int column.
        private static void InsertCategoryRow(SqlConnection conn, SqlTransaction tx, string name, bool archived)
        {
            const string insertSql = @"
                IF COLUMNPROPERTY(OBJECT_ID('categories'), 'category_id', 'IsIdentity') = 1
                BEGIN
                    INSERT INTO categories (category_name, is_menu_category, is_archived)
                    VALUES (@name, 1, @archived);
                END
                ELSE
                BEGIN
                    INSERT INTO categories (category_id, category_name, is_menu_category, is_archived)
                    SELECT ISNULL(MAX(category_id), 0) + 1, @name, 1, @archived
                    FROM categories WITH (TABLOCKX);
                END";

            using (var ins = new SqlCommand(insertSql, conn, tx))
            {
                ins.Parameters.AddWithValue("@name", name);
                ins.Parameters.AddWithValue("@archived", archived ? 1 : 0);
                ins.ExecuteNonQuery();
            }
        }
    }
}
