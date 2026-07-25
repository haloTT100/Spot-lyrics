using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfApp = System.Windows.Application;
using System.Collections.ObjectModel;
using Windows.Media.Control;

namespace lyrics_overlay;

public interface IPlaybackSource
{
    string SourceName { get; }
    bool IsAvailable { get; }
    Task InitializeAsync();
    Task<SpotifyPlaybackState?> GetPlaybackAsync();
}

public partial class MainWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly MusixmatchClient _musixmatch = new();
    private IPlaybackSource? _playbackSource;
    private IPlaybackSource? _spotifySource;
    private IPlaybackSource? _fallbackSource;

    private Forms.NotifyIcon? _trayIcon;
    private bool _isRealExit;
    private bool _isDraggable = false;
    private bool _isResizable = false;
    private bool _textOnlyMode = true;
    private List<SyncedLyricLine> _syncedLyrics = new();
    private List<KaraokeLine> _karaokeLyrics = new();
    private DispatcherTimer? _spotifyPollTimer;
    private string _lastKaraokeRenderKey = "";
    private string _currentTrackId = "";
    private string _lastDisplayedText = "";
    private bool _pollInProgress = false;
    private bool _currentTrackHasNoLyrics = false;
    private readonly Dictionary<string, List<SyncedLyricLine>> _lyricsCache = new();
    private readonly Dictionary<string, List<KaraokeLine>> _karaokeCache = new();
    private readonly HashSet<string> _noLyricsCache = new();

    private DispatcherTimer? _renderTimer;
    private int _baseProgressMs = 0;
    private DateTime _baseProgressUtc = DateTime.UtcNow;
    private bool _isPlaying = false;

    public ObservableCollection<DisplayLyricLine> VisibleLyrics { get; } = new();

    private readonly string _windowSettingsPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lyrics_overlay",
            "window_settings.json");

    private bool _restoringWindowSettings = false;
    private int _lastProgressMs = 0;

    private static readonly System.Windows.Media.Color SungWordColor =
        System.Windows.Media.Color.FromArgb(255, 255, 255, 255);

    private static readonly System.Windows.Media.Color ActiveWordStartColor =
        System.Windows.Media.Color.FromArgb(170, 255, 255, 255);

    private static readonly System.Windows.Media.Color ActiveWordEndColor =
        System.Windows.Media.Color.FromArgb(255, 255, 230, 120);

    private static readonly System.Windows.Media.Color UpcomingWordColor =
        System.Windows.Media.Color.FromArgb(120, 255, 255, 255);

    private static readonly System.Windows.Media.SolidColorBrush SungWordBrush =
        CreateFrozenBrush(SungWordColor);

    private static readonly System.Windows.Media.SolidColorBrush UpcomingWordBrush =
        CreateFrozenBrush(UpcomingWordColor);

    static System.Windows.Media.SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static System.Windows.Media.Color LerpColor(System.Windows.Media.Color from, System.Windows.Media.Color to, double t)
    {
        t = Math.Max(0.0, Math.Min(1.0, t));

        byte a = (byte)Math.Round(from.A + ((to.A - from.A) * t));
        byte r = (byte)Math.Round(from.R + ((to.R - from.R) * t));
        byte g = (byte)Math.Round(from.G + ((to.G - from.G) * t));
        byte b = (byte)Math.Round(from.B + ((to.B - from.B) * t));

        return System.Windows.Media.Color.FromArgb(a, r, g, b);
    }

    static double EaseOutCubic(double t)
    {
        t = Math.Max(0.0, Math.Min(1.0, t));
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }

    static double EaseInOutSine(double t)
    {
        t = Math.Max(0.0, Math.Min(1.0, t));
        return -(Math.Cos(Math.PI * t) - 1.0) / 2.0;
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LyricsItemsControl.ItemsSource = VisibleLyrics;
        AppLogger.Log("MainWindow ctor");

        StateChanged += (_, __) =>
        {
            if (WindowState != WindowState.Normal)
            {
                AppLogger.Log($"StateChanged forcing WindowState back to Normal from {WindowState}");
                WindowState = WindowState.Normal;
            }
        };

        MouseLeftButtonDown += (_, __) =>
        {
            if (_isDraggable)
            {
                AppLogger.Log("Window drag initiated");
                try
                {
                    ResizeMode = ResizeMode.NoResize;
                    UpdateLayout();
                    DragMove();
                    ResizeMode = _isResizable ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
                    UpdateLayout();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"DragMove exception: {ex.Message}");
                    ResizeMode = _isResizable ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
                }
            }
        };

        LocationChanged += (_, __) =>
        {
            if (IsLoaded && WindowState == WindowState.Normal)
                SaveWindowSettings();
        };

        SizeChanged += (_, __) =>
        {
            if (IsLoaded && WindowState == WindowState.Normal)
                SaveWindowSettings();

            RefreshVisibleLyrics(_lastProgressMs);
        };

        Loaded += async (_, __) =>
        {
            AppLogger.Log("MainWindow Loaded begin");

            try
            {
                LoadWindowSettings();
                MakeClickThrough();
                SetupTrayIcon();

                ApplyOverlayStyle();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                ApplyOverlayStyle();

                await _musixmatch.EnsureTokenAsync();

                _spotifySource = new SpotifyWebApiPlaybackSource();
                _fallbackSource = new SmtcPlaybackSource();

                await _spotifySource.InitializeAsync();
                if (_spotifySource.IsAvailable)
                {
                    _playbackSource = _spotifySource;
                    AppLogger.Log("Using Spotify Web API as primary playback source");
                }
                else
                {
                    await _fallbackSource.InitializeAsync();
                    _playbackSource = _fallbackSource.IsAvailable ? _fallbackSource : null;
                    AppLogger.Log(_playbackSource != null
                        ? "Using SMTC as fallback playback source"
                        : "No playback source is currently available");
                }

                StartSpotifyPolling();
                StartRenderTimer();

                var state = _playbackSource != null
                    ? await _playbackSource.GetPlaybackAsync()
                    : null;

                if (state != null)
                {
                    _baseProgressMs = state.ProgressMs;
                    _baseProgressUtc = DateTime.UtcNow;
                    _isPlaying = state.IsPlaying;
                    _lastProgressMs = state.ProgressMs;
                    AppLogger.Log($"Initial playback state: {state.Artist} - {state.Title} | TrackId={state.TrackId} | ProgressMs={state.ProgressMs} | IsPlaying={state.IsPlaying}");
                    SetOverlayMessage($"{state.Artist} - {state.Title}");
                }
                else
                {
                    AppLogger.Log("Initial playback state is null");
                    SetOverlayMessage("Nothing is currently playing.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MainWindow Loaded exception: {ex}");
                SetOverlayMessage(ex.Message);
            }

            AppLogger.Log("MainWindow Loaded end");
        };
    }

    void StartRenderTimer()
    {
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _renderTimer.Tick += (_, __) =>
        {
            int progressMs = _baseProgressMs;

            if (_isPlaying)
                progressMs += (int)(DateTime.UtcNow - _baseProgressUtc).TotalMilliseconds;

            _lastProgressMs = progressMs;
            RefreshVisibleLyrics(progressMs);
        };

        _renderTimer.Start();
    }

    void StartSpotifyPolling()
    {
        AppLogger.Log("StartSpotifyPolling invoked");

        _spotifyPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        _spotifyPollTimer.Tick += async (_, __) =>
        {
            if (_pollInProgress) return;
            _pollInProgress = true;

            try
            {
                SpotifyPlaybackState? state = null;

                if (_playbackSource != null)
                    state = await _playbackSource.GetPlaybackAsync();

                // Only switch to fallback if the SOURCE became unavailable (403),
                // NOT just because state is null (204 = nothing playing)
                if (_playbackSource == _spotifySource
                    && _spotifySource != null
                    && !_spotifySource.IsAvailable  // <-- this is the key check
                    && _fallbackSource != null)
                {
                    await _fallbackSource.InitializeAsync();
                    if (_fallbackSource.IsAvailable)
                    {
                        _playbackSource = _fallbackSource;
                        AppLogger.Log("Switched playback source to SMTC fallback (Spotify unavailable/forbidden)");
                        state = await _playbackSource.GetPlaybackAsync();
                    }
                }

                if (state == null)
                {
                    AppLogger.Log("Playback state is null / nothing currently playing");
                    _currentTrackId = "";
                    _syncedLyrics.Clear();
                    _karaokeLyrics.Clear();
                    _currentTrackHasNoLyrics = false;
                    _lastProgressMs = 0;
                    _baseProgressMs = 0;
                    _baseProgressUtc = DateTime.UtcNow;
                    _isPlaying = false;
                    _lastKaraokeRenderKey = "";
                    ReplaceVisibleLyrics(Array.Empty<DisplayLyricLine>());
                    SetOverlayMessage("Nothing is currently playing.");
                    return;
                }

                _lastProgressMs = state.ProgressMs;
                _baseProgressMs = state.ProgressMs;
                _baseProgressUtc = DateTime.UtcNow;
                _isPlaying = state.IsPlaying;

                AppLogger.Log($"Playback state: TrackId={state.TrackId} | Artist={state.Artist} | Title={state.Title} | Album={state.Album} | Uri={state.Uri} | DurationMs={state.DurationMs} | ProgressMs={state.ProgressMs} | IsPlaying={state.IsPlaying}");

                if (state.TrackId != _currentTrackId)
                {
                    AppLogger.Log($"Track change detected. OldTrackId={_currentTrackId}, NewTrackId={state.TrackId}");

                    _currentTrackId = state.TrackId;
                    _lastDisplayedText = "";
                    _lastKaraokeRenderKey = "";
                    _baseProgressMs = state.ProgressMs;
                    _baseProgressUtc = DateTime.UtcNow;
                    _isPlaying = state.IsPlaying;
                    ReplaceVisibleLyrics(Array.Empty<DisplayLyricLine>());
                    _syncedLyrics.Clear();
                    _karaokeLyrics.Clear();
                    _currentTrackHasNoLyrics = false;

                    if (_lyricsCache.TryGetValue(state.TrackId, out var cachedLyrics))
                    {
                        _syncedLyrics = new List<SyncedLyricLine>(cachedLyrics);
                        _karaokeLyrics = _karaokeCache.TryGetValue(state.TrackId, out var cachedKaraoke)
                            ? CloneKaraokeLines(cachedKaraoke)
                            : new List<KaraokeLine>();
                        _currentTrackHasNoLyrics = false;
                        AppLogger.Log($"Loaded lyrics from cache for track {state.TrackId}, count={_syncedLyrics.Count}, karaokeCount={_karaokeLyrics.Count}");
                    }
                    else if (_noLyricsCache.Contains(state.TrackId))
                    {
                        _syncedLyrics.Clear();
                        _karaokeLyrics.Clear();
                        _currentTrackHasNoLyrics = true;
                        SetOverlayMessage($"{state.Artist} - {state.Title}");
                        AppLogger.Log($"Track {state.TrackId} is cached as no-lyrics");
                    }
                    else
                    {
                        _syncedLyrics = await _musixmatch.GetSyncedLyricsAsync(
                            state.Artist,
                            state.Title,
                            state.Album,
                            state.Uri,
                            state.DurationMs
                        );

                        _karaokeLyrics = CloneKaraokeLines(_musixmatch.LastKaraokeLines);

                        AppLogger.Log($"Musixmatch returned {_syncedLyrics.Count} synced lines and {_karaokeLyrics.Count} karaoke lines for {state.Artist} - {state.Title}");

                        if (_syncedLyrics.Count == 0)
                        {
                            _noLyricsCache.Add(state.TrackId);
                            _currentTrackHasNoLyrics = true;
                            SetOverlayMessage($"{state.Artist} - {state.Title}");
                            AppLogger.Log($"Caching no-lyrics result for track {state.TrackId}");
                        }
                        else
                        {
                            _lyricsCache[state.TrackId] = new List<SyncedLyricLine>(_syncedLyrics);
                            _karaokeCache[state.TrackId] = CloneKaraokeLines(_karaokeLyrics);
                            _currentTrackHasNoLyrics = false;
                            AppLogger.Log($"Caching {_syncedLyrics.Count} lyrics and {_karaokeLyrics.Count} karaoke lines for track {state.TrackId}");
                            AppLogger.Log($"First synced line: {_syncedLyrics[0].StartTimeMs} ms | {_syncedLyrics[0].Text}");
                        }
                    }
                }

                if (state.IsPlaying && !_currentTrackHasNoLyrics)
                {
                    UpdateLyricFromMilliseconds(state.ProgressMs);
                }
                else if (!state.IsPlaying)
                {
                    AppLogger.Log("Playback is paused, not advancing lyric");
                    if (_currentTrackHasNoLyrics)
                        SetOverlayMessage($"{state.Artist} - {state.Title}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Polling exception: {ex}");
                SetOverlayMessage(ex.Message);
            }
            finally
            {
                _pollInProgress = false;
                AppLogger.Log("Polling tick end");
            }
        };

        _spotifyPollTimer.Start();
        AppLogger.Log("Spotify polling timer started");
    }

    static List<KaraokeLine> CloneKaraokeLines(IEnumerable<KaraokeLine>? source)
    {
        if (source == null)
            return new List<KaraokeLine>();

        return source.Select(line => new KaraokeLine
        {
            StartTimeMs = line.StartTimeMs,
            EndTimeMs = line.EndTimeMs,
            Performer = line.Performer,
            Words = (line.Words ?? new List<KaraokeWord>()).Select(word => new KaraokeWord
            {
                Word = word.Word,
                OffsetMs = word.OffsetMs,
                DurationMs = word.DurationMs
            }).ToList()
        }).ToList();
    }

    void UpdateLyricFromMilliseconds(int progressMs)
    {
        if ((_syncedLyrics == null || _syncedLyrics.Count == 0) && (_karaokeLyrics == null || _karaokeLyrics.Count == 0))
            return;

        _lastProgressMs = progressMs;
        RefreshVisibleLyrics(progressMs);
    }

    void RefreshVisibleLyrics(int? progressOverrideMs = null)
    {
        if ((_syncedLyrics == null || _syncedLyrics.Count == 0) &&
            (_karaokeLyrics == null || _karaokeLyrics.Count == 0))
            return;

        int progressMs = progressOverrideMs ?? _lastProgressMs;

        if (_karaokeLyrics != null && _karaokeLyrics.Count > 0)
        {
            RefreshVisibleKaraokeLyrics(progressMs);
            return;
        }

        if (_syncedLyrics == null || _syncedLyrics.Count == 0)
            return;

        int currentIndex = 0;

        for (int i = 0; i < _syncedLyrics.Count; i++)
        {
            var lyric = _syncedLyrics[i];
            if (lyric != null && lyric.StartTimeMs <= progressMs)
                currentIndex = i;
            else
                break;
        }

        var (start, end, previousLinesToShow) = CalculateVisibleRange(currentIndex, _syncedLyrics.Count);

        var lines = new List<DisplayLyricLine>();

        for (int i = start; i <= end; i++)
        {
            var lyric = _syncedLyrics[i];
            var text = string.IsNullOrWhiteSpace(lyric?.Text) ? " " : lyric!.Text;

            lines.Add(new DisplayLyricLine
            {
                Text = text,
                DistanceFromCurrent = i - currentIndex,
                IsKaraokeLine = false,
                Segments = new List<DisplayKaraokeSegment>()
            });
        }

        var currentLyric = _syncedLyrics[currentIndex];
        var currentText = string.IsNullOrWhiteSpace(currentLyric?.Text) ? " " : currentLyric!.Text;

        if (!string.Equals(currentText, _lastDisplayedText, StringComparison.Ordinal))
        {
            _lastDisplayedText = currentText;
            AppLogger.Log($"Displaying lyric currentIndex={currentIndex}, previousLines={previousLinesToShow}, nextLines={end - currentIndex}, current='{_lastDisplayedText}'");
        }

        ReplaceVisibleLyrics(lines);
    }

    void RefreshVisibleKaraokeLyrics(int progressMs)
    {
        if (_karaokeLyrics == null || _karaokeLyrics.Count == 0)
            return;

        int currentIndex = 0;

        for (int i = 0; i < _karaokeLyrics.Count; i++)
        {
            if (_karaokeLyrics[i].StartTimeMs <= progressMs)
                currentIndex = i;
            else
                break;
        }

        var (start, end, previousLinesToShow) = CalculateVisibleRange(currentIndex, _karaokeLyrics.Count);

        var lines = new List<DisplayLyricLine>();

        for (int i = start; i <= end; i++)
        {
            var line = _karaokeLyrics[i];

            lines.Add(new DisplayLyricLine
            {
                Text = string.IsNullOrWhiteSpace(line.FullText) ? " " : line.FullText,
                DistanceFromCurrent = i - currentIndex,
                IsKaraokeLine = true,
                Segments = BuildKaraokeSegments(line, progressMs)
            });
        }

        string currentDisplay = string.IsNullOrWhiteSpace(_karaokeLyrics[currentIndex].FullText)
            ? " "
            : _karaokeLyrics[currentIndex].FullText;

        if (!string.Equals(currentDisplay, _lastDisplayedText, StringComparison.Ordinal))
        {
            _lastDisplayedText = currentDisplay;
            AppLogger.Log($"Displaying karaoke currentIndex={currentIndex}, previousLines={previousLinesToShow}, nextLines={end - currentIndex}, current='{_lastDisplayedText}'");
        }

        string renderKey = string.Join("|", lines.Select(line =>
            $"{line.DistanceFromCurrent}:{string.Join("~", line.Segments.Select(s =>
                $"{s.Text}#{(s.ForegroundBrush as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? s.ForegroundBrush.ToString()}"))}"));

        if (renderKey == _lastKaraokeRenderKey)
            return;

        _lastKaraokeRenderKey = renderKey;

        ReplaceVisibleLyrics(lines);
    }

    (int start, int end, int previousLinesToShow) CalculateVisibleRange(int currentIndex, int totalCount)
    {
        double estimatedLineHeight = 42;
        double usableHeight = Math.Max(120, ActualHeight - 36);
        int visibleLineCount = Math.Max(3, (int)Math.Floor(usableHeight / estimatedLineHeight));
        int previousLinesToShow = Math.Min(1, currentIndex);
        int nextLinesToShow = Math.Max(1, visibleLineCount - 1 - previousLinesToShow);
        int start = Math.Max(0, currentIndex - previousLinesToShow);
        int end = Math.Min(totalCount - 1, currentIndex + nextLinesToShow);
        return (start, end, previousLinesToShow);
    }

    List<DisplayKaraokeSegment> BuildKaraokeSegments(KaraokeLine line, int progressMs)
    {
        var segments = new List<DisplayKaraokeSegment>();

        if (line.Words == null || line.Words.Count == 0)
        {
            segments.Add(new DisplayKaraokeSegment
            {
                Text = " ",
                ForegroundBrush = UpcomingWordBrush
            });
            return segments;
        }

        for (int i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            string text = word.Word ?? "";

            if (string.IsNullOrEmpty(text))
                continue;

            int wordStartMs = line.StartTimeMs + word.OffsetMs;

            int wordEndMs;
            if (word.DurationMs > 0)
            {
                wordEndMs = wordStartMs + word.DurationMs;
            }
            else if (i + 1 < line.Words.Count)
            {
                wordEndMs = line.StartTimeMs + line.Words[i + 1].OffsetMs;
            }
            else
            {
                wordEndMs = line.EndTimeMs > wordStartMs ? line.EndTimeMs : wordStartMs + 900;
            }

            if (wordEndMs <= wordStartMs)
                wordEndMs = wordStartMs + 120;

            if (progressMs < wordStartMs)
            {
                segments.Add(new DisplayKaraokeSegment
                {
                    Text = text,
                    ForegroundBrush = UpcomingWordBrush
                });
                continue;
            }

            if (progressMs >= wordEndMs)
            {
                segments.Add(new DisplayKaraokeSegment
                {
                    Text = text,
                    ForegroundBrush = SungWordBrush
                });
                continue;
            }

            double rawT = (double)(progressMs - wordStartMs) / (wordEndMs - wordStartMs);
            double easedT = EaseInOutSine(rawT);

            int charCount = text.Length;
            double charProgress = easedT * charCount;

            int fullChars = Math.Clamp((int)Math.Floor(charProgress), 0, charCount);
            double currentCharT = charProgress - fullChars;

            if (fullChars > 0)
            {
                segments.Add(new DisplayKaraokeSegment
                {
                    Text = text.Substring(0, fullChars),
                    ForegroundBrush = SungWordBrush
                });
            }

            if (fullChars < charCount)
            {
                var currentChar = text.Substring(fullChars, 1);
                var currentCharColor = LerpColor(ActiveWordStartColor, ActiveWordEndColor, currentCharT);

                segments.Add(new DisplayKaraokeSegment
                {
                    Text = currentChar,
                    ForegroundBrush = CreateFrozenBrush(currentCharColor)
                });

                if (fullChars + 1 < charCount)
                {
                    segments.Add(new DisplayKaraokeSegment
                    {
                        Text = text.Substring(fullChars + 1),
                        ForegroundBrush = UpcomingWordBrush
                    });
                }
            }
        }

        return MergeSegments(segments);
    }

    static bool SameBrush(System.Windows.Media.Brush a, System.Windows.Media.Brush b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a is System.Windows.Media.SolidColorBrush sa &&
            b is System.Windows.Media.SolidColorBrush sb)
            return sa.Color == sb.Color;

        return false;
    }

    List<DisplayKaraokeSegment> MergeSegments(List<DisplayKaraokeSegment> rawSegments)
    {
        if (rawSegments.Count <= 1)
            return rawSegments;

        var merged = new List<DisplayKaraokeSegment>();
        var current = new DisplayKaraokeSegment
        {
            Text = rawSegments[0].Text,
            ForegroundBrush = rawSegments[0].ForegroundBrush
        };

        for (int i = 1; i < rawSegments.Count; i++)
        {
            if (SameBrush(current.ForegroundBrush, rawSegments[i].ForegroundBrush))
            {
                current.Text += rawSegments[i].Text;
            }
            else
            {
                merged.Add(current);
                current = new DisplayKaraokeSegment
                {
                    Text = rawSegments[i].Text,
                    ForegroundBrush = rawSegments[i].ForegroundBrush
                };
            }
        }

        merged.Add(current);
        return merged;
    }

    void ReplaceVisibleLyrics(IEnumerable<DisplayLyricLine> lines)
    {
        VisibleLyrics.Clear();
        foreach (var line in lines)
            VisibleLyrics.Add(line);
    }

    void SetOverlayMessage(string message)
    {
        ReplaceVisibleLyrics(new[]
        {
            new DisplayLyricLine
            {
                Text = string.IsNullOrWhiteSpace(message) ? "..." : message,
                DistanceFromCurrent = 0,
                IsKaraokeLine = false,
                Segments = new List<DisplayKaraokeSegment>()
            }
        });
    }

    void SetupTrayIcon()
    {
        AppLogger.Log("SetupTrayIcon begin");

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Lyrics Overlay"
        };

        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("Show", null, (_, __) =>
        {
            AppLogger.Log("Tray menu: Show clicked");
            ShowOverlay();
        });

        menu.Items.Add("Hide", null, (_, __) =>
        {
            AppLogger.Log("Tray menu: Hide clicked");
            Hide();
        });

        menu.Items.Add(new Forms.ToolStripSeparator());

        var draggableItem = new Forms.ToolStripMenuItem("Draggable")
        {
            CheckOnClick = true,
            Checked = _isDraggable
        };
        draggableItem.CheckedChanged += (_, __) =>
        {
            _isDraggable = draggableItem.Checked;
            AppLogger.Log($"Tray menu: Draggable changed to {_isDraggable}");
            ApplyInteractionMode();
        };
        menu.Items.Add(draggableItem);

        var resizableItem = new Forms.ToolStripMenuItem("Resizable")
        {
            CheckOnClick = true,
            Checked = _isResizable
        };
        resizableItem.CheckedChanged += (_, __) =>
        {
            _isResizable = resizableItem.Checked;
            AppLogger.Log($"Tray menu: Resizable changed to {_isResizable}");
            ApplyInteractionMode();
        };
        menu.Items.Add(resizableItem);

        var textOnlyItem = new Forms.ToolStripMenuItem("Text Only Mode")
        {
            CheckOnClick = true,
            Checked = _textOnlyMode
        };
        textOnlyItem.CheckedChanged += (_, __) =>
        {
            _textOnlyMode = textOnlyItem.Checked;
            AppLogger.Log($"Tray menu: TextOnlyMode changed to {_textOnlyMode}");
            ApplyOverlayStyle();
        };
        menu.Items.Add(textOnlyItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, __) =>
        {
            AppLogger.Log("Tray menu: Exit clicked");
            ExitApplication();
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, __) =>
        {
            AppLogger.Log("Tray icon double-click");
            ShowOverlay();
        };

        AppLogger.Log("SetupTrayIcon end");
    }

    void ShowOverlay()
    {
        AppLogger.Log("ShowOverlay called");
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        AppLogger.Log($"OnClosing called, _isRealExit={_isRealExit}");

        if (!_isRealExit)
        {
            e.Cancel = true;
            Hide();
            AppLogger.Log("Close intercepted, window hidden to tray");
            return;
        }

        base.OnClosing(e);
    }

    void ExitApplication()
    {
        AppLogger.Log("ExitApplication begin");
        _isRealExit = true;
        SaveWindowSettings();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
            AppLogger.Log("Tray icon disposed");
        }

        WpfApp.Current.Shutdown();
        AppLogger.Log("Application shutdown requested");
    }

    void MakeClickThrough()
    {
        AppLogger.Log("MakeClickThrough begin");
        ApplyInteractionMode();
    }

    void ApplyInteractionMode()
    {
        AppLogger.Log($"ApplyInteractionMode begin | Draggable={_isDraggable} | Resizable={_isResizable}");

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (_isDraggable || _isResizable)
            exStyle &= ~WS_EX_TRANSPARENT;
        else
            exStyle |= WS_EX_TRANSPARENT;

        exStyle |= WS_EX_LAYERED;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        ResizeMode = _isResizable ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;

        AppLogger.Log($"ApplyInteractionMode end | exStyle={exStyle} | ResizeMode={ResizeMode}");
    }

    void ApplyOverlayStyle()
    {
        if (OverlayBorder == null)
            return;

        if (_textOnlyMode)
        {
            OverlayBorder.Background = System.Windows.Media.Brushes.Transparent;
            OverlayBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            OverlayBorder.BorderThickness = new Thickness(0);
            OverlayBorder.CornerRadius = new CornerRadius(0);
            OverlayBorder.Padding = new Thickness(4);
            AppLogger.Log("ApplyOverlayStyle -> Text only mode");
        }
        else
        {
            OverlayBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 0, 0));
            OverlayBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 255, 255, 255));
            OverlayBorder.BorderThickness = new Thickness(1);
            OverlayBorder.CornerRadius = new CornerRadius(8);
            OverlayBorder.Padding = new Thickness(10);
            AppLogger.Log("ApplyOverlayStyle -> Background mode");
        }
    }

    void LoadWindowSettings()
    {
        try
        {
            if (!System.IO.File.Exists(_windowSettingsPath))
            {
                AppLogger.Log("No saved window settings found, using defaults");
                Width = 900;
                Height = 220;
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
                Top = SystemParameters.PrimaryScreenHeight - Height - 120;
                return;
            }

            string json = System.IO.File.ReadAllText(_windowSettingsPath);
            var settings = JsonSerializer.Deserialize<WindowSettings>(json);
            if (settings == null)
                return;

            _restoringWindowSettings = true;

            Width = IsValidWindowNumber(settings.Width) && settings.Width > 0 ? settings.Width : 900;
            Height = IsValidWindowNumber(settings.Height) && settings.Height > 0 ? settings.Height : 220;
            Left = IsValidWindowNumber(settings.Left) ? settings.Left : (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = IsValidWindowNumber(settings.Top) ? settings.Top : (SystemParameters.PrimaryScreenHeight - Height - 120);

            AppLogger.Log($"Loaded window settings | Left={Left} | Top={Top} | Width={Width} | Height={Height}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LoadWindowSettings failed: {ex}");
        }
        finally
        {
            _restoringWindowSettings = false;
        }
    }

    void SaveWindowSettings()
    {
        try
        {
            if (_restoringWindowSettings)
                return;

            if (!IsLoaded)
                return;

            if (WindowState != WindowState.Normal)
            {
                AppLogger.Log($"Skipping SaveWindowSettings because WindowState={WindowState}");
                return;
            }

            if (!TryGetSafeWindowSettings(out var settings))
                return;

            var dir = System.IO.Path.GetDirectoryName(_windowSettingsPath)!;
            System.IO.Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            System.IO.File.WriteAllText(_windowSettingsPath, json);
            AppLogger.Log($"Saved window settings | Left={settings.Left} | Top={settings.Top} | Width={settings.Width} | Height={settings.Height}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"SaveWindowSettings failed: {ex}");
        }
    }

    bool TryGetSafeWindowSettings(out WindowSettings settings)
    {
        settings = new WindowSettings();

        double left = Left;
        double top = Top;
        double width = Width;
        double height = Height;

        if (!IsValidWindowNumber(left) ||
            !IsValidWindowNumber(top) ||
            !IsValidWindowNumber(width) ||
            !IsValidWindowNumber(height))
        {
            AppLogger.Log($"Skipping SaveWindowSettings due to invalid values | Left={left} | Top={top} | Width={width} | Height={height}");
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            AppLogger.Log($"Skipping SaveWindowSettings due to non-positive size | Width={width} | Height={height}");
            return false;
        }

        settings = new WindowSettings
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };

        return true;
    }

    static bool IsValidWindowNumber(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

public static class AppLogger
{
    public static void Log(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}

public class WindowSettings
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class DisplayLyricLine
{
    public string Text { get; set; } = "";
    public int DistanceFromCurrent { get; set; }
    public bool IsKaraokeLine { get; set; }
    public List<DisplayKaraokeSegment> Segments { get; set; } = new();
}

public class DisplayKaraokeSegment
{
    public string Text { get; set; } = "";
    public System.Windows.Media.Brush ForegroundBrush { get; set; } = System.Windows.Media.Brushes.White;
}

public class LineOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int d = value is int i ? i : 0;

        if (d < 0)
            return 0.55;

        if (d == 0)
            return 1.0;

        return 0.82;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class LineFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int d = value is int i ? i : 0;

        if (d < 0)
            return 24.0;

        if (d == 0)
            return 34.0;

        return 24.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class LineWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int d = value is int i ? i : 0;

        if (d == 0)
            return FontWeights.Bold;

        return FontWeights.Medium;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SpotifyAuth
{
    private const string ClientId = "3aab4e03b07f45ac900b9d3ef4d307fc";
    private const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string ListenerPrefix = "http://127.0.0.1:43821/callback/";
    private static string _codeVerifier = "";

    private static readonly string TokenFilePath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lyrics_overlay",
            "spotify_refresh_token.txt");

    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    public bool HasSavedRefreshToken()
    {
        bool exists = System.IO.File.Exists(TokenFilePath) &&
                      !string.IsNullOrWhiteSpace(System.IO.File.ReadAllText(TokenFilePath));
        AppLogger.Log($"SpotifyAuth.HasSavedRefreshToken -> {exists}");
        return exists;
    }

    public void LoadSavedRefreshToken()
    {
        AppLogger.Log($"SpotifyAuth.LoadSavedRefreshToken from {TokenFilePath}");

        if (System.IO.File.Exists(TokenFilePath))
        {
            RefreshToken = System.IO.File.ReadAllText(TokenFilePath).Trim();
            AppLogger.Log($"Loaded refresh token length={RefreshToken.Length}");
        }
    }

    private void SaveRefreshToken()
    {
        var dir = System.IO.Path.GetDirectoryName(TokenFilePath)!;
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(TokenFilePath, RefreshToken);
        AppLogger.Log($"Saved refresh token to {TokenFilePath}, length={RefreshToken.Length}");
    }

    public async Task LoginAsync()
    {
        AppLogger.Log("SpotifyAuth.LoginAsync begin");

        _codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(_codeVerifier);

        string authUrl =
            "https://accounts.spotify.com/authorize" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString("user-read-playback-state")}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}";

        AppLogger.Log($"Spotify auth URL prepared, redirect={RedirectUri}");

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);
        listener.Start();
        AppLogger.Log($"HTTP listener started on {ListenerPrefix}");

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        AppLogger.Log("Spotify auth browser launched");

        var context = await listener.GetContextAsync();
        string code = context.Request.QueryString["code"] ?? "";
        AppLogger.Log($"Spotify auth callback received, codeLength={code.Length}");

        byte[] responseBytes = Encoding.UTF8.GetBytes("Spotify auth complete. You can close this tab.");
        context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        context.Response.OutputStream.Close();

        using var http = new HttpClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("client_id", ClientId),
            new KeyValuePair<string,string>("grant_type", "authorization_code"),
            new KeyValuePair<string,string>("code", code),
            new KeyValuePair<string,string>("redirect_uri", RedirectUri),
            new KeyValuePair<string,string>("code_verifier", _codeVerifier)
        });

        var tokenResp = await http.PostAsync("https://accounts.spotify.com/api/token", content);
        AppLogger.Log($"Spotify token exchange HTTP status={(int)tokenResp.StatusCode}");
        tokenResp.EnsureSuccessStatusCode();

        var json = await tokenResp.Content.ReadAsStringAsync();
        AppLogger.Log($"Spotify token exchange response length={json.Length}");

        using var doc = JsonDocument.Parse(json);
        AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() ?? ""
            : "";

        AppLogger.Log($"Spotify access token length={AccessToken.Length}, refresh token length={RefreshToken.Length}");

        if (!string.IsNullOrWhiteSpace(RefreshToken))
            SaveRefreshToken();

        AppLogger.Log("SpotifyAuth.LoginAsync end");
    }

    public async Task RefreshAsync()
    {
        AppLogger.Log("SpotifyAuth.RefreshAsync begin");

        using var http = new HttpClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("client_id", ClientId),
            new KeyValuePair<string,string>("grant_type", "refresh_token"),
            new KeyValuePair<string,string>("refresh_token", RefreshToken)
        });

        var tokenResp = await http.PostAsync("https://accounts.spotify.com/api/token", content);
        AppLogger.Log($"Spotify refresh HTTP status={(int)tokenResp.StatusCode}");
        tokenResp.EnsureSuccessStatusCode();

        var json = await tokenResp.Content.ReadAsStringAsync();
        AppLogger.Log($"Spotify refresh response length={json.Length}");

        using var doc = JsonDocument.Parse(json);
        AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        AppLogger.Log($"Spotify refreshed access token length={AccessToken.Length}");

        if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
        {
            var newRefresh = rt.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(newRefresh))
            {
                RefreshToken = newRefresh;
                SaveRefreshToken();
                AppLogger.Log("Spotify refresh token rotated");
            }
        }

        AppLogger.Log("SpotifyAuth.RefreshAsync end");
    }

    private static string GenerateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(64);
        return Base64Url(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        byte[] bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public class SpotifyPlaybackState
{
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Uri { get; set; } = "";
    public int DurationMs { get; set; }
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
}

public class SpotifyClient
{
    public async Task<SpotifyPlaybackState?> GetPlaybackAsync(string accessToken)
    {
        AppLogger.Log($"SpotifyClient.GetPlaybackAsync begin, accessTokenLength={accessToken?.Length ?? 0}");

        var requestStart = Stopwatch.GetTimestamp();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var resp = await http.GetAsync("https://api.spotify.com/v1/me/player");

        var requestEnd = Stopwatch.GetTimestamp();
        var rttMs = (requestEnd - requestStart) * 1000.0 / Stopwatch.Frequency;
        AppLogger.Log($"Spotify /me/player HTTP status={(int)resp.StatusCode}, RTT={rttMs:F2} ms");

        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            AppLogger.Log("Spotify returned 204 NoContent");
            return null;
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            AppLogger.Log("Spotify returned 401 Unauthorized");
            throw new Exception("Token expired");
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            string body = await resp.Content.ReadAsStringAsync();
            AppLogger.Log($"Spotify returned 403 Forbidden. Body={body}");
            throw new Exception("Spotify playback API forbidden. Premium account is required, or the saved token belongs to a different account.");
        }

        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync();
        AppLogger.Log($"Spotify /me/player response length={json.Length}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null)
        {
            AppLogger.Log("Spotify response has no item");
            return null;
        }

        var artists = item.GetProperty("artists");
        string artist = artists[0].GetProperty("name").GetString() ?? "";

        var state = new SpotifyPlaybackState
        {
            TrackId = item.GetProperty("id").GetString() ?? "",
            Title = item.GetProperty("name").GetString() ?? "",
            Artist = artist,
            Album = item.GetProperty("album").GetProperty("name").GetString() ?? "",
            Uri = item.GetProperty("uri").GetString() ?? "",
            DurationMs = item.GetProperty("duration_ms").GetInt32(),
            ProgressMs = root.GetProperty("progress_ms").GetInt32(),
            IsPlaying = root.GetProperty("is_playing").GetBoolean()
        };

        state.ProgressMs = (int)Math.Round(state.ProgressMs + (rttMs / 2.0));

        AppLogger.Log($"Spotify parsed state: {state.Artist} - {state.Title} | TrackId={state.TrackId} | Album={state.Album} | Uri={state.Uri} | DurationMs={state.DurationMs} | ProgressMs={state.ProgressMs} | IsPlaying={state.IsPlaying}");
        return state;
    }
}

public sealed class SpotifyWebApiPlaybackSource : IPlaybackSource
{
    private readonly SpotifyAuth _auth = new();
    private readonly SpotifyClient _spotify = new();

    public string SourceName => "Spotify Web API";
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            if (_auth.HasSavedRefreshToken())
            {
                AppLogger.Log("SpotifyWebApiPlaybackSource: saved refresh token found");
                _auth.LoadSavedRefreshToken();
                await _auth.RefreshAsync();
            }
            else
            {
                AppLogger.Log("SpotifyWebApiPlaybackSource: no saved refresh token, starting login flow");
                await _auth.LoginAsync();
            }

            IsAvailable = true;
            AppLogger.Log("SpotifyWebApiPlaybackSource initialized successfully");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AppLogger.Log($"SpotifyWebApiPlaybackSource initialization failed: {ex}");
        }
    }

    public async Task<SpotifyPlaybackState?> GetPlaybackAsync()
    {
        if (!IsAvailable)
            return null;

        try
        {
            return await _spotify.GetPlaybackAsync(_auth.AccessToken);
        }
        catch (Exception ex) when (ex.Message.Contains("Token expired", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log("SpotifyWebApiPlaybackSource: token expired, refreshing");
            await _auth.RefreshAsync();
            return await _spotify.GetPlaybackAsync(_auth.AccessToken);
        }
        catch (Exception ex) when (
            ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("premium", StringComparison.OrdinalIgnoreCase))
        {
            IsAvailable = false;
            AppLogger.Log($"SpotifyWebApiPlaybackSource unavailable: {ex.Message}");
            return null;
        }
    }
}

