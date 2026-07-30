using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace lyrics_overlay;

/// <summary>Dedicated view for the in-memory application log.</summary>
public sealed class LogViewerWindow : Window
{
    private readonly TextBlock _statusText;
    private readonly TextBox _logTextBox;
    private readonly Button _pauseButton;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshPending;

    public LogViewerWindow(Window owner)
    {
        Title = "Active Log";
        Width = 900;
        Height = 500;
        Owner = owner;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _statusText = new TextBlock { Margin = new Thickness(10, 10, 10, 6) };
        Grid.SetRow(_statusText, 0);
        grid.Children.Add(_statusText);

        _logTextBox = new TextBox
        {
            Margin = new Thickness(10, 0, 10, 10),
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            IsUndoEnabled = false,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        Grid.SetRow(_logTextBox, 1);
        grid.Children.Add(_logTextBox);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 10, 10)
        };

        buttons.Children.Add(CreateButton("Copy", (_, __) => CopyLog(), true));
        _pauseButton = CreateButton(AppLogger.IsMemoryLoggingPaused ? "Resume Logging" : "Pause Logging", (_, __) => ToggleLogging(), true);
        buttons.Children.Add(_pauseButton);
        buttons.Children.Add(CreateButton("Clear", (_, __) => AppLogger.Clear(), true));
        buttons.Children.Add(CreateButton("Close", (_, __) => Hide(), false));

        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, __) =>
        {
            _refreshTimer.Stop();
            _refreshPending = false;
            Refresh();
        };

        Closed += (_, __) =>
        {
            _refreshTimer.Stop();
            AppLogger.LogChanged -= RequestRefresh;
        };
        IsVisibleChanged += (_, __) =>
        {
            if (IsVisible)
                Refresh();
        };
        AppLogger.LogChanged += RequestRefresh;
    }

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (!IsVisible)
                return;

            _logTextBox.Text = AppLogger.GetLogText();
            _logTextBox.CaretIndex = _logTextBox.Text.Length;
            _logTextBox.ScrollToEnd();

            double mib = AppLogger.GetStoredBytes() / 1024.0 / 1024.0;
            string state = AppLogger.IsMemoryLoggingPaused ? "Paused" : "Recording";
            _statusText.Text = $"State: {state} | Entries: {AppLogger.GetEntryCount()} | Memory: {mib:F2} MiB / 1.00 MiB";
            _pauseButton.Content = AppLogger.IsMemoryLoggingPaused ? "Resume Logging" : "Pause Logging";
        });
    }

    private void RequestRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RequestRefresh);
            return;
        }

        if (_refreshPending)
            return;

        _refreshPending = true;
        _refreshTimer.Start();
    }

    private static Button CreateButton(string content, RoutedEventHandler click, bool addRightMargin)
    {
        var button = new Button
        {
            Content = content,
            Margin = addRightMargin ? new Thickness(0, 0, 8, 0) : new Thickness(0),
            Padding = new Thickness(12, 4, 12, 4)
        };
        button.Click += click;
        return button;
    }

    private void CopyLog()
    {
        string text = !string.IsNullOrEmpty(_logTextBox.SelectedText) ? _logTextBox.SelectedText : _logTextBox.Text;
        if (!string.IsNullOrEmpty(text))
            System.Windows.Clipboard.SetText(text);
    }

    private void ToggleLogging()
    {
        AppLogger.SetMemoryLoggingPaused(!AppLogger.IsMemoryLoggingPaused);
        Refresh();
    }
}
