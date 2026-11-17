using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dota2ChatExtractor;

public sealed record TranscriptPart(string Text, int? PlayerSlot = null);

internal static class DotaSlotColors
{
    // The OpenDota palette uses Valve's player_slot values for both teams.
    private static readonly IReadOnlyDictionary<int, SolidColorBrush> Colors = new Dictionary<int, SolidColorBrush>
    {
        [0] = ColorOf(0x3375FF), [1] = ColorOf(0x66FFBF), [2] = ColorOf(0xBF00BF),
        [3] = ColorOf(0xF3F00B), [4] = ColorOf(0xFF6B00), [128] = ColorOf(0xFE86C2),
        [129] = ColorOf(0xA1B447), [130] = ColorOf(0x65D9F7), [131] = ColorOf(0x008321), [132] = ColorOf(0xA46900)
    };

    public static SolidColorBrush? For(int? slot) => slot is { } value ? Colors.GetValueOrDefault(value) : null;

    private static SolidColorBrush ColorOf(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}

public sealed class TranscriptText : TextBlock
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<TranscriptPart>), typeof(TranscriptText), new PropertyMetadata(null, RebuildRuns));

    public static readonly DependencyProperty SlotBackgroundProperty = DependencyProperty.Register(
        nameof(SlotBackground), typeof(Brush), typeof(TranscriptText), new PropertyMetadata(null, RebuildRuns));

    public IReadOnlyList<TranscriptPart>? Parts
    {
        get => (IReadOnlyList<TranscriptPart>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public Brush? SlotBackground
    {
        get => (Brush?)GetValue(SlotBackgroundProperty);
        set => SetValue(SlotBackgroundProperty, value);
    }

    private static void RebuildRuns(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var block = (TranscriptText)element;
        block.Inlines.Clear();
        foreach (var part in block.Parts ?? [])
        {
            var run = new Run(part.Text);
            if (DotaSlotColors.For(part.PlayerSlot) is { } color)
            {
                run.Foreground = color;
                run.Background = block.SlotBackground;
            }
            block.Inlines.Add(run);
        }
    }
}
