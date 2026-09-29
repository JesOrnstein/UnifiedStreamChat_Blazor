using UnifiedStreamChat.Components;
using UnifiedStreamChat.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor services.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

// Session storage.
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Platform connection managers.
builder.Services.AddSingleton<TwitchConnectionManager>();
builder.Services.AddSingleton<YouTubeConnectionManager>();
builder.Services.AddSingleton<KickConnectionManager>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSession();
app.UseAntiforgery();
app.MapStaticAssets();

// --- Här registrerar vi våra nya filer! ---
app.MapTwitchAuth();
app.MapYouTubeAuth();
app.MapKickAuth();
// ------------------------------------------

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();