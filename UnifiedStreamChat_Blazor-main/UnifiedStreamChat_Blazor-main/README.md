# Unified Stream Chat

A modern, multi-platform live chat aggregator built with **Blazor (Server)** and **.NET 9**. It combines live chat streams from **Twitch**, **YouTube**, and **Kick** into a single, unified interface.

---

## ✨ Features

- **Multi-Platform Support**: Connect and read live messages simultaneously from Twitch, YouTube, and Kick.
- **Secure Authentication**: Implements OAuth 2.1 with PKCE flows for secure token management.
- **Dynamic Channel Management**: Seamless session-based connection handling via custom connection managers.
- **Modern UI**: Clean, responsive layout matching platform-specific designs (including a custom Kick authentication/connection flow).
- **Real-Time Streaming**: Uses native web sockets and Pusher/KickLib integrations for instant message delivery.

---

## 🛠️ Tech Stack

- **Framework**: .NET 9 / ASP.NET Core Blazor (Interactive Server)
- **APIs & Libraries**:
    - Twitch API / EventSub
    - YouTube Data API v3
    - `KickLib` (for unofficial Kick API and Pusher WebSocket chat)
- **State Management**: Session-based singletons (`ConnectionManager` pattern)

---

## 🚀 Getting Started

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/) installed on your machine.
- Developer accounts and Client IDs/Secrets set up for Twitch, YouTube, and Kick.

### Configuration
1. Clone the repository.
2. Set up your configuration in `appsettings.json` or use .NET User Secrets for sensitive credentials:

```json
{
  "Twitch": {
    "ClientId": "YOUR_TWITCH_CLIENT_ID",
    "ClientSecret": "YOUR_TWITCH_CLIENT_SECRET"
  },
  "YouTube": {
    "ClientId": "YOUR_YOUTUBE_CLIENT_ID",
    "ClientSecret": "YOUR_YOUTUBE_CLIENT_SECRET"
  },
  "Kick": {
    "ClientId": "YOUR_KICK_CLIENT_ID",
    "ClientSecret": "YOUR_KICK_CLIENT_SECRET",
    "RedirectUri": "https://localhost:7008/auth/kick/callback"
  }
}

```

Running the App

    Open a terminal in the project root directory.

    Restore dependencies and run the application:
    Bash

    dotnet restore
    dotnet run

    Open your browser and navigate to https://localhost:7008.

---

📝 License

This project is for educational and personal streaming purposes.

---

## 📝 ToDo / Framtida förbättringar

- [ ] **Auto-scroll** – Lägg till JavaScript via Blazor JSInterop så att chattfönstret automatiskt scrollar ner när nya meddelanden kommer in.

- [ ] **Emotes** – Bygg in logik för att omvandla textkoder, exempelvis `LUL` och `Kappa`, till faktiska emote-bilder så att chatten känns mer levande.

- [ ] **Chattruta** – Skapa ett snyggt och användarvänligt inmatningsfält längst ner i `Chat.razor`.

- [ ] **Skicka-logik** – Låt användaren skicka sitt meddelande till en specifik vald plattform eller skicka det till alla tre plattformarna samtidigt.

- [ ] **Systemmeddelanden** – Lyssna efter och färgkoda viktiga händelser som prenumerationer (Subs), Super Chats och nya följare så att de sticker ut i chattflödet.

- [ ] **Mod-verktyg** – Lägg till små dolda ikoner, exempelvis en papperskorg, som visas när man hovrar över ett meddelande. Detta ska ge möjlighet att snabbt radera meddelanden eller tysta en användare (`timeout`).


