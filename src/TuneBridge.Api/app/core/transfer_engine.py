from __future__ import annotations

from .matcher import find_match
from .models import MatchStatus, Playlist, Track, TransferReport


def build_report(
    playlist: Playlist,
    candidate_lists: list[list[Track]],
) -> TransferReport:
    results = []
    matched = ambiguous = missing = manual = 0

    for source, candidates in zip(playlist.tracks, candidate_lists):
        if not source.version_tags:
            from .matcher import extract_version_tags
            source.version_tags = extract_version_tags(source.title, source.album)

        result = find_match(source, candidates)
        results.append(result)

        if result.status == MatchStatus.MATCHED:
            matched += 1
        elif result.status == MatchStatus.AMBIGUOUS:
            ambiguous += 1
        elif result.status == MatchStatus.MANUAL:
            manual += 1
        else:
            missing += 1

    return TransferReport(
        playlist_name=playlist.name,
        total=len(playlist.tracks),
        matched=matched,
        ambiguous=ambiguous,
        missing=missing,
        manual=manual,
        results=results,
    )


def build_export_playlist(
    source: Playlist,
    report: TransferReport,
    *,
    include_ambiguous: bool = False,
    include_missing_as_source: bool = False,
) -> Playlist:
    tracks: list[Track] = []
    for result in report.results:
        if result.status == MatchStatus.MATCHED and result.candidate:
            t = result.candidate.model_copy(deep=True)
            t.position = len(tracks)
            tracks.append(t)
        elif result.status == MatchStatus.AMBIGUOUS and include_ambiguous and result.candidate:
            t = result.candidate.model_copy(deep=True)
            t.position = len(tracks)
            tracks.append(t)
        elif result.status == MatchStatus.MISSING and include_missing_as_source:
            t = result.source.model_copy(deep=True)
            t.position = len(tracks)
            tracks.append(t)

    return Playlist(
        name=source.name,
        description=source.description or f"Transferred via TuneBridge from {source.source_service}",
        tracks=tracks,
        source_service="tunebridge",
    )
