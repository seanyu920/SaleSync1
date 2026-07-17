using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System;

namespace SaleSync.Controllers
{
    public class ChatController : Controller
    {
        private readonly string connectionString;

        public ChatController(IConfiguration configuration)
        {
            connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // GET: Chat (Dedicated page for Staff)
        public async Task<IActionResult> Index(string chatWith = "")
        {
            string currentUser = User.Identity?.Name ?? HttpContext.Session.GetString("Username");

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
                    string historyQuery = @"
                        SELECT sender_username, receiver_username, message_text, timestamp 
                        FROM chat_messages 
                        WHERE (sender_username = @UserA AND receiver_username = @UserB) 
                           OR (sender_username = @UserB AND receiver_username = @UserA)
                        ORDER BY timestamp ASC";

                    using (SqlCommand cmd = new SqlCommand(historyQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@UserA", currentUser);
                        cmd.Parameters.AddWithValue("@UserB", chatWith);
                        using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                        {
                            while (r.Read())
                            {
                                messages.Add(new
                                {
                                    Sender = r["sender_username"].ToString(),
                                    Receiver = r["receiver_username"].ToString(),
                                    Text = r["message_text"].ToString(),
                                    Time = Convert.ToDateTime(r["timestamp"]).ToString("hh:mm tt")
                                });
                            }
                        }
                    }
                }
            }

            ViewBag.Messages = messages;
            ViewBag.Contacts = contacts;

            return View();
        }

        // GET: Chat/GetChatHistory (API endpoint for the customer widget)
        [HttpGet]
        public async Task<IActionResult> GetChatHistory(string chatWith)
        {
            string currentUser = User.Identity?.Name ?? HttpContext.Session.GetString("Username");
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            var messages = new List<object>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string historyQuery = @"
                    SELECT sender_username, message_text, timestamp 
                    FROM chat_messages 
                    WHERE (sender_username = @UserA AND receiver_username = @UserB) 
                       OR (sender_username = @UserB AND receiver_username = @UserA)
                    ORDER BY timestamp ASC";

                using (SqlCommand cmd = new SqlCommand(historyQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@UserA", currentUser);
                    cmd.Parameters.AddWithValue("@UserB", chatWith);

                    await conn.OpenAsync();
                    using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                    {
                        while (r.Read())
                        {
                            messages.Add(new
                            {
                                sender = r["sender_username"].ToString(),
                                text = r["message_text"].ToString(),
                                time = Convert.ToDateTime(r["timestamp"]).ToString("hh:mm tt")
                            });
                        }
                    }
                }
            }

            return Json(messages);
        }
    }
}