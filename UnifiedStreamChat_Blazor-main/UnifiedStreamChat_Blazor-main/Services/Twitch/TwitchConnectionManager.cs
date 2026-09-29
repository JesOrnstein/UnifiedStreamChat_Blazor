using System.Collections.Concurrent;

namespace UnifiedStreamChat.Services;

public class TwitchConnectionManager
{
    private readonly ConcurrentDictionary<string, TwitchChatService> _connections = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public TwitchConnectionManager(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public TwitchChatService GetOrCreateConnection(string sessionId)
    {
        return _connections.GetOrAdd(
            sessionId,
            _ => new TwitchChatService(
                _httpClientFactory,
                _configuration));
    }

    public bool TryGetConnection(
        string sessionId,
        out TwitchChatService? twitchChatService)
    {
        return _connections.TryGetValue(
            sessionId,
            out twitchChatService);
    }

    public async Task RemoveConnectionAsync(string sessionId)
    {
        if (_connections.TryRemove(
                sessionId,
                out var twitchChatService))
        {
            await twitchChatService.StopChatAsync();
            twitchChatService.Disconnect();
        }
    }
}