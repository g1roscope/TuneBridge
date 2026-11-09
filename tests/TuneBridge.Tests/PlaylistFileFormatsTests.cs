using TuneBridge.Core.ImportExport;
using TuneBridge.Core.Models;

namespace TuneBridge.Tests;

public class PlaylistFileFormatsTests
{
    [Fact]
    public void Csv_round_trip_preserves_metadata_and_quoted_commas()
    {
        var source = new Playlist
        {
            Name = "Road trip",
            Tracks =
            [
                new Track
                {
                    Title = "Night, Drive",
                    Artist = "Artist \"One\"",
                    Album = "Album",
                    DurationMs = 201500,
                    Isrc = "USABC1234567",
                    SourceUrl = "https://example.test/track/1",
                    Position = 0
                }
            ]
        };

        var csv = PlaylistFileFormats.Export(source, ".csv");
        var imported = PlaylistFileFormats.Import(csv, "csv");

        var track = Assert.Single(imported.Tracks);
        Assert.Equal("Night, Drive", track.Title);
        Assert.Equal("Artist \"One\"", track.Artist);
        Assert.Equal(201500, track.DurationMs);
        Assert.Equal("USABC1234567", track.Isrc);
        Assert.Equal("https://example.test/track/1", track.SourceUrl);
        Assert.Equal(0, track.Position);
    }

    [Fact]
    public void Csv_import_supports_semicolon_delimiter_and_seconds()
    {
        const string csv = "title;artist;duration\nSong;Performer;183.25";
        var playlist = PlaylistFileFormats.Import(csv, ".csv");

        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Performer", track.Artist);
        Assert.Equal(183250, track.DurationMs);
    }

    [Fact]
    public void M3u_import_reads_extinf_duration_artist_and_url()
    {
        const string m3u = "#EXTM3U\n#PLAYLIST:Favorites\n#EXTINF:215,Artist - Song title\nhttps://example.test/song";
        var playlist = PlaylistFileFormats.Import(m3u, "m3u8");

        Assert.Equal("Favorites", playlist.Name);
        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Song title", track.Title);
        Assert.Equal(215000, track.DurationMs);
        Assert.Equal("https://example.test/song", track.SourceUrl);
    }

    [Fact]
    public void M3u_export_keeps_order_and_round_trips_track_information()
    {
        var source = new Playlist
        {
            Name = "Mix",
            Tracks =
            [
                new Track { Title = "First", Artist = "A", DurationMs = 120000, SourceUrl = "https://example.test/1" },
                new Track { Title = "Second", Artist = "B", SourceUrl = "https://example.test/2" }
            ]
        };

        var imported = PlaylistFileFormats.Import(PlaylistFileFormats.Export(source, "m3u"), ".m3u");

        Assert.Equal("Mix", imported.Name);
        Assert.Equal(2, imported.Tracks.Count);
        Assert.Equal("First", imported.Tracks[0].Title);
        Assert.Equal("Second", imported.Tracks[1].Title);
        Assert.Equal(120000, imported.Tracks[0].DurationMs);
        Assert.Null(imported.Tracks[1].DurationMs);
    }

    [Fact]
    public void Json_import_accepts_track_array_and_sets_positions()
    {
        const string json = "[{\"title\":\"A\",\"artist\":\"One\"},{\"title\":\"B\",\"artist\":\"Two\"}]";
        var playlist = PlaylistFileFormats.Import(json, ".json");

        Assert.Equal(2, playlist.Tracks.Count);
        Assert.Equal(0, playlist.Tracks[0].Position);
        Assert.Equal(1, playlist.Tracks[1].Position);
    }

    [Fact]
    public void Csv_import_supports_utf8_bom_and_quoted_newlines()
    {
        const string csv = "\uFEFFtitle,artist\n\"Song\nTitle\",Artist";
        var playlist = PlaylistFileFormats.Import(csv, ".csv");

        Assert.Equal("Song\nTitle", Assert.Single(playlist.Tracks).Title);
    }

    [Fact]
    public void Unsupported_extension_throws_clear_exception()
    {
        Assert.Throws<NotSupportedException>(() => PlaylistFileFormats.Import("data", ".xml"));
    }
    [Fact]
    public void Csv_import_supports_russian_headers_and_clock_duration()
    {
        const string csv = "Название;Исполнитель;Альбом;Длительность;Ссылка\nНочной город;Группа;Альбом;3:15;https://example.test/track";
        var playlist = PlaylistFileFormats.Import(csv, ".csv");

        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("Ночной город", track.Title);
        Assert.Equal("Группа", track.Artist);
        Assert.Equal("Альбом", track.Album);
        Assert.Equal(195000, track.DurationMs);
        Assert.Equal("https://example.test/track", track.SourceUrl);
    }

    [Fact]
    public void Csv_import_supports_spotify_export_headers()
    {
        const string csv = "Track URI,Track Name,Album Name,Artist Name(s),Duration (ms)\nspotify:track:abc,Song,Album,Artist,201000";
        var track = Assert.Single(PlaylistFileFormats.Import(csv, ".csv").Tracks);

        Assert.Equal("Song", track.Title);
        Assert.Equal("Album", track.Album);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(201000, track.DurationMs);
        Assert.Equal("spotify:track:abc", track.SourceUrl);
    }

    [Fact]
    public void M3u_export_refuses_to_drop_tracks_without_source_locations()
    {
        var playlist = new Playlist
        {
            Tracks = [new Track { Title = "Metadata-only track", Artist = "Artist" }]
        };

        var error = Assert.Throws<InvalidOperationException>(() => PlaylistFileFormats.Export(playlist, ".m3u"));
        Assert.Contains("requires a URL", error.Message);
    }

}
