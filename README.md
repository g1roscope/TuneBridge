# TuneBridge 2.0 (C# / WPF / .NET 10)

**One library. Every music platform.**

Native Windows desktop app for transferring music playlists. Pure C# — no Python runtime in the shipped EXE.

## Solution layout

| Project | Role |
|---------|------|
| `TuneBridge.Core` | Models, `IMusicProvider`, `MatchEngine` |
| `TuneBridge.Infrastructure` | Spotify (HTTP + Authorization Code PKCE + DPAPI), Zvuk (experimental) |
| `TuneBridge.Desktop` | WPF UI (MVVM) |
| `TuneBridge.Tests` | Unit tests |

## Requirements

- .NET 10 SDK
- Windows 10 x64 or later to **run** the WPF EXE (DPAPI token store is Windows-only)
- Spotify Developer app (**Client ID only** for desktop PKCE)

## Configure Spotify (no secrets in the binary)

```bat
set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
rem optional — only for limited client_credentials without user login:
set TUNEBRIDGE_SPOTIFY_CLIENT_SECRET=your_secret
rem must match Spotify Dashboard exactly (no trailing slash):
set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback
```

1. Create an app at https://developer.spotify.com/dashboard  
2. Redirect URI (exact): `http://127.0.0.1:43821/callback`  
3. Scopes: `playlist-read-private`, `playlist-read-collaborative`, `playlist-modify-public`, `playlist-modify-private`

**No Client Secret is required for the desktop PKCE flow.**

## What works now

- Authorization Code + PKCE (S256), loopback callback, state validation
- Access + refresh tokens; refresh keeps old refresh_token if Spotify omits a new one
- DPAPI (CurrentUser) token storage on Windows; in-memory for tests
- WPF: Connect / Disconnect Spotify, auth status
- Spotify URL parse
- Playlist **items** fetch for playlists you **own or collaborate on** (API 2026)
- Track search (limit max **10**)
- MatchEngine (ISRC, normalization, duration, version tags)
- Create playlist API surface: `POST /me/playlists`
- Add items API surface: `POST /playlists/{id}/items` (see AuthMap)

## What does **not** work yet (honest)

- **Full end-to-end transfer UI** — Import runs match **preview** only; destination playlist is **not** created/filled by the Import button yet
- Reading **arbitrary public** playlists you do not own/collaborate on (Spotify API restriction since Feb 2026)
- Звук live API (parse only + `NotSupportedException`)
- Other services (Deezer, Tidal, YouTube Music, …)
- CSV/export file dialog fully wired

## Spotify Web API (2026) limitations (enforced in code)

| Topic | Behaviour |
|-------|-----------|
| Playlist fields | `items` / `item` (not `tracks` / `track` as media object) |
| GET playlist items | Only owned or collaborative playlists |
| Search `limit` | Max **10** |
| Create playlist | `POST /me/playlists` |
| Add items | `POST /playlists/{id}/items` |

## Build

```bash
dotnet restore TuneBridge.sln
dotnet build TuneBridge.sln -c Release
dotnet test TuneBridge.sln -c Release
```

## Publish (Windows x64 self-contained)

```bash
dotnet publish src/TuneBridge.Desktop/TuneBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
```

## License

MIT
