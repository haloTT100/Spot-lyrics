# Spot-lyrics

A lightweight always-on-top lyrics overlay for Windows built with **WPF**.  
It tracks the current song from Spotify, fetches synced lyrics, and renders them in a clean floating overlay with karaoke-style highlighting.

## Features

- Always-on-top WPF lyrics overlay
- Spotify Web API as the primary playback source
- SMTC (System Media Transport Controls) fallback when Spotify playback data is unavailable
- Synced lyric fetching through Musixmatch
- Karaoke-style per-word highlighting
- Tray icon controls for show/hide, drag, resize, and text-only mode
- Window position and size persistence
- Romanization support for Japanese and Cyrillic lyrics
- Built-in runtime memory profiler available from the tray menu

## How it works

Spot-lyrics separates playback detection from lyric rendering:

- **Playback source layer**
  - Primary: Spotify Web API
  - Fallback: SMTC
- **Lyrics layer**
  - Musixmatch for synced, unsynced, and karaoke-capable lyrics
- **Rendering layer**
  - WPF overlay with an `ObservableCollection`-based display pipeline
  - Reused lyric-line containers, cached karaoke brushes, and throttled karaoke updates to reduce WPF allocation churn
- **Diagnostics layer**
  - A fixed-size runtime sampler for memory, GC, allocation rate, handles, and thread counts

This keeps the overlay responsive while preserving smooth karaoke transitions.

## Performance and memory

The app avoids loading the Kawazu/MeCab Japanese dictionary until Japanese lyrics actually require romanization. It also bounds lyric and romanization caches, reuses the Spotify HTTP client, and limits karaoke refreshes to reduce short-lived WPF allocations.

To inspect runtime memory, open the tray menu and select **Log Memory Profile**. The report is shown in the active log and includes:

- Working set and private memory
- Managed live memory, GC heap size, and fragmentation
- Managed allocation rate and memory growth since profiling began
- Handle, thread, and GC collection counts

The profiler samples once every 10 seconds and stores only the latest 60 samples.

## Tech stack

- .NET / WPF
- C#
- Spotify Web API
- Windows SMTC (`Windows.Media.Control`)
- Musixmatch
- Kawazu for romanization support

## Requirements

- Windows
- .NET SDK with WPF support
- A Spotify Premium account with playback access (primary playback source)

## Setup

1. Clone the repository:

   ```bash
   git clone https://github.com/haloTT100/Spot-lyrics.git
   cd Spot-lyrics
   ```

2. Restore dependencies:

   ```bash
   dotnet restore
   ```

3. Build the project:

   ```bash
   dotnet build
   ```

4. Run it:

   ```bash
   dotnet run
   ```

## Spotify authentication

The app uses Spotify OAuth to read the current playback state.

On first launch:
- a browser window opens for Spotify login,
- the app requests playback-state access,
- a refresh token is stored locally so future launches can reuse it.

If Spotify playback is not available, the app can fall back to SMTC session data on Windows.

## Usage

- Start playing a track in Spotify
- Launch the overlay
- Lyrics should appear automatically when available
- Use the tray icon to:
  - Show or hide the overlay
  - Enable dragging
  - Enable resizing
  - Toggle text-only mode
  - Open the active log or write a memory-profile report to it
  - Exit the app

## Notes

- Not every track has synced or karaoke lyrics available
- Playback metadata quality may differ between Spotify Web API and SMTC fallback
- Japanese romanization loads its dictionary only when it is needed; the first Japanese track may therefore take slightly longer to prepare
- Some lyric sources and APIs may change behavior over time

## Project structure

The main implementation is organized into these focused components:

- `KaraokeSegmentRenderer.cs` — timed karaoke word colouring and segment generation
- `WindowSettingsStore.cs` — saved overlay layout and preference persistence
- `LogViewerWindow.cs` — active-log window with throttled refreshes
- `RuntimeProfiler.cs` — lightweight runtime memory and allocation sampling

The remaining core pieces include:

- `MainWindow.xaml` — overlay UI
- `MainWindow.xaml.cs` — playback polling, overlay state, lyric rendering pipeline
- `MusixmatchClient.cs` — lyric lookup, rich sync, subtitle parsing, romanization
- Spotify auth/client classes — OAuth and playback fetch
- playback source implementations — Spotify primary, SMTC fallback

## Roadmap

Possible future improvements:

- Better source switching back to Spotify after fallback
- More settings for fonts, opacity, colors, and layout
- Multi-monitor positioning support
- Better lyric-source fallback handling
- Packaging and installer support

## Disclaimer

This project is intended for personal and educational use.  
Spotify, Musixmatch, and all related trademarks belong to their respective owners.
