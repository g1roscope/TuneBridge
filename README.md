# TuneBridge 2.0 (C# / WPF / .NET 10)

**One library. Every music platform.**

Native Windows desktop app for transferring music playlists. Pure C# — no Python runtime in the shipped EXE.

## Solution layout

| Project | Role |
|---------|------|
| `TuneBridge.Core` | Models, `IMusicProvider`, `MatchEngine` |
| `TuneBridge.Infrastructure` | Spotify (HTTP + Authorization Code PKCE + DPAPI), Zvuk (experimental), local JSON storage |
| `TuneBridge.Desktop` | WPF UI (MVVM) |
| `TuneBridge.Tests` | Unit tests |

## Requirements

- .NET 10 SDK
- Windows 10 x64 or later to **run** the WPF EXE (DPAPI token store is Windows-only)
- Spotify Developer app (**Client ID only** for desktop PKCE)

## Configure Spotify (no secrets in the binary)

```bat
set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback
```

1. Create an app at https://developer.spotify.com/dashboard  
2. Redirect URI (exact): `http://127.0.0.1:43821/callback`  
3. Scopes: `playlist-read-private`, `playlist-read-collaborative`, `playlist-modify-public`, `playlist-modify-private`

**No Client Secret is required for the desktop PKCE flow.**

## What works now

- Authorization Code + PKCE (S256), loopback callback, state validation
- Access + refresh tokens; DPAPI (CurrentUser) on Windows
- WPF: Connect / Disconnect, dark Spotify-style UI, sidebar Transfer / History / Settings
- Spotify playlist items for owned/collaborative playlists (API 2026)
- Track search (limit max **10**)
- MatchEngine (ISRC, NFKD, duration, version tags, ambiguous, alternates)
- **Transfer Spotify → Spotify**: create playlist + add selected matched tracks (deduped)
- Row selection (auto-select high-confidence; ambiguous unchecked by default)
- Progress + cancel during analyze/transfer
- History (local JSON under `%LocalAppData%\\TuneBridge`)
- Settings persisted locally
- Export match results to **JSON** and **CSV**
- Honest Звук status: no official public third-party API → NotSupportedException

## What does not work yet

| Feature | Reason |
|---------|--------|
| Spotify → Звук | No official public Zvuk API; only reverse-engineered unofficial clients |
| Звук → Spotify | Same |
| Manual alternate picker UI | Alternates on MatchResult; table does not yet expose picker |
| Theme switcher | Dark only |

## Build / test / publish

```bat
dotnet restore TuneBridge.sln
dotnet build TuneBridge.sln -c Release
dotnet test TuneBridge.sln -c Release
dotnet publish src\\TuneBridge.Desktop\\TuneBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\\win-x64
```

## License

See LICENSE.
