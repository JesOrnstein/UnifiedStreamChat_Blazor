using System.Security.Cryptography;
using UnifiedStreamChat.Models;

namespace UnifiedStreamChat.Services;

public static class TwitchAuthEndpoints
{
    public static IEndpointRouteBuilder MapTwitchAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/twitch/login", (
            HttpContext context,
            IConfiguration configuration) =>
        {
            var clientId = configuration["Twitch:ClientId"];
            var redirectUri = configuration["Twitch:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Problem("Twitch configuration is missing.");
            }

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                context.Session.SetString("SessionId", sessionId);
            }

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Session.SetString("TwitchOAuthState", state);

            var twitchAuthUrl =
                "https://id.twitch.tv/oauth2/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                "&scope=user%3Aread%3Achat" +
                $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(twitchAuthUrl);
        });

        app.MapGet("/auth/twitch/callback", async (
            HttpContext context,
            string? code,
            string? state,
            string? error,
            string? error_description,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            TwitchConnectionManager connectionManager) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Results.Content($"Twitch authorization failed: {error_description ?? error}");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest("No authorization code was received from Twitch.");
            }

            var expectedState = context.Session.GetString("TwitchOAuthState");

            if (string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid Twitch OAuth state.");
            }

            context.Session.Remove("TwitchOAuthState");

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest("User session could not be found.");
            }

            var clientId = configuration["Twitch:ClientId"];
            var clientSecret = configuration["Twitch:ClientSecret"];
            var redirectUri = configuration["Twitch:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Problem("Twitch configuration is missing.");
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

            var tokenResponseMessage = await httpClient.PostAsync("https://id.twitch.tv/oauth2/token", tokenRequest);

            if (!tokenResponseMessage.IsSuccessStatusCode)
            {
                var errorResponse = await tokenResponseMessage.Content.ReadAsStringAsync();
                return Results.Problem($"Failed to get Twitch access token: {errorResponse}");
            }

            var tokenResponse = await tokenResponseMessage.Content.ReadFromJsonAsync<TwitchTokenResponse>();

            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return Results.Problem("Twitch did not return an access token.");
            }

            var twitchChatService = connectionManager.GetOrCreateConnection(sessionId);
            twitchChatService.SetTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken);

            var userLoaded = await twitchChatService.LoadCurrentUserAsync();
            if (!userLoaded)
            {
                return Results.Problem("Twitch connected, but user information could not be loaded.");
            }

            await twitchChatService.StartChatAsync();
            Console.WriteLine($"Twitch connected: {twitchChatService.DisplayName}");

            return Results.Redirect("/chat");
        });

        return app;
    }
}