# TuneBridge 2.0

**One library. Every music platform.**

Open-source playlist transfer. Primary client: Windows Desktop (C#). Backend: Python FastAPI.

## Current status: 2.0.0-dev (implementation branch)

### What works right now

| Feature | Status |
|---------|--------|
| Import public Spotify playlist by URL | **Real** (needs `SPOTIFY_CLIENT_ID` + `SPOTIFY_CLIENT_SECRET`) |
| Matching engine (ISRC, title, artist, album, duration, version tags) | **Real** |
| SSRF protection on URLs | **Real** |
| Export matched playlist to JSON / CSV | **Real** |
| Local JSON/CSV import | **Real** |
| Provider capabilities + registry | **Real** |
| Experimental Звук adapter skeleton | Present (requires `zvuk-music` + user token) |
| Simple browser UI for testing | Present at `/static/index.html` |
| WPF Desktop skeleton | Present (not yet wired to live API) |

### What is NOT done yet

- OAuth / creating playlists in user accounts
- Deezer, Tidal, SoundCloud, YouTube Music, Apple Music adapters
- Full Desktop UI connected to API
- PostgreSQL / Redis / history
- Self-contained offline mode

## Quick start (API)

```bash
cd src/TuneBridge.Api
python -m venv .venv
source .venv/bin/activate   # Windows: .venv\\Scripts\\activate
pip install -r requirements.txt

export SPOTIFY_CLIENT_ID=your_id
export SPOTIFY_CLIENT_SECRET=your_secret

PYTHONPATH=. uvicorn app.main:app --reload --port 8000
```

Open:
- http://127.0.0.1:8000/api/health
- http://127.0.0.1:8000/api/providers
- http://127.0.0.1:8000/static/index.html  (test UI)

### Example flow

```bash
# 1. Import
curl -X POST http://127.0.0.1:8000/api/import \\
  -H "Content-Type: application/json" \\
  -d '{"url":"https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"}'

# 2. Match (send playlist from step 1 + destination)
# 3. Export
```

## Project layout

```
src/
  TuneBridge.Api/          ← FastAPI + adapters + matching + transfer service
  TuneBridge.Desktop/      ← C# WPF skeleton
  TuneBridge.Core/         ← shared contracts
docs/                      ← architecture, audit, integration map, risks
```

## Branch policy

- `main` — stable releases only
- `grok/implementation` — active development (this work)

## License

MIT
