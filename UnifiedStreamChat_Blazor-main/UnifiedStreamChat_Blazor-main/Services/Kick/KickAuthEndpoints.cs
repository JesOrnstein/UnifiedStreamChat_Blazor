using System.Security.Cryptography;
using System.Text;
using UnifiedStreamChat.Models;

namespace UnifiedStreamChat.Services;

public static class KickAuthEndpoints
{
    public static IEndpointRouteBuilder MapKickAuth(this IEndpointRouteBuilder app)
    {
        // 1. VI LÄGGER TILL 'channel' SOM PARAMETER
        app.MapGet("/auth/kick/login", (HttpContext context, IConfiguration configuration, string? channel) =>
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                return Results.BadRequest("Du måste skriva in ett Kick-namn för att ansluta.");
            }

            var clientId = configuration["Kick:ClientId"];
            var redirectUri = configuration["Kick:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Problem("Kick configuration is missing.");
            }

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                context.Session.SetString("SessionId", sessionId);
            }

            // 2. VI SPARAR NAMNET I SESSIONEN MEDAN ANVÄNDAREN LOGGAR IN
            context.Session.SetString("KickChannelName", channel.Trim().ToLower());

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var codeVerifier = CreatePkceCodeVerifier();
            var codeChallenge = CreatePkceCodeChallenge(codeVerifier);

            context.Session.SetString("KickOAuthState", state);
            context.Session.SetString("KickCodeVerifier", codeVerifier);

            var scope = "user:read events:subscribe";
            var kickAuthUrl = "https://id.kick.com/oauth/authorize" +
                              $"?client_id={Uri.EscapeDataString(clientId)}" +
                              $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                              "&response_type=code" +
                              $"&scope={Uri.EscapeDataString(scope)}" +
                              $"&state={Uri.EscapeDataString(state)}" +
                              $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                              "&code_challenge_method=S256";

            return Results.Redirect(kickAuthUrl);
        });

        app.MapGet("/auth/kick/callback", async (
            HttpContext context, string? code, string? state, string? error, string? error_description,
            IConfiguration configuration, IHttpClientFactory httpClientFactory, KickConnectionManager connectionManager) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return Results.Content($"Kick auth failed: {error_description ?? error}");
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest("No code.");

            var expectedState = context.Session.GetString("KickOAuthState");
            if (state != expectedState) return Results.BadRequest("Invalid state.");
            var codeVerifier = context.Session.GetString("KickCodeVerifier");
            if (string.IsNullOrWhiteSpace(codeVerifier)) return Results.BadRequest("No code verifier.");

            context.Session.Remove("KickOAuthState");
            context.Session.Remove("KickCodeVerifier");

            var sessionId = context.Session.GetString("SessionId");
            if (string.IsNullOrWhiteSpace(sessionId)) return Results.BadRequest("No session.");

            // 3. VI HÄMTAR TILLBAKA NAMNET FRÅN SESSIONEN
            var channelName = context.Session.GetString("KickChannelName");
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return Results.BadRequest("Kunde inte hitta namnet i minnet. Försök logga in igen.");
            }

            var clientId = configuration["Kick:ClientId"]?.Trim();
            var clientSecret = configuration["Kick:ClientSecret"]?.Trim();
            var redirectUri = configuration["Kick:RedirectUri"]?.Trim();

            var httpClient = httpClientFactory.CreateClient();
            var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
                ["client_secret"] = clientSecret!,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri!,
                ["code_verifier"] = codeVerifier
            });

            var tokenResponseMessage = await httpClient.PostAsync("https://id.kick.com/oauth/token", tokenRequest);
            if (!tokenResponseMessage.IsSuccessStatusCode)
            {
                var errorBody = await tokenResponseMessage.Content.ReadAsStringAsync();
                Console.WriteLine($"Kick token exchange failed: {errorBody}");
                return Results.Problem("Token failed.");
            }

            var tokenResponse = await tokenResponseMessage.Content.ReadFromJsonAsync<KickTokenResponse>();
            if (tokenResponse is null) return Results.Problem("No token.");

            var kickChatService = connectionManager.GetOrCreateConnection(sessionId);
            
            // 4. GE BÅDE TOKEN OCH NAMN TILL KICKLIB
            kickChatService.SetTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken ?? "");
            kickChatService.SetChannelName(channelName);

            var userLoaded = await kickChatService.LoadCurrentUserAsync();
            if (userLoaded)
            {
                await kickChatService.StartChatAsync();
            }

            return Results.Redirect("/chat");
        });

        return app;
    }

    private static string CreatePkceCodeVerifier() { return Base64UrlEncode(RandomNumberGenerator.GetBytes(64)); }
    private static string CreatePkceCodeChallenge(string codeVerifier) { return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))); }
    private static string Base64UrlEncode(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
}
