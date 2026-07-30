using System;
using System.Collections.Generic;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace lyrics_overlay;

/// <summary>
/// Converts timed karaoke words into presentation-ready, colour-coded segments.
/// Keeping this animation logic outside the window makes it reusable and keeps
/// MainWindow focused on binding the result to the overlay.
/// </summary>
public sealed class KaraokeSegmentRenderer
{
    private static readonly Color SungWordColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color ActiveWordStartColor = Color.FromArgb(170, 255, 255, 255);
    private static readonly Color ActiveWordEndColor = Color.FromArgb(255, 255, 230, 120);
    private static readonly Color UpcomingWordColor = Color.FromArgb(120, 255, 255, 255);

    private static readonly SolidColorBrush SungWordBrush = CreateFrozenBrush(SungWordColor);
    private static readonly SolidColorBrush UpcomingWordBrush = CreateFrozenBrush(UpcomingWordColor);
    private static readonly Dictionary<Color, SolidColorBrush> ActiveWordBrushes = new();

    public List<DisplayKaraokeSegment> Build(KaraokeLine line, int progressMs)
    {
        var segments = new List<DisplayKaraokeSegment>();

        if (line.Words == null || line.Words.Count == 0)
        {
            segments.Add(new DisplayKaraokeSegment { Text = "", ForegroundBrush = UpcomingWordBrush });
            return segments;
        }

        for (int index = 0; index < line.Words.Count; index++)
        {
            var word = line.Words[index];
            string text = word.RomanizedWord ?? word.Word ?? "";
            if (string.IsNullOrEmpty(text))
                continue;

            int wordStartMs = line.StartTimeMs + word.OffsetMs;
            int wordEndMs = GetWordEndTime(line, index, wordStartMs);

            if (progressMs < wordStartMs)
            {
                segments.Add(new DisplayKaraokeSegment { Text = text, ForegroundBrush = UpcomingWordBrush });
            }
            else if (progressMs >= wordEndMs)
            {
                segments.Add(new DisplayKaraokeSegment { Text = text, ForegroundBrush = SungWordBrush });
            }
            else
            {
                AddActiveWordSegments(segments, text, wordStartMs, wordEndMs, progressMs);
            }
        }

        return MergeAdjacentSegments(segments);
    }

    private static int GetWordEndTime(KaraokeLine line, int index, int wordStartMs)
    {
        var word = line.Words[index];
        int wordEndMs = word.DurationMs > 0
            ? wordStartMs + word.DurationMs
            : index + 1 < line.Words.Count
                ? line.StartTimeMs + line.Words[index + 1].OffsetMs
                : line.EndTimeMs > wordStartMs ? line.EndTimeMs : wordStartMs + 900;

        return wordEndMs <= wordStartMs ? wordStartMs + 120 : wordEndMs;
    }

    private static void AddActiveWordSegments(List<DisplayKaraokeSegment> segments, string text, int startMs, int endMs, int progressMs)
    {
        double progress = EaseInOutSine((double)(progressMs - startMs) / (endMs - startMs));
        double characterProgress = progress * text.Length;
        int completedCharacters = Math.Clamp((int)Math.Floor(characterProgress), 0, text.Length);

        if (completedCharacters > 0)
            segments.Add(new DisplayKaraokeSegment { Text = text[..completedCharacters], ForegroundBrush = SungWordBrush });

        if (completedCharacters >= text.Length)
            return;

        double activeCharacterProgress = characterProgress - completedCharacters;
        segments.Add(new DisplayKaraokeSegment
        {
            Text = text.Substring(completedCharacters, 1),
            ForegroundBrush = GetActiveWordBrush(LerpColor(ActiveWordStartColor, ActiveWordEndColor, activeCharacterProgress))
        });

        if (completedCharacters + 1 < text.Length)
            segments.Add(new DisplayKaraokeSegment { Text = text[(completedCharacters + 1)..], ForegroundBrush = UpcomingWordBrush });
    }

    private static List<DisplayKaraokeSegment> MergeAdjacentSegments(List<DisplayKaraokeSegment> segments)
    {
        if (segments.Count <= 1)
            return segments;

        var merged = new List<DisplayKaraokeSegment>();
        var current = new DisplayKaraokeSegment { Text = segments[0].Text, ForegroundBrush = segments[0].ForegroundBrush };

        for (int index = 1; index < segments.Count; index++)
        {
            if (SameBrush(current.ForegroundBrush, segments[index].ForegroundBrush))
                current.Text += segments[index].Text;
            else
            {
                merged.Add(current);
                current = new DisplayKaraokeSegment { Text = segments[index].Text, ForegroundBrush = segments[index].ForegroundBrush };
            }
        }

        merged.Add(current);
        return merged;
    }

    private static bool SameBrush(Brush first, Brush second) =>
        ReferenceEquals(first, second) ||
        (first is SolidColorBrush firstColor && second is SolidColorBrush secondColor && firstColor.Color == secondColor.Color);

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetActiveWordBrush(Color color)
    {
        if (ActiveWordBrushes.TryGetValue(color, out var brush))
            return brush;

        brush = CreateFrozenBrush(color);
        ActiveWordBrushes[color] = brush;
        return brush;
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Round(from.A + ((to.A - from.A) * amount)),
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static double EaseInOutSine(double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return -(Math.Cos(Math.PI * amount) - 1.0) / 2.0;
    }
}
