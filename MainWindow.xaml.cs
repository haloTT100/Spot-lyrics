
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

namespace lyrics_overlay;

public partial class MainWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly SpotifyAuth _auth = new();
    private readonly SpotifyClient _spotify = new();
    private readonly MusixmatchClient _musixmatch = new();

    private Forms.NotifyIcon? _trayIcon;
    private bool _isRealExit;
    private bool _isDraggable = false;
    private bool _isResizable = false;
    private bool _textOnlyMode = true;
    private List<SyncedLyricLine> _syncedLyrics = new();
    private List<KaraokeLine> _karaokeLyrics = new();
    private DispatcherTimer? _spotifyPollTimer;
    private string _currentTrackId = "";
    private string _lastDisplayedText = "";
    private bool _pollInProgress = false;
    private bool _currentTrackHasNoLyrics = false;
    private readonly Dictionary<string, List<SyncedLyricLine>> _lyricsCache = new();
    private readonly Dictionary<string, List<KaraokeLine>> _karaokeCache = new();
    private readonly HashSet<string> _noLyricsCache = new();

    public List<DisplayLyricLine> VisibleLyrics { get; set; } = new();

    private readonly string _windowSettingsPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lyrics_overlay",
            "window_settings.json");

    private bool _restoringWindowSettings = false;
    private int _lastProgressMs = 0;

    private static readonly System.Windows.Media.SolidColorBrush SungWordBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
    private static readonly System.Windows.Media.SolidColorBrush ActiveWordBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(255, 255, 230, 120));
    private static readonly System.Windows.Media.SolidColorBrush UpcomingWordBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(150, 255, 255, 255));

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
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

                if (_auth.HasSavedRefreshToken())
                {
                    AppLogger.Log("Saved Spotify refresh token found");
                    _auth.LoadSavedRefreshToken();
                    await _auth.RefreshAsync();
                }
                else
                {
                    AppLogger.Log("No saved Spotify refresh token, starting login flow");
                    await _auth.LoginAsync();
                }

                await _musixmatch.EnsureTokenAsync();

                StartSpotifyPolling();

                var state = await _spotify.GetPlaybackAsync(_auth.AccessToken);
                if (state != null)
                {
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

    static System.Windows.Media.SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
            if (_pollInProgress)
            {
                AppLogger.Log("Polling tick skipped because previous poll is still running");
                return;
            }

            _pollInProgress = true;

            try
            {
                AppLogger.Log("Polling tick start");

                var state = await _spotify.GetPlaybackAsync(_auth.AccessToken);
                if (state == null)
                {
                    AppLogger.Log("Spotify state is null / nothing currently playing");
                    _currentTrackId = "";
                    _syncedLyrics.Clear();
                    _karaokeLyrics.Clear();
                    _currentTrackHasNoLyrics = false;
                    _lastProgressMs = 0;
                    SetOverlayMessage("Nothing is currently playing.");
                    return;
                }

                _lastProgressMs = state.ProgressMs;

                AppLogger.Log($"Spotify state: TrackId={state.TrackId} | Artist={state.Artist} | Title={state.Title} | Album={state.Album} | Uri={state.Uri} | DurationMs={state.DurationMs} | ProgressMs={state.ProgressMs} | IsPlaying={state.IsPlaying}");

                if (state.TrackId != _currentTrackId)
                {
                    AppLogger.Log($"Track change detected. OldTrackId={_currentTrackId}, NewTrackId={state.TrackId}");

                    _currentTrackId = state.TrackId;
                    _lastDisplayedText = "";
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
                    AppLogger.Log("Spotify is paused, not advancing lyric");
                    if (_currentTrackHasNoLyrics)
                        SetOverlayMessage($"{state.Artist} - {state.Title}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Polling exception: {ex}");

                if (ex.Message.Contains("Token expired", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        AppLogger.Log("Spotify token expired, attempting refresh");
                        await _auth.RefreshAsync();
                        AppLogger.Log("Spotify token refresh succeeded");
                    }
                    catch (Exception refreshEx)
                    {
                        AppLogger.Log($"Spotify token refresh failed: {refreshEx}");
                        SetOverlayMessage($"Spotify refresh failed: {refreshEx.Message}");
                    }

                    return;
                }

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

        VisibleLyrics = lines;
        LyricsItemsControl.ItemsSource = null;
        LyricsItemsControl.ItemsSource = VisibleLyrics;
    }

    void RefreshVisibleKaraokeLyrics(int progressMs)
    {
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
            bool isCurrent = i == currentIndex;

            lines.Add(new DisplayLyricLine
            {
                Text = string.IsNullOrWhiteSpace(line.FullText) ? " " : line.FullText,
                DistanceFromCurrent = i - currentIndex,
                IsKaraokeLine = true,
                Segments = BuildKaraokeSegments(line, progressMs, isCurrent)
            });
        }

        string currentDisplay = _karaokeLyrics[currentIndex].FullText;
        if (!string.Equals(currentDisplay, _lastDisplayedText, StringComparison.Ordinal))
        {
            _lastDisplayedText = currentDisplay;
            AppLogger.Log($"Displaying karaoke currentIndex={currentIndex}, previousLines={previousLinesToShow}, nextLines={end - currentIndex}, current='{_lastDisplayedText}'");
        }

        VisibleLyrics = lines;
        LyricsItemsControl.ItemsSource = null;
        LyricsItemsControl.ItemsSource = VisibleLyrics;
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

    List<DisplayKaraokeSegment> BuildKaraokeSegments(KaraokeLine line, int progressMs, bool isCurrentLine)
    {
        if (line.Words.Count == 0)
        {
            return new List<DisplayKaraokeSegment>
            {
                new DisplayKaraokeSegment
                {
                    Text = string.IsNullOrWhiteSpace(line.FullText) ? " " : line.FullText,
                    ForegroundBrush = isCurrentLine ? ActiveWordBrush : UpcomingWordBrush
                }
            };
        }

        int relativeMs = Math.Max(0, progressMs - line.StartTimeMs);
        var rawSegments = new List<DisplayKaraokeSegment>();

        foreach (var word in line.Words)
        {
            bool completed = relativeMs >= word.OffsetMs + word.DurationMs;
            bool active = relativeMs >= word.OffsetMs && relativeMs < word.OffsetMs + word.DurationMs;

            System.Windows.Media.Brush brush;
            if (!isCurrentLine)
                brush = line.StartTimeMs < progressMs ? SungWordBrush : UpcomingWordBrush;
            else if (completed)
                brush = SungWordBrush;
            else if (active)
                brush = ActiveWordBrush;
            else
                brush = UpcomingWordBrush;

            rawSegments.Add(new DisplayKaraokeSegment
            {
                Text = word.Word,
                ForegroundBrush = brush
            });
        }

        return MergeSegments(rawSegments);
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
            if (ReferenceEquals(current.ForegroundBrush, rawSegments[i].ForegroundBrush))
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

    void SetOverlayMessage(string message)
    {
        VisibleLyrics = new List<DisplayLyricLine>
        {
            new DisplayLyricLine
            {
                Text = string.IsNullOrWhiteSpace(message) ? " " : message,
                DistanceFromCurrent = 0,
                IsKaraokeLine = false,
                Segments = new List<DisplayKaraokeSegment>()
            }
        };

        LyricsItemsControl.ItemsSource = null;
        LyricsItemsControl.ItemsSource = VisibleLyrics;
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