public sealed class SmtcPlaybackSource : IPlaybackSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public string SourceName => "SMTC";
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged -= Manager_SessionsChanged;
            _manager.SessionsChanged += Manager_SessionsChanged;

            AttachSession(_manager.GetCurrentSession());

            IsAvailable = _session != null;
            AppLogger.Log($"SmtcPlaybackSource initialized. SessionAvailable={IsAvailable}");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AppLogger.Log($"SmtcPlaybackSource initialization failed: {ex.Message}");
        }
    }

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try
        {
            AttachSession(sender.GetCurrentSession());
            IsAvailable = _session != null;
            AppLogger.Log($"SMTC sessions changed. SessionAvailable={IsAvailable}");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AppLogger.Log($"SMTC sessions changed handler failed: {ex.Message}");
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }

        _session = session;

        if (_session != null)
        {
            _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        AppLogger.Log("SMTC media properties changed");
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        AppLogger.Log("SMTC playback info changed");
    }

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        AppLogger.Log("SMTC timeline changed");
    }

    public async Task<SpotifyPlaybackState?> GetPlaybackAsync()
    {
        try
        {
            if (_manager == null)
            {
                await InitializeAsync();
                if (_manager == null)
                    return null;
            }

            if (_session == null)
            {
                AttachSession(_manager.GetCurrentSession());
                IsAvailable = _session != null;
            }

            if (_session == null)
            {
                AppLogger.Log("SMTC GetPlaybackAsync: no active session");
                return null;
            }

            var props = await _session.TryGetMediaPropertiesAsync();
            var pbInfo = _session.GetPlaybackInfo();
            var tlInfo = _session.GetTimelineProperties();

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            string album = props?.AlbumTitle ?? "";

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            {
                AppLogger.Log("SMTC GetPlaybackAsync: active session has no usable title/artist");
                return null;
            }

            bool isPlaying = pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TimeSpan currentPosition = tlInfo.Position;
            if (isPlaying)
            {
                double rate = pbInfo.PlaybackRate ?? 1.0;
                var elapsed = DateTimeOffset.Now - tlInfo.LastUpdatedTime;
                currentPosition = tlInfo.Position + TimeSpan.FromSeconds(elapsed.TotalSeconds * rate);

                if (currentPosition < TimeSpan.Zero)
                    currentPosition = TimeSpan.Zero;

                if (tlInfo.EndTime > TimeSpan.Zero && currentPosition > tlInfo.EndTime)
                    currentPosition = tlInfo.EndTime;
            }

            int progressMs = (int)Math.Max(0, Math.Round(currentPosition.TotalMilliseconds));
            int durationMs = (int)Math.Max(0, Math.Round(tlInfo.EndTime.TotalMilliseconds));

            string trackId = BuildFallbackTrackId(artist, title, album, durationMs);

            var state = new SpotifyPlaybackState
            {
                TrackId = trackId,
                Title = title,
                Artist = artist,
                Album = album,
                Uri = "",
                DurationMs = durationMs,
                ProgressMs = progressMs,
                IsPlaying = isPlaying
            };

            IsAvailable = true;

            AppLogger.Log(
                $"SMTC parsed state {state.Artist} - {state.Title} " +
                $"TrackId={state.TrackId} Album={state.Album} Uri={state.Uri} " +
                $"DurationMs={state.DurationMs} ProgressMs={state.ProgressMs} IsPlaying={state.IsPlaying}");

            return state;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"SmtcPlaybackSource GetPlaybackAsync failed: {ex.Message}");
            IsAvailable = false;
            return null;
        }
    }

    private static string BuildFallbackTrackId(string artist, string title, string album, int durationMs)
    {
        string raw = $"{artist}|{title}|{album}|{durationMs}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}