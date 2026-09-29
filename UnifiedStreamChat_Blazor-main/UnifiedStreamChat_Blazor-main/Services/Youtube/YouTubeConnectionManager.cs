using System.Collections.Concurrent;

namespace UnifiedStreamChat.Services;

public class YouTubeConnectionManager
{
    private readonly ConcurrentDictionary<string, YouTubeChatService>
        _connections = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public YouTubeConnectionManager(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public YouTubeChatService GetOrCreateConnection(
        string sessionId)
    {
        return _connections.GetOrAdd(
            sessionId,
            _ => new YouTubeChatService(
                _httpClientFactory,
                _configuration));
    }

    public bool TryGetConnection(
        string sessionId,
        out YouTubeChatService? youtubeChatService)
    {
        return _connections.TryGetValue(
            sessionId,
            out youtubeChatService);
    }

    public async Task RemoveConnectionAsync(
        string sessionId)
    {
        if (_connections.TryRemove(
                sessionId,
                out var youtubeChatService))
        {
            await youtubeChatService.StopChatAsync();

            youtubeChatService.Disconnect();
        }
    }
}