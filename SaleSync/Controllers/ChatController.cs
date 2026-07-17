using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;

namespace SaleSync.Controllers
{
    public class ChatController : Controller
    {
        private readonly string connectionString;

        public ChatController(IConfiguration configuration)
        {
            connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // The "Username" claim is set at login (see HomeController) and matches
        // users.username. ClaimTypes.Name holds the display/full name instead,
        // so it must NOT be used here or chat routing breaks.
        private string CurrentUsername =>
            User.FindFirst("Username")?.Value ?? HttpContext.Session.GetString("Username");

        // Older chat rows stored full_name or role names instead of users.username.
        // Collect every identifier that should match one account in history queries.
        private static async Task<List<string>> GetUserIdentifiersAsync(SqlConnection conn, string accountKey)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(accountKey)) return ids;

            string trimmed = accountKey.Trim();
            ids.Add(trimmed);

            const string profileQuery = @"
                SELECT username, full_name
                FROM users
                WHERE username = @Key OR full_name = @Key";

            using (var cmd = new SqlCommand(profileQuery, conn))
            {
                cmd.Parameters.AddWithValue("@Key", trimmed);
                using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                {
                    if (r.Read())
                    {
                        AddIfMissing(ids, r["username"]?.ToString());
                        AddIfMissing(ids, r["full_name"]?.ToString());
                    }
                }
            }

            return ids;
        }

