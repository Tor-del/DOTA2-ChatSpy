using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Dota2ChatExtractor;

public partial class MainWindow : Window
{
    private const int PageSize = 25;
    private readonly OpenDotaClient _api = new();
    private readonly ObservableCollection<MatchView> _matches = [];
    private readonly ObservableCollection<Transcript> _feed = [];
    private readonly HashSet<long> _matchIds = [];
    private CancellationTokenSource? _operation;
    private uint _account;
    private int _offset;
    private bool _hasMore;
    private bool _closed;
    private bool _startFeedWhenIdle;
    private bool _feedAutoPage = true;
    private bool _refreshTimelinesWhenIdle;
    private bool _exportingFeed;
    private uint[] _accounts = [];
    private readonly string _accountHistory = Path.Combine(LocalData.DirectoryPath, "accounts.json");

    public MainWindow()
    {
        UiText.ApplyLanguage(russian: false);
        InitializeComponent();
        MatchList.ItemsSource = _matches;
        FeedList.ItemsSource = _feed;
        LanguagePicker.SelectionChanged += LanguagePicker_SelectionChanged;
        UpdateControls();
    }

    private MatchView? SelectedMatch => MatchList.SelectedItem as MatchView;
    private bool InFeed => FeedMode.IsChecked == true;
    private bool WithScore => ShowScore.IsChecked == true;
    private bool HasPendingChats => _matches.Any(CanLoadChat);

    private static bool CanLoadChat(MatchView match) => !match.IsLoaded && !match.NotFound;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        var accounts = await LocalData.ReadAsync<uint[]>(_accountHistory, TimeSpan.MaxValue, CancellationToken.None) ?? [];
        _accounts = _accounts.Concat(accounts).Where(account => account is not (0 or uint.MaxValue)).Distinct().ToArray();
        if (_closed)
            return;
        UpdateControls();
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAccountAsync();

