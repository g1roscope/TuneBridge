# TuneBridge

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
- Repeated identical track searches are cached during a playlist analysis to reduce API calls and rate-limit pressure.
- Spotify writes are prevalidated before the first batch; tracks without Spotify IDs cannot be silently dropped.
- Large Spotify writes are batched in groups of up to 100. If a later batch fails or cancellation occurs between batches, the app reports the confirmed number already added and warns when the interrupted batch outcome is unknown.
- Completed, partial, failed and cancelled transfer attempts are recorded in local transfer history.

## Service support

| Service | Current status |
|---------|---------------|
| Spotify | OAuth, playlist read, search, create playlist and add tracks |
| Local playlist files | JSON / CSV / M3U / M3U8 import; JSON / CSV / M3U export |
| Яндекс Музыка | Experimental public/private playlist read + search; authenticated playlist creation and track insertion are implemented, but require real-account validation |
| VK Музыка | No live connector yet; use a supported playlist-file export |
| Звук | Experimental public playlist read + track search; playlist creation/addition not supported |
| Deezer / YouTube Music / Apple Music | Not implemented yet |

The experimental Yandex Music connector uses the YandexMusic 0.7.0 library and an OAuth device-code sign-in flow. It supports public playlist reading, authenticated private playlist reading, catalogue search, playlist creation and track insertion in code. Its API integration is unofficial and may change without notice; authenticated write operations have not yet passed end-to-end testing against a real account. Track insertion is performed one track at a time, and an interrupted request is conservatively reported as having an unknown outcome so users can check the destination before retrying. The Zvuk connector uses an undocumented public GraphQL interface and an anonymous token; it is experimental and may stop working without notice. It currently reads public playlists and searches tracks only. It does not create destination playlists or add tracks. VK Music has no live connector yet. The project does not scrape passwords, browser cookies or private session data. Services without a suitable supported integration should use playlist-file import/export until a safe connector is available.

## Requirements

- .NET 10 SDK to build.
- Windows 10 x64 or later to run the WPF application.
- A Spotify Developer app Client ID for PKCE login. Development Mode apps are subject to Spotify's current account and app restrictions.

## Configure Spotify

1. Create an app in the Spotify Developer Dashboard: https://developer.spotify.com/dashboard
2. Add this redirect URI exactly: http://127.0.0.1:43821/callback
3. Open TuneBridge **Settings → Spotify**, paste the app's public Client ID and select **Save Client ID**. The value is stored in local app settings; it is not a client secret.

Alternatively, set the environment variables before launching:

    set TUNEBRIDGE_SPOTIFY_CLIENT_ID=your_public_client_id
    set TUNEBRIDGE_SPOTIFY_REDIRECT_URI=http://127.0.0.1:43821/callback

The environment variable takes precedence over the value saved in the app.
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

## Diagnostics and local data

TuneBridge stores application settings and transfer history under `%LocalAppData%\\TuneBridge`. Diagnostic logs are written to the `logs` subfolder and can be opened from **Settings → Diagnostics**. Logs are local to the current Windows user and are not uploaded automatically.

Errors include stable codes to make support reports easier to understand:

| Code | Meaning |
|------|---------|
| `TB-000` | Operation cancelled |
| `TB-207` | Partial transfer or uncertain write outcome; check the destination before retrying |\n| `TB-401` | Access denied or authentication required |
| `TB-408` | Operation timed out |
| `TB-422` | Invalid local or remote data |
| `TB-503` | Network or service communication failure |
| `TB-IO` | Local file operation failed |
| `TB-501` | Requested service operation is unsupported |
| `TB-999` | Unexpected error |

The logger redacts common bearer-token, token-assignment, OAuth code/state/PKCE challenge and cookie formats. It does not intentionally record HTTP request or response bodies. Review a log before sharing it; never send passwords, one-time codes, or account recovery information.

## Known limitations

- Spotify is the only destination whose complete transfer flow is currently treated as established. Yandex playlist creation and insertion are implemented behind experimental authentication and require real-account end-to-end validation before being advertised as release-ready. Zvuk currently supports experimental source reading and search only; VK has no live connector yet.
- Ambiguous matches are not yet manually reassignable to an alternate candidate.
- No pause/resume or automatic resume after application restart yet.
- A network interruption during an in-flight Spotify or Yandex write can leave the current operation outcome unknown. TuneBridge reports confirmed earlier writes separately; check the destination playlist before retrying.
- Transfer history is local to the current Windows user/device.
- M3U requires a source URL or local file path for every track; use JSON or CSV for metadata-only playlists.

## License

See LICENSE.
