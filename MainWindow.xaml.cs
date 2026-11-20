using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DisplayBrightness;

public partial class MainWindow : Window
{
    private const int SchedulesPerPage = 2;
    private const double MonitorSlotWidth = 328;
    private readonly List<MonitorCard> cards = [];
    private int schedulePage;
    private int monitorPage;
    private int monitorsPerPage = 2;
    private readonly DispatcherTimer scheduleTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, int> pendingCorrections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DdcMonitor Monitor, int Percent)> pendingManualLevels = new(StringComparer.OrdinalIgnoreCase);
    private int? pendingBasePercent;
    private int defaultBasePercent = 50;
    private readonly Dictionary<string, (int Target, DateTimeOffset RetryAt)> failedManualTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool monitorRecoveryRequested;
    private readonly HashSet<DdcMonitor> scheduledMonitors = [];
    private BrightnessSchedule schedule = BrightnessSchedule.Empty;
    private SyncPreferences sync = SyncPreferences.Default;
    private bool syncLoadFailed;
    private ScheduledPeriod? activePeriod;
    private DateTimeOffset retryAfter;
    private BrightnessTray? tray;
    private bool initialized;
    private bool editingSchedule;
    private bool busy;
    private Task? automaticApply;
    private bool closed;
    private bool exitRequested;
    private bool retryStartupDiscovery;
    private DateTimeOffset startupDiscoveryAfter;
    private string statusMessage = "";
    private string scheduleMessage = "";

    public MainWindow()
    {
        InitializeComponent();
    }

    internal async Task InitializeAsync(bool silent)
    {
        if (tray is not null)
            return;

        tray = new BrightnessTray(
            () => Dispatcher.BeginInvoke(ShowFromTray),
            () => Dispatcher.BeginInvoke(ExitFromTray),
            TrayOpening);

        try
        {
            schedule = BrightnessSchedule.Load();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            SetScheduleStatus("无法读取计划。");
        }

        try
        {
            sync = SyncPreferences.Load();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            syncLoadFailed = true;
        }

        SyncToggle.IsChecked = sync.Enabled;
        BaseInput.SetNumber(sync.BasePercent ?? defaultBasePercent);
        retryStartupDiscovery = silent || schedule.Enabled;
        bool startupLoadFailed = false;
        try
        {
            StartupToggle.IsChecked = StartupRegistration.IsEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            startupLoadFailed = true;
        }

        await RefreshAsync();
        if (closed)
            return;

        if (sync.BasePercent is null && cards.FirstOrDefault(card => card.Reading.HasValue)?.Reading is Brightness first)
        {
            defaultBasePercent = first.Percent;
            BaseInput.SetNumber(defaultBasePercent);
        }
        if (syncLoadFailed)
            SetStatus("无法读取已保存的同步设置。重新应用同步亮度即可保存新设置。");
        if (startupLoadFailed)
            SetStatus("无法读取 Windows 的启动设置。");

        initialized = true;
        scheduleTimer.Tick += ScheduleTick;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.SessionSwitch += SessionSwitched;
        scheduleTimer.Start();
        await CheckScheduleAsync();
    }

    private void StartupClicked(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupToggle.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            SetStatus(enabled ? "已启用开机静默启动。" : "已关闭开机静默启动。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            StartupToggle.IsChecked = !enabled;
            SetStatus(ex is PathTooLongException
                ? "启动路径过长。请把程序移到路径更短的位置再试。"
                : "无法更新开机启动设置，请检查当前用户的权限。");
        }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        await WaitForAutomaticApplyAsync();
        await ApplyPendingEditsAsync();
        if (!busy && !closed && !editingSchedule)
            await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        SetStatus("正在检测显示器…");
        try
        {
            var readings = await Task.Run(DiscoverAndReadMonitors);
            if (closed)
            {
                foreach (var (monitor, _, _) in readings)
                    monitor.Dispose();
                return;
            }

            scheduledMonitors.Clear();
            retryAfter = default;
            ReleaseCards();
            foreach (var (monitor, reading, message) in readings)
            {
                int correction = pendingCorrections.GetValueOrDefault(monitor.Id, sync.CorrectionFor(monitor.Id));
                int manualPercent = pendingManualLevels.TryGetValue(monitor.Id, out var edit)
                    ? edit.Percent : sync.ManualPercentFor(monitor.Id) ?? reading?.Percent ?? defaultBasePercent;
                var card = new MonitorCard(monitor, correction, manualPercent);
                card.SetReading(reading, message);
                card.ManualConfirmed += ManualConfirmed;
                card.InputEdited += BrightnessInputChanged;
                card.CorrectionConfirmed += CorrectionConfirmed;
                cards.Add(card);
            }

            MonitorHeading.Text = $"显示器 · {cards.Count} 台";
            UpdateMonitorPage();
            UpdateSyncControls();
            SetStatus(cards.Count == 0
                ? "未找到可访问的显示器。请确认显示器已连接并点亮，再刷新屏幕。"
                : "");
            var recorded = sync.RecordMonitors(readings.Select(result => result.Monitor));
            if (!syncLoadFailed && !ReferenceEquals(recorded, sync))
                TrySaveSync(recorded);
        }
        catch (Win32Exception ex)
        {
            SetStatus($"检测显示器失败（0x{ex.NativeErrorCode:X8}）。请确认显示器已连接，再刷新屏幕。");
        }
        finally
        {
            retryStartupDiscovery &= cards.Count == 0;
            startupDiscoveryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            RefreshScheduleList();
            FinishOperation();
        }
    }

    private static List<(DdcMonitor Monitor, Brightness? Reading, string Message)> DiscoverAndReadMonitors()
    {
        var monitors = DdcMonitor.Discover();
        try
        {
            var readings = new List<(DdcMonitor, Brightness?, string)>(monitors.Count);
            foreach (DdcMonitor monitor in monitors)
            {
                try
                {
                    readings.Add((monitor, monitor.Read(), ""));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    readings.Add((monitor, null, "无法读取亮度，请检查显示器的 DDC/CI 设置。"));
                }
            }
            return readings;
        }
        catch
        {
            foreach (DdcMonitor monitor in monitors)
                monitor.Dispose();
            throw;
        }
    }

    private void CardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        Cards.Tag = Math.Min(MonitorSlotWidth, width);
        int capacity = width >= 2 * MonitorSlotWidth ? 2 : 1;
        if (capacity == monitorsPerPage)
            return;

        monitorPage = monitorPage * monitorsPerPage / capacity;
        monitorsPerPage = capacity;
        UpdateMonitorPage();
    }

    private void MonitorPageClicked(object sender, RoutedEventArgs e)
    {
        monitorPage += sender == NextMonitorPage ? 1 : -1;
        UpdateMonitorPage();
    }

    private void UpdateMonitorPage()
    {
        int pages = Math.Max(1, (cards.Count + monitorsPerPage - 1) / monitorsPerPage);
        monitorPage = Math.Clamp(monitorPage, 0, pages - 1);
        Cards.ItemsSource = cards.Skip(monitorPage * monitorsPerPage).Take(monitorsPerPage).ToArray();
        MonitorPager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousMonitorPage.IsEnabled = monitorPage > 0;
        NextMonitorPage.IsEnabled = monitorPage + 1 < pages;
        MonitorPageLabel.Text = $"{monitorPage + 1}/{pages}";
    }

    private async void SyncChanged(object sender, RoutedEventArgs e)
    {
        await WaitForAutomaticApplyAsync();
        await ApplyPendingEditsAsync();
        if (closed)
            return;

        bool enabled = SyncToggle.IsChecked == true;
        if (initialized && (busy || editingSchedule) && enabled != sync.Enabled)
        {
            SyncToggle.IsChecked = sync.Enabled;
            return;
        }

        if (initialized && !busy && enabled != sync.Enabled)
        {
            if (!TrySaveSync(sync with { Enabled = enabled }))
                SyncToggle.IsChecked = sync.Enabled;
            else
            {
                failedManualTargets.Clear();
                if (!enabled)
                {
                    foreach (var card in cards)
                    {
                        card.SetManualTarget(sync.ManualPercentFor(card.Monitor.Id) ??
                            card.Reading?.Percent ?? defaultBasePercent);
                    }
                }
                scheduledMonitors.Clear();
                retryAfter = default;
                RefreshScheduleList();
                UpdateTrayBrightness();
            }
        }
        UpdateSyncControls();
        await CheckScheduleAsync();
    }

    private void BrightnessInputChanged(object? sender, EventArgs e) => UpdateStatusLine();

    private void UpdateSyncControls()
    {
        BaseInput.IsEnabled = sync.Enabled && !schedule.Enabled && !busy;
        foreach (MonitorCard card in cards)
            card.UpdateMode(sync.Enabled, schedule.Enabled);
        UpdateStatusLine();
    }

    private bool TrySaveSync(SyncPreferences preferences)
    {
        try
        {
            preferences.Save();
            sync = preferences;
            syncLoadFailed = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("无法保存显示器设置。请检查本地应用数据目录的写入权限后重试。");
            return false;
        }
    }

    private async void BaseConfirmed(object sender, RoutedEventArgs e)
    {
        if (closed || schedule.Enabled || !sync.Enabled || BaseInput.Number is not int percent)
            return;

        pendingBasePercent = percent;
        await ApplyPendingEditsAsync();
    }

    private async void CorrectionConfirmed(object? sender, EventArgs e)
    {
        if (closed || !sync.Enabled || sender is not MonitorCard { Correction: int correction } card)
            return;

        pendingCorrections[card.Monitor.Id] = correction;
        await ApplyPendingEditsAsync();
    }

    private async void ManualConfirmed(object? sender, EventArgs e)
    {
        if (closed || schedule.Enabled || sync.Enabled || sender is not MonitorCard { ManualPercent: int percent } card)
            return;

        pendingManualLevels[card.Monitor.Id] = (card.Monitor, percent);
        await ApplyPendingEditsAsync();
    }

    private bool HasPendingEdits => pendingBasePercent.HasValue || pendingCorrections.Count > 0 || pendingManualLevels.Count > 0;

    private void ResumePendingEdits()
    {
        if (!closed && HasPendingEdits)
            _ = Dispatcher.InvokeAsync(ApplyPendingEditsAsync, DispatcherPriority.Background).Task.Unwrap();
    }

    private async Task ApplyPendingEditsAsync()
    {
        if (closed || busy || editingSchedule || automaticApply is not null || !HasPendingEdits)
            return;

        int? basePercent = pendingBasePercent;
        pendingBasePercent = null;
        var corrections = new Dictionary<string, int>(sync.Corrections, StringComparer.OrdinalIgnoreCase);
        var changedCorrections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, correction) in pendingCorrections)
        {
            if (correction == sync.CorrectionFor(id))
                continue;
            corrections[id] = correction;
            changedCorrections.Add(id);
        }
        pendingCorrections.Clear();

        var manualLevels = new Dictionary<string, (DdcMonitor Monitor, int Percent)>(pendingManualLevels, StringComparer.OrdinalIgnoreCase);
        pendingManualLevels.Clear();
        var updated = sync;
        if (basePercent.HasValue || changedCorrections.Count > 0)
            updated = sync with { BasePercent = basePercent ?? sync.BasePercent, Corrections = corrections };
        foreach (var (monitor, percent) in manualLevels.Values)
        {
            if (updated.ManualPercentFor(monitor.Id) != percent)
                updated = updated.WithManualPercent(monitor, percent);
        }

        if (!ReferenceEquals(updated, sync) && !TrySaveSync(updated))
            return;

        var changedCards = cards.Where(card => changedCorrections.Contains(card.Monitor.Id)).ToArray();
        if (schedule.Enabled)
        {
            if (sync.Enabled && changedCards.Length > 0)
            {
                foreach (var card in changedCards)
                    scheduledMonitors.Remove(card.Monitor);
                retryAfter = default;
                await CheckScheduleAsync(changedCards);
            }
            return;
        }

        var targets = sync.Enabled
            ? (basePercent.HasValue ? cards.ToArray() : changedCards).Select(card =>
                (card, SyncPreferences.Adjust(sync.BasePercent ?? defaultBasePercent, sync.CorrectionFor(card.Monitor.Id))))
            : cards.Where(card => manualLevels.ContainsKey(card.Monitor.Id))
                .Select(card => (card, manualLevels[card.Monitor.Id].Percent));
        var pendingTargets = targets.ToArray();
        if (pendingTargets.Length > 0)
            await RunAutomaticApplyAsync(ApplyManuallyAsync(pendingTargets));
    }

    private async Task ApplyManuallyAsync((MonitorCard Card, int Target)[] targets)
    {
        int confirmed = await ApplyTargetsAsync(targets, scheduled: false);
        MonitorCard? failed = targets.Select(target => target.Card).FirstOrDefault(card => card.Status.Length > 0);
        SetStatus(confirmed == targets.Length
            ? ""
            : failed is null ? $"{targets.Length - confirmed} 台屏幕待检查。"
            : $"{failed.Monitor.Position} · {failed.Status}");
        UpdateTrayBrightness();
    }

    private async Task<int> ApplyTargetsAsync((MonitorCard Card, int Target)[] targets, bool scheduled)
    {
        int confirmedCount = 0;
        foreach (var (card, target) in targets)
        {
            if (closed)
                break;

            if (!scheduled)
                failedManualTargets[card.Monitor.Id] = (target, DateTimeOffset.UtcNow.AddSeconds(30));
            try
            {
                var result = await Task.Run(() =>
                {
                    Brightness reading = card.Monitor.Set(target);
                    return (Reading: reading, Confirmed: reading.Current == reading.FromPercent(target));
                });
                if (closed)
                    break;

                card.SetReading(result.Reading, result.Confirmed
                    ? ""
                    : $"目标 {target}%，显示器回报 {result.Reading.Percent}%。");
                if (result.Confirmed)
                {
                    confirmedCount++;
                    failedManualTargets.Remove(card.Monitor.Id);
                    if (scheduled || (activePeriod is not null && activePeriod == schedule.ActiveAt(DateTime.Now)))
                        scheduledMonitors.Add(card.Monitor);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                if (!closed)
                    card.SetReading(null, ex is Win32Exception error
                        ? $"亮度通信失败（0x{error.NativeErrorCode:X8}），稍后重试。"
                        : "无法确认亮度，稍后重试。");
            }
        }

        return confirmedCount;
    }

    private async void AddScheduleClicked(object sender, RoutedEventArgs e) => await EditScheduleAsync(null);

    private async void EditScheduleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrightnessPeriod period })
            await EditScheduleAsync(period);
    }

    private async Task EditScheduleAsync(BrightnessPeriod? period)
    {
        await WaitForAutomaticApplyAsync();
        if (busy || editingSchedule || closed ||
            (period is not null && !schedule.Periods.Any(entry => ReferenceEquals(entry, period))))
            return;

        int basePercent = sync.BasePercent ?? defaultBasePercent;
        var dialog = new ScheduleEntryDialog(schedule, period, sync.Enabled, basePercent,
            cards.Select(card => (card.Monitor.Id, card.Monitor.Name, sync.ManualPercentFor(card.Monitor.Id) ?? basePercent)).ToArray())
        {
            Owner = this
        };
        editingSchedule = true;
        bool saved;
        try
        {
            saved = dialog.ShowDialog() == true;
        }
        finally
        {
            editingSchedule = false;
            ResumePendingEdits();
        }

        if (closed)
            return;
        if (saved)
            await ApplyUpdatedScheduleAsync(dialog.UpdatedSchedule);
        else
            await CheckScheduleAsync();
    }

    private async void ScheduleToggleClicked(object sender, RoutedEventArgs e)
    {
        await WaitForAutomaticApplyAsync();
        await ApplyPendingEditsAsync();
        if (closed)
            return;
        if (busy || editingSchedule)
        {
            ScheduleToggle.IsChecked = schedule.Enabled;
            return;
        }

        var updated = schedule with { Enabled = ScheduleToggle.IsChecked == true };
        if (updated.Enabled == schedule.Enabled)
            return;

        try
        {
            updated.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScheduleToggle.IsChecked = schedule.Enabled;
            SetScheduleStatus("无法保存计划。请检查本地应用数据目录的写入权限。");
            return;
        }

        await ApplyUpdatedScheduleAsync(updated);
    }

    private async Task ApplyUpdatedScheduleAsync(BrightnessSchedule updated)
    {
        if (updated.Enabled)
            failedManualTargets.Clear();
        var now = DateTime.Now;
        if (schedule.ActiveAt(now) != updated.ActiveAt(now))
        {
            activePeriod = null;
            scheduledMonitors.Clear();
            retryAfter = default;
        }
        schedule = updated;
        RefreshScheduleList();
        UpdateScheduleControls();
        UpdateSyncControls();
        SetScheduleStatus("");
        UpdateTrayBrightness();
        await CheckScheduleAsync();
    }

    private void RefreshScheduleList()
    {
        if (closed)
            return;

        BrightnessPeriod? current = schedule.ActiveAt(DateTime.Now)?.Period;
        var rows = schedule.Periods.OrderBy(period => period.Start)
            .Select(period => new ScheduleListRow(period, period.Start.ToString("HH:mm"),
                DescribeLevels(period), ReferenceEquals(period, current) ? "生效中" : "")).ToArray();
        int pages = Math.Max(1, (rows.Length + SchedulesPerPage - 1) / SchedulesPerPage);
        schedulePage = Math.Clamp(schedulePage, 0, pages - 1);
        ScheduleList.ItemsSource = rows.Skip(schedulePage * SchedulesPerPage).Take(SchedulesPerPage).ToArray();
        ScheduleList.MinHeight = pages > 1 ? 98 : 0;
        ScheduleList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SchedulePager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousSchedulePage.IsEnabled = schedulePage > 0;
        NextSchedulePage.IsEnabled = schedulePage + 1 < pages;
        SchedulePageLabel.Text = $"{schedulePage + 1}/{pages}";
        ScheduleToggle.IsChecked = schedule.Enabled;
    }

    private void SchedulePageClicked(object sender, RoutedEventArgs e)
    {
        schedulePage += sender == NextSchedulePage ? 1 : -1;
        RefreshScheduleList();
    }

    private async void ScheduleTick(object? sender, EventArgs e)
    {
        if (closed || busy || automaticApply is not null || editingSchedule)
            return;

        if (monitorRecoveryRequested)
        {
            monitorRecoveryRequested = false;
            scheduledMonitors.Clear();
            retryAfter = default;
            foreach (string id in failedManualTargets.Keys.ToArray())
                failedManualTargets[id] = (failedManualTargets[id].Target, default);
            if (cards.Count > 0)
                await RunAutomaticApplyAsync(ReadMonitorCardsAsync());
            else
                retryStartupDiscovery = true;
        }

        if (retryStartupDiscovery && !closed && !busy && automaticApply is null && !editingSchedule &&
            DateTimeOffset.UtcNow >= startupDiscoveryAfter)
            await RefreshAsync();

        if (!closed && !busy && automaticApply is null && !editingSchedule && !schedule.Enabled && !HasPendingEdits)
        {
            var targets = cards.Where(card => failedManualTargets.TryGetValue(card.Monitor.Id, out var failed) &&
                DateTimeOffset.UtcNow >= failed.RetryAt)
                .Select(card => (card, failedManualTargets[card.Monitor.Id].Target)).ToArray();
            if (targets.Length > 0)
                await RunAutomaticApplyAsync(ApplyManuallyAsync(targets));
        }
        await CheckScheduleAsync();
    }

    private void RequestMonitorRecovery()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closed)
            {
                monitorRecoveryRequested = true;
                startupDiscoveryAfter = default;
            }
        });
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            RequestMonitorRecovery();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => RequestMonitorRecovery();

    private void SessionSwitched(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            RequestMonitorRecovery();
    }

    private async Task CheckScheduleAsync(IReadOnlyCollection<MonitorCard>? priority = null)
    {
        if (closed || busy || automaticApply is not null || editingSchedule || !schedule.Enabled)
            return;

        ScheduledPeriod? period = schedule.ActiveAt(DateTime.Now);
        if (period != activePeriod)
        {
            activePeriod = period;
            scheduledMonitors.Clear();
            retryAfter = default;
            RefreshScheduleList();
            UpdateSyncControls();
            UpdateTrayBrightness();
        }

        if (period is null)
        {
            SetScheduleStatus("");
            return;
        }

        var slot = period.Period;
        if (cards.Count == 0)
        {
            SetScheduleStatus("未检测到显示器。");
            return;
        }

        if (scheduledMonitors.Count == cards.Count)
        {
            SetScheduleStatus("");
            return;
        }

        if (DateTimeOffset.UtcNow < retryAfter)
            return;

        await RunAutomaticApplyAsync(ApplyScheduledTargetsAsync(slot, priority));
    }

    private async Task RunAutomaticApplyAsync(Task operation)
    {
        automaticApply = operation;
        try
        {
            await operation;
        }
        finally
        {
            automaticApply = null;
            if (closed)
                ReleaseCards();
            else
                ResumePendingEdits();
        }
    }

    private async Task ApplyScheduledTargetsAsync(BrightnessPeriod slot, IReadOnlyCollection<MonitorCard>? priority)
    {
        var pending = cards.Where(card => !scheduledMonitors.Contains(card.Monitor))
            .OrderByDescending(card => priority?.Contains(card) == true)
            .Select(card => (card, slot.TargetFor(card.Monitor.Id, sync))).ToArray();
        await ApplyTargetsAsync(pending, scheduled: true);
        int remaining = cards.Count - scheduledMonitors.Count;
        retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
        SetScheduleStatus(remaining == 0 ? "" : $"{remaining} 台屏幕待重试");
        UpdateTrayBrightness();
    }

    private Task WaitForAutomaticApplyAsync() => automaticApply ?? Task.CompletedTask;

    private static string PeriodTime(BrightnessPeriod period) => $"{period.Start:HH\\:mm} 起";

    private string DescribeLevels(BrightnessPeriod period) =>
        sync.Enabled ? $"基准 {period.Percent}%"
            : cards.Count == 0 ? $"默认 {period.Percent}%"
            : string.Join(" / ", cards.Select(card => $"{card.Monitor.Name} {period.TargetFor(card.Monitor.Id, sync)}%"));

    private string DescribePeriod(BrightnessPeriod period) => $"{PeriodTime(period)} · {DescribeLevels(period)}";

    private void SetScheduleStatus(string text)
    {
        scheduleMessage = text;
        UpdateStatusLine();
    }

    private void UpdateTrayBrightness()
    {
        if (tray is null || closed)
            return;

        var current = schedule.ActiveAt(DateTime.Now);
        string planStatus = !schedule.Enabled ? "定时计划未启用"
            : current is null ? "当前无定时计划" : $"定时 {PeriodTime(current.Period)}";
        tray.SetBrightness(cards.Select(card => card.Reading is Brightness reading
            ? $"{card.Monitor.Name}  {reading.Percent}%" : $"{card.Monitor.Name}  暂无读数").ToArray(), planStatus);
    }

    private async void TrayOpening()
    {
        UpdateTrayBrightness();
        var current = schedule.ActiveAt(DateTime.Now);
        tray?.SetPlan(schedule.Enabled ? "定时计划 · 已启用" : "定时计划 · 未启用",
            schedule.Periods.OrderBy(period => period.Start).Select(period =>
                (DescribePeriod(period), ReferenceEquals(period, current?.Period))));

        if (busy || automaticApply is not null || editingSchedule || closed || cards.Count == 0)
            return;

        SetBusy(true);
        try
        {
            await ReadMonitorCardsAsync();
        }
        finally
        {
            FinishOperation();
        }
    }

    private async Task ReadMonitorCardsAsync()
    {
        foreach (MonitorCard card in cards)
        {
            if (closed)
                break;
            try
            {
                Brightness reading = await Task.Run(card.Monitor.Read);
                if (closed)
                    break;
                card.SetReading(reading, "");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                if (closed)
                    break;
                card.SetReading(null, "暂时无法读取亮度。");
                scheduledMonitors.Remove(card.Monitor);
            }
        }
        UpdateStatusLine();
        UpdateTrayBrightness();
    }

    internal void ShowFromTray()
    {
        if (closed)
            return;

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        exitRequested = true;
        Close();
    }

    private void SetStatus(string text)
    {
        statusMessage = text;
        UpdateStatusLine();
    }

    private void UpdateStatusLine()
    {
        if (closed || StatusText is null)
            return;

        string text = statusMessage;
        if (BaseInput is { IsEnabled: true, Number: null })
            text = "基准亮度请输入 0 到 100 的整数。";
        else if (!busy && sync.Enabled &&
            cards.FirstOrDefault(card => !card.Correction.HasValue) is { } invalid)
            text = $"{invalid.Monitor.Position} · 亮度修正请输入 −100 到 +100 的整数。";
        else if (!busy && !sync.Enabled && !schedule.Enabled &&
            cards.FirstOrDefault(card => !card.ManualPercent.HasValue) is { } invalidManual)
            text = $"{invalidManual.Monitor.Position} · 亮度请输入 0 到 100 的整数。";
        if (text.Length == 0)
            text = scheduleMessage;
        if (text.Length == 0 && cards.FirstOrDefault(card => card.Status.Length > 0) is { } failed)
            text = $"{failed.Monitor.Position} · {failed.Status}";

        SetLiveText(StatusText, text);
    }

    private void SetLiveText(TextBlock block, string text)
    {
        if (closed || block.Text == text)
            return;

        block.Text = text;
        UIElementAutomationPeer.FromElement(block)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Cards.IsEnabled = !busy;
        UpdateScheduleControls();
        UpdateSyncControls();
    }

    private void UpdateScheduleControls()
    {
        RefreshButton.IsEnabled = !busy;
        ScheduleButton.IsEnabled = !busy;
        ScheduleToggle.IsEnabled = !busy && schedule.Periods.Length > 0;
        ScheduleList.IsEnabled = !busy;
        SyncToggle.IsEnabled = !busy;
    }

    private void FinishOperation()
    {
        if (closed)
            ReleaseCards();
        else
        {
            SetBusy(false);
            UpdateTrayBrightness();
            ResumePendingEdits();
        }
    }

    private void ReleaseCards()
    {
        foreach (MonitorCard card in cards)
            card.Monitor.Dispose();
        cards.Clear();
        Cards.ItemsSource = null;
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (exitRequested || tray is null)
            return;

        e.Cancel = true;
        Hide();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        scheduleTimer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.SessionSwitch -= SessionSwitched;
        tray?.Dispose();
        if (!busy && automaticApply is null)
            ReleaseCards();
    }
}

internal sealed record ScheduleListRow(BrightnessPeriod Entry, string Time, string Levels, string State)
{
    public string AccessibleName => $"编辑 {Time} 的计划，{Levels} {State}";
}
