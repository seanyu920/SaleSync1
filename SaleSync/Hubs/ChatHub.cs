using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace SaleSync.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly string _connectionString;

        public ChatHub(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        private string CurrentUsername => Context.User?.FindFirst("Username")?.Value;

        public override async Task OnConnectedAsync()
        {
            if (!string.IsNullOrWhiteSpace(CurrentUsername))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, CurrentUsername);
            }

            await base.OnConnectedAsync();
        }

        public async Task SendMessage(string receiver, string message)
        {
            string sender = CurrentUsername;

            if (string.IsNullOrWhiteSpace(sender) ||
                string.IsNullOrWhiteSpace(receiver) ||
                string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            if (!await IsAllowedConversationAsync(conn, sender, receiver))
            {
                return; // Blocks Admin↔Manager, Cashier↔Admin, and Customer↔Customer chats.
            }

            DateTime timestamp = DateTime.Now;

            const string insertQuery = @"
                INSERT INTO chat_messages
                    (sender_username, receiver_username, message_text, timestamp, is_read)
                VALUES
                    (@Sender, @Receiver, @Message, @Timestamp, 0)";

            using (var cmd = new SqlCommand(insertQuery, conn))
            {
                cmd.Parameters.AddWithValue("@Sender", sender);
                cmd.Parameters.AddWithValue("@Receiver", receiver);
                cmd.Parameters.AddWithValue("@Message", message.Trim());
                cmd.Parameters.AddWithValue("@Timestamp", timestamp);
                await cmd.ExecuteNonQueryAsync();
            }

            string time = timestamp.ToString("hh:mm tt");

            await Clients.Group(sender)
                .SendAsync("ReceiveMessage", sender, receiver, message.Trim(), time);

            if (!string.Equals(sender, receiver, StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Group(receiver)
                    .SendAsync("ReceiveMessage", sender, receiver, message.Trim(), time);
            }
        }

        private static async Task<bool> IsAllowedConversationAsync(
            SqlConnection conn, string sender, string receiver)
        {
            const string query = @"
                SELECT u.username, r.role_name
                FROM users u
                LEFT JOIN roles r ON r.role_id = u.role_id
                WHERE u.username IN (@Sender, @Receiver)
                  AND u.is_active = 1";

            string senderRole = null;
            string receiverRole = null;

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Sender", sender);
            cmd.Parameters.AddWithValue("@Receiver", receiver);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string username = reader["username"].ToString();
                string role = reader["role_name"]?.ToString() ?? "";

                if (string.Equals(username, sender, StringComparison.OrdinalIgnoreCase))
                    senderRole = role;

                if (string.Equals(username, receiver, StringComparison.OrdinalIgnoreCase))
                    receiverRole = role;
            }

            if (senderRole == null || receiverRole == null) return false;

            bool senderIsStaff = IsStaff(senderRole);
            bool receiverIsStaff = IsStaff(receiverRole);

            return senderIsStaff != receiverIsStaff;
        }

        private static bool IsStaff(string role) =>
            role == "Admin" || role == "Manager" || role == "Cashier";
    }
}