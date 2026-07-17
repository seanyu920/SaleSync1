using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace SaleSync.Hubs
{
    public class ChatHub : Hub
    {
        private readonly string _connectionString;

        public ChatHub(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // Accepts Sender, Receiver, and the Message text
        public async Task SendMessage(string sender, string receiver, string message)
        {
            // 1. Save to your SQL Server (SSMS) database
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
                    cmd.Parameters.AddWithValue("@Timestamp", DateTime.Now);

                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 2. Broadcast the message (sending all 3 parameters: sender, receiver, message)
            await Clients.All.SendAsync("ReceiveMessage", sender, receiver, message);
        }
    }
}