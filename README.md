# TuneBridge 2.0

**One library. Every music platform.**

TuneBridge is an open-source Windows desktop app for moving playlists between music services. The current implementation focuses on a dependable Spotify destination and service-independent playlist files.

## Current implementation

- Windows WPF desktop app on .NET 10.
- Spotify Authorization Code + PKCE with loopback callback and state validation.
- Refresh-token persistence; DPAPI protection on Windows.
- Read Spotify playlists that the signed-in user owns or collaborates on.
- Search Spotify for tracks (API search limit is capped at 10).
- Import playlist files: **JSON, CSV, M3U and M3U8**.
  - CSV supports quoted fields, commas, semicolon-delimited exports, UTF-8 BOM and common metadata columns.
  - M3U/M3U8 reads `#EXTINF` title, artist and duration metadata.
  - JSON accepts a TuneBridge playlist object or an array of tracks.
- Export the imported playlist as JSON from the desktop app. The Core file-format layer can also export CSV and M3U.
- Track matching uses ISRC, Unicode-aware normalization (including Cyrillic), title/artist/album similarity, duration and version tags.
- Transfer creates a **new private Spotify playlist** and adds only high-confidence matches. Ambiguous and missing tracks are skipped rather than silently choosing a potentially wrong recording.
- Spotify adds are sent in batches of up to 100 tracks.

## Service support

| Service | Current status |
|---------|---------------|
| Spotify | OAuth, playlist read, search, create playlist and add tracks |
| Local playlist files | JSON / CSV / M3U / M3U8 import; JSON export in the UI |
| Звук | URL parsing / capability information only; no live transfer |
| Яндекс Музыка | Not implemented yet |
| VK Музыка | Not implemented yet |
| Deezer / YouTube Music / Apple Music | Not implemented yet |

The project does not scrape passwords, browser cookies or private session data. Services without a suitable supported integration should use playlist-file import/export until a safe connector is available.

## Requirements

- .NET 10 SDK to build.
- Windows 10 x64 or later to run the WPF application.
- A Spotify Developer app Client ID for PKCE login.

## Configure Spotify

Set the public Client ID and exact redirect URI before launching:

```bat
set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback
```

1. Create an app in the Spotify Developer Dashboard: https://developer.spotify.com/dashboard
2. Add this redirect URI exactly: `http://127.0.0.1:43821/callback`
3. The app requests these scopes:
   - `playlist-read-private`
   - `playlist-read-collaborative`
   - `playlist-modify-public`
   - `playlist-modify-private`

The desktop PKCE flow does not require a Client Secret. Never put a secret in source code or a distributed executable.

Spotify may restrict playlist-item access to playlists owned by or shared with the signed-in user. TuneBridge reports API errors instead of fabricating playlist contents.

## Build, test and publish

```bat
dotnet restore TuneBridge.sln
dotnet build TuneBridge.sln --configuration Release
dotnet test TuneBridge.sln --configuration Release
dotnet publish src\TuneBridge.Desktop\TuneBridge.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true --output publish\win-x64
```

GitHub Actions builds and tests the solution on Windows. It also publishes a self-contained Windows x64 artifact when the workflow succeeds.

## Known limitations

- Ambiguous matches are skipped automatically; the manual alternate picker is not implemented yet.
- No pause/resume or cancel control for a long transfer yet.
- Transfer history and settings screens are still incomplete.
- Only Spotify is currently a live music-service integration.
- A failed transfer may have created the destination playlist before an add operation failed; check Spotify before retrying.

## License

See [LICENSE](LICENSE).
