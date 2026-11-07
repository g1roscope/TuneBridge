"""Application configuration. Secrets only from environment."""
from __future__ import annotations

import os
from functools import lru_cache

from pydantic import BaseModel


class Settings(BaseModel):
    # Spotify (required for live Spotify features)
    spotify_client_id: str | None = None
    spotify_client_secret: str | None = None
    spotify_redirect_uri: str = "http://127.0.0.1:8765/callback"

    # Feature flags
    enable_zvuk: bool = True
    enable_experimental: bool = True

    # Security
    allowed_playlist_hosts: list[str] = [
        "open.spotify.com",
        "spotify.com",
        "www.deezer.com",
        "deezer.com",
        "listen.tidal.com",
        "tidal.com",
        "soundcloud.com",
        "www.soundcloud.com",
        "music.youtube.com",
        "youtube.com",
        "www.youtube.com",
        "music.apple.com",
        "zvuk.com",
        "sber-zvuk.com",
    ]

    class Config:
        extra = "ignore"


@lru_cache
def get_settings() -> Settings:
    return Settings(
        spotify_client_id=os.getenv("SPOTIFY_CLIENT_ID"),
        spotify_client_secret=os.getenv("SPOTIFY_CLIENT_SECRET"),
        spotify_redirect_uri=os.getenv("SPOTIFY_REDIRECT_URI", "http://127.0.0.1:8765/callback"),
        enable_zvuk=os.getenv("ENABLE_ZVUK", "1") not in ("0", "false", "False"),
        enable_experimental=os.getenv("ENABLE_EXPERIMENTAL", "1") not in ("0", "false", "False"),
    )
