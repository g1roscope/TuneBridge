from __future__ import annotations

from enum import Enum
from typing import Any
from pydantic import BaseModel, Field, ConfigDict


class MatchStatus(str, Enum):
    MATCHED = "matched"
    AMBIGUOUS = "ambiguous"
    MISSING = "missing"
    MANUAL = "manual"


class Track(BaseModel):
    model_config = ConfigDict(extra="allow")

    title: str
    artist: str = ""
    album: str = ""
    duration_ms: int | None = None
    isrc: str | None = None
    source_url: str | None = None
    provider_ids: dict[str, str] = Field(default_factory=dict)
    position: int = 0
    version_tags: list[str] = Field(default_factory=list)
    metadata: dict[str, Any] = Field(default_factory=dict)


class Playlist(BaseModel):
    name: str = "Imported playlist"
    description: str = ""
    tracks: list[Track] = Field(default_factory=list)
    source_service: str = "file"
    source_url: str | None = None
    external_id: str | None = None


class MatchCandidate(BaseModel):
    track: Track
    score: float
    reasons: list[str] = Field(default_factory=list)


class MatchResult(BaseModel):
    source: Track
    candidate: Track | None = None
    candidates: list[MatchCandidate] = Field(default_factory=list)
    score: float = 0.0
    status: MatchStatus = MatchStatus.MISSING
    reasons: list[str] = Field(default_factory=list)
    selected_provider_id: str | None = None


class TransferReport(BaseModel):
    playlist_name: str
    total: int
    matched: int
    ambiguous: int
    missing: int
    manual: int = 0
    results: list[MatchResult]


class ParsedPlaylistRef(BaseModel):
    """Normalized reference extracted from a playlist URL."""
    provider_id: str
    playlist_id: str
    url: str
    is_public: bool = True
    extra: dict[str, Any] = Field(default_factory=dict)


class ProviderCapabilities(BaseModel):
    """What a provider can actually do right now."""
    can_parse_url: bool = False
    can_read_public: bool = False
    can_read_private: bool = False
    can_search: bool = False
    can_create_playlist: bool = False
    can_add_tracks: bool = False
    supports_isrc: bool = False
    requires_oauth: bool = False
    is_experimental: bool = False
    notes: str = ""


class AuthToken(BaseModel):
    """Opaque token container. Never log the raw value."""
    access_token: str
    refresh_token: str | None = None
    expires_at: int | None = None
    token_type: str = "Bearer"
    scope: str | None = None
    extra: dict[str, Any] = Field(default_factory=dict)
