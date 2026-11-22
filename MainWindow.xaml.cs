using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LolMatchAssistant;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _accountScope = new();
    private CancellationTokenSource? _historyScope;
    private CancellationTokenSource? _historyNavigationScope;
    private CancellationTokenSource? _selectionScope;
    private CancellationTokenSource? _rosterScope;
    private ClientConnection? _connection;
    private LeagueService? _league;
    private ChampionPortraits? _portraits;
    private Player? _current;
    private Player? _target;
    private Selection? _selection;
    private RuneRow? _applied;
    private string? _lockfile;
    private string _game = "";
    private DateTime _retryRunes;
    private bool _polling, _loadingHistory, _applying, _canContinue;
    private readonly Dictionary<long, MatchRow> _expandedMatches = [];
    private int _offset;
    private readonly List<Match> _matches = [];
    private RunePreset[] _presets = [];
    private RuneSample[] _samples = [];
    private Rune[] _clientRunes = [];
    private readonly ObservableCollection<SeatRow> _blue = [];
    private readonly ObservableCollection<SeatRow> _red = [];
    private readonly SemaphoreSlim _seatRequests = new(3);
    private readonly SemaphoreSlim _timelineRequests = new(2);
    private readonly Dictionary<string, PlayerRecord> _rosterProfiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _openedHistoryFromRoster;
    private readonly ObservableCollection<HistoryPlayerPage> _historyPages = [];
    private HistoryPlayerPage? _activeHistoryPage;
    private AssistantPreferences _preferences;
    private bool _loadingPreferences;
    private bool _acceptedReadyCheck;
    private long? _readyCheckStarted;
    private string _automationFailure = "";
    private OpGgWindow? _opGgWindow;
    private bool _opGgShowsChampion;
    private MasteryRow[] _masteryRows = [];
    private bool _showAllMastery;
    private RankSummary? _soloSummary, _flexSummary;
    private int _copyFeedbackVersion;

    public MainWindow() : this(AssistantPreferences.Load()) { }

    private MainWindow(AssistantPreferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();
        _loadingPreferences = true;
        ThemePicker.SelectedIndex = Math.Clamp(_preferences.Theme, 0, AssistantTheme.LastChoice);
        AutoAcceptToggle.IsChecked = _preferences.AutoAccept;
        AcceptDelayPicker.SelectedIndex = Math.Clamp(_preferences.AcceptDelay, 0, 5);
        AutoPickToggle.IsChecked = _preferences.AutoPick;
        LockInToggle.IsChecked = _preferences.LockIn;
        _loadingPreferences = false;
        AssistantTheme.Apply(ThemePicker.SelectedIndex);
        UpdateAutomationControls();
        BlueSeats.ItemsSource = _blue;
        RedSeats.ItemsSource = _red;
        HistoryPlayerTabs.ItemsSource = _historyPages;
        SizeChanged += (_, _) => UpdateHistorySummaryWidth();
        SetButtons();
        SourceInitialized += (_, _) =>
        {
            // Windows 11 can apply an outline even when the caption is drawn by WPF.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            const int borderColor = 34;
            uint noBorder = 0xFFFFFFFE;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, borderColor, ref noBorder, sizeof(uint));
        };
        Loaded += async (_, _) =>
        {
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            _timer.Tick += async (_, _) => await Poll();
            _timer.Start();
            await Poll();
        };
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
            _timer.Stop();
            _lifetime.Cancel();
            EndScope(ref _historyScope);
            EndScope(ref _historyNavigationScope);
            EndScope(ref _selectionScope);
            EndScope(ref _rosterScope);
            _accountScope.Dispose();
            _lifetime.Dispose();
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    private void MinimizeMainWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseMainWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMainWindowSize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    internal MainWindow(LeagueService league, Player current, AssistantPreferences? preferences = null)
        : this(preferences ?? AssistantPreferences.Load())
    {
        _league = league;
        _current = current;
        SetButtons();
    }

    private async Task Poll()
    {
        if (_polling || _lifetime.IsCancellationRequested) return;
        _polling = true;
        var ct = _lifetime.Token;
        try
        {
            var connection = await Task.Run(() => ClientDiscovery.Find(_lockfile), ct);
            if (connection != _connection)
            {
                ClearAccount();
                _connection = connection;
                var transport = connection is null ? null : new LeagueTransport(connection);
                _league = transport is null ? null : new(transport);
                _portraits = transport is null ? null : new(transport, ct);
            }
            var league = _league;
            if (league is null)
            {
                ConnectionStatus.Text = "等待客户端连接";
                ConnectionStatus.ToolTip = "请先登录英雄联盟。若已启动，可手动选择客户端的 lockfile。";
                return;
            }
            var player = await league.Current(ct);
            if (player.Puuid != _current?.Puuid)
            {
                ClearAccount();
                _current = player;
                ConnectionStatus.Text = $"已连接 · {player.Name}";
                SetButtons();
                await league.LoadMetadata(ct);
                LoadChampionChoices(league);
                var own = OwnHistoryPage(player);
                if (_activeHistoryPage is null) _ = ActivateHistoryPage(own, showHistory: false);
            }
            else _current = player;
            ConnectionStatus.Text = $"已连接 · {player.Name}";
            ConnectionStatus.ToolTip = ConnectionStatus.Text;
            Selection selection;
            try { selection = await league.State(ct); }
            catch (LeagueException) { return; }
            var previous = _selection;
            if (selection != previous) ChangeSelection(selection, previous);
            await RunAutomation(league, selection, ct);
            if (selection.IsPicking && selection.Champion > 0 && _clientRunes.Length == 0 && DateTime.UtcNow >= _retryRunes)
                _ = RefreshRunes(selection);
            if (selection.IsPicking || selection.CanShowRoster)
            {
                try
                {
                    var (game, seats) = selection.IsPicking
                        ? ($"pick:{selection.Session}", await league.PickingRoster(selection, player, ct))
                        : await league.Roster(selection, ct);
                    if (selection.CanShowRoster && game.Length == 0)
                        game = _game.Length == 0 || _game.StartsWith("pick:", StringComparison.Ordinal) ? "live" : _game;
                    if (game != _game)
                    {
                        // Reuse public profiles across the pick-to-game transition only for the same game.
                        if (!selection.CanShowRoster || game == "live" || _game != $"pick:{game}") _rosterProfiles.Clear();
                        _game = game;
                        StartScope(ref _rosterScope, _accountScope.Token);
                        _blue.Clear();
                        _red.Clear();
                    }
                    UpdateSeats(seats, selection);
                }
                catch (LeagueException ex) { RosterStatus.Text = ex.Message; }
            }
        }
        catch (LeagueException ex)
        {
            if (_current is not null) ClearAccount();
            ConnectionStatus.Text = ex.Status is 401 or 403 ? "客户端连接已失效，等待重新连接" : ex.Message;
        }
        catch (OperationCanceledException) { }
        finally { _polling = false; }
    }

    private void ClearAccount()
    {
        _accountScope.Cancel();
        _accountScope.Dispose();
        _accountScope = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        EndScope(ref _historyScope);
        EndScope(ref _historyNavigationScope);
        EndScope(ref _selectionScope);
        EndScope(ref _rosterScope);
        _current = _target = null;
        _selection = null;
        _acceptedReadyCheck = false;
        _readyCheckStarted = null;
        _timer.Interval = TimeSpan.FromSeconds(2);
        _automationFailure = "";
        AutomationStatus.Text = "连接客户端后可用";
        _clientRunes = [];
        _applied = null;
        _game = "";
        _rosterProfiles.Clear();
        _activeHistoryPage = null;
        _historyPages.Clear();
        BackToMatchButton.Visibility = Visibility.Collapsed;
        SetRosterReturn(false);
        SetTeamLayout(false);
        _loadingHistory = _applying = _canContinue = false;
        _matches.Clear();
        _expandedMatches.Clear();
        _blue.Clear();
        _red.Clear();
        RosterTitle.Text = "当前对局";
        RosterStatus.Text = "进入选人后查看队友，加载或开局后查看双方阵容。";
        HistoryTitle.Text = "近期对局";
        ResetProfile();
        HistoryStatus.Text = "登录客户端后可查询战绩。";
        UpdateHistory();
        ShowRunes();
        SetButtons();
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => { if (ThemePicker.SelectedIndex == 0) AssistantTheme.Apply(0); });
    }

    private void PreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences || ThemePicker is null || AcceptDelayPicker is null || LockInToggle is null ||
            ChampionPicker is null || AutomationStatus is null) return;
        var updated = new AssistantPreferences(ThemePicker.SelectedIndex, AutoAcceptToggle.IsChecked == true,
            AutoPickToggle.IsChecked == true, (ChampionPicker.SelectedItem as ChampionChoice)?.Id ?? _preferences.Champion,
            LockInToggle.IsChecked == true, Math.Clamp(AcceptDelayPicker.SelectedIndex, 0, 5));
        if (updated == _preferences) return;
        if (updated.Theme != _preferences.Theme) AssistantTheme.Apply(updated.Theme);
        _preferences = updated;
        UpdateAutomationControls();
        try { updated.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AutomationStatus.Text = "设置已应用，保存失败。"; }
    }

    private void LoadChampionChoices(LeagueService league)
    {
        _loadingPreferences = true;
        ChampionPicker.ItemsSource = league.Champions;
        ChampionPicker.SelectedValue = _preferences.Champion;
        _loadingPreferences = false;
        UpdateAutomationControls();
    }

    private void UpdateAutomationControls()
    {
        AcceptDelayPicker.IsEnabled = AutoAcceptToggle.IsChecked == true;
        ChampionPicker.IsEnabled = AutoPickToggle.IsChecked == true && ChampionPicker.HasItems;
        LockInToggle.IsEnabled = AutoPickToggle.IsChecked == true;
    }

    internal async Task RunAutomation(LeagueService league, Selection selection, CancellationToken ct)
    {
        if (selection.Phase == "ReadyCheck")
            _readyCheckStarted ??= Stopwatch.GetTimestamp();
        else
        {
            _readyCheckStarted = null;
            _acceptedReadyCheck = false;
        }
        _timer.Interval = selection.Phase == "ReadyCheck" && _preferences.AutoAccept && !_acceptedReadyCheck
            ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(2);
        if (!selection.IsPicking && selection.Phase != "ReadyCheck")
        {
            _automationFailure = "";
            AutomationStatus.Text = _preferences.AutoAccept || _preferences.AutoPick ? "等待匹配确认或选人" : "自动操作已关闭";
            return;
        }
        try
        {
            if (_preferences.AutoAccept && selection.Phase == "ReadyCheck" && !_acceptedReadyCheck)
            {
                var pending = TimeSpan.FromSeconds(Math.Clamp(_preferences.AcceptDelay, 0, 5)) -
                    Stopwatch.GetElapsedTime(_readyCheckStarted!.Value);
                if (pending > TimeSpan.Zero)
                {
                    AutomationStatus.Text = $"将在 {Math.Ceiling(pending.TotalSeconds):0} 秒后接受对局";
                    return;
                }
                _acceptedReadyCheck = await league.AcceptReadyCheck(ct);
                if (_acceptedReadyCheck)
                {
                    _timer.Interval = TimeSpan.FromSeconds(2);
                    AutomationStatus.Text = "已接受本次对局";
                }
            }
            if (_preferences.AutoPick && selection.IsPicking)
            {
                var result = _preferences.Champion > 0
                    ? await league.AutoPick(selection, _preferences.Champion, _preferences.LockIn, ct)
                    : "请先在功能页选择英雄";
                if (result.Length > 0) AutomationStatus.Text = result;
            }
            _automationFailure = "";
        }
        catch (LeagueException ex)
        {
            if (_automationFailure != ex.Message) AutomationStatus.Text = $"自动操作暂未完成 · {ex.Message}";
            _automationFailure = ex.Message;
        }
    }

    private void SelectClient(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择英雄联盟客户端的 lockfile", Filter = "客户端连接文件|lockfile", FileName = "lockfile" };
        if (dialog.ShowDialog(this) == true)
        {
            if (ClientDiscovery.ReadLockfile(dialog.FileName) is null)
            {
                ConnectionStatus.Text = "此文件不属于正在运行的英雄联盟客户端。";
                return;
            }
            _lockfile = dialog.FileName;
            _ = Poll();
        }
    }

    private void SetButtons()
    {
        SearchButton.IsEnabled = MineButton.IsEnabled = _current is not null;
        MoreButton.IsEnabled = _canContinue && !_loadingHistory && _target is not null;
        MoreButton.Visibility = _canContinue && _target is not null ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !_applying && _selection?.IsPicking == true &&
            _selection.Champion > 0 && RuneList.SelectedItem is RuneRow;
        CopyButton.IsEnabled = RuneList.SelectedItem is RuneRow;
    }

    private async void ShowMine(object sender, RoutedEventArgs e)
    {
        if (_current is { } player) await ActivateHistoryPage(OwnHistoryPage(player));
    }

    private async void SearchPlayer(object sender, RoutedEventArgs e) => await OpenHistoryPage(riotId: SearchInput.Text.Trim());

    private void OpenProjectLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!e.Uri.IsAbsoluteUri || e.Uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            ProjectLinkStatus.Text = "";
        }
        catch (Win32Exception) { ProjectLinkStatus.Text = "无法启动默认浏览器。"; }
    }
    private async void SearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenHistoryPage(riotId: SearchInput.Text.Trim()); }
    }

    private HistoryPlayerPage OwnHistoryPage(Player player)
    {
        if (_historyPages.FirstOrDefault(page => page.IsOwn) is { } own) return own;
        own = new(player, true, _portraits?.Profile(player.IconId));
        _historyPages.Insert(0, own);
        return own;
    }

    private HistoryPlayerPage? FindHistoryPage(Player? player, string? riotId = null) =>
        _historyPages.FirstOrDefault(page => player is not null && HistoryInsights.SamePlayer(page.Player, player) ||
            page.Player.HasRiotId && page.Player.Name.Equals(player?.Name ?? riotId, StringComparison.OrdinalIgnoreCase));

    private async Task OpenHistoryPage(Player? player = null, string? riotId = null, MatchReturn? origin = null)
    {
        if (_league is not { } league || _current is not { } current) return;
        var ct = StartScope(ref _historyNavigationScope, _accountScope.Token);
        OwnHistoryPage(current);
        try
        {
            var page = FindHistoryPage(player, riotId);
            if (page is null)
            {
                var found = player is null ? await league.Find(riotId ?? "", current, ct) :
                    await league.ResolvePublicPlayer(player, current, ct);
                ct.ThrowIfCancellationRequested();
                page = FindHistoryPage(found);
                if (page is null)
                {
                    page = new(found, false, _portraits?.Profile(found.IconId));
                    _historyPages.Add(page);
                }
            }
            if (origin is not null && origin.Page != page && _historyPages.Contains(origin.Page))
                page.ReturnTo = origin;
            await ActivateHistoryPage(page);
        }
        catch (LeagueException ex) when (!ct.IsCancellationRequested) { HistoryStatus.Text = ex.Message; }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is LeagueException or OperationCanceledException) { }
    }

    private async Task ActivateHistoryPage(HistoryPlayerPage page, bool showHistory = true)
    {
        if (_league is not { } league || _current is not { } current || !_historyPages.Contains(page)) return;
        EndScope(ref _historyNavigationScope);
        if (showHistory) Pages.SelectedItem = HistoryTab;
        if (_activeHistoryPage == page && (page.HasPage || _loadingHistory))
        {
            RefreshMatchReturn();
            return;
        }
        if (_activeHistoryPage is { } previous && previous != page)
        {
            previous.Matches = _matches.ToArray();
            previous.Expanded = _expandedMatches.Values.ToArray();
            previous.Offset = _offset;
            previous.CanContinue = _canContinue;
            previous.Filter = QueueFilter.SelectedIndex;
            previous.Status = _loadingHistory && previous.HasPage ? $"已读取 {_matches.Count} 场" : HistoryStatus.Text;
        }

        var ct = StartScope(ref _historyScope, _accountScope.Token);
        _activeHistoryPage = page;
        HistoryPlayerTabs.SelectedItem = page;
        HistoryPlayerTabs.ScrollIntoView(page);
        _target = page.Player;
        _offset = page.Offset;
        _canContinue = page.CanContinue;
        _matches.Clear();
        _matches.AddRange(page.Matches);
        _expandedMatches.Clear();
        foreach (var row in page.Expanded) _expandedMatches.Add(row.Match.Id, row);
        QueueFilter.SelectedIndex = page.Filter;
        SearchInput.Text = page.Player.Name;
        ResetProfile();
        SetHistoryPlayer(league, page.Player, ct);
        _ = LoadHistoryAvatar(league, page, ct);
        RefreshMatchReturn();
        HistoryStatus.Text = page.HasPage ? page.Status : "正在查询战绩…";
        _loadingHistory = !page.HasPage;
        UpdateHistory();
        SetButtons();
        foreach (var row in page.Expanded.Where(row => row.IsExpanded && row.IsLoadingDetail))
        {
            row.SetDetail(false);
            _ = ShowDetails(row);
        }
        if (page.HasPage) return;
        try { await AppendPage(league, page.Player, current, ct); }
        catch (LeagueException ex) when (!ct.IsCancellationRequested) { HistoryStatus.Text = ex.Message; }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is LeagueException or OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) { _loadingHistory = false; SetButtons(); }
        }
    }

    private async void LoadMore(object sender, RoutedEventArgs e) => await LoadMoreHistory();

    internal async Task LoadMoreHistory()
    {
        if (_loadingHistory || !_canContinue || _target is not { } target || _current is not { } current ||
            _league is not { } league || _historyScope is not { } scope) return;
        var ct = scope.Token;
        _loadingHistory = true;
        SetButtons();
        HistoryStatus.Text = "正在加载更早的对局…";
        try { await AppendPage(league, target, current, ct); }
        catch (LeagueException ex) when (!ct.IsCancellationRequested) { HistoryStatus.Text = ex.Message; }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is LeagueException or OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _loadingHistory = false;
                SetButtons();
            }
        }
    }

    private async Task AppendPage(LeagueService league, Player target, Player current, CancellationToken ct)
    {
        if (_activeHistoryPage is not { } open) return;
        var known = _matches.Select(match => match.Id).ToHashSet();
        var newMatches = 0;
        HistoryPage? recovered = null;
        if (!open.UseShard && _offset > 0)
        {
            try
            {
                var candidate = await league.ShardHistoryPage(target, 0, ct);
                if (candidate.Matches.Any(match => match.For(target) is not null)) recovered = candidate;
            }
            catch (LeagueException) { }
            ct.ThrowIfCancellationRequested();
            if (recovered is not null) _offset = 0;
        }
        HistoryPage page;
        var pagesRead = 0;
        do
        {
            page = recovered ?? await league.History(target, current, _offset, open.UseShard, ct);
            recovered = null;
            ct.ThrowIfCancellationRequested();
            open.HasPage = true;
            open.UseShard = page.IsShard;
            var added = page.Matches.Where(match => match.For(target) is not null && known.Add(match.Id)).ToArray();
            _matches.AddRange(added);
            newMatches += added.Length;
            _offset += page.Entries;
            _canContinue = page.CanContinue;
            pagesRead++;
        } while (_canContinue && newMatches == 0 && pagesRead < 3);
        var oldest = _matches.Select(match => match.Started).Min();
        var status = new List<string> { page.Source, $"已读取 {_matches.Count} 场" };
        if (oldest is { } date) status.Add($"最早 {date:yyyy-MM-dd}");
        if (!_canContinue) status.Add("此数据源暂无更早记录");
        else if (newMatches == 0) status.Add("本次未找到新对局，可继续加载");
        HistoryStatus.Text = string.Join(" · ", status);
        UpdateHistory();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e) => UpdateHistory();

    private void ResetProfile()
    {
        AccountAvatar.DataContext = null;
        CopyPlayerIdButton.Visibility = Visibility.Collapsed;
        _copyFeedbackVersion++;
        ResetCopyFeedback();
        _soloSummary = _flexSummary = null;
        AccountSubtitle.Text = "查询玩家后查看排位段位";
        SoloRank.Text = FlexRank.Text = "—";
        SoloRecord.Text = FlexRecord.Text = "";
        SoloEmblem.Source = FlexEmblem.Source = null;
        SoloEmblem.Visibility = FlexEmblem.Visibility = Visibility.Collapsed;
        _masteryRows = [];
        _showAllMastery = false;
        MasteryList.ItemsSource = null;
        MasteryToggle.Visibility = Visibility.Collapsed;
        MasteryStatus.Text = "—";
        AssetList.ItemsSource = null;
        AssetStatus.Text = "登录后查看账号资产。";
    }

    private void SetHistoryPlayer(LeagueService league, Player player, CancellationToken ct)
    {
        ShowHistoryIdentity(player);
        _ = LoadRanks(league, player, ct);
        if (_current is { } current)
        {
            _ = LoadMastery(league, player, current, ct);
            if (HistoryInsights.SamePlayer(player, current)) _ = LoadAssets(league, current, ct);
            else AssetStatus.Text = "资产数量仅对当前登录账号可见。";
        }
    }

    private void ShowHistoryIdentity(Player player)
    {
        HistoryTitle.Text = JsonFields.First(player.Name, "队友战绩");
        CopyPlayerIdButton.Visibility = player.HasRiotId ? Visibility.Visible : Visibility.Collapsed;
        AccountAvatar.DataContext = _portraits?.Profile(player.IconId);
        AccountSubtitle.Text = player.Level > 0 ? $"{player.Level} 级" : "";
    }

    private async void CopyPlayerId(object sender, RoutedEventArgs e)
    {
        if (_target is not { HasRiotId: true } player) return;
        var feedback = ++_copyFeedbackVersion;
        try
        {
            Clipboard.SetText(player.Name);
            CopyPlayerIdButton.Content = "\uE73E";
            CopyPlayerIdButton.ToolTip = "已复制";
        }
        catch (ExternalException) { CopyPlayerIdButton.ToolTip = "剪贴板正忙，请重试。"; }
        await Task.Delay(1200);
        if (feedback == _copyFeedbackVersion) ResetCopyFeedback();
    }

    private void ResetCopyFeedback()
    {
        CopyPlayerIdButton.Content = "\uE8C8";
        CopyPlayerIdButton.ToolTip = "复制游戏 ID";
    }

    private async Task LoadHistoryAvatar(LeagueService league, HistoryPlayerPage page, CancellationToken ct)
    {
        if (page.ProfileLoaded) return;
        try
        {
            var profile = await league.CompletePublicProfile(page.Player, ct);
            ct.ThrowIfCancellationRequested();
            page.ProfileLoaded = profile.IconId >= 0;
            page.SetProfile(profile, _portraits?.Profile(profile.IconId));
            _target = profile;
            ShowHistoryIdentity(profile);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadMastery(LeagueService league, Player player, Player current, CancellationToken ct)
    {
        MasteryStatus.Text = "正在读取…";
        try
        {
            var entries = await league.Mastery(player, current, ct);
            ct.ThrowIfCancellationRequested();
            _masteryRows = entries?.Select(entry => new MasteryRow(league.ChampionName(entry.Champion),
                entry.Level, entry.Points, _portraits?.Get(entry.Champion))).ToArray() ?? [];
            MasteryStatus.Text = entries is null ? "客户端未返回英雄点数。" : entries.Length == 0 ? "暂无英雄点数。" :
                $"总等级 {entries.Sum(entry => entry.Level):N0}";
            UpdateMasteryRows();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAssets(LeagueService league, Player current, CancellationToken ct)
    {
        AssetStatus.Text = "正在读取账号资产…";
        try
        {
            var counts = await league.Collection(current, ct);
            ct.ThrowIfCancellationRequested();
            AssetList.ItemsSource = counts;
            AssetStatus.Text = counts.All(item => item.Count is null) ? "客户端暂未返回资产数据。" :
                counts.Any(item => item.Count is null) ? "“—”表示当前无法读取。" : "";
        }
        catch (OperationCanceledException) { }
    }

    private void ToggleMastery(object sender, RoutedEventArgs e)
    {
        _showAllMastery = !_showAllMastery;
        UpdateMasteryRows();
    }

    private void UpdateMasteryRows()
    {
        MasteryList.ItemsSource = _showAllMastery ? _masteryRows : _masteryRows.Take(5);
        MasteryToggle.Visibility = _masteryRows.Length > 5 ? Visibility.Visible : Visibility.Collapsed;
        MasteryToggle.Content = _showAllMastery ? "收起" : "查看更多";
    }

    private async Task LoadRanks(LeagueService league, Player player, CancellationToken ct)
    {
        try
        {
            var (solo, flex) = await league.Ranks(player, ct, _current is { } current && HistoryInsights.SamePlayer(player, current));
            if (ct.IsCancellationRequested) return;
            (_soloSummary, _flexSummary) = (solo, flex);
            SoloRank.Text = solo.Label;
            FlexRank.Text = flex.Label;
            UpdateRankRecords();
            SetRankEmblem(SoloEmblem, solo.Tier, solo.Label);
            SetRankEmblem(FlexEmblem, flex.Tier, flex.Label);
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateRankRecords()
    {
        if (_target is not { } target) return;
        if (_soloSummary is { } solo) SoloRecord.Text = RankRecordText(solo, _matches, target, 420);
        if (_flexSummary is { } flex) FlexRecord.Text = RankRecordText(flex, _matches, target, 440);
    }

    internal static string RankRecordText(RankSummary rank, IEnumerable<Match> matches, Player target, int queue)
    {
        if (rank.HasVerifiedRecord) return rank.Record;
        var sample = matches.Where(match => match.Queue == queue && !match.EarlyEnd)
            .Select(match => match.For(target)).OfType<Participant>().Where(p => p.OutcomeKnown).ToArray();
        var wins = sample.Count(p => p.Won);
        var record = sample.Length > 0 ? $"近 {sample.Length} 场 · {wins} 胜 {sample.Length - wins} 负" : "暂无胜负数据";
        if (sample.Length >= 3) record += $" · {(double)wins / sample.Length:P0}";
        return rank.Record.Length > 0 ? $"{rank.Record}\n{record}" : record;
    }

    internal static void SetRankEmblem(Image image, string tier, string label)
    {
        image.Source = RankEmblem(tier);
        image.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
        image.ToolTip = label;
    }

    internal static ImageSource? RankEmblem(string tier) => tier.ToUpperInvariant() switch
        {
            "IRON" or "BRONZE" or "SILVER" or "GOLD" or "PLATINUM" or "EMERALD" or "DIAMOND"
                or "MASTER" or "GRANDMASTER" or "CHALLENGER" => new System.Windows.Media.Imaging.BitmapImage(
                    new Uri($"pack://application:,,,/Assets/Ranks/{tier.ToLowerInvariant()}.png")),
            _ => null
        };

    private void UpdateHistory()
    {
        if (MatchGrid is null || StatsList is null) return;
        var selected = QueueFilter.SelectedIndex;
        var rows = _target is { } target && _league is { } league
            ? _matches.Where(match => selected switch
                {
                    1 => match.Queue is 420 or 440, 2 => match.Queue is 400 or 430 or 480 or 490,
                    3 => match.Queue is 450 or 2400, _ => true
                })
                .Select(m => (Match: m, Participant: m.For(target)))
                .Where(entry => entry.Participant is not null)
                .OrderByDescending(entry => entry.Match.Started)
                .Select(entry => _expandedMatches.TryGetValue(entry.Match.Id, out var expanded)
                    ? expanded : CreateMatchRow(entry.Match, entry.Participant!, league))
                .ToArray() : [];
        MatchGrid.ItemsSource = rows;
        var eligible = rows.Where(row => row.HasWinResult).ToArray();
        var wins = eligible.Count(row => row.Won);
        var smallSample = _activeHistoryPage is { IsOwn: false } && eligible.Length is > 0 and < 3;
        StatsCaption.Text = eligible.Length > 0
            ? $"当前筛选 · {eligible.Length} 场有效对局"
            : "当前筛选暂无有效胜负";
        RecentRate.Text = eligible.Length > 0 && !smallSample ? $"{(double)wins / eligible.Length:P0}" : "—";
        SetWinRing(eligible.Length > 0 && !smallSample ? (double)wins / eligible.Length : 0);
        RecentRecord.Text = eligible.Length > 0
            ? $"{wins} 胜 {eligible.Length - wins} 负 · {eligible.Length} 场{(smallSample ? " · 样本少" : "")}" : "暂无有效胜负";
        var totalMinutes = rows.Sum(row => row.Match.Duration.TotalMinutes);
        RecentCs.Text = totalMinutes > 0 ? $"{rows.Sum(row => row.Participant.Cs) / totalMinutes:F1} 补刀 / 分" : "";
        UpdateHistoryInsights(rows);
        StatsList.ItemsSource = eligible.Where(row => row.Participant.Champion > 0).GroupBy(row => row.Participant.Champion)
            .OrderByDescending(group => group.Count())
            .Select(group => new StatRow(group.First().Champion, group.Count(row => row.Participant.Won), group.Count(),
                group.First().Portrait))
            .ToArray();
        UpdateRankRecords();
    }

    private void UpdateHistoryInsights(MatchRow[] rows)
    {
        HistoryTags.ItemsSource = PerformanceReview.Recent(rows.Select(row => row.Review));
        var records = rows.Select(row => new PlayerMatch(row.Match, row.Participant)).ToArray();
        HistoryMetrics.ItemsSource = HistoryInsights.Summary(records);
        TeammatesList.ItemsSource = HistoryInsights.Teammates(records).Select(teammate => new TeammateRow(
            teammate.Player, _portraits?.Get(teammate.Champion), $"{teammate.Wins} 胜 {teammate.Games - teammate.Wins} 负")).ToArray();
        EncountersList.ItemsSource = _current is { } current ? HistoryInsights.Encounters(records, current)
            .Select(encounter => new EncounterRow(
                encounter.Viewed.Participant.Team == encounter.Self.Team ? "队友" : "对手",
                PreviewGame(encounter.Viewed), encounter.Self.Kda)).ToArray() : [];
    }

    private MatchRow CreateMatchRow(Match match, Participant player, LeagueService league)
    {
        var (allies, opponents) = MatchLineups(match, player, league);
        return new(match, player, league.ChampionName(player.Champion), _portraits?.Get(player.Champion),
            ItemIcons(player, league), allies, opponents,
            PerformanceReview.Evaluate(match, player), SpellIcons(player, league), PreviewModifierIcons(match, player, league));
    }

    private (MatchRosterRow[] Allies, MatchRosterRow[] Opponents) MatchLineups(Match match, Participant viewed, LeagueService league)
    {
        MatchRosterRow[] Team(bool allies) => match.Players.Where(p => (p.Team == viewed.Team) == allies)
            .Select(p => new MatchRosterRow(p.Player, league.ChampionName(p.Champion),
                _portraits?.Get(p.Champion), ReferenceEquals(p, viewed))).ToArray();
        return (Team(true), Team(false));
    }

    internal void SetWinRing(double rate)
    {
        const double center = 27, radius = 22;
        RecentRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, rate >= .5 ? "Accent" : "LossInk");
        if (rate <= 0) { RecentRing.Data = null; return; }
        if (rate >= 1) { RecentRing.Data = new EllipseGeometry(new Point(center, center), radius, radius); return; }
        var angle = rate * Math.Tau;
        var stroke = new StreamGeometry();
        using (var path = stroke.Open())
        {
            path.BeginFigure(new Point(center, center - radius), false, false);
            path.ArcTo(new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, rate > .5, SweepDirection.Clockwise, true, false);
        }
        stroke.Freeze();
        RecentRing.Data = stroke;
    }

    private MatchIcon[] ItemIcons(Participant player, LeagueService league) =>
        player.Items.Split(" · ", StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0)
            .Select(id => new MatchIcon(league.ItemName(id), _portraits?.Asset(league.ItemIcon(id)), league.ItemDescription(id))).ToArray();

    private async void OpenMatch(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as MatchRow ?? MatchGrid.SelectedItem as MatchRow;
        if (row is not null) await ShowDetails(row);
    }

    private async void LoadMatchCardReview(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatchRow row } && _league is { } league && _historyScope is { } scope)
        {
            var ct = scope.Token;
            try
            {
                var match = await LoadMatchCardDetails(row, league, ct);
                await CompleteMatchReview(row, match, league, ct);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<Match> LoadMatchCardDetails(MatchRow row, LeagueService league, CancellationToken ct)
    {
        if (row.ReviewedMatch is { HasCompleteLineup: true } reviewed) return reviewed;
        if (row.Match.HasCompleteLineup) return row.Match;
        var request = row.CardDetails ??= RequestMatchCardDetails(row.Match, row.Participant.Player, league, ct);
        try
        {
            var match = await request;
            ct.ThrowIfCancellationRequested();
            if (match != row.Match && match.For(row.Participant.Player) is { } player)
            {
                var (allies, opponents) = MatchLineups(match, player, league);
                row.SetMatch(match, player, allies, opponents, ItemIcons(player, league),
                    SpellIcons(player, league), PreviewModifierIcons(match, player, league));
                var index = _matches.FindIndex(game => game.Id == match.Id);
                if (index >= 0) _matches[index] = match;
                if (MatchGrid.ItemsSource is MatchRow[] rows && rows.Contains(row)) UpdateHistoryInsights(rows);
            }
            return row.Match;
        }
        finally { if (row.CardDetails == request) row.CardDetails = null; }
    }

    private async Task<Match> RequestMatchCardDetails(Match match, Player viewed, LeagueService league, CancellationToken ct)
    {
        await _timelineRequests.WaitAsync(ct);
        try { return await league.Details(match, ct, viewed); }
        finally { _timelineRequests.Release(); }
    }

    private async Task CompleteMatchReview(MatchRow row, Match match, LeagueService league, CancellationToken ct)
    {
        if (row.TimelinePending || row.ReviewedMatch is not null || !MatchTimeline.NeedsReview(match)) return;
        row.TimelinePending = true;
        try
        {
            await _timelineRequests.WaitAsync(ct);
            Match? detailed;
            try { detailed = MatchTimeline.Enrich(match, await league.Timeline(match.Id, ct)); }
            finally { _timelineRequests.Release(); }
            ct.ThrowIfCancellationRequested();
            row.ReviewedMatch = detailed ?? match with { HasTimelineReview = true };
            var index = _matches.FindIndex(game => game.Id == match.Id);
            if (index >= 0) _matches[index] = row.ReviewedMatch;
            if (detailed?.For(row.Participant.Player) is not { } player) return;
            row.Review = PerformanceReview.Evaluate(detailed, player);
            row.NotifyTags();
            if (MatchGrid.ItemsSource is MatchRow[] rows)
                HistoryTags.ItemsSource = PerformanceReview.Recent(rows.Select(entry => entry.Review));
        }
        catch (OperationCanceledException) { }
        finally { row.TimelinePending = false; }
    }
    private async void OpenMatchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not Button && MatchGrid.SelectedItem is MatchRow)
        {
            e.Handled = true;
            await OpenSelectedMatch();
        }
    }

    internal Task OpenSelectedMatch() => MatchGrid.SelectedItem is MatchRow row ? ShowDetails(row) : Task.CompletedTask;

    private async Task ShowDetails(MatchRow row)
    {
        if (_league is not { } league || _historyScope is not { } scope) return;
        var ct = scope.Token;
        if (row.IsExpanded)
        {
            _expandedMatches.Remove(row.Match.Id);
            row.SetDetail(false);
            return;
        }
        _expandedMatches[row.Match.Id] = row;
        var request = ++row.DetailRequest;
        row.SetDetail(true, "正在读取单局详情…");
        try
        {
            var match = await LoadMatchCardDetails(row, league, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentDetail(row, request)) return;
            var highestDamage = match.Players.Max(p => p.Combat?.ChampionDamage ?? 0);
            var highestTaken = match.Players.Max(p => p.Combat?.DamageTaken ?? 0);
            var teams = match.Players.GroupBy(player => player.Team).OrderBy(team => team.Key)
                .Select(team =>
                {
                    var players = team.ToArray();
                    var comparison = TeamSummary(players);
                    var teamKills = players.Length > 1 && players.All(p => p.Combat?.Kills is >= 0)
                        ? players.Sum(p => p.Combat!.Kills!.Value) : 0;
                    var objectives = ObjectiveSummary(match.Objectives?.FirstOrDefault(value => value.Team == team.Key));
                    return new DetailTeam(team.Key == 100 ? "蓝方" : team.Key == 200 ? "红方" : $"队伍 {team.Key}",
                    players.Any(player => player.Won), comparison, objectives, players.Select(p => new DetailRow(
                        JsonFields.First(p.Player.Name, "身份未公开"), league.ChampionName(p.Champion),
                        p.Kda, HistoryInsights.Kda([p]), Participation(p, teamKills), p.Cs,
                        p.Combat?.ChampionDamage, p.Combat?.DamageTaken, p.Combat?.Gold,
                        highestDamage > 0 ? (p.Combat?.ChampionDamage ?? 0) * 100d / highestDamage : 0,
                        highestTaken > 0 ? (p.Combat?.DamageTaken ?? 0) * 100d / highestTaken : 0,
                        match.Duration.TotalMinutes, p.Loadout?.Level ?? 0,
                        SpellIcons(p, league), ModifierIcons(match, p, league),
                        ItemIcons(p, league), _portraits?.Get(p.Champion), p.Player, match.Id, RadarSnapshot.Build(match, p),
                        [.. PerformanceReview.Evaluate(match, p).Tags, .. HistoryInsights.MetricTags([new(match, p)]).Take(4)])).ToArray());
                }).ToArray();
            row.SetDetail(true, match.HasCompleteLineup ? "" : $"数据源仅返回 {match.Players.Length} 位玩家。", teams);
            _ = CompleteMatchReview(row, match, league, ct);
        }
        catch (LeagueException ex) when (!ct.IsCancellationRequested)
        {
            if (IsCurrentDetail(row, request)) row.SetDetail(true, ex.Message);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is LeagueException or OperationCanceledException) { }
    }

    private bool IsCurrentDetail(MatchRow row, int request) =>
        row.DetailRequest == request && _expandedMatches.TryGetValue(row.Match.Id, out var open) && ReferenceEquals(open, row);

    private static string Participation(Participant p, long teamKills) =>
        teamKills > 0 && p.Combat is { Kills: >= 0, Assists: >= 0 } stats
            ? $"{(stats.Kills!.Value + stats.Assists!.Value) / (double)teamKills:P0}" : "";

    private MatchIcon[] SpellIcons(Participant p, LeagueService league) =>
        p.Loadout?.Spells.Select(id => new MatchIcon(league.SpellName(id), _portraits?.Asset(league.SpellIcon(id)), league.SpellDescription(id))).ToArray() ?? [];

    private MatchIcon RuneIcon(LeagueService league, int id) =>
        new(league.PerkName(id), _portraits?.Asset(league.PerkIcon(id)), league.PerkDescription(id));

    private MatchIcon AugmentIcon(LeagueService league, int id) =>
        new(league.AugmentName(id), _portraits?.Asset(league.AugmentIcon(id)), league.AugmentDescription(id), IsAugment: true);

    internal static bool UsesAugments(Match match, Participant p) =>
        match.Queue == 2400 || (match.Queue is not (420 or 440 or 400 or 430 or 480 or 490) &&
            p.Loadout is { Augments.Length: > 0 });

    private MatchIcon[] ModifierIcons(Match match, Participant p, LeagueService league)
    {
        if (p.Loadout is not { } loadout) return [];
        if (UsesAugments(match, p))
            return loadout.Augments.Select(id => AugmentIcon(league, id)).ToArray();
        return loadout.Runes.Select(id => RuneIcon(league, id)).ToArray();
    }

    private MatchIcon[] PreviewModifierIcons(Match match, Participant p, LeagueService league)
    {
        if (p.Loadout is not { } loadout) return [];
        if (UsesAugments(match, p)) return loadout.Augments.Select(id => AugmentIcon(league, id)).ToArray();
        var secondary = loadout.SecondaryStyle > 0 && league.PerkIcon(loadout.SecondaryStyle).Length > 0
            ? loadout.SecondaryStyle : loadout.Runes.Skip(4).FirstOrDefault();
        return new[] { loadout.Runes.FirstOrDefault(), secondary }.Where(id => id > 0).Select(id => RuneIcon(league, id)).ToArray();
    }

    private static string ObjectiveSummary(TeamObjective? objective)
    {
        if (objective is null) return "";
        return string.Join("  ·  ", new[] { ("塔", objective.Towers), ("龙", objective.Dragons), ("男爵", objective.Barons),
            ("先锋", objective.Heralds), ("水晶", objective.Inhibitors), ("巢虫", objective.Grubs) }
            .Where(entry => entry.Item2.HasValue).Select(entry => $"{entry.Item1} {entry.Item2}"));
    }

    private void ChangeSelection(Selection selection, Selection? previous)
    {
        _selection = selection;
        if (selection.IsPicking || previous?.IsPicking == true)
        {
            StartScope(ref _selectionScope, _accountScope.Token);
            _clientRunes = [];
            _retryRunes = DateTime.MinValue;
            if (selection.IsPicking) _applied = null;
        }
        if (!_openedHistoryFromRoster && ((selection.IsPicking && previous?.IsPicking != true) ||
            (selection.CanShowRoster && previous?.CanShowRoster != true))) Pages.SelectedItem = RosterTab;
        SetTeamLayout(selection.IsPicking);
        if (selection.IsPicking && (previous?.IsPicking != true || selection.Position != previous.Position))
            OpRole.SelectedIndex = selection.Position switch
            {
                "top" => 1, "jungle" => 2, "middle" or "mid" => 3, "bottom" or "adc" => 4, "utility" or "support" => 5, _ => 0
            };
        OpBuildButton.IsEnabled = selection.IsPicking && _league?.ChampionAlias(selection.Champion).Length > 0;
        if (selection.IsPicking) _ = UpdateOpGgPage();
        if (!selection.IsPicking && !selection.CanShowRoster && previous is { } old && (old.IsPicking || old.CanShowRoster))
        {
            EndScope(ref _rosterScope);
            _game = "";
            _rosterProfiles.Clear();
            _blue.Clear();
            _red.Clear();
            _applied = null;
            RosterTitle.Text = "当前对局";
            RosterStatus.Text = "进入选人后查看队友，加载或开局后查看双方阵容。";
            BlueTeamTitle.Text = "蓝方";
            RedTeamTitle.Text = "红方";
        }
        ShowRunes();
    }

    private void SetTeamLayout(bool picking)
    {
        OpponentPanel.Visibility = picking ? Visibility.Collapsed : Visibility.Visible;
        PickTools.Visibility = picking ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static Uri OpGgUrl(string alias, int roleIndex)
    {
        const string champions = "https://op.gg/zh-cn/lol/champions";
        string[] roles = ["", "top", "jungle", "mid", "adc", "support"];
        var role = roles[Math.Clamp(roleIndex, 0, roles.Length - 1)];
        if (alias.Length > 0)
            return new($"{champions}/{Uri.EscapeDataString(alias.ToLowerInvariant())}/build{(role.Length > 0 ? "/" + role : "")}");
        return new($"{champions}?position={JsonFields.First(role, "all")}");
    }

    private async void OpenOpGg(object sender, RoutedEventArgs e)
    {
        if (_selection is not { IsPicking: true }) return;
        _opGgShowsChampion = ReferenceEquals(sender, OpBuildButton);
        if (_opGgWindow is null)
        {
            var sideWindow = new OpGgWindow { Owner = this };
            sideWindow.Closed += (_, _) => _opGgWindow = null;
            _opGgWindow = sideWindow;
            sideWindow.PlaceBeside(this);
            sideWindow.Show();
        }
        else
        {
            if (_opGgWindow.WindowState == WindowState.Minimized) _opGgWindow.WindowState = WindowState.Normal;
            _opGgWindow.Activate();
        }
        await UpdateOpGgPage();
    }

    private async void OpRoleChanged(object sender, SelectionChangedEventArgs e) => await UpdateOpGgPage();

    private Task UpdateOpGgPage()
    {
        if (_opGgWindow is not { } sideWindow || _selection is not { IsPicking: true } selection || _league is not { } league)
            return Task.CompletedTask;
        var alias = _opGgShowsChampion ? league.ChampionAlias(selection.Champion) : "";
        var role = (OpRole.SelectedItem as ComboBoxItem)?.Content as string ?? "全部分路";
        var label = alias.Length > 0 ? $"{league.ChampionName(selection.Champion)} · {role}" : $"{role} · 英雄排行";
        return sideWindow.OpenPage(OpGgUrl(alias, OpRole.SelectedIndex), label);
    }

    private void ResizeRoster(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)(e.NewSize.Width / 192), 1, 5);
        BlueSeats.Tag = RedSeats.Tag = columns;
    }

    private void ToggleHistorySummary(object sender, RoutedEventArgs e)
    {
        HistorySummary.Visibility = HistorySummaryToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistorySummaryWidth();
        HistoryFeed.Margin = HistorySummaryToggle.IsChecked == true ? new Thickness(12, 0, 0, 0) : new Thickness(0);
    }

    private void HistoryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer) return;
        var fade = ReferenceEquals(viewer, HistorySummaryScroll) ? SummaryBottomFade :
            ReferenceEquals(viewer.TemplatedParent, MatchGrid) ? MatchBottomFade : null;
        if (fade is not null) fade.Visibility = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHistorySummaryWidth() => HistorySummaryColumn.Width = HistorySummaryToggle.IsChecked == true
        ? new GridLength(Math.Clamp(ActualWidth * .21, 218, 276)) : new GridLength(0);

    private async Task RefreshRunes(Selection selection)
    {
        if (_league is not { } league || _selectionScope is not { } scope) return;
        var ct = scope.Token;
        _retryRunes = DateTime.UtcNow.AddSeconds(6);
        try
        {
            var runes = await league.Recommendations(selection, ct);
            if (ct.IsCancellationRequested || _selection != selection) return;
            _clientRunes = runes;
            ShowRunes();
        }
        catch (OperationCanceledException) { }
    }

    private void ShowRunes()
    {
        if (RuneList is null || _applying) return;
        var rows = Array.Empty<RuneRow>();
        RunePortrait.DataContext = null;
        if (_selection is { IsPicking: true, Champion: > 0 } selection && _league is { } league)
        {
            RuneTitle.Text = league.ChampionName(selection.Champion);
            RunePortrait.DataContext = _portraits?.Get(selection.Champion);
            rows = _samples.Where(sample => PresetFits(sample.Preset, selection))
                .OrderByDescending(sample => (double)sample.Games / sample.EligibleGames)
                .ThenByDescending(sample => sample.Games)
                .Select(sample => sample.ToRune()).Concat(_clientRunes).Concat(_presets.Where(p => PresetFits(p, selection))
                    .Select(p => new Rune(p.Name, p.PrimaryStyleId, p.SubStyleId, p.SelectedPerkIds, "本地预设")))
                .DistinctBy(r => string.Join(",", r.SelectedPerkIds))
                .Select(r => new RuneRow(r, r.SelectedPerkIds
                    .Select(id => RuneIcon(league, id)).ToArray(),
                    selection.Champion, selection.Position, selection.Map, string.Join(".", selection.Version.Split('.').Take(2))))
                .ToArray();
            RuneStatus.Text = _applied is { } applied ? $"已应用 {applied.Name}" : rows.Length > 0
                ? "选定方案后可应用到本局。" : "暂无适用方案，可稍后重试或导入符文预设。";
        }
        else if (_selection?.CanShowRoster == true && _applied is { } current)
        {
            RuneTitle.Text = "本局已应用符文";
            RunePortrait.DataContext = _portraits?.Get(current.ChampionId);
            rows = [current];
            RuneStatus.Text = "选人已结束，可查看或复制本局符文。";
        }
        else
        {
            RuneTitle.Text = "选人时推荐符文";
            RuneStatus.Text = _presets.Length > 0 ? $"已导入 {_presets.Length} 个预设，选中匹配英雄后显示。" : "选中英雄后显示适用的方案。";
        }
        var previous = RuneList.SelectedItem as RuneRow;
        RuneList.ItemsSource = rows;
        if (RuneList.Items.Count > 0)
        {
            var index = Array.FindIndex(rows, r => previous is not null && r.Rune.SelectedPerkIds.SequenceEqual(previous.Rune.SelectedPerkIds));
            RuneList.SelectedIndex = Math.Max(0, index);
        }
        SetButtons();
    }

    private static bool PresetFits(RunePreset preset, Selection selection) =>
        preset.ChampionId == selection.Champion && (preset.MapId == 0 || preset.MapId == selection.Map) &&
        (string.IsNullOrWhiteSpace(preset.Position) || preset.Position.Equals(selection.Position, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(preset.Version) || string.Join(".", selection.Version.Split('.').Take(2)).Equals(preset.Version, StringComparison.OrdinalIgnoreCase));

    private void RuneChanged(object sender, SelectionChangedEventArgs e) { if (CopyButton is not null) SetButtons(); }
    private async void ApplyRune(object sender, RoutedEventArgs e)
    {
        if (_applying || _league is not { } league || _selection is not { IsPicking: true } selection ||
            RuneList.SelectedItem is not RuneRow row || _selectionScope is not { } scope) return;
        var ct = scope.Token;
        _applying = true;
        RuneStatus.Text = "正在应用符文…";
        SetButtons();
        string? error = null;
        try
        {
            await league.Apply(selection, row.Rune, ct);
            if (!ct.IsCancellationRequested) _applied = row;
        }
        catch (LeagueException ex) { if (!ct.IsCancellationRequested) error = ex.Message; }
        catch (OperationCanceledException) { }
        finally
        {
            _applying = false;
            ShowRunes();
            if (error is not null) RuneStatus.Text = error;
        }
    }

    private void ImportRunes(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入符文预设", Filter = "符文预设 JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1_048_576) throw new JsonException();
            var data = JsonSerializer.Deserialize<RunePreset[]>(File.ReadAllText(dialog.FileName), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data is null || data.Length == 0 || data.Any(p => p is null || p.ChampionId <= 0 ||
                !new Rune(p.Name, p.PrimaryStyleId, p.SubStyleId, p.SelectedPerkIds, "").IsValid)) throw new JsonException();
            _presets = data;
            ShowRunes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { RuneStatus.Text = "无法导入预设，请检查 JSON 和符文配置。"; }
    }

    private void CopyRune(object sender, RoutedEventArgs e)
    {
        if (RuneList.SelectedItem is not RuneRow row) return;
        try
        {
            Clipboard.SetText(JsonSerializer.Serialize(new[] { new RunePreset(row.ChampionId, row.Position, row.MapId, row.Version,
                row.Rune.Name, row.Rune.PrimaryStyleId, row.Rune.SubStyleId, row.Rune.SelectedPerkIds) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            RuneStatus.Text = "已复制符文配置。";
        }
        catch (System.Runtime.InteropServices.ExternalException) { RuneStatus.Text = "剪贴板正忙，请重试。"; }
    }

    private void ImportRuneSamples(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入符文统计", Filter = "符文统计 JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1_048_576) throw new JsonException();
            var data = JsonSerializer.Deserialize<RuneSample[]>(File.ReadAllText(dialog.FileName), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data is null || data.Length == 0 || data.Any(sample => sample is null || !sample.IsValid)) throw new JsonException();
            _samples = data;
            ShowRunes();
            if (_selection?.IsPicking != true) RuneStatus.Text = $"已导入 {_samples.Length} 套符文统计，选中匹配英雄后显示。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { RuneStatus.Text = "无法导入符文统计，请检查 JSON、版本和样本量。"; }
    }

    internal void UpdateSeats(Seat[] seats, Selection selection)
    {
        if (_current is not { } current || _league is not { } league) return;
        RosterTitle.Text = selection.IsPicking ? "选人阶段 · 队友战绩" : "双方阵容";
        RosterStatus.Text = selection.IsPicking ? PickRosterStatus(seats, current) :
            seats.Length > 0 ? "点击玩家名称查看战绩" :
                selection.Phase == "GameStart" ? "正在等待客户端返回本场阵容…" : "当前暂未取得本场阵容，正在重试。";
        var own = Array.Find(seats, seat => seat.Player.Puuid == current.Puuid || current.Id > 0 && seat.Player.Id == current.Id ||
            current.Name.Length > 0 && current.Name.Equals(seat.Player.Name, StringComparison.OrdinalIgnoreCase));
        BlueTeamTitle.Text = selection.IsPicking ? "己方" : own?.Side == 1 ? "蓝方 · 己方" : own?.Side == 2 ? "蓝方 · 对手" : "蓝方";
        RedTeamTitle.Text = selection.IsPicking ? "对手 · 开局后显示" : own?.Side == 2 ? "红方 · 己方" : own?.Side == 1 ? "红方 · 对手" : "红方";
        foreach (var (side, rows) in new[] { (1, _blue), (2, _red) })
        {
            var team = seats.Where(seat => seat.Side == side).ToArray();
            if (team.Length == 0) continue;
            while (rows.Count > team.Length) rows.RemoveAt(rows.Count - 1);
            for (var index = 0; index < team.Length; index++)
            {
                var seat = team[index];
                if (index < rows.Count && rows[index].Seat == seat)
                {
                    RequestProfile(rows[index]);
                    continue;
                }
                var id = seat.Champion > 0 ? seat.Champion : league.ChampionId(seat.ChampionLabel);
                var champion = id > 0 ? league.ChampionName(id) : JsonFields.First(seat.ChampionLabel,
                    selection.IsPicking ? "等待选择英雄" : league.ChampionName(id));
                if (index < rows.Count && rows[index].Seat.Player == seat.Player && rows[index].Seat.IsBot == seat.IsBot &&
                    rows[index].Seat.IdentityHidden == seat.IdentityHidden)
                {
                    rows[index].UpdateChampion(seat, champion, _portraits?.Get(id));
                    RequestProfile(rows[index]);
                    continue;
                }
                var row = new SeatRow(seat, champion, _portraits?.Get(id));
                if (index == rows.Count) rows.Add(row); else rows[index] = row;
                RequestProfile(row);
            }
        }

        void RequestProfile(SeatRow row)
        {
            if (!row.CanOpenHistory || row.ProfileReady || row.ProfileLoading || row.RetryProfileAfter > DateTime.UtcNow) return;
            _ = FillSeat(league, row, current, _rosterScope?.Token ?? _accountScope.Token);
        }
    }

    private static string PickRosterStatus(Seat[] seats, Player current)
    {
        if (seats.Length == 0) return "正在等待客户端返回己方选人信息…";
        var teammates = seats.Where(seat => !seat.IsBot && !HistoryInsights.SamePlayer(seat.Player, current)).ToArray();
        if (teammates.Length == 0) return "当前阵容暂无其他玩家。";
        var available = teammates.Count(seat => seat.Player.HasLookupIdentity);
        var hidden = teammates.Count(seat => seat.IdentityHidden);
        if (available == 0 && hidden > 0)
            return $"客户端隐藏了 {hidden} 位队友的身份资料。身份公开后可查看战绩。";
        if (available == 0) return "正在等待客户端提供队友查询标识…";
        return $"{available} 位队友可查询最近 10 场 · 点击名称打开完整战绩" +
            (hidden > 0 ? $" · {hidden} 位身份隐藏" : "");
    }

    private async Task FillSeat(LeagueService league, SeatRow row, Player self, CancellationToken ct)
    {
        var key = HistoryInsights.PlayerKey(row.Seat.Player);
        row.ProfileLoading = true;
        try
        {
            if (_rosterProfiles.TryGetValue(key, out var cached))
            {
                ShowProfile(row, cached, ct);
                return;
            }
            await _seatRequests.WaitAsync(ct);
            try
            {
                var lookup = row.LookupPlayer;
                if (lookup.Puuid.Length == 0)
                {
                    lookup = await league.ResolvePublicPlayer(lookup, self, ct);
                    if (!IsCurrentSeat(row, ct)) return;
                    row.SetIdentity(lookup);
                }
                var identity = LoadSeatIdentity(league, row, ct);
                var profile = await league.Profile(lookup, self, ct, rank =>
                {
                    if (IsCurrentSeat(row, ct)) row.SetProfile(new(rank, 0, 0, "正在读取近期战绩…"));
                });
                ShowProfile(row, profile, ct);
                var resolved = await identity;
                if (!ct.IsCancellationRequested && profile.Status.Length == 0)
                    _rosterProfiles[key] = profile with { Identity = resolved ?? lookup };
            }
            finally { _seatRequests.Release(); }
        }
        catch (LeagueException ex) { if (IsCurrentSeat(row, ct)) row.SetProfile(new(row.Stats?.Rank, 0, 0, ex.Message)); }
        catch (OperationCanceledException) { }
        finally
        {
            row.ProfileLoading = false;
            row.RetryProfileAfter = DateTime.UtcNow.AddSeconds(8);
        }
    }

    private bool IsCurrentSeat(SeatRow row, CancellationToken ct) =>
        !ct.IsCancellationRequested && (_blue.Contains(row) || _red.Contains(row));

    private async Task<Player?> LoadSeatIdentity(LeagueService league, SeatRow row, CancellationToken ct)
    {
        if (row.LookupPlayer.HasRiotId || row.LookupPlayer.Puuid.Length == 0) return null;
        try
        {
            var player = await league.CompletePublicProfile(row.LookupPlayer, ct);
            if (IsCurrentSeat(row, ct)) row.SetIdentity(player);
            return player;
        }
        catch (OperationCanceledException) { return null; }
    }

    private void ShowProfile(SeatRow row, PlayerRecord profile, CancellationToken ct)
    {
        if (!IsCurrentSeat(row, ct)) return;
        if (profile.Identity is { } identity) row.SetIdentity(identity);
        row.ProfileReady = profile.Status.Length == 0;
        row.SetProfile(profile, profile.Games?.Select(PreviewGame).ToArray());
    }

    private RecentGameRow PreviewGame(PlayerMatch game) => new(game.Match.Id, game.Match.Mode,
        game.Match.Started?.ToString("MM-dd HH:mm") ?? "时间未知", game.Participant.Won, game.Participant.Kda,
        _portraits?.Get(game.Participant.Champion));

    private async void OpenPreviewGame(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecentGameRow game } button) return;
        e.Handled = true;
        if (button.CommandParameter is Player player)
        {
            await OpenPlayerHistory(player, true);
            if (_target is null || (!HistoryInsights.SamePlayer(player, _target) &&
                !player.Name.Equals(_target.Name, StringComparison.OrdinalIgnoreCase))) return;
        }
        if (MatchGrid.ItemsSource is not MatchRow[] rows || rows.FirstOrDefault(row => row.Match.Id == game.Id) is not { } match) return;
        MatchGrid.ScrollIntoView(match);
        if (!match.IsExpanded) await ShowDetails(match);
    }

    private static string TeamSummary(Participant[] team)
    {
        var parts = new List<string>();
        if (team.All(p => p.Combat is { Kills: >= 0, Deaths: >= 0, Assists: >= 0 }))
            parts.Add($"{team.Sum(p => p.Combat!.Kills!.Value)} / {team.Sum(p => p.Combat!.Deaths!.Value)} / {team.Sum(p => p.Combat!.Assists!.Value)}");
        if (team.All(p => p.Combat?.Gold is >= 0)) parts.Add($"{team.Sum(p => p.Combat!.Gold!.Value):N0} 金币");
        return string.Join("    ·    ", parts);
    }

    private async void OpenPlayer(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SeatRow row }) await OpenPlayerHistory(row.LookupPlayer, true);
    }

    private async void OpenPlayerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not Button && sender is ListBox { SelectedItem: SeatRow row })
        {
            e.Handled = true;
            await OpenPlayerHistory(row.LookupPlayer, true);
        }
    }

    private async void OpenPlayerLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement control) return;
        e.Handled = true;
        switch (control.DataContext)
        {
            case SeatRow seat: await OpenPlayerHistory(seat.LookupPlayer, true); break;
            case DetailRow detail: await OpenDetailPlayerHistory(detail.Identity, detail.MatchId); break;
            case TeammateRow teammate: await OpenPlayerHistory(teammate.Player, false); break;
            case MatchRosterRow entry: await OpenPlayerHistory(entry.Player, false); break;
        }
    }

    internal async Task OpenDetailPlayerHistory(Player player, long matchId = 0)
    {
        if (player.Name.Length == 0 || _target is not { } target || HistoryInsights.SamePlayer(player, target)) return;
        var origin = _activeHistoryPage is { } source && _expandedMatches.Count > 0 ? new MatchReturn(source, matchId) : null;
        await OpenHistoryPage(player, origin: origin);
    }

    private void RefreshMatchReturn() => BackToMatchButton.Visibility =
        _activeHistoryPage?.ReturnTo is { } origin && _historyPages.Contains(origin.Page) ? Visibility.Visible : Visibility.Collapsed;

    private async void BackToMatch(object sender, RoutedEventArgs e)
    {
        if (_activeHistoryPage is not { ReturnTo: { } origin } page || !_historyPages.Contains(origin.Page)) return;
        page.ReturnTo = null;
        await ActivateHistoryPage(origin.Page);
        if (_activeHistoryPage == origin.Page && (_expandedMatches.GetValueOrDefault(origin.MatchId) ??
            _expandedMatches.Values.LastOrDefault()) is { } match) MatchGrid.ScrollIntoView(match);
    }

    internal async Task OpenPlayerHistory(Player player, bool fromRoster)
    {
        if ((player.Name.Length == 0 && player.Puuid.Length == 0 && player.Id <= 0) || _league is null || _current is null) return;
        if (fromRoster) SetRosterReturn(true);
        await OpenHistoryPage(player);
    }

    private async void HistoryPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == HistoryPlayerTabs && HistoryPlayerTabs.SelectedItem is HistoryPlayerPage selected && selected != _activeHistoryPage)
            await ActivateHistoryPage(selected);
    }

    private async void CloseHistoryPage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryPlayerPage { IsOwn: false } page }) return;
        e.Handled = true;
        var index = _historyPages.IndexOf(page);
        if (index < 0) return;
        var active = page == _activeHistoryPage;
        foreach (var tab in _historyPages.Where(tab => tab.ReturnTo?.Page == page)) tab.ReturnTo = null;
        if (active) _activeHistoryPage = null;
        _historyPages.RemoveAt(index);
        if (active && _historyPages.Count > 0)
            await ActivateHistoryPage(_historyPages[Math.Min(index, _historyPages.Count - 1)]);
        else RefreshMatchReturn();
    }

    private void BackToRoster(object sender, RoutedEventArgs e)
    {
        EndScope(ref _historyNavigationScope);
        SetRosterReturn(false);
        Pages.SelectedItem = RosterTab;
    }

    private void SetRosterReturn(bool available)
    {
        _openedHistoryFromRoster = available;
        BackToRosterButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private static CancellationToken StartScope(ref CancellationTokenSource? source, CancellationToken parent)
    {
        EndScope(ref source);
        source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        return source.Token;
    }
    private static void EndScope(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private sealed record MatchIcon(string Name, ChampionPortrait? Portrait, string Description = "", bool IsAugment = false)
    {
        public object Tooltip => Description.Length > 0 ? new IconHint(Name, Description) : Name;
    }
    internal sealed record MatchRosterRow(Player Player, string Champion, ChampionPortrait? Portrait, bool IsViewed)
    {
        public string Name => Player.Name;
        public bool CanOpenHistory => Name.Length > 0 && (Player.Puuid.Length > 0 || Player.Id > 0 || Player.HasRiotId);
        public string Title => Name.Length == 0 ? Champion : Name.Split('#', 2)[0];
        public string Tooltip => Name.Length == 0 ? Champion : $"{Name} · {Champion}";
    }
    private sealed record MasteryRow(string Champion, long Level, long Points, ChampionPortrait? Portrait);
    private sealed class MatchRow(Match match, Participant participant, string champion, ChampionPortrait? portrait,
        MatchIcon[] items, MatchRosterRow[] allies, MatchRosterRow[] opponents, MatchReview initialReview,
        MatchIcon[] spells, MatchIcon[] modifiers) : INotifyPropertyChanged
    {
        public Match Match { get; private set; } = match;
        public Participant Participant { get; private set; } = participant;
        public string Champion { get; } = champion;
        public ChampionPortrait? Portrait { get; } = portrait;
        public MatchIcon[] Items { get; private set; } = items;
        public MatchRosterRow[] Allies { get; set; } = allies;
        public MatchRosterRow[] Opponents { get; set; } = opponents;
        public MatchIcon[] Spells { get; set; } = spells;
        public MatchIcon[] PreviewModifiers { get; set; } = modifiers;
        public RadarSnapshot? Radar { get; set; } = RadarSnapshot.Build(match, participant);
        public string Result => Match.EarlyEnd ? "重开" : !Participant.OutcomeKnown ? "结果未返回" : Participant.Won ? "胜利" : "失败";
        public bool Won => Participant.Won;
        public bool HasWinResult => Participant.OutcomeKnown && !Match.EarlyEnd;
        public string Mode => Match.Mode;
        public string Kda => Participant.Kda;
        public string Damage => Participant.Damage.ToString("N0");
        public string Cs => Participant.Cs.ToString("N0");
        public string DamageShare { get; set; } = Share(match, participant);
        public static string Share(Match game, Participant viewed)
        {
            var team = game.Players.Where(p => p.Team == viewed.Team).ToArray();
            return team.Length > 1 && team.All(p => p.Combat?.ChampionDamage is >= 0) &&
                team.Sum(p => p.Damage) is > 0 and var total ? $"{(double)viewed.Damage / total:P0}" : "—";
        }
        public string KdaSummary => Radar?.Axes.FirstOrDefault(a => a.Name == "KDA")?.Value is { } ratio
            ? $"{ratio} KDA" + (Radar.Axes.FirstOrDefault(a => a.Name == "参团率")?.Value is { } kp ? $" · {kp}" : "") : "K / D / A";
        public string CsPerMinute => Match.Duration.TotalMinutes > 0 ? $"{Participant.Cs / Match.Duration.TotalMinutes:F1} / 分" : "补刀";
        public string Time => Match.Started?.ToString("MM-dd HH:mm") ?? "—";
        public string Duration => $"{(int)Match.Duration.TotalMinutes}:{Match.Duration.Seconds:00}";
        public string Caption => $"{Champion}  ·  {Mode}  ·  {Time}  ·  {Duration}";
        public bool IsExpanded { get; private set; }
        public bool IsLoadingDetail => DetailStatus == "正在读取单局详情…";
        public int DetailRequest { get; set; }
        public string DetailLabel => IsExpanded ? "收起详情" : "对局详情 ›";
        public string DetailStatus { get; private set; } = "";
        public DetailTeam[] Teams { get; private set; } = [];
        public MatchReview Review { get; set; } = initialReview;
        public Task<Match>? CardDetails { get; set; }
        public bool TimelinePending { get; set; }
        public Match? ReviewedMatch { get; set; }
        public ReviewTag[] Metrics { get; set; } = HistoryInsights.MetricTags([new(match, participant)]);
        public ReviewTag[] Tags => [.. Review.Tags, .. Metrics.Take(4)];
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyTags() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tags)));

        public void SetMatch(Match updated, Participant viewed, MatchRosterRow[] allies, MatchRosterRow[] opponents,
            MatchIcon[] items, MatchIcon[] spells, MatchIcon[] modifiers)
        {
            Match = updated;
            Participant = viewed;
            Allies = allies;
            Opponents = opponents;
            Items = items;
            Spells = spells;
            PreviewModifiers = modifiers;
            Radar = RadarSnapshot.Build(updated, viewed);
            DamageShare = Share(updated, viewed);
            Review = PerformanceReview.Evaluate(updated, viewed);
            Metrics = HistoryInsights.MetricTags([new(updated, viewed)]);
            ReviewedMatch = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public void SetDetail(bool expanded, string status = "", DetailTeam[]? teams = null)
        {
            IsExpanded = expanded;
            DetailStatus = status;
            Teams = teams ?? [];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
    private sealed record StatRow(string Champion, int Wins, int Games, ChampionPortrait? Portrait)
    {
        public string RateLabel => Games >= 3 ? $"{(double)Wins / Games:P0}" : "样本少";
        public string Summary => $"{Games} 场 · {Wins} 胜 {Games - Wins} 负";
    }
    private sealed record RecentGameRow(long Id, string Mode, string Time, bool Won, string Kda, ChampionPortrait? Portrait)
    {
        public string Result => Won ? "胜" : "负";
    }
    private sealed record TeammateRow(Player Player, ChampionPortrait? Portrait, string Record)
    {
        public string Title => Player.Name;
        public bool CanOpenHistory => Player.Name.Length > 0;
    }
    private sealed record EncounterRow(string Relation, RecentGameRow Game, string SelfKda);
    private sealed record MatchReturn(HistoryPlayerPage Page, long MatchId);
    private sealed class HistoryPlayerPage(Player player, bool isOwn, ChampionPortrait? avatar) : INotifyPropertyChanged
    {
        public Player Player { get; private set; } = player;
        public bool IsOwn { get; } = isOwn;
        public bool CanClose => !IsOwn;
        public string Title => IsOwn ? "我的战绩" : JsonFields.First(Player.Name, "队友战绩");
        public string Tooltip => Player.Name;
        public ChampionPortrait? Avatar { get; private set; } = avatar;
        public bool ProfileLoaded { get; set; }
        public bool HasPage { get; set; }
        public bool UseShard { get; set; } = true;
        public Match[] Matches { get; set; } = [];
        public MatchRow[] Expanded { get; set; } = [];
        public int Offset { get; set; }
        public int Filter { get; set; }
        public bool CanContinue { get; set; }
        public string Status { get; set; } = "";
        public MatchReturn? ReturnTo { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetProfile(Player profile, ChampionPortrait? portrait)
        {
            Player = profile;
            Avatar = portrait;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
    private sealed record DetailTeam(string Name, bool Won, string Summary, string Objectives, DetailRow[] Players)
    {
        public string Result => Won ? "胜利" : "失败";
    }
    private sealed record DetailRow(string Player, string Champion, string Kda, string Ratio, string Participation,
        long Cs, long? Damage, long? Taken, long? Gold, double DamagePercent, double TakenPercent,
        double Minutes, int Level, MatchIcon[] Spells, MatchIcon[] Modifiers,
        MatchIcon[] Items, ChampionPortrait? Portrait, Player Identity, long MatchId, RadarSnapshot? Radar, ReviewTag[] Tags)
    {
        public bool CanOpenHistory => Identity.Name.Length > 0;
        public string CombatLine => Participation.Length > 0 ? $"{Kda.Replace(" ", "")} ({Participation})" : Kda;
        public string KdaLine => Ratio == "—" ? "—" : $"{Ratio} KDA";
        public string GoldPerMinute => Minutes > 0 && Gold is { } value ? $"{value / Minutes:N0} / 分" : "—";
        public string LevelText => Level > 0 ? Level.ToString() : "";
        public string ReviewNote => string.Join("\n", Tags.Select(tag => $"{tag.Label} · {tag.Reason}"));
    }
    private sealed record RuneRow(Rune Rune, MatchIcon[] Icons, int ChampionId, string Position, int MapId, string Version)
    {
        public string Name => Rune.Name;
        public string Summary => Rune.Summary;
        public string Perks => string.Join("、", Icons.Select(icon => icon.Name));
    }
    private sealed class SeatRow(Seat seat, string champion, ChampionPortrait? portrait) : INotifyPropertyChanged
    {
        public Seat Seat { get; private set; } = seat;
        public ChampionPortrait? Portrait { get; private set; } = portrait;
        private string _champion = champion;
        public string Champion => Seat.Position.ToUpperInvariant() switch
        {
            "TOP" => $"上路 · {_champion}", "JUNGLE" => $"打野 · {_champion}",
            "MIDDLE" => $"中路 · {_champion}", "BOTTOM" => $"下路 · {_champion}",
            "UTILITY" => $"辅助 · {_champion}", _ => _champion
        };
        public Player LookupPlayer { get; private set; } = seat.Player;
        public string Title => JsonFields.First(LookupPlayer.Name, Seat.IsBot ? "机器人" : CanOpenHistory ? "队友战绩" :
            Seat.IdentityHidden ? "匿名队友" : "身份未公开");
        public bool CanOpenHistory => LookupPlayer.HasLookupIdentity;
        public bool ProfileLoading { get; set; }
        public bool ProfileReady { get; set; }
        public DateTime RetryProfileAfter { get; set; }
        public string Summary { get; private set; } = seat.Player.HasLookupIdentity ? "正在读取资料…" :
            seat.IsBot ? "电脑玩家" : seat.IdentityHidden ? "客户端隐藏了身份" : "等待客户端提供查询标识";
        public PlayerRecord? Stats { get; private set; }
        public ImageSource? RankIcon { get; private set; }
        public ReviewTag[] Tags { get; private set; } = [];
        public RecentGameRow[] Games { get; private set; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetIdentity(Player player)
        {
            if (player == LookupPlayer) return;
            LookupPlayer = player;
            PropertyChanged?.Invoke(this, new(nameof(LookupPlayer)));
            PropertyChanged?.Invoke(this, new(nameof(Title)));
        }
        public void UpdateChampion(Seat updated, string name, ChampionPortrait? image)
        {
            Seat = updated;
            _champion = name;
            Portrait = image;
            PropertyChanged?.Invoke(this, new(nameof(Champion)));
            PropertyChanged?.Invoke(this, new(nameof(Portrait)));
        }
        public void SetProfile(PlayerRecord profile, RecentGameRow[]? games = null)
        {
            Stats = profile;
            RankIcon = profile.Rank is { } rank ? RankEmblem(rank.Tier) : null;
            Summary = profile.Status.Length > 0 ? profile.Status : profile.HasResults ? "" : "暂无有效胜负";
            Tags = profile.Tags ?? [];
            Games = games ?? [];
            PropertyChanged?.Invoke(this, new(nameof(Stats)));
            PropertyChanged?.Invoke(this, new(nameof(RankIcon)));
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
            PropertyChanged?.Invoke(this, new(nameof(Tags)));
            PropertyChanged?.Invoke(this, new(nameof(Games)));
        }
    }
}
