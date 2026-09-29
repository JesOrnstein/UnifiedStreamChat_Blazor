using System.Security.Cryptography;
using UnifiedStreamChat.Models;

namespace UnifiedStreamChat.Services;

public static class YouTubeAuthEndpoints
{
    public static IEndpointRouteBuilder MapYouTubeAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/youtube/login", (
            HttpContext context,
            IConfiguration configuration) =>
        {
            var clientId = configuration["YouTube:ClientId"];
            var redirectUri = configuration["YouTube:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Problem("YouTube configuration is missing.");
            }

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                context.Session.SetString("SessionId", sessionId);
            }

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Session.SetString("YouTubeOAuthState", state);

            var scope = "https://www.googleapis.com/auth/youtube.readonly";

            var youtubeAuthUrl =
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(scope)}" +
                "&access_type=offline" +
                "&include_granted_scopes=true" +
                "&prompt=consent" +
                $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(youtubeAuthUrl);
        });

        app.MapGet("/auth/youtube/callback", async (
            HttpContext context,
            string? code,
            string? state,
            string? error,
            string? error_description,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            YouTubeConnectionManager connectionManager) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Results.Content($"YouTube authorization failed: {error_description ?? error}");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest("No authorization code was received from YouTube.");
            }

            var expectedState = context.Session.GetString("YouTubeOAuthState");

            if (string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid YouTube OAuth state.");
            }

            context.Session.Remove("YouTubeOAuthState");

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest("User session could not be found.");
            }

            var clientId = configuration["YouTube:ClientId"];
            var clientSecret = configuration["YouTube:ClientSecret"];
            var redirectUri = configuration["YouTube:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Problem("YouTube configuration is missing.");
            }

            var httpClient = httpClientFactory.CreateClient();
            var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            });

            var tokenResponseMessage = await httpClient.PostAsync("https://oauth2.googleapis.com/token", tokenRequest);

            if (!tokenResponseMessage.IsSuccessStatusCode)
            {
                var errorResponse = await tokenResponseMessage.Content.ReadAsStringAsync();
                return Results.Problem($"Failed to get YouTube access token: {errorResponse}");
            }

            var tokenResponse = await tokenResponseMessage.Content.ReadFromJsonAsync<YouTubeTokenResponse>();

            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return Results.Problem("YouTube did not return an access token.");
            }

            var youtubeChatService = connectionManager.GetOrCreateConnection(sessionId);
            youtubeChatService.SetTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken);

            var broadcastLoaded = await youtubeChatService.LoadActiveBroadcastAsync();

            if (broadcastLoaded)
            {
                await youtubeChatService.StartChatAsync();
                Console.WriteLine("YouTube connected and live chat started.");
            }
            else
            {
                Console.WriteLine("YouTube connected, but no active livestream was found.");
            }

            return Results.Redirect("/chat");
        });

        return app;
    }
}