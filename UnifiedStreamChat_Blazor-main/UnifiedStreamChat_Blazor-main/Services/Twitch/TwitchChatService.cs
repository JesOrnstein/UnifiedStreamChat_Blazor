using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedStreamChat.Models;

namespace UnifiedStreamChat.Services;

public class TwitchChatService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;

    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    // Stores Twitch badge definitions while this connection is active.
    private readonly Dictionary<string, ChatBadge> _badgeCache = new();

    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    public string UserId { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    public bool IsConnected =>
        !string.IsNullOrWhiteSpace(AccessToken);

    public event Action<ChatMessage>? MessageReceived;

    public TwitchChatService(
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
        RefreshToken = refreshToken;
    }

    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(RefreshToken))
        {
            Console.WriteLine(
                "No Twitch refresh token is available.");

            return false;
        }

        var clientId =
            _configuration["Twitch:ClientId"];

        var clientSecret =
            _configuration["Twitch:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            Console.WriteLine(
                "Twitch configuration is missing.");

            return false;
        }

        var httpClient =
            _httpClientFactory.CreateClient();

        var refreshRequest =
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = RefreshToken
                });

        var response =
            await httpClient.PostAsync(
                "https://id.twitch.tv/oauth2/token",
                refreshRequest);

        if (!response.IsSuccessStatusCode)
        {
            var errorResponse =
                await response.Content.ReadAsStringAsync();

            Console.WriteLine(
                $"Failed to refresh Twitch access token: {errorResponse}");

            return false;
        }

        var tokenResponse =
            await response.Content
                .ReadFromJsonAsync<TwitchRefreshTokenResponse>();

        if (tokenResponse is null ||
            string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            Console.WriteLine(
                "Twitch did not return a new access token.");

            return false;
        }

        AccessToken = tokenResponse.AccessToken;

        if (!string.IsNullOrWhiteSpace(
                tokenResponse.RefreshToken))
        {
            RefreshToken =
                tokenResponse.RefreshToken;
        }

        Console.WriteLine(
            $"Twitch access token refreshed for {DisplayName}.");

        return true;
    }

    public async Task<bool> LoadCurrentUserAsync()
    {
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            return false;
        }

        var response =
            await SendAuthenticatedGetAsync(
                "https://api.twitch.tv/helix/users");

        if (response is null ||
            !response.IsSuccessStatusCode)
        {
            response?.Dispose();
            return false;
        }

        var userResponse =
            await response.Content
                .ReadFromJsonAsync<TwitchUserResponse>();

        response.Dispose();

        var user =
            userResponse?.Data.FirstOrDefault();

        if (user is null)
        {
            return false;
        }

        UserId = user.Id;
        UserName = user.Login;
        DisplayName = user.DisplayName;

        return true;
    }

    // ---------------------------------------------------------
    // Twitch Badges
    // ---------------------------------------------------------

    public async Task LoadBadgesAsync()
    {
        if (string.IsNullOrWhiteSpace(AccessToken) ||
            string.IsNullOrWhiteSpace(UserId))
        {
            return;
        }

        _badgeCache.Clear();

        // Load Twitch-wide badges first.
        await LoadBadgeEndpointAsync(
            "https://api.twitch.tv/helix/chat/badges/global");

        // Load channel-specific badges afterwards.
        // These can override a global badge with the same key.
        await LoadBadgeEndpointAsync(
            "https://api.twitch.tv/helix/chat/badges" +
            $"?broadcaster_id={Uri.EscapeDataString(UserId)}");

        Console.WriteLine(
            $"Loaded {_badgeCache.Count} Twitch badge versions for {DisplayName}.");
    }

    private async Task LoadBadgeEndpointAsync(
        string url)
    {
        var response =
            await SendAuthenticatedGetAsync(url);

        if (response is null)
        {
            Console.WriteLine(
                $"Could not load Twitch badges from: {url}");

            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error =
                await response.Content.ReadAsStringAsync();

            Console.WriteLine(
                $"Failed to load Twitch badges: {error}");

            response.Dispose();

            return;
        }

        var badgeResponse =
            await response.Content
                .ReadFromJsonAsync<TwitchBadgeResponse>();

        response.Dispose();

        if (badgeResponse is null)
        {
            return;
        }

        foreach (var badgeSet in badgeResponse.Data)
        {
            foreach (var version in badgeSet.Versions)
            {
                var key =
                    CreateBadgeKey(
                        badgeSet.SetId,
                        version.Id);

                _badgeCache[key] =
                    new ChatBadge
                    {
                        SetId = badgeSet.SetId,
                        VersionId = version.Id,

                        // 2x gives us a sharper source image.
                        // CSS can still display it at 18x18.
                        ImageUrl = version.ImageUrl2X,
                        Title = version.Title
                    };
            }
        }
    }

    private static string CreateBadgeKey(
        string setId,
        string versionId)
    {
        return $"{setId}:{versionId}";
    }

    private List<ChatBadge> GetMessageBadges(
        JsonElement eventData)
    {
        var messageBadges =
            new List<ChatBadge>();

        if (!eventData.TryGetProperty(
                "badges",
                out var badgesElement))
        {
            return messageBadges;
        }

        if (badgesElement.ValueKind !=
            JsonValueKind.Array)
        {
            return messageBadges;
        }

        foreach (var badgeElement in badgesElement.EnumerateArray())
        {
            if (!badgeElement.TryGetProperty(
                    "set_id",
                    out var setIdElement) ||
                !badgeElement.TryGetProperty(
                    "id",
                    out var idElement))
            {
                continue;
            }

            var setId =
                setIdElement.GetString() ?? "";

            var versionId =
                idElement.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(setId) ||
                string.IsNullOrWhiteSpace(versionId))
            {
                continue;
            }

            var key =
                CreateBadgeKey(
                    setId,
                    versionId);

            if (_badgeCache.TryGetValue(
                    key,
                    out var badge))
            {
                messageBadges.Add(
                    new ChatBadge
                    {
                        SetId = badge.SetId,
                        VersionId = badge.VersionId,
                        ImageUrl = badge.ImageUrl,
                        Title = badge.Title
                    });
            }
            else
            {
                // Keep the badge information even if an image
                // was not found in the current cache.
                messageBadges.Add(
                    new ChatBadge
                    {
                        SetId = setId,
                        VersionId = versionId,
                        ImageUrl = "",
                        Title = setId
                    });
            }
        }

        return messageBadges;
    }

    private async Task<HttpResponseMessage?>
        SendAuthenticatedGetAsync(string url)
    {
        var response =
            await SendGetRequestAsync(url);

        if (response.StatusCode !=
            HttpStatusCode.Unauthorized)
        {
            return response;
        }

        Console.WriteLine(
            "Twitch access token expired. Attempting refresh.");

        response.Dispose();

        var refreshed =
            await RefreshAccessTokenAsync();

        if (!refreshed)
        {
            return null;
        }

        return await SendGetRequestAsync(url);
    }

    private async Task<HttpResponseMessage>
        SendGetRequestAsync(string url)
    {
        var clientId =
            _configuration["Twitch:ClientId"];

        var httpClient =
            _httpClientFactory.CreateClient();

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AccessToken);

        httpClient.DefaultRequestHeaders.Add(
            "Client-Id",
            clientId);

        return await httpClient.GetAsync(url);
    }

    public async Task StartChatAsync()
    {
        if (string.IsNullOrWhiteSpace(AccessToken) ||
            string.IsNullOrWhiteSpace(UserId))
        {
            throw new InvalidOperationException(
                "Twitch user is not authenticated.");
        }

        // Load the badge definitions before receiving chat messages.
        await LoadBadgesAsync();

        await StopSocketOnlyAsync();

        _cancellationTokenSource =
            new CancellationTokenSource();

        var newSocket =
            new ClientWebSocket();

        await newSocket.ConnectAsync(
            new Uri("wss://eventsub.wss.twitch.tv/ws"),
            _cancellationTokenSource.Token);

        _webSocket = newSocket;

        _ = ReceiveMessagesAsync(
            newSocket,
            _cancellationTokenSource.Token,
            createSubscriptionOnWelcome: true);
    }

    private async Task ReceiveMessagesAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken,
        bool createSubscriptionOnWelcome)
    {
        try
        {
            while (
                socket.State == WebSocketState.Open &&
                !cancellationToken.IsCancellationRequested)
            {
                var json =
                    await ReceiveCompleteMessageAsync(
                        socket,
                        cancellationToken);

                if (json is null)
                {
                    break;
                }

                await HandleWebSocketMessageAsync(
                    json,
                    socket,
                    createSubscriptionOnWelcome);

                createSubscriptionOnWelcome = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disconnecting.
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine(
                $"Twitch WebSocket error: {ex.Message}");

            if (!cancellationToken.IsCancellationRequested &&
                ReferenceEquals(socket, _webSocket))
            {
                await ReconnectAfterFailureAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Unexpected Twitch WebSocket error: {ex.Message}");
        }
    }

    private static async Task<string?>
        ReceiveCompleteMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        using var memoryStream =
            new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result =
                await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                return null;
            }

            memoryStream.Write(
                buffer,
                0,
                result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(
            memoryStream.ToArray());
    }

    private async Task HandleWebSocketMessageAsync(
        string json,
        ClientWebSocket socket,
        bool createSubscriptionOnWelcome)
    {
        using var document =
            JsonDocument.Parse(json);

        var root =
            document.RootElement;

        var messageType =
            root.GetProperty("metadata")
                .GetProperty("message_type")
                .GetString();

        if (messageType == "session_welcome")
        {
            var sessionId =
                root.GetProperty("payload")
                    .GetProperty("session")
                    .GetProperty("id")
                    .GetString();

            if (createSubscriptionOnWelcome &&
                !string.IsNullOrWhiteSpace(sessionId))
            {
                await CreateChatSubscriptionAsync(
                    sessionId);
            }

            return;
        }

        if (messageType == "session_keepalive")
        {
            return;
        }

        if (messageType == "session_reconnect")
        {
            var reconnectUrl =
                root.GetProperty("payload")
                    .GetProperty("session")
                    .GetProperty("reconnect_url")
                    .GetString();

            if (!string.IsNullOrWhiteSpace(reconnectUrl))
            {
                await ReconnectUsingTwitchUrlAsync(
                    socket,
                    reconnectUrl);
            }

            return;
        }

        if (messageType == "revocation")
        {
            var status =
                root.GetProperty("payload")
                    .GetProperty("subscription")
                    .GetProperty("status")
                    .GetString();

            Console.WriteLine(
                $"Twitch subscription revoked: {status}");

            return;
        }

        if (messageType != "notification")
        {
            return;
        }

        var subscriptionType =
            root.GetProperty("metadata")
                .GetProperty("subscription_type")
                .GetString();

        if (subscriptionType !=
            "channel.chat.message")
        {
            return;
        }

        var eventData =
            root.GetProperty("payload")
                .GetProperty("event");

        var username =
            eventData
                .GetProperty("chatter_user_name")
                .GetString()
            ?? "Unknown";

        var message =
            eventData
                .GetProperty("message")
                .GetProperty("text")
                .GetString()
            ?? "";

        var chatterUserId =
            eventData.TryGetProperty(
                "chatter_user_id",
                out var chatterUserIdElement)
                ? chatterUserIdElement.GetString() ?? ""
                : "";

        var broadcasterUserId =
            eventData.TryGetProperty(
                "broadcaster_user_id",
                out var broadcasterUserIdElement)
                ? broadcasterUserIdElement.GetString() ?? ""
                : "";

        var isSubscriber =
            HasBadge(eventData, "subscriber") ||
            HasBadge(eventData, "founder");

        var isModerator =
            HasBadge(eventData, "moderator");

        var isBroadcaster =
            HasBadge(eventData, "broadcaster") ||
            (
                !string.IsNullOrWhiteSpace(chatterUserId) &&
                !string.IsNullOrWhiteSpace(broadcasterUserId) &&
                chatterUserId == broadcasterUserId
            );

        var badges =
            GetMessageBadges(eventData);

        var chatMessage =
            new ChatMessage
            {
                Platform = "Twitch",
                Username = username,
                Message = message,
                Timestamp = DateTime.Now,

                IsSubscriber = isSubscriber,
                IsModerator = isModerator,
                IsBroadcaster = isBroadcaster,

                Badges = badges
            };

        MessageReceived?.Invoke(
            chatMessage);
    }

    private static bool HasBadge(
        JsonElement eventData,
        string badgeSetId)
    {
        if (!eventData.TryGetProperty(
                "badges",
                out var badgesElement))
        {
            return false;
        }

        if (badgesElement.ValueKind !=
            JsonValueKind.Array)
        {
            return false;
        }

        foreach (var badge in badgesElement.EnumerateArray())
        {
            if (!badge.TryGetProperty(
                    "set_id",
                    out var setIdElement))
            {
                continue;
            }

            var setId =
                setIdElement.GetString();

            if (string.Equals(
                    setId,
                    badgeSetId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ReconnectUsingTwitchUrlAsync(
        ClientWebSocket oldSocket,
        string reconnectUrl)
    {
        await _reconnectLock.WaitAsync();

        try
        {
            if (!ReferenceEquals(
                    oldSocket,
                    _webSocket))
            {
                return;
            }

            if (_cancellationTokenSource is null)
            {
                return;
            }

            Console.WriteLine(
                "Twitch requested WebSocket reconnect.");

            var newSocket =
                new ClientWebSocket();

            await newSocket.ConnectAsync(
                new Uri(reconnectUrl),
                _cancellationTokenSource.Token);

            var welcomeReceived =
                await WaitForWelcomeAsync(
                    newSocket,
                    _cancellationTokenSource.Token);

            if (!welcomeReceived)
            {
                newSocket.Dispose();
                return;
            }

            _webSocket = newSocket;

            _ = ReceiveMessagesAsync(
                newSocket,
                _cancellationTokenSource.Token,
                createSubscriptionOnWelcome: false);

            if (oldSocket.State ==
                WebSocketState.Open)
            {
                await oldSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Reconnected",
                    CancellationToken.None);
            }

            oldSocket.Dispose();

            Console.WriteLine(
                "Twitch WebSocket reconnected successfully.");
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private static async Task<bool> WaitForWelcomeAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (
            socket.State == WebSocketState.Open &&
            !cancellationToken.IsCancellationRequested)
        {
            var json =
                await ReceiveCompleteMessageAsync(
                    socket,
                    cancellationToken);

            if (json is null)
            {
                return false;
            }

            using var document =
                JsonDocument.Parse(json);

            var messageType =
                document.RootElement
                    .GetProperty("metadata")
                    .GetProperty("message_type")
                    .GetString();

            if (messageType ==
                "session_welcome")
            {
                return true;
            }
        }

        return false;
    }

    private async Task ReconnectAfterFailureAsync()
    {
        await _reconnectLock.WaitAsync();

        try
        {
            if (_cancellationTokenSource is null ||
                _cancellationTokenSource
                    .IsCancellationRequested)
            {
                return;
            }

            Console.WriteLine(
                "Attempting Twitch WebSocket reconnect.");

            await Task.Delay(
                TimeSpan.FromSeconds(2),
                _cancellationTokenSource.Token);

            var newSocket =
                new ClientWebSocket();

            await newSocket.ConnectAsync(
                new Uri("wss://eventsub.wss.twitch.tv/ws"),
                _cancellationTokenSource.Token);

            _webSocket?.Dispose();

            _webSocket = newSocket;

            _ = ReceiveMessagesAsync(
                newSocket,
                _cancellationTokenSource.Token,
                createSubscriptionOnWelcome: true);

            Console.WriteLine(
                "Twitch WebSocket connection restored.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Twitch reconnect failed: {ex.Message}");
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task CreateChatSubscriptionAsync(
        string sessionId)
    {
        var response =
            await SendChatSubscriptionRequestAsync(
                sessionId);

        if (response.StatusCode ==
            HttpStatusCode.Unauthorized)
        {
            Console.WriteLine(
                "Twitch access token expired while creating chat subscription.");

            response.Dispose();

            var refreshed =
                await RefreshAccessTokenAsync();

            if (!refreshed)
            {
                return;
            }

            response =
                await SendChatSubscriptionRequestAsync(
                    sessionId);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error =
                await response.Content
                    .ReadAsStringAsync();

            Console.WriteLine(
                $"Failed to create Twitch chat subscription: {error}");

            response.Dispose();

            return;
        }

        Console.WriteLine(
            "Twitch chat subscription created successfully.");

        response.Dispose();
    }

    private async Task<HttpResponseMessage>
        SendChatSubscriptionRequestAsync(
            string sessionId)
    {
        var clientId =
            _configuration["Twitch:ClientId"];

        var httpClient =
            _httpClientFactory.CreateClient();

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AccessToken);

        httpClient.DefaultRequestHeaders.Add(
            "Client-Id",
            clientId);

        var request =
            new
            {
                type = "channel.chat.message",
                version = "1",
                condition = new
                {
                    broadcaster_user_id =
                        UserId,

                    user_id =
                        UserId
                },
                transport = new
                {
                    method = "websocket",
                    session_id = sessionId
                }
            };

        return await httpClient.PostAsJsonAsync(
            "https://api.twitch.tv/helix/eventsub/subscriptions",
            request);
    }

    public async Task StopChatAsync()
    {
        _cancellationTokenSource?.Cancel();

        await StopSocketOnlyAsync();

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task StopSocketOnlyAsync()
    {
        var socket = _webSocket;

        _webSocket = null;

        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State ==
                WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Chat stopped",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Socket may already be gone.
        }
        finally
        {
            socket.Dispose();
        }
    }

    public void Disconnect()
    {
        AccessToken = "";
        RefreshToken = "";

        UserId = "";
        UserName = "";
        DisplayName = "";

        // Remove the badge cache when this Twitch connection ends.
        _badgeCache.Clear();
    }
}


// ---------------------------------------------------------
// Twitch User Models
// ---------------------------------------------------------

public class TwitchUserResponse
{
    [JsonPropertyName("data")]
    public List<TwitchUser> Data { get; set; } = [];
}

public class TwitchUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
}


// ---------------------------------------------------------
// Twitch Token Models
// ---------------------------------------------------------

public class TwitchRefreshTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string[] Scope { get; set; } = [];
}


// ---------------------------------------------------------
// Twitch Badge API Models
// ---------------------------------------------------------

public class TwitchBadgeResponse
{
    [JsonPropertyName("data")]
    public List<TwitchBadgeSet> Data { get; set; } = [];
}

public class TwitchBadgeSet
{
    [JsonPropertyName("set_id")]
    public string SetId { get; set; } = "";

    [JsonPropertyName("versions")]
    public List<TwitchBadgeVersion> Versions { get; set; } = [];
}

public class TwitchBadgeVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("image_url_1x")]
    public string ImageUrl1X { get; set; } = "";

    [JsonPropertyName("image_url_2x")]
    public string ImageUrl2X { get; set; } = "";

    [JsonPropertyName("image_url_4x")]
    public string ImageUrl4X { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}