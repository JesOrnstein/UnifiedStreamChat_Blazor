namespace UnifiedStreamChat.Models;

public class ChatMessage
{
    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsSubscriber { get; set; }
    public bool IsModerator { get; set; }
    public bool IsBroadcaster { get; set; }

    public List<ChatBadge> Badges { get; set; } = [];
}