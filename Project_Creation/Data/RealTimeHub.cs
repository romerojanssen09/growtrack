using Microsoft.AspNetCore.SignalR;
using Project_Creation.Models.Entities;

namespace Project_Creation.Data
{
    public class RealTimeHub : Hub
    {
        private readonly IHubContext<RealTimeHub> _hubContext;

        public RealTimeHub(IHubContext<RealTimeHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task SendMessage(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveMessage", user, message);
        }

        public async Task SendNotification(string user, string notification)
        {
            await Clients.All.SendAsync("ReceiveNotification", user, notification);
        }

        public async Task SendPrivateNotification(string userId, string title, string message, string notificationId, string url = "", string type = "General")
        {
            await Clients.User(userId).SendAsync("ReceivePrivateNotification", title, message, notificationId, url, type);
        }

        public async Task UpdateNotificationCount(string userId, int count)
        {
            await Clients.User(userId).SendAsync("UpdateNotificationCount", count);
        }

        public async Task UpdateChatCount(string userId, int count)
        {
            await Clients.User(userId).SendAsync("UpdateChatCount", count);
        }

        public async Task SendChatMessage(string sender, string receiver, string message, string createdAt, string messageId)
        {
            await Clients.Users(receiver, sender).SendAsync("ReceiveChatMessage", sender, receiver, message, createdAt, messageId);
        }

        public async Task RemoveChat(string sender, string receiver, string messageId)
        {
            await Clients.Users(receiver, sender).SendAsync("RemoveChat", sender, receiver, messageId);
        }

        public async Task RemoveChatmate(string sender, string receiver)
        {
            await Clients.Users(receiver, sender).SendAsync("RemoveChatmate", sender, receiver);
        }

        public async Task SendProductRequest2(string sender, string receiver, string status, string chatJson, string messageId)
        {
            await Clients.Users(receiver, sender).SendAsync("SendProductRequest2", sender, receiver, status, chatJson, messageId);
        }

        // Add these methods to your hub
        public async Task SendLeadRequest(int senderId, int receiverId, string status, string jsonString, int messageId)
        {
            await Clients.Users(senderId.ToString(), receiverId.ToString())
                .SendAsync("ReceiveLeadRequest", senderId, receiverId, messageId, status, jsonString);
        }

        public async Task NotifyLeadRequest(int senderId, int receiverId, int messageId, string senderName, string leadName, int leadId, string createdAt, string status, string jsonString)
        {
            await Clients.Users(receiverId.ToString())
                .SendAsync("ReceiveLeadRequestNotification", senderId, receiverId, messageId, senderName, leadName, leadId, createdAt, status, jsonString);
        }

        public async Task SendAndRemoveMessage(string sender, string receiver, string messageId, string status, string chatJson)
        {
            await Clients.Users(receiver, sender).SendAsync("SendAndRemoveMessage", sender, receiver, messageId, status, chatJson);
        }

        public async Task EditMessage(string sender, string receiver, string messageId, string status, string chatJson, string oldMessageId)
        {
            await Clients.Users(receiver, sender)
                .SendAsync("EditMessage", sender, receiver, messageId, status, chatJson, oldMessageId);
        }

        public async Task SendProductRequest(string receiverId, string senderId, int messageId, int productId, string productName, int quantity, string customerName, string message, string createdAt)
        {
            await Clients.Users(receiverId, senderId).SendAsync("ReceiveProductRequest", messageId, productId, productName, quantity, customerName, message, createdAt);
        }

        public async Task ReceiveLeadRequest(string receiverId, string senderId, int messageId, string senderName, string leadName, int leadId, string createdAt, string status, string jsonString)
        {
            await Clients.Users(receiverId, senderId).SendAsync("ReceiveLeadRequest", senderId, receiverId, messageId, senderName, leadName, leadId, createdAt, status, jsonString);
        }

        // Allow staff members to join a group with their ID for targeted updates
        public async Task JoinStaffGroup(string staffId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"staff_{staffId}");
        }

        // Notify a specific staff member about access level changes
        public async Task NotifyAccessLevelChanged(string staffId, List<string> previous, List<string> added, List<string> removed)
        {
            await Clients.Group($"staff_{staffId}").SendAsync("AccessLevelChanged", staffId, previous, added, removed);
        }
    }
}
