# TuneBridge 2.0

**One library. Every music platform.**

TuneBridge is an open-source Windows desktop app for moving playlists between music services. The current release candidate focuses on Spotify as a live destination and portable playlist files as a safe bridge for services without a supported connector.

## Current implementation

- Windows WPF desktop app on .NET 10.
- Spotify Authorization Code + PKCE with loopback callback and state validation.
- Refresh-token persistence; DPAPI protection on Windows.
- Read Spotify playlists that the signed-in user owns or collaborates on.
- Search Spotify for tracks, respecting the current API maximum of 10 results per request.
- Bounded retry handling for Spotify rate limits and transient read failures. Write requests retry only explicit HTTP 429 responses to avoid replaying ambiguous writes.
- Import playlist files: **JSON, CSV, M3U and M3U8**.
  - CSV supports quoted fields, commas, semicolon/tab delimiters, UTF-8 BOM, Russian column names, Spotify export headers and durations in milliseconds, seconds or mm:ss.
  - M3U/M3U8 reads EXTINF title, artist and duration metadata.
  - JSON accepts a TuneBridge playlist object or an array of tracks.
- Export playlists as JSON or CSV. M3U export is available when every track has a source URL or file path; the app refuses a lossy export otherwise.
- Track matching uses ISRC, Unicode-aware normalization (including Cyrillic), title/artist/album similarity, duration and English/Russian recording-version tags.
- Search failures are reported separately from genuine missing tracks.
- High-confidence matches are selected by default. Ambiguous and missing tracks cannot be selected for automatic transfer.
- Transfer creates a **new private Spotify playlist** and adds selected high-confidence matches in batches of up to 100 tracks.
- Analysis and transfer can be cancelled. Progress is shown during analysis.
- Completed, failed and cancelled transfer attempts are recorded in local transfer history.

## Service support

| Service | Current status |
|---------|---------------|
| Spotify | OAuth, playlist read, search, create playlist and add tracks |
| Local playlist files | JSON / CSV / M3U / M3U8 import; JSON / CSV / M3U export |
| Яндекс Музыка | Experimental anonymous public playlist read + track search; playlist creation/addition not supported |
| VK Музыка | No live connector yet; use a supported playlist-file export |
| Звук | Experimental public playlist read + track search; playlist creation/addition not supported |
| Deezer / YouTube Music / Apple Music | Not implemented yet |

The experimental Yandex Music connector uses YandexMusicResolver 7.3.0 with its anonymous credential provider. It reads public playlists and searches track metadata without asking for a Yandex password. Private playlists, playlist creation and adding tracks are not supported; album names are not exposed by the library's track model. Cancellation cannot interrupt an in-flight request inside the library. The Zvuk connector uses an undocumented public GraphQL interface and an anonymous token; it is experimental and may stop working without notice. It currently reads public playlists and searches tracks only. It does not create destination playlists or add tracks. The project does not scrape passwords, browser cookies or private session data. Services without a suitable supported integration should use playlist-file import/export until a safe connector is available.

## Requirements

- .NET 10 SDK to build.
- Windows 10 x64 or later to run the WPF application.
- A Spotify Developer app Client ID for PKCE login. Development Mode apps are subject to Spotify's current account and app restrictions.

## Configure Spotify

Set the public Client ID and exact redirect URI before launching:

    set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
    set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback

1. Create an app in the Spotify Developer Dashboard: https://developer.spotify.com/dashboard
2. Add this redirect URI exactly: http://127.0.0.1:43821/callback
3. The app requests these scopes:
   - playlist-read-private
   - playlist-read-collaborative
   - playlist-modify-public
   - playlist-modify-private

The desktop PKCE flow does not require a Client Secret. Never put a secret in source code or a distributed executable.

Spotify's February 2026 Development Mode changes restrict playlist-item access to playlists owned by or shared with the signed-in user. Spotify may also require the app owner to have Premium and limits Development Mode apps. TuneBridge reports API errors instead of fabricating playlist contents.

## Build, test and publish

    dotnet restore TuneBridge.sln
    dotnet build TuneBridge.sln --configuration Release
    dotnet test TuneBridge.sln --configuration Release
    dotnet publish src\TuneBridge.Desktop\TuneBridge.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true --output publish\win-x64

GitHub Actions builds and tests the solution on Windows. It also publishes a self-contained Windows x64 artifact when the workflow succeeds.

## Known limitations

- Spotify is the only fully supported transfer destination. Zvuk currently supports experimental source reading and search only.
- Ambiguous matches are not yet manually reassignable to an alternate candidate.
- No pause/resume or automatic resume after application restart yet.
- Cancelling or losing connection during transfer can leave a partially populated private playlist. Check Spotify before retrying.
- Transfer history is local to the current Windows user/device.
- M3U requires a source URL or local file path for every track; use JSON or CSV for metadata-only playlists.

## License

See LICENSE.
