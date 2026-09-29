using System.Collections.Concurrent;

namespace UnifiedStreamChat.Services;

public class KickConnectionManager
{
    private readonly ConcurrentDictionary<string, KickChatService> _connections = new();
    private readonly IServiceProvider _serviceProvider;

    public KickConnectionManager(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public KickChatService GetOrCreateConnection(string sessionId)
    {
        // Om det redan finns en anslutning för denna session, hämta den. 
        // Annars, be .NET (via ActivatorUtilities) att bygga en ny KickChatService 
        // och injicera IHttpClientFactory och IConfiguration automatiskt.
        return _connections.GetOrAdd(sessionId, _ =>
            ActivatorUtilities.CreateInstance<KickChatService>(_serviceProvider));
    }

    public KickChatService? GetConnection(string sessionId)
    {
        _connections.TryGetValue(sessionId, out var connection);
        return connection;
    }

    public void RemoveConnection(string sessionId)
    {
        if (_connections.TryRemove(sessionId, out var connection))
        {
            connection.Disconnect();
        }
    }
}