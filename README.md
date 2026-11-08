# TuneBridge 2.0 (C# / WPF / .NET 10)

**One library. Every music platform.**

Native Windows desktop app for transferring music playlists. Pure C# — no Python runtime in the shipped EXE.

## Solution layout

| Project | Role |
|---------|------|
| `TuneBridge.Core` | Models, `IMusicProvider`, `MatchEngine` |
| `TuneBridge.Infrastructure` | Spotify (HTTP + PKCE), Zvuk (experimental / not supported yet) |
| `TuneBridge.Desktop` | WPF UI (MVVM) |
| `TuneBridge.Tests` | Unit tests |

## Requirements

- .NET 10 SDK
- Windows 10 x64 or later to **run** the WPF EXE
- Spotify Developer app (Client ID; Client Secret for client-credentials public import)

## Configure Spotify (no secrets in the binary)

```bat
set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_client_id
set TUNEBRIDGE_SPOTIFY_CLIENT_SECRET=your_client_secret
```

Create an app at https://developer.spotify.com/dashboard  
Redirect URI for future PKCE: `http://127.0.0.1:43821/callback`

## Build

```bash
dotnet restore
dotnet build TuneBridge.sln -c Release
dotnet test TuneBridge.sln -c Release
```

## Publish self-contained single-file EXE (Windows x64)

Run **on Windows** (or with `EnableWindowsTargeting`):

```bash
dotnet publish src/TuneBridge.Desktop/TuneBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
```

Output: `artifacts/publish/TuneBridge.exe`  
Compatible with Windows 10 x64 without installing .NET on the target machine.

## What works in this branch

- MatchEngine (ISRC, normalization, duration, version tags, ambiguous detection) + unit tests
- Spotify: URL parse, Client Credentials token, real public playlist fetch, search, create/add (user token), PKCE helpers
- Zvuk: parse + honest `NotSupportedException` (no fake data)
- WPF shell with import/match flow when credentials are set

## What is not done yet

- Browser-based PKCE loopback listener in the UI
- Secure token storage (DPAPI)
- Zvuk live API (blocked until a supported legal integration exists)
- Other services (Deezer, Tidal, …)
- CSV export dialog fully wired

## License

MIT
