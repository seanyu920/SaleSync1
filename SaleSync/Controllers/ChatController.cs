using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SaleSync.Controllers
{
    public class ChatController : Controller
    {
        private readonly string connectionString;

        public ChatController(IConfiguration configuration)
        {
            connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        private string CurrentUsername =>
            User.FindFirst("Username")?.Value ?? HttpContext.Session.GetString("Username");

        public async Task<IActionResult> Index(string chatWith = "")
        {
            string currentUser = CurrentUsername;
            if (string.IsNullOrWhiteSpace(currentUser))
                return RedirectToAction("Login", "Home");

            var contacts = await GetContactsForUserAsync(currentUser);
            string currentRole = await GetRoleAsync(currentUser);

            if (string.IsNullOrWhiteSpace(chatWith) && contacts.Count > 0)
                chatWith = contacts[0].Username;

            if (!string.IsNullOrWhiteSpace(chatWith) &&
                !await CanChatAsync(currentUser, chatWith))
            {
                chatWith = contacts.Count > 0 ? contacts[0].Username : "";
            }

            var messages = string.IsNullOrWhiteSpace(chatWith)
                ? new List<object>()
                : await GetHistoryAsync(currentUser, chatWith);

            if (!string.IsNullOrWhiteSpace(chatWith))
                await MarkAsReadAsync(currentUser, chatWith);

            ViewBag.CurrentUser = currentUser;
            ViewBag.CurrentRole = currentRole;
            ViewBag.ChatWith = chatWith;
            ViewBag.Contacts = contacts;
            ViewBag.Messages = messages;

            return View("~/Views/Chats/Index.cshtml");
        }

        [HttpGet]
        public async Task<IActionResult> GetContacts()
        {
            if (string.IsNullOrWhiteSpace(CurrentUsername)) return Unauthorized();
            return Json(await GetContactsForUserAsync(CurrentUsername));
        }

        [HttpGet]
        public async Task<IActionResult> GetChatHistory(string chatWith)
        {
            if (string.IsNullOrWhiteSpace(CurrentUsername)) return Unauthorized();
            if (!await CanChatAsync(CurrentUsername, chatWith)) return Forbid();

            await MarkAsReadAsync(CurrentUsername, chatWith);
            return Json(await GetHistoryAsync(CurrentUsername, chatWith));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAsRead(string chatWith)
        {
            if (string.IsNullOrWhiteSpace(CurrentUsername)) return Unauthorized();
            if (!await CanChatAsync(CurrentUsername, chatWith)) return Forbid();

            await MarkAsReadAsync(CurrentUsername, chatWith);
            return Ok();
        }

        private async Task<List<dynamic>> GetContactsForUserAsync(string currentUser)
        {
            var contacts = new List<dynamic>();
            string currentRole = await GetRoleAsync(currentUser);
            bool isStaff = IsStaff(currentRole);

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            string query = isStaff
                ? @"
                    SELECT u.username, r.role_name,
                           ISNULL(lastMessage.message_text, '') AS preview,
                           ISNULL(unread.total, 0) AS unread_count
                    FROM users u
                    LEFT JOIN roles r ON r.role_id = u.role_id
                    OUTER APPLY (
                        SELECT TOP 1 cm.message_text, cm.timestamp
                        FROM chat_messages cm
                        WHERE (cm.sender_username = @CurrentUser AND cm.receiver_username = u.username)
                           OR (cm.sender_username = u.username AND cm.receiver_username = @CurrentUser)
                        ORDER BY cm.timestamp DESC
                    ) lastMessage
                    OUTER APPLY (
                        SELECT COUNT(*) AS total
                        FROM chat_messages cm
                        WHERE cm.sender_username = u.username
                          AND cm.receiver_username = @CurrentUser
                          AND cm.is_read = 0
                    ) unread
                    WHERE u.is_active = 1
                      AND u.username <> @CurrentUser
                      AND ISNULL(r.role_name, '') NOT IN ('Admin', 'Manager', 'Cashier')
                    ORDER BY lastMessage.timestamp DESC, u.username"
                : @"
                    SELECT TOP 1 u.username, r.role_name,
                           '' AS preview, 0 AS unread_count
                    FROM users u
                    INNER JOIN roles r ON r.role_id = u.role_id
                    WHERE u.is_active = 1
                      AND r.role_name IN ('Admin', 'Manager', 'Cashier')
                    ORDER BY CASE r.role_name
                        WHEN 'Admin' THEN 0
                        WHEN 'Manager' THEN 1
                        ELSE 2
                    END, u.username";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@CurrentUser", currentUser);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                contacts.Add(new
                {
                    Username = reader["username"].ToString(),
                    Role = reader["role_name"]?.ToString() ?? "Customer",
                    Preview = reader["preview"]?.ToString() ?? "",
                    UnreadCount = Convert.ToInt32(reader["unread_count"])
                });
            }

            return contacts;
        }

        private async Task<List<object>> GetHistoryAsync(string currentUser, string chatWith)
        {
            var messages = new List<object>();

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
                SELECT sender_username, receiver_username, message_text, timestamp
                FROM chat_messages
                WHERE (sender_username = @CurrentUser AND receiver_username = @ChatWith)
                   OR (sender_username = @ChatWith AND receiver_username = @CurrentUser)
                ORDER BY timestamp ASC";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@CurrentUser", currentUser);
            cmd.Parameters.AddWithValue("@ChatWith", chatWith);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new
                {
                    Sender = reader["sender_username"].ToString(),
                    Receiver = reader["receiver_username"].ToString(),
                    Text = reader["message_text"].ToString(),
                    Time = Convert.ToDateTime(reader["timestamp"]).ToString("hh:mm tt")
                });
            }

            return messages;
        }

        private async Task MarkAsReadAsync(string currentUser, string chatWith)
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
                UPDATE chat_messages
                SET is_read = 1
                WHERE sender_username = @ChatWith
                  AND receiver_username = @CurrentUser
                  AND is_read = 0";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@CurrentUser", currentUser);
            cmd.Parameters.AddWithValue("@ChatWith", chatWith);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<bool> CanChatAsync(string firstUser, string secondUser)
        {
            if (string.IsNullOrWhiteSpace(secondUser)) return false;

            string firstRole = await GetRoleAsync(firstUser);
            string secondRole = await GetRoleAsync(secondUser);

            return !string.IsNullOrWhiteSpace(firstRole) &&
                   !string.IsNullOrWhiteSpace(secondRole) &&
                   IsStaff(firstRole) != IsStaff(secondRole);
        }

        private async Task<string> GetRoleAsync(string username)
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
                SELECT r.role_name
                FROM users u
                LEFT JOIN roles r ON r.role_id = u.role_id
                WHERE u.username = @Username AND u.is_active = 1";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Username", username);

            return (await cmd.ExecuteScalarAsync())?.ToString();
        }

        private static bool IsStaff(string role) =>
            role == "Admin" || role == "Manager" || role == "Cashier";
    }
}