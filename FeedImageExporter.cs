using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dota2ChatExtractor;

internal static class FeedImageExporter
{
    private const int Width = 1180;
    private const int MaxHeight = 10_000;
    private const double Margin = 32;
    private const double HeaderHeight = 112;
    private const double MatchHeight = 70;
    private const double MessageX = 144;

    public static Task<int> SaveAsync(IReadOnlyList<Transcript> transcripts, uint account, string path)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Save(transcripts, account, path)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static int Save(IReadOnlyList<Transcript> transcripts, uint account, string path)
    {
        var pages = Paginate(transcripts);
        var names = Enumerable.Range(1, pages.Count).Select(part => part == 1 ? path
            : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{part:000}.png")).ToArray();
        if (names.Skip(1).Any(File.Exists))
            throw new IOException(UiText.Get("PngPartExists"));

        var temporary = names.Select(name => $"{name}.{Guid.NewGuid():N}.tmp").ToArray();
        var committed = new List<string>();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var mode = transcripts[0].AllAuthors ? UiText.Get("AllPlayers") : UiText.Get("PlayerMessages");
                using var output = File.Create(temporary[i]);
                Render(pages[i], UiText.Get("FeedMetadata", account, mode, transcripts.Count), i + 1, pages.Count).Save(output);
            }

            for (var i = 1; i < names.Length; i++)
            {
                File.Move(temporary[i], names[i]);
                committed.Add(names[i]);
            }
            File.Move(temporary[0], names[0], overwrite: true);
            return pages.Count;
        }
        catch
        {
            foreach (var file in committed)
                TryDelete(file);
            throw;
        }
        finally
        {
            foreach (var file in temporary)
                TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<Page> Paginate(IReadOnlyList<Transcript> transcripts)
    {
        var pages = new List<Page> { new() };
        foreach (var match in transcripts)
        {
            var matchHeader = UiText.Get("MatchTitle", match.MatchId);
            var needsHeader = true;
            foreach (var row in match.Messages)
            {
                var height = Math.Ceiling(MessageText(row).Height) + 12;
                var page = pages[^1];
                if (page.Height + height + (needsHeader ? MatchHeight : 0) > MaxHeight - Margin)
                {
                    page = new Page();
                    pages.Add(page);
                    page.Add(new Line(needsHeader ? matchHeader : UiText.Get("Continued", matchHeader), match.Metadata, null, MatchHeight));
                    needsHeader = false;
                }
                if (needsHeader)
                {
                    page.Add(new Line(matchHeader, match.Metadata, null, MatchHeight));
                    needsHeader = false;
                }
                page.Add(new Line(null, null, row, height));
            }
        }
        return pages;
    }

    private static PngBitmapEncoder Render(Page page, string metadata, int part, int parts)
    {
        var height = (int)Math.Ceiling(page.Height + Margin);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(25, 39, 39), Color.FromRgb(13, 18, 24), 53);
            context.DrawRectangle(background, null, new Rect(0, 0, Width, height));
            context.DrawRectangle(ColorBrush("#CE5946"), null, new Rect(0, 0, Width, 4));
            context.DrawText(Text("DOTA 2", 16, "#F0927B", bold: true), new Point(Margin, 21));
            context.DrawText(Text(UiText.Get("ExportFeedTitle"), 32, "#FFF3D2", bold: true), new Point(Margin, 42));
            var pageLabel = parts > 1 ? $"  ·  {UiText.Get("FeedPart", part, parts)}" : "";
            context.DrawText(Text(metadata + pageLabel, 15, "#B6C8C4"), new Point(Margin + 2, 85));

            var y = HeaderHeight;
            foreach (var line in page.Lines)
            {
                if (line.Message is { } row)
                {
                    var rowText = MessageText(row);
                    if (row.Kind == MessageKind.FinalHeading)
                        context.DrawRectangle(ColorBrush("#263334"), null, new Rect(Margin, y, Width - 2 * Margin, line.Height));
                    if (row.Clock.Length > 0)
                        context.DrawText(Text(row.Clock, 15, "#A5B7B5"), new Point(Margin + 12, y + 5));
                    if (row.Hero?.Icon is { } icon)
                        context.DrawImage(icon, new Rect(MessageX - 30, y + 3, 23, 23));
                    else if (row.Kind == MessageKind.Chat || row.PlayerSlot.HasValue)
                        context.DrawRectangle(DotaSlotColors.For(row.PlayerSlot) ?? ColorBrush("#C1D3D3"), null,
                            new Rect(MessageX - 17, y + 10, 7, 7));
                    context.DrawText(rowText, new Point(MessageX, y + 3));
                }
                else
                {
                    context.DrawRectangle(ColorBrush("#263334"), null, new Rect(Margin, y + 7, Width - 2 * Margin, 53));
                    context.DrawRectangle(ColorBrush("#CE5946"), null, new Rect(Margin, y + 7, 3, 53));
                    context.DrawText(Text(line.Title!, 19, "#FFF3D2", bold: true), new Point(Margin + 13, y + 12));
                    context.DrawText(Text(line.Metadata!, 13, "#B6C8C4"), new Point(Margin + 13, y + 36));
                }
                y += line.Height;
            }
        }

        var bitmap = new RenderTargetBitmap(Width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        return png;
    }

    private static FormattedText MessageText(MessageView row)
    {
        if (row.Kind == MessageKind.Interlude)
        {
            var parts = row.BodyParts;
            var interlude = WrappedText(string.Concat(parts.Select(part => part.Text)), 17, "#C2CFC8", italic: true);
            var offset = 0;
            foreach (var part in parts)
            {
                if (part.Text.Length > 0 && DotaSlotColors.For(part.PlayerSlot) is { } brush)
                    interlude.SetForegroundBrush(brush, offset, part.Text.Length);
                offset += part.Text.Length;
            }
            return interlude;
        }
        if (row.Kind == MessageKind.FinalHeading)
            return WrappedText($"{row.Author} · {row.Text}", 18, "#FFF3D2", bold: true);

        var score = row.Score is null ? "" : $"  [{row.Score}]";
        var hero = row.Hero is null ? "" : $" · {row.Hero.DisplayName}";
        var caption = row.Kind == MessageKind.Chat ? $"{row.Author}{hero}:" : $"{row.Author}{hero}";
        var text = WrappedText($"{caption}{score}  {row.Text}", 20, "#FFF3D2", bold: true);
        if (row.Author.Length > 0 && DotaSlotColors.For(row.PlayerSlot) is { } color)
            text.SetForegroundBrush(color, 0, row.Author.Length);
        if (hero.Length > 0)
            text.SetForegroundBrush(ColorBrush("#B6C8C4"), row.Author.Length, hero.Length);
        if (score.Length > 0)
            text.SetForegroundBrush(ColorBrush("#B6C8C4"), caption.Length, score.Length);
        return text;
    }

    private static FormattedText WrappedText(string text, double size, string color, bool bold = false, bool italic = false)
    {
        var formatted = Text(text, size, color, bold, italic);
        formatted.MaxTextWidth = Width - MessageX - Margin;
        return formatted;
    }

    private static FormattedText Text(string text, double size, string color, bool bold = false, bool italic = false) =>
        new(text, UiText.Culture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, ColorBrush(color), 1);

    private static SolidColorBrush ColorBrush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private sealed record Line(string? Title, string? Metadata, MessageView? Message, double Height);

    private sealed class Page
    {
        public List<Line> Lines { get; } = [];
        public double Height { get; private set; } = HeaderHeight;

        public void Add(Line line)
        {
            Lines.Add(line);
            Height += line.Height;
        }
    }
}
