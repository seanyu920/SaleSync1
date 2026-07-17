using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace SaleSync.Hubs
{
    // Requires the same cookie auth used by the rest of the site, so
    // Context.User is populated with the logged-in user's claims.
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly string _connectionString;

        public ChatHub(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        private string CurrentUsername => Context.User?.FindFirst("Username")?.Value;

        // Every connection for a given account joins a group named after their
        // username. This lets us deliver a message only to the two people
        // involved in a conversation instead of broadcasting it to every
        // connected browser (customers and staff alike).
        public override async Task OnConnectedAsync()
        {
            var username = CurrentUsername;
            if (!string.IsNullOrEmpty(username))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, username);
            }

            await base.OnConnectedAsync();
        }

        // The sender is taken from the authenticated connection, not from the
        // client, so a customer can't spoof messages as another user.
        public async Task SendMessage(string receiver, string message)
        {
            string sender = CurrentUsername;

            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(receiver) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            DateTime timestamp = DateTime.Now;

            // 1. Save to SQL Server
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                string insertQuery = @"
                    INSERT INTO chat_messages (sender_username, receiver_username, message_text, timestamp)
                    VALUES (@Sender, @Receiver, @Message, @Timestamp)";

                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Sender", sender);
                    cmd.Parameters.AddWithValue("@Receiver", receiver);
                    cmd.Parameters.AddWithValue("@Message", message);
                    cmd.Parameters.AddWithValue("@Timestamp", timestamp);

                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            string time = timestamp.ToString("hh:mm tt");

            // 2. Deliver only to the sender's other tabs/devices and the receiver
            //    (not every connected client).
            await Clients.Group(sender).SendAsync("ReceiveMessage", sender, receiver, message, time);

            if (!string.Equals(sender, receiver, StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Group(receiver).SendAsync("ReceiveMessage", sender, receiver, message, time);
            }
        }
    }
}