    private async void AccountInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await LoadAccountAsync();
    }

    private async Task LoadAccountAsync()
    {
        if (!uint.TryParse(AccountInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var account) ||
            account is 0 or uint.MaxValue)
        {
            Status.Text = UiText.Get("InvalidId");
            AccountInput.Focus();
            return;
        }

        await RunAsync(UiText.Get("LoadingMatches"), async token =>
        {
            if (_accounts.FirstOrDefault() != account)
            {
                _accounts = [account, .. _accounts.Where(previous => previous != account)];
                await LocalData.WriteAsync(_accountHistory, _accounts, token);
            }
            var page = await _api.GetMatchesAsync(account, PageSize, 0, token);
            _account = account;
            _feed.Clear();
            ScrollFeedToTop();
            _feedAutoPage = true;
            _matches.Clear();
            _matchIds.Clear();
            _offset = 0;
            AddPage(page);

            // Start on the newest match with a known replay parse, if the page contains one.
            var first = _matches.FirstOrDefault(match => match.IsParsed) ?? _matches.FirstOrDefault();
            MatchList.SelectedItem = first;
            if (InFeed && first is not null)
                await LoadPendingChatsAsync(token);
            else if (first is not null)
            {
                MatchList.ScrollIntoView(first);
                Status.Text = UiText.Get("LoadingMatchChat", first.Id);
                await LoadChatAsync(first, token);
                Status.Text = UiText.Get("MatchesLoaded", _matches.Count);
            }
            else
                Status.Text = UiText.Get("NoPublicMatches", account);
        });
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = HistoryButton, MaxHeight = 420 };
        foreach (var account in _accounts)
        {
            var item = new MenuItem { Header = account.ToString(CultureInfo.InvariantCulture) };
            item.Click += async (_, _) =>
            {
                AccountInput.Text = account.ToString(CultureInfo.InvariantCulture);
                await LoadAccountAsync();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, UiText.Get("ConfirmClearCache"),
                UiText.Get("ClearCache"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunAsync(UiText.Get("ClearingCache"), async token =>
        {
            try
            {
                var count = await _api.ClearCacheAsync(token);
                Status.Text = UiText.Get("CacheCleared", count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status.Text = UiText.Get("ClearCacheError");
            }
        });
    }

    private async void More_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(UiText.Get("LoadingMoreMatches"), async token =>
        {
            AddPage(await _api.GetMatchesAsync(_account, PageSize, _offset, token));
            Status.Text = _hasMore ? UiText.Get("MatchesLoaded", _matches.Count) : UiText.Get("EndOfHistory", _matches.Count);
        });

    private void AddPage(MatchSummary[] page)
    {
        _offset += page.Length;
        _hasMore = page.Length == PageSize;
        foreach (var summary in page)
        {
            if (_matchIds.Add(summary.MatchId))
                _matches.Add(new MatchView(summary));
        }
        MatchCount.Text = _matches.Count.ToString(CultureInfo.InvariantCulture);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(UiText.Get("ScanningChats"), LoadPendingChatsAsync);

    private async Task LoadPendingChatsAsync(CancellationToken token)
    {
        var pending = _matches.Where(CanLoadChat).ToArray();
        var missing = 0;
        for (var i = 0; i < pending.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var match = pending[i];
            Status.Text = UiText.Get("ScanningMatch", i + 1, pending.Length, match.Id);
            try { await LoadChatAsync(match, token); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { missing++; }
        }

        var withMessages = _matches.Count(match => match.TargetCount > 0);
        Status.Text = UiText.Get("PlayerChatCount", withMessages, _matches.Count);
        if (missing > 0)
            Status.Text += UiText.Get("MissingMatchCount", missing);
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        RenderSelection(resetScroll: false);
        UpdateControls();
        if (!InFeed)
        {
            _startFeedWhenIdle = false;
            await RefreshTimelinesAsync();
            return;
        }

        if (_operation is not null)
            _startFeedWhenIdle = true;
        else if (HasPendingChats && _feedAutoPage)
            await LoadFeedAsync(allowNextPage: false);
        await RefreshTimelinesAsync();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        var action = app.IsDark ? "Ui.LightTheme" : "Ui.DarkTheme";
        ThemeButton.SetResourceReference(ToolTipProperty, action);
        ThemeButton.SetResourceReference(AutomationProperties.NameProperty, action);
        ThemeIcon.Text = app.IsDark ? "\uE706" : "\uE708";
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UiText.ApplyLanguage(LanguagePicker.SelectedIndex == 1);
        foreach (var match in _matches)
            match.RefreshLanguage();
        RebuildFeed();
        Status.Text = "";
        RenderSelection(resetScroll: false);
        UpdateControls();
    }

    private async void FeedMore_Click(object sender, RoutedEventArgs e) => await LoadFeedAsync();

    private async void FeedList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (InFeed && _feedAutoPage && _operation is null && _hasMore &&
            e.VerticalChange > 0 && e.ExtentHeight - e.ViewportHeight - e.VerticalOffset < 520)
            await LoadFeedAsync();
    }

    private async Task LoadFeedAsync(bool allowNextPage = true)
    {
        if (_operation is not null || (!HasPendingChats && !(allowNextPage && _hasMore)))
            return;

        await RunAsync(UiText.Get("LoadingFeed"), async token =>
        {
            if (!HasPendingChats && _hasMore)
            {
                Status.Text = UiText.Get("LoadingMoreMatches");
                AddPage(await _api.GetMatchesAsync(_account, PageSize, _offset, token));
            }
            await LoadPendingChatsAsync(token);
        });
    }

    private async void MatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderSelection();
        if (!InFeed && SelectedMatch is { IsLoaded: false } match)
            await FetchSelectedAsync(match);
        else if (!InFeed)
            await RefreshTimelinesAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMatch is { } match)
            await FetchSelectedAsync(match);
    }

    private Task FetchSelectedAsync(MatchView match) =>
        RunAsync(UiText.Get("LoadingMatchChat", match.Id), async token =>
        {
            await LoadChatAsync(match, token);
            Status.Text = $"{match.Title} · {match.ChatStatus}.";
        });

    private async Task LoadChatAsync(MatchView match, CancellationToken token)
    {
        try
        {
            match.SetChat(await _api.GetMatchAsync(match.Id, token, WithScore), _account);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                match.SetFailed(true, ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound });
            throw;
        }

        if (_closed)
            return;

        if (WithScore && match.NeedsTimeline)
            _refreshTimelinesWhenIdle = true;
        UpdateTranscript(match);
    }

    private void UpdateTranscript(MatchView match)
    {
        var index = _feed.Count;
        while (index > 0 && _feed[index - 1].MatchId < match.Id)
            index--;

        var existing = index > 0 && _feed[index - 1].MatchId == match.Id;
        if (CreateTranscript(match) is { } transcript)
        {
            if (existing)
                _feed[index - 1] = transcript;
            else
                _feed.Insert(index, transcript);
        }
        else if (existing)
            _feed.RemoveAt(index - 1);

        if (InFeed || ReferenceEquals(SelectedMatch, match))
            RenderSelection();
    }

    private async Task RefreshTimelinesAsync()
    {
        if (_closed || !WithScore)
            return;

        var matches = InFeed ? _matches.Where(match => match.NeedsTimeline && (AllAuthors.IsChecked == true
            ? match.Messages.Count > 0 : match.TargetCount > 0)).ToArray()
            : SelectedMatch is { NeedsTimeline: true } selected ? [selected] : [];
        if (matches.Length == 0)
            return;

        if (_operation is not null)
        {
            _refreshTimelinesWhenIdle = true;
            return;
        }

        await RunAsync(UiText.Get("LoadingCombatLog"), async token =>
        {
            var loaded = 0;
            for (var i = 0; i < matches.Length && WithScore; i++)
            {
                var match = matches[i];
                Status.Text = UiText.Get("CombatLogProgress", i + 1, matches.Length, match.Id);
                var updated = await _api.GetMatchAsync(match.Id, token, requireTimeline: true);
                if (updated.CacheVersion >= MatchChat.TimelineCacheVersion)
                {
                    match.SetChat(updated, _account);
                    UpdateTranscript(match);
                    loaded++;
                }
            }
            Status.Text = UiText.Get("CombatLogsUpdated", loaded, matches.Length);
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        if (_operation is not null)
            return;

        using var operation = new CancellationTokenSource();
        _operation = operation;
        Status.Text = status;
        UpdateControls();
        if (InFeed)
            RenderSelection(resetScroll: false);
        var completed = false;
        try
        {
            await action(operation.Token);
            completed = true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_closed)
                Status.Text = UiText.Get("LoadingStopped");
        }
        catch (OperationCanceledException)
        {
            Status.Text = UiText.Get("Timeout");
        }
        catch (HttpRequestException ex)
        {
            Status.Text = ex.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => UiText.Get("RateLimited"),
                { } code => UiText.Get("HttpError", (int)code),
                null => UiText.Get("ConnectionError")
            };
        }
        catch (JsonException)
        {
            Status.Text = UiText.Get("ReadError");
        }
        finally
        {
            _operation = null;
            if (InFeed)
                _feedAutoPage = completed;
            if (!_closed)
            {
                RenderSelection(resetScroll: false);
                UpdateControls();
            }
        }

        var startFeed = _startFeedWhenIdle;
        _startFeedWhenIdle = false;
        if (completed && startFeed && !_closed && InFeed && HasPendingChats)
            await LoadFeedAsync(allowNextPage: false);

        var refreshTimelines = _refreshTimelinesWhenIdle;
        _refreshTimelinesWhenIdle = false;
        if (completed && refreshTimelines)
            await RefreshTimelinesAsync();
    }

    private void UpdateControls()
    {
        var ready = _operation is null;
        LanguagePicker.IsEnabled = ready && !_exportingFeed;
        AccountInput.IsEnabled = LoadButton.IsEnabled = RetryButton.IsEnabled = ready;
        HistoryButton.IsEnabled = ready && _accounts.Length > 0;
        ClearCacheButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = ready && _hasMore;
        var pending = HasPendingChats;
        ScanButton.IsEnabled = ready && pending;
        FeedMoreButton.Visibility = pending || _hasMore ? Visibility.Visible : Visibility.Collapsed;
        FeedMoreButton.IsEnabled = ready;
        FeedMoreButton.Content = pending ? UiText.Get("LoadChats") : UiText.Get("MoreMatches");
        CancelButton.Visibility = Activity.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void Filters_Changed(object sender, RoutedEventArgs e)
    {
        RebuildFeed();
        RenderSelection();
        ScrollFeedToTop();
        await RefreshTimelinesAsync();
    }

    private void RebuildFeed()
    {
        _feed.Clear();
        foreach (var match in _matches)
        {
            if (CreateTranscript(match) is { } transcript)
                _feed.Add(transcript);
        }
    }

    private void ScrollFeedToTop()
    {
        FeedList.ApplyTemplate();
        if (FeedList.Template.FindName("FeedScroller", FeedList) is ScrollViewer scroller)
            scroller.ScrollToTop();
    }

    private Transcript? CreateTranscript(MatchView match)
    {
        var all = AllAuthors.IsChecked == true;
        if (all ? match.Messages.Count == 0 : match.TargetCount == 0)
            return null;

        var rows = match.GetRows(all, WithScore);
        var filter = all ? UiText.Get("AllPlayers") : UiText.Get("PlayerMessagesOnly");
        return new Transcript(match.Id, all, UiText.Get("TranscriptTitle", match.Title),
            UiText.Get("TranscriptMetadata", match.Date, _account, filter, rows.Count(row => row.Kind == MessageKind.Chat)) +
            (WithScore ? $"  ·  {UiText.Get("KdaAbbreviation")}" : ""), rows);
    }

    private void RenderSelection(bool resetScroll = true)
    {
        var inFeed = InFeed;
        MatchSidebar.Visibility = inFeed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ChatPane, inFeed ? 0 : 2);
        Grid.SetColumnSpan(ChatPane, inFeed ? 3 : 1);
        MatchActions.Visibility = inFeed ? Visibility.Collapsed : Visibility.Visible;
        FeedList.Visibility = inFeed && _feed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedFooter.Visibility = inFeed ? Visibility.Visible : Visibility.Collapsed;
        if (inFeed)
        {
            ChatToolbar.Visibility = Visibility.Visible;
            AllAuthors.IsEnabled = true;
            ShowScore.IsEnabled = true;
            ExportFeedButton.IsEnabled = _feed.Count > 0 && !_exportingFeed;
            ChatScroll.Visibility = RetryButton.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = _feed.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyTitle.Text = UiText.Get("FeedTitle");
            EmptyBody.Text = _operation is not null ? UiText.Get("FeedLoading")
                : UiText.Get("FeedEmpty");
            FeedCount.Text = UiText.Get("FeedMatchCount", _feed.Count);
            return;
        }

        var match = SelectedMatch;
        var transcript = match is null ? null : CreateTranscript(match);
        var hasRows = transcript is not null;
        ChatToolbar.Visibility = match is null ? Visibility.Collapsed : Visibility.Visible;
        AllAuthors.IsEnabled = match?.HasChat == true;
        ShowScore.IsEnabled = match?.HasChat == true;
        ExportButton.IsEnabled = hasRows;
        ChatScroll.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = match is { IsLoaded: false } ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Content = match?.Failed == true ? UiText.Get("Retry") : UiText.Get("LoadChat");

        if (hasRows)
        {
            TranscriptView.Content = transcript;
            if (resetScroll)
                ChatScroll.ScrollToTop();
            return;
        }

        TranscriptView.Content = null;
        EmptyTitle.Text = match?.Title ?? UiText.Get("SelectMatch");
        EmptyBody.Text = match switch
        {
            null => UiText.Get("ChatEmpty"),
            { Failed: true } => UiText.Get("MatchChatError"),
            { IsLoaded: false } => UiText.Get("MatchChatPrompt"),
            { IsParsed: false, HasChat: false } => UiText.Get("ReplayNotParsed"),
            { HasChat: false } => UiText.Get("ChatMissing"),
            { Messages.Count: 0 } => UiText.Get("NoTextChat"),
            _ => UiText.Get("PlayerChatMissing")
        };
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var transcript = (sender as FrameworkElement)?.DataContext as Transcript ?? TranscriptView.Content as Transcript;
        if (transcript is null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"dota-chat-{transcript.MatchId}-{(transcript.AllAuthors ? "all" : _account.ToString(CultureInfo.InvariantCulture))}.png"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SaveTranscript(transcript, dialog.FileName);
            Status.Text = UiText.Get("PngSaved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status.Text = UiText.Get("PngSaveError");
        }
    }

    private async void ExportFeed_Click(object sender, RoutedEventArgs e)
    {
        if (_feed.Count == 0 || _exportingFeed)
            return;

        var snapshot = _feed.ToArray();
        var account = _account;
        var dialog = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"dota-chat-feed-{account}.png"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _exportingFeed = true;
        ExportFeedButton.IsEnabled = false;
        LanguagePicker.IsEnabled = false;
        Status.Text = UiText.Get("SavingFeed", snapshot.Length);
        try
        {
            var parts = await FeedImageExporter.SaveAsync(snapshot, account, dialog.FileName);
            Status.Text = parts == 1 ? UiText.Get("FeedSaved", Path.GetFileName(dialog.FileName))
                : UiText.Get("FeedPartsSaved", parts, Path.GetFileName(dialog.FileName));
        }
        catch (IOException ex)
        {
            Status.Text = UiText.Get("FeedIoError", ex.Message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Status.Text = UiText.Get("FeedSaveError");
        }
        finally
        {
            _exportingFeed = false;
            ExportFeedButton.IsEnabled = _feed.Count > 0;
            LanguagePicker.IsEnabled = _operation is null;
        }
    }

    private void SaveTranscript(Transcript transcript, string path)
    {
        const double width = 960;
        var view = new ContentControl
        {
            ContentTemplate = (DataTemplate)FindResource("TranscriptTemplate"),
            Content = transcript,
            FontFamily = FontFamily,
            FontSize = FontSize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var page = new Border { Background = (Brush)FindResource("Canvas"), Padding = new Thickness(16), Child = view };
        page.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Ceiling(page.DesiredSize.Height);
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();

        // Keep the entire transcript in one PNG while staying within the render target's size limit.
        var scale = Math.Min(1.5, 16_000 / height);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = File.Create(temporary))
                png.Save(output);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void OpenDocs_Click(object sender, RoutedEventArgs e) => OpenUrl("https://docs.opendota.com/");

    private void OpenMatch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMatch is { } match)
            OpenUrl($"https://www.opendota.com/matches/{match.Id}/chat");
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { Status.Text = UiText.Get("BrowserError"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _operation?.Cancel();
        _api.Dispose();
        base.OnClosed(e);
    }
}