        private static void AddIfMissing(List<string> ids, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string trimmed = value.Trim();
            if (!ids.Any(id => string.Equals(id, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                ids.Add(trimmed);
            }
        }

        private static string BuildInClause(List<string> values, string paramPrefix, SqlCommand cmd)
        {
            var parts = new List<string>();
            for (int i = 0; i < values.Count; i++)
            {
                string param = $"{paramPrefix}{i}";
                parts.Add($"@{param}");
                cmd.Parameters.AddWithValue($"@{param}", values[i]);
            }

            return string.Join(", ", parts);
        }

        private static async Task<List<object>> ReadChatHistoryAsync(
            SqlConnection conn,
            List<string> userAIds,
            List<string> userBIds)
        {
            var messages = new List<object>();

            if (userAIds.Count == 0 || userBIds.Count == 0) return messages;

            using (var cmd = new SqlCommand())
            {
                cmd.Connection = conn;
                string senderA = BuildInClause(userAIds, "UserA", cmd);
                string senderB = BuildInClause(userBIds, "UserB", cmd);

                cmd.CommandText = $@"
                    SELECT sender_username, receiver_username, message_text, timestamp
                    FROM chat_messages
                    WHERE (RTRIM(sender_username) IN ({senderA}) AND RTRIM(receiver_username) IN ({senderB}))
                       OR (RTRIM(sender_username) IN ({senderB}) AND RTRIM(receiver_username) IN ({senderA}))
                       OR (
                            RTRIM(sender_username) IN ({senderA})
                            AND LEN(RTRIM(ISNULL(receiver_username, ''))) = 0
                            AND EXISTS (
                                SELECT 1
                                FROM chat_messages thread
                                WHERE (RTRIM(thread.sender_username) IN ({senderA}) AND RTRIM(thread.receiver_username) IN ({senderB}))
                                   OR (RTRIM(thread.sender_username) IN ({senderB}) AND RTRIM(thread.receiver_username) IN ({senderA}))
                            )
                          )
                       OR (
                            RTRIM(sender_username) IN ({senderB})
                            AND LEN(RTRIM(ISNULL(receiver_username, ''))) = 0
                            AND EXISTS (
                                SELECT 1
                                FROM chat_messages thread
                                WHERE (RTRIM(thread.sender_username) IN ({senderA}) AND RTRIM(thread.receiver_username) IN ({senderB}))
                                   OR (RTRIM(thread.sender_username) IN ({senderB}) AND RTRIM(thread.receiver_username) IN ({senderA}))
                            )
                          )
                    ORDER BY timestamp ASC";

                using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                {
                    while (r.Read())
                    {
                        messages.Add(new
                        {
                            sender = r["sender_username"].ToString().Trim(),
                            receiver = r["receiver_username"].ToString().Trim(),
                            text = r["message_text"].ToString(),
                            time = Convert.ToDateTime(r["timestamp"]).ToString("hh:mm tt")
                        });
                    }
                }
            }

            return messages;
        }

        // GET: Chat (Dedicated page for Staff)
        public async Task<IActionResult> Index(string chatWith = "")
        {
            string currentUser = CurrentUsername;

            if (string.IsNullOrEmpty(currentUser))
            {
                return RedirectToAction("Login", "Home");
            }

            ViewBag.CurrentUser = currentUser;

            var messages = new List<dynamic>();
            var contacts = new List<dynamic>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                // Fetch other active accounts for the coordination sidebar
                string usersQuery = @"
                    SELECT u.username, r.role_name 
                    FROM users u
                    LEFT JOIN roles r ON u.role_id = r.role_id
                    WHERE u.username <> @CurrentUser AND u.is_active = 1";

                using (SqlCommand cmd = new SqlCommand(usersQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@CurrentUser", currentUser);
                    using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                    {
                        while (r.Read())
                        {
                            contacts.Add(new
                            {
                                Username = r["username"].ToString(),
                                Role = r["role_name"].ToString()
                            });
                        }
                    }
                }

                if (string.IsNullOrEmpty(chatWith) && contacts.Count > 0)
                {
                    chatWith = contacts[0].Username;
                }

                ViewBag.ChatWith = chatWith;

                // Fetch conversational history
                if (!string.IsNullOrEmpty(chatWith))
                {
                    var currentUserIds = await GetUserIdentifiersAsync(conn, currentUser);
                    var chatWithIds = await GetUserIdentifiersAsync(conn, chatWith);

                    if (await IsStaffAccountAsync(conn, currentUser) || await IsStaffAccountAsync(conn, chatWith))
                    {
                        currentUserIds.Add("Admin");
                        chatWithIds.Add("Admin");
                    }

                    var history = await ReadChatHistoryAsync(conn, currentUserIds, chatWithIds);

                    foreach (dynamic entry in history)
                    {
                        messages.Add(new
                        {
                            Sender = entry.sender,
                            Receiver = entry.receiver,
                            Text = entry.text,
                            Time = entry.time
                        });
                    }
                }
            }

            ViewBag.Messages = messages;
            ViewBag.Contacts = contacts;

            return View("~/Views/Chats/Index.cshtml");
        }

        // GET: Chat/GetChatHistory (API endpoint for the customer widget and staff drawer)
        [HttpGet]
        public async Task<IActionResult> GetChatHistory(string chatWith)
        {
            string currentUser = CurrentUsername;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (string.IsNullOrEmpty(chatWith)) return Json(new List<object>());

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                var currentUserIds = await GetUserIdentifiersAsync(conn, currentUser);
                var chatWithIds = await GetUserIdentifiersAsync(conn, chatWith);

                // Legacy rows used the role label "Admin" instead of a staff username.
                if (await IsStaffAccountAsync(conn, currentUser) || await IsStaffAccountAsync(conn, chatWith))
                {
                    chatWithIds.Add("Admin");
                    currentUserIds.Add("Admin");
                }

                var messages = await ReadChatHistoryAsync(conn, currentUserIds, chatWithIds);
                return Json(messages);
            }
        }

        // GET: Chat/GetContacts (API endpoint for widgets like the Admin dashboard chat drawer)
        [HttpGet]
        public async Task<IActionResult> GetContacts()
        {
            string currentUser = CurrentUsername;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            var contacts = new List<object>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();
                var currentUserIds = await GetUserIdentifiersAsync(conn, currentUser);
                string senderMatch = null;

                using (var cmd = new SqlCommand())
                {
                    cmd.Connection = conn;

                    if (currentUserIds.Count > 0)
                    {
                        senderMatch = BuildInClause(currentUserIds, "CurrentUser", cmd);
                    }

                    string usersQuery = currentUserIds.Count > 0
                        ? $@"
                            SELECT u.username, r.role_name,
                                   MAX(cm.timestamp) AS last_message_at
                            FROM users u
                            LEFT JOIN roles r ON u.role_id = r.role_id
                            LEFT JOIN chat_messages cm ON
                                (RTRIM(cm.sender_username) IN ({senderMatch}) AND RTRIM(cm.receiver_username) IN (u.username, u.full_name, 'Admin'))
                                OR (RTRIM(cm.receiver_username) IN ({senderMatch}) AND RTRIM(cm.sender_username) IN (u.username, u.full_name, 'Admin'))
                            WHERE u.username <> @CurrentUser AND u.is_active = 1
                            GROUP BY u.username, r.role_name
                            ORDER BY CASE WHEN MAX(cm.timestamp) IS NULL THEN 1 ELSE 0 END,
                                     MAX(cm.timestamp) DESC,
                                     u.username"
                        : @"
                            SELECT u.username, r.role_name, CAST(NULL AS datetime) AS last_message_at
                            FROM users u
                            LEFT JOIN roles r ON u.role_id = r.role_id
                            WHERE u.username <> @CurrentUser AND u.is_active = 1
                            ORDER BY u.username";

                    cmd.CommandText = usersQuery;
                    cmd.Parameters.AddWithValue("@CurrentUser", currentUser);

                    using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                    {
                        while (r.Read())
                        {
                            contacts.Add(new
                            {
                                username = r["username"].ToString(),
                                role = r["role_name"]?.ToString() ?? "N/A"
                            });
                        }
                    }
                }
            }

            return Json(contacts);
        }

