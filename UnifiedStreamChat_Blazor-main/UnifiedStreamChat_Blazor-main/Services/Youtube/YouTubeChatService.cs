using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using Grpc.Net.Client;
using UnifiedStreamChat.Models;
using Youtube.Api.V3; // Denna namespace skapades automatiskt från vår .proto-fil!

namespace UnifiedStreamChat.Services;

public class YouTubeChatService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly HashSet<string> _processedMessageIds = new();

    private CancellationTokenSource? _chatCancellationTokenSource;
    private Task? _chatTask;

    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    public string LiveBroadcastId { get; private set; } = "";
    public string LiveChatId { get; private set; } = "";

    public bool IsConnected =>
        !string.IsNullOrWhiteSpace(AccessToken);

    public bool HasActiveLiveChat =>
        !string.IsNullOrWhiteSpace(LiveChatId);

    public event Action<ChatMessage>? MessageReceived;

    public YouTubeChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public void SetTokens(
        string accessToken,
        string refreshToken)
    {
        AccessToken = accessToken;

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            RefreshToken = refreshToken;
        }
    }

    public async Task<bool> LoadActiveBroadcastAsync()
    {
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            return false;
        }

        // Vi använder fortfarande REST API:et för att HITTTA sändningen (med våra korrigerade parametrar)
        var url = "https://www.googleapis.com/youtube/v3/liveBroadcasts?part=id,snippet,status&mine=true&maxResults=50";
        var response = await SendAuthenticatedGetAsync(url);

        if (response is null || !response.IsSuccessStatusCode)
        {
            if (response is not null)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to load YouTube broadcast: {error}");
            }
            return false;
        }

        var broadcastResponse = await response.Content.ReadFromJsonAsync<YouTubeLiveBroadcastResponse>();

        var broadcast = broadcastResponse?.Items.FirstOrDefault(b => 
            b.Status.LifeCycleStatus == "live" || 
            b.Status.LifeCycleStatus == "testing");

        if (broadcast is null)
        {
            LiveBroadcastId = "";
            LiveChatId = "";
            Console.WriteLine("No active YouTube livestream was found.");
            return false;
        }

        LiveBroadcastId = broadcast.Id;
        LiveChatId = broadcast.Snippet.LiveChatId;

        if (string.IsNullOrWhiteSpace(LiveChatId))
        {
            Console.WriteLine("The active YouTube livestream does not have a live chat.");
            return false;
        }

        Console.WriteLine("YouTube active live chat found.");
        return true;
    }

    public Task StartChatAsync()
    {
        if (string.IsNullOrWhiteSpace(LiveChatId))
        {
            throw new InvalidOperationException("No active YouTube live chat is available.");
        }

        if (_chatTask is not null && !_chatTask.IsCompleted)
        {
            return Task.CompletedTask;
        }

        _chatCancellationTokenSource = new CancellationTokenSource();

        // Vi byter ut PollChatAsync mot StreamChatAsync!
        _chatTask = StreamChatAsync(_chatCancellationTokenSource.Token);

        Console.WriteLine("YouTube live chat gRPC stream started.");

        return Task.CompletedTask;
    }

    private async Task StreamChatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1. Skapa en gRPC-kanal mot YouTube
                using var channel = GrpcChannel.ForAddress("https://youtube.googleapis.com");
                
                // 2. Skapa den automatiskt genererade klienten
                var client = new V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient(channel);

                // 3. Bifoga vår OAuth-token i headern
                var metadata = new Metadata
                {
                    { "authorization", $"Bearer {AccessToken}" }
                };

                // 4. Konfigurera förfrågan
                var request = new LiveChatMessageListRequest
                {
                    LiveChatId = LiveChatId
                };
                request.Part.Add("snippet");
                request.Part.Add("authorDetails");

                // 5. Öppna den strömmande anslutningen!
                using var call = client.StreamList(request, headers: metadata, cancellationToken: cancellationToken);

                // 6. Läs meddelanden i realtid så fort YouTube skickar dem
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var response = call.ResponseStream.Current;

                    foreach (var item in response.Items)
                    {
                        // 1. Kolla om meddelandet saknar text (behåll denna från tidigare)
                        if (item.Snippet is null || 
                            !item.Snippet.HasDisplayContent || 
                            string.IsNullOrWhiteSpace(item.Snippet.DisplayMessage))
                        {
                            continue;
                        }

                        // 2. NY KOD: Kolla om vi redan har hanterat detta meddelande!
                        // Add() returnerar 'false' om ID:t redan fanns i listan.
                        if (!_processedMessageIds.Add(item.Id))
                        {
                            continue; 
                        }

                        // ... resten av din kod nedanför (timestamp, ChatMessage, Invoke etc)
                        var timestamp = DateTime.Now;
                        if (DateTime.TryParse(item.Snippet.PublishedAt, out var publishedAt))
                        {
                            timestamp = publishedAt.ToLocalTime();
                        }

                        var chatMessage = new ChatMessage
                        {
                            Platform = "YouTube",
                            Username = item.AuthorDetails?.DisplayName ?? "Unknown",
                            Message = item.Snippet.DisplayMessage,
                            Timestamp = timestamp,

                            IsSubscriber = item.AuthorDetails?.IsChatSponsor ?? false,
                            IsModerator = item.AuthorDetails?.IsChatModerator ?? false,
                            IsBroadcaster = item.AuthorDetails?.IsChatOwner ?? false
                        };

                        MessageReceived?.Invoke(chatMessage);
                    }
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                Console.WriteLine("YouTube gRPC token expired. Attempting refresh...");
                var refreshed = await RefreshAccessTokenAsync();
                if (!refreshed)
                {
                    break;
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                // Strömmen avbröts medvetet
                break;
            }
            catch (OperationCanceledException)
            {
                // Appen stängs av eller kopplas från
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"YouTube gRPC stream error: {ex.Message}");
                // Vänta lite innan vi försöker återansluta vid ett nätverksfel
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage?> SendAuthenticatedGetAsync(string url)
    {
        var response = await SendGetRequestAsync(url);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        Console.WriteLine("YouTube access token expired. Attempting refresh.");
        response.Dispose();

        var refreshed = await RefreshAccessTokenAsync();
        if (!refreshed)
        {
            return null;
        }

        return await SendGetRequestAsync(url);
    }

    private async Task<HttpResponseMessage> SendGetRequestAsync(string url)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return await httpClient.GetAsync(url);
    }

    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(RefreshToken))
        {
            Console.WriteLine("No YouTube refresh token is available.");
            return false;
        }

        var clientId = _configuration["YouTube:ClientId"];
        var clientSecret = _configuration["YouTube:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Console.WriteLine("YouTube configuration is missing.");
            return false;
        }

        var httpClient = _httpClientFactory.CreateClient();
        var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = RefreshToken,
            ["grant_type"] = "refresh_token"
        });

        var response = await httpClient.PostAsync("https://oauth2.googleapis.com/token", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Failed to refresh YouTube access token: {error}");
            return false;
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<YouTubeRefreshTokenResponse>();

        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            return false;
        }

        AccessToken = tokenResponse.AccessToken;
        Console.WriteLine("YouTube access token refreshed.");

        return true;
    }

    public async Task StopChatAsync()
    {
        if (_chatCancellationTokenSource is null) return;

        await _chatCancellationTokenSource.CancelAsync();

        if (_chatTask is not null)
        {
            try { await _chatTask; }
            catch (OperationCanceledException) { }
        }

        _chatCancellationTokenSource.Dispose();
        _chatCancellationTokenSource = null;
        _chatTask = null;

        Console.WriteLine("YouTube live chat stopped.");
    }

    public void Disconnect()
    {
        AccessToken = "";
        RefreshToken = "";
        LiveBroadcastId = "";
        LiveChatId = "";
    }
}

// ---------------------------------------------------------
// YouTube Broadcast Models (REST API)
// ---------------------------------------------------------
// Vi sparar dessa eftersom vi fortfarande använder REST API:et för att hitta sändningen

public class YouTubeLiveBroadcastResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeLiveBroadcast> Items { get; set; } = [];
}

public class YouTubeLiveBroadcast
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("snippet")]
    public YouTubeLiveBroadcastSnippet Snippet { get; set; } = new();

    [JsonPropertyName("status")]
    public YouTubeLiveBroadcastStatus Status { get; set; } = new();
}

public class YouTubeLiveBroadcastSnippet
{
    [JsonPropertyName("liveChatId")]
    public string LiveChatId { get; set; } = "";
}

public class YouTubeLiveBroadcastStatus
{
    [JsonPropertyName("lifeCycleStatus")]
    public string LifeCycleStatus { get; set; } = "";
}

public class YouTubeRefreshTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";
}