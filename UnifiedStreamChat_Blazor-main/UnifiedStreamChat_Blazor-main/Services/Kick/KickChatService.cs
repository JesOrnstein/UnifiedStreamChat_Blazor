using System.Text.Json;
using KickLib.Client;
using KickLib.Client.Interfaces;
using UnifiedStreamChat.Models;

namespace UnifiedStreamChat.Services;

public class KickChatService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    
    // Vi använder nu KickLib istället för PusherClient
    private IKickClient? _kickClient;

    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    public string ChannelSlug { get; private set; } = "";
    public int ChatroomId { get; private set; } // KickLib använder int istället för string

    public bool IsConnected => !string.IsNullOrWhiteSpace(AccessToken) && ChatroomId > 0;

    public event Action<ChatMessage>? MessageReceived;

    public KickChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public void SetTokens(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    public void SetChannelName(string channelName)
    {
        ChannelSlug = channelName.Trim().ToLower();
    }

    public async Task<bool> LoadCurrentUserAsync()
    {
        if (string.IsNullOrWhiteSpace(ChannelSlug))
        {
            Console.WriteLine("Inget Kick-kanalnamn har angivits.");
            return false;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var response = await httpClient.GetAsync($"https://kick.com/api/v2/channels/{ChannelSlug}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Kunde inte hitta kanalen '{ChannelSlug}' på Kick. Status: {response.StatusCode}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            
            // Vi hämtar ID:t som en siffra (int) eftersom KickLib kräver det
            ChatroomId = document.RootElement.GetProperty("chatroom").GetProperty("id").GetInt32();
            DisplayName = ChannelSlug;
            
            Console.WriteLine($"Kick Chatroom ID hittat: {ChatroomId} för kanalen {ChannelSlug}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fel vid hämtning av Kick Chatroom ID: {ex.Message}");
            return false;
        }
    }

    public async Task StartChatAsync()
    {
        if (ChatroomId == 0)
        {
            Console.WriteLine("Kan inte starta Kick-chatten eftersom ChatroomId saknas.");
            return;
        }

        if (_kickClient != null)
        {
            await StopChatAsync();
        }

        try
        {
            // 1. Skapa klienten
            _kickClient = new KickClient();

            // 2. Lyssna på det inbyggda Message-eventet
            _kickClient.OnMessage += (sender, e) =>
            {
                // LÄGG TILL DENNA RAD FÖR FELSÖKNING:
                Console.WriteLine($"[KICK-CHATT] {e.Data.Sender.Username} skrev: {e.Data.Content}");

                var chatMessage = new ChatMessage
                {
                    Platform = "Kick",
                    Username = e.Data.Sender.Username,
                    Message = e.Data.Content,
                    Timestamp = e.Data.CreatedAt.ToLocalTime() 
                };

                // Skicka till Blazor UI
                MessageReceived?.Invoke(chatMessage);
            };

            // 3. Anslut
            await _kickClient.ListenToChatRoomAsync(ChatroomId);
            await _kickClient.ConnectAsync();

            Console.WriteLine($"KickLib ansluten till chatt {ChatroomId}. Skriv ett meddelande i chatten nu!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ett fel uppstod när KickLib skulle startas: {ex.Message}");
        }
    }

    public async Task StopChatAsync()
    {
        if (_kickClient != null)
        {
            await _kickClient.DisconnectAsync();
            _kickClient = null;
            Console.WriteLine("Kick live chat stopped.");
        }
    }

    public void Disconnect()
    {
        _ = StopChatAsync();
        
        AccessToken = "";
        RefreshToken = "";
        DisplayName = "";
        ChannelSlug = "";
        ChatroomId = 0;
    }
}