        private static async Task<bool> IsStaffAccountAsync(SqlConnection conn, string accountKey)
        {
            const string query = @"
                SELECT 1
                FROM users u
                INNER JOIN roles r ON u.role_id = r.role_id
                WHERE (u.username = @Key OR u.full_name = @Key)
                  AND u.is_active = 1
                  AND r.role_name IN ('Admin', 'Manager', 'Cashier')";

            using (var cmd = new SqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("@Key", accountKey.Trim());
                var result = await cmd.ExecuteScalarAsync();
                return result != null;
            }
        }

        // GET: Chat/GetSupportAgent
        // The customer-facing widget doesn't know any staff usernames, and
        // "Admin" is a role name, not an account. Prefer the staff member the
        // customer already has a thread with, then fall back to the default
        // active staff account.
        [HttpGet]
        public async Task<IActionResult> GetSupportAgent()
        {
            string currentUser = CurrentUsername;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                var currentUserIds = await GetUserIdentifiersAsync(conn, currentUser);
                if (currentUserIds.Count > 0)
                {
                    using (var cmd = new SqlCommand())
                    {
                        cmd.Connection = conn;
                        string senderMatch = BuildInClause(currentUserIds, "CurrentUser", cmd);

                        cmd.CommandText = $@"
                            SELECT TOP 1
                                CASE
                                    WHEN RTRIM(sender_username) IN ({senderMatch}) THEN RTRIM(receiver_username)
                                    ELSE RTRIM(sender_username)
                                END AS partner,
                                timestamp
                            FROM chat_messages
                            WHERE RTRIM(sender_username) IN ({senderMatch})
                               OR RTRIM(receiver_username) IN ({senderMatch})
                            ORDER BY timestamp DESC";

                        string partner = null;
                        using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                        {
                            if (r.Read())
                            {
                                partner = r["partner"]?.ToString()?.Trim();
                            }
                        }

                        if (!string.IsNullOrEmpty(partner))
                        {
                            if (string.Equals(partner, "Admin", StringComparison.OrdinalIgnoreCase))
                            {
                                string staffAgent = await ResolveDefaultStaffAgentAsync(conn, currentUser);
                                if (!string.IsNullOrEmpty(staffAgent))
                                {
                                    return Json(new { agent = staffAgent });
                                }
                            }
                            else
                            {
                                string resolvedPartner = await ResolveUsernameAsync(conn, partner);
                                if (!string.IsNullOrEmpty(resolvedPartner))
                                {
                                    return Json(new { agent = resolvedPartner });
                                }
                            }
                        }
                    }
                }

                string agent = await ResolveDefaultStaffAgentAsync(conn, currentUser);
                return Json(new { agent });
            }
        }

        private static async Task<string> ResolveUsernameAsync(SqlConnection conn, string accountKey)
        {
            const string query = @"
                SELECT TOP 1 username
                FROM users
                WHERE username = @Key OR full_name = @Key";

            using (var cmd = new SqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("@Key", accountKey.Trim());
                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString();
            }
        }

        private static async Task<string> ResolveDefaultStaffAgentAsync(SqlConnection conn, string currentUser)
        {
            const string query = @"
                SELECT TOP 1 u.username
                FROM users u
                INNER JOIN roles r ON u.role_id = r.role_id
                WHERE u.is_active = 1
                  AND u.username <> @CurrentUser
                  AND r.role_name IN ('Admin', 'Manager', 'Cashier')
                ORDER BY
                    CASE r.role_name
                        WHEN 'Admin' THEN 0
                        WHEN 'Manager' THEN 1
                        ELSE 2
                    END, u.username";

            using (var cmd = new SqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("@CurrentUser", currentUser);
                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString();
            }
        }
    }
}
