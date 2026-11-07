from __future__ import annotations

import re
from typing import Type

from app.adapters.base import MusicProvider
from app.adapters.local import LocalProvider

_REGISTRY: dict[str, Type[MusicProvider]] = {
    "local": LocalProvider,
}


def _load_providers() -> None:
    global _REGISTRY
    try:
        from app.adapters.spotify import SpotifyProvider
        _REGISTRY["spotify"] = SpotifyProvider
    except Exception:
        pass

    try:
        from app.config import get_settings
        settings = get_settings()
        if settings.enable_zvuk and settings.enable_experimental:
            from app.adapters.zvuk import ZvukProvider
            _REGISTRY["zvuk"] = ZvukProvider
    except Exception:
        pass


_load_providers()


def register(provider_cls: Type[MusicProvider]) -> Type[MusicProvider]:
    instance = provider_cls()  # type: ignore[call-arg]
    _REGISTRY[instance.provider_id] = provider_cls
    return provider_cls


def get_provider(provider_id: str) -> MusicProvider:
    cls = _REGISTRY.get(provider_id)
    if cls is None:
        raise KeyError(f"Unknown provider: {provider_id}")
    return cls()


def list_providers() -> list[MusicProvider]:
    return [cls() for cls in _REGISTRY.values()]


def get_provider_for_url(url: str) -> MusicProvider | None:
    for provider in list_providers():
        patterns = getattr(provider, "_url_patterns", None) or []
        for pattern in patterns:
            if re.search(pattern, url, re.IGNORECASE):
                return provider
    return None
