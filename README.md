# TuneBridge 2.0 (C# / WPF / .NET 10)

**One library. Every music platform.**

Native Windows desktop app for transferring music playlists. Pure C# — no Python runtime in the shipped EXE.

## Solution layout

| Project | Role |
|---------|------|
| `TuneBridge.Core` | Models, `IMusicProvider`, `MatchEngine` |
| `TuneBridge.Infrastructure` | Spotify (HTTP + Authorization Code PKCE + DPAPI), Zvuk (experimental / not supported yet) |
| `TuneBridge.Desktop` | WPF UI (MVVM) |
| `TuneBridge.Tests` | Unit tests |

## Requirements

- .NET 10 SDK
- Windows 10 x64 or later to **run** the WPF EXE (DPAPI token store is Windows-only)
- Spotify Developer app (**Client ID only** for desktop PKCE; Client Secret is optional and never embedded)

## Configure Spotify (no secrets in the binary)

```bat
set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
rem optional — only for limited client_credentials metadata without user login:
set TUNEBRIDGE_SPOTIFY_CLIENT_SECRET=your_secret
rem optional override (must match Spotify Dashboard redirect URI):
set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback
```

1. Create an app at https://developer.spotify.com/dashboard  
2. App type: **Web** or **Desktop** is fine; mark the client as **public** (PKCE).  
3. Redirect URI (exact): `http://127.0.0.1:43821/callback`  
4. Scopes used: `playlist-read-private`, `playlist-read-collaborative`, `playlist-modify-public`, `playlist-modify-private`

**No Client Secret is required or stored for the desktop PKCE flow.**

## Authorization (this branch)

- Authorization Code + PKCE (S256)
- Loopback listener on `http://127.0.0.1:43821/callback` with **state** validation
- System browser for login
- Access token + refresh token; automatic refresh near expiry
- Tokens encrypted with **Windows DPAPI (CurrentUser)** under `%LocalAppData%\\TuneBridge\\tokens\\`
- UI: **Connect Spotify** / **Disconnect** + status line
- Handles user denial, timeout, network errors, state mismatch

## Spotify Web API (2026) compatibility

| Topic | Behaviour in this codebase |
|-------|----------------------------|
| Playlist fields | Uses `items` (not `tracks`); item media under `item` (not `track`) |
| GET playlist items | Only for playlists you **own** or **collaborate** on — otherwise clear error |
| Search `limit` | Clamped to **10** (API maximum) |
| Create playlist | `POST /me/playlists` |
| Add items | `POST /playlists/{id}/items` (when write path is used) |
| Public arbitrary playlists | **Not** claimed as fully readable without ownership/collaboration |

## Build

```bash
dotnet restore TuneBridge.sln
dotnet build TuneBridge.sln -c Release
dotnet test TuneBridge.sln -c Release
```

## Publish self-contained single-file EXE (Windows x64)

Run **on Windows** (or with `EnableWindowsTargeting`):

```bash
dotnet publish src/TuneBridge.Desktop/TuneBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
```

Output: `artifacts/publish/TuneBridge.exe`

## What works in this branch (`work/grok-spotify-auth`)

- MatchEngine (ISRC, normalization, duration, version tags, ambiguous) + unit tests
- Full Spotify Authorization Code + PKCE with loopback, refresh, DPAPI
- Spotify: URL parse, user-token playlist fetch (owned/collaborative), search (limit 10), create playlist (`/me/playlists`)
- WPF: Connect / Disconnect Spotify, auth status, import & match flow
- Zvuk: parse + honest `NotSupportedException` (no fake data)
- Unit tests: PKCE, loopback query/state helpers, token store, session restore/logout

## Remaining limitations

- **Playlist items**: only for playlists the signed-in user owns or collaborates on (Spotify API policy since Feb 2026)
- **Search**: max 10 results per request
- **Zvuk**: experimental — no live API integration yet
- **DPAPI**: Windows only; non-Windows tests use in-memory store
- **Write path** (add tracks to destination playlist): API surface present; end-to-end UI flow still thin
- Other services (Deezer, Tidal, …) not started

## License

MIT
