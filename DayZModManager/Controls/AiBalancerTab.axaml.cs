using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DayZModManager.Models;
using DayZModManager.Services;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace DayZModManager.Controls;

public partial class AiBalancerTab : UserControl
{
    private readonly EconomyApiListener _listener = new();
    private readonly AiBalancerService _ai = new();
    private readonly XmlApplyService _xml = new();
    private readonly ServerFilesService _serverFiles = new();
    private readonly AiTaskService _taskAi = new();
    private readonly TaskApplyService _taskApply = new();
    private readonly DispatcherTimer _statusTimer;

    private readonly ObservableCollection<EconomyRowViewModel> _allEconomy = new();
    private readonly ObservableCollection<EconomyRowViewModel> _viewEconomy = new();
    private readonly ObservableCollection<SuggestionRowViewModel> _allSuggestions = new();
    private readonly ObservableCollection<SuggestionRowViewModel> _viewSuggestions = new();
    private readonly ObservableCollection<WorkerCardViewModel> _workers = new();
    private readonly ObservableCollection<TaskAction> _taskActions = new();
    private readonly ObservableCollection<string> _historyItems = new();

    private ServerFilesSnapshot? _serverFilesSnap;
    private CancellationTokenSource? _runCts;
    private AiBalancerConfig _cfg = new();
    private string _historyPath = string.Empty;

    public AiBalancerTab()
    {
        InitializeComponent();

        EconomyGrid.ItemsSource = _viewEconomy;
        SuggestionsGrid.ItemsSource = _viewSuggestions;
        WorkersList.ItemsSource = _workers;
        TaskPlanGrid.ItemsSource = _taskActions;
        HistoryList.ItemsSource = _historyItems;

        _listener.SnapshotReceived += OnSnapshotReceived;
        _listener.LogMessage += (_, m) => AppendLog(m);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateLastReceivedLabel();
        _statusTimer.Start();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = AppConfigStore.Load();
            _cfg = cfg.AiBalancer ?? new AiBalancerConfig();
            ApplyConfigToUi(_cfg);

            _listener.LoadRecentFromDisk();
            var last = _listener.RecentSnapshots.LastOrDefault();
            if (last != null) RebuildEconomyView(last);

            _historyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DayZModManager", "balance_history.jsonl");

            RefreshHistoryList();

            if (_cfg.AutoStartListener && !string.IsNullOrWhiteSpace(_cfg.ListenerSecret))
                StartListener();

            UpdateRunButtonEnabled();
        }
        catch (Exception ex)
        {
            AppendLog("Init error: " + ex.Message);
        }
    }

    // ─── Config UI ↔ model ───

    private void ApplyConfigToUi(AiBalancerConfig cfg)
    {
        PortTextBox.Text = cfg.ListenerPort.ToString();
        SecretBox.Text = cfg.ListenerSecret ?? string.Empty;
        ApiKeyBox.Text = ApiKeyProtection.Unprotect(cfg.OpenAiApiKeyEncrypted ?? string.Empty);
        SelectComboByText(ModelCombo, cfg.OpenAiModel);
        SelectComboByText(ServerTypeCombo, cfg.ServerType);
        SelectComboByText(ConcurrencyCombo, cfg.Concurrency.ToString());
        SelectComboByText(BatchSizeCombo, cfg.BatchSize.ToString());
        TypesXmlPathBox.Text = cfg.TypesXmlPath ?? string.Empty;
        EventsXmlPathBox.Text = cfg.EventsXmlPath ?? string.Empty;
        GlobalsXmlPathBox.Text = cfg.GlobalsXmlPath ?? string.Empty;
        SpawnableTypesXmlPathBox.Text = cfg.SpawnableTypesXmlPath ?? string.Empty;
        ServerRootPathBox.Text = cfg.ServerRootPath ?? string.Empty;
        MissionPathBox.Text = cfg.MissionPath ?? string.Empty;
        BackupCheck.IsChecked = cfg.BackupBeforeApply;
    }

    private static void SelectComboByText(ComboBox combo, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            var content = combo.Items[i] is ComboBoxItem cbi ? cbi.Content?.ToString()
                        : combo.Items[i]?.ToString();
            if (string.Equals(content, text, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static string? GetComboText(ComboBox combo)
        => combo.SelectedItem is ComboBoxItem cbi ? cbi.Content?.ToString()
         : combo.SelectedItem?.ToString();

    private AiBalancerConfig CaptureConfigFromUi()
    {
        return new AiBalancerConfig
        {
            ListenerPort = int.TryParse(PortTextBox.Text, out var p) ? p : 7823,
            ListenerSecret = SecretBox.Text ?? string.Empty,
            OpenAiApiKeyEncrypted = ApiKeyProtection.Protect(ApiKeyBox.Text ?? string.Empty),
            OpenAiModel = GetComboText(ModelCombo) ?? "gpt-5.4-nano-2026-03-17",
            ServerType = GetComboText(ServerTypeCombo) ?? "PvE",
            Concurrency = int.TryParse(GetComboText(ConcurrencyCombo), out var c) ? c : 3,
            BatchSize = int.TryParse(GetComboText(BatchSizeCombo), out var b) ? b : 30,
            TypesXmlPath = TypesXmlPathBox.Text?.Trim() ?? string.Empty,
            EventsXmlPath = EventsXmlPathBox.Text?.Trim() ?? string.Empty,
            GlobalsXmlPath = GlobalsXmlPathBox.Text?.Trim() ?? string.Empty,
            SpawnableTypesXmlPath = SpawnableTypesXmlPathBox.Text?.Trim() ?? string.Empty,
            ServerRootPath = ServerRootPathBox.Text?.Trim() ?? string.Empty,
            MissionPath = MissionPathBox.Text?.Trim() ?? string.Empty,
            BackupBeforeApply = BackupCheck.IsChecked ?? true,
            AutoStartListener = _cfg.AutoStartListener,
        };
    }

    private async void OnSaveAiSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            _cfg = CaptureConfigFromUi();
            var store = AppConfigStore.Load();
            store.AiBalancer = _cfg;
            AppConfigStore.Save(store);
            AppendLog("Settings saved.");
        }
        catch (Exception ex)
        {
            await ShowError("Save failed: " + ex.Message);
        }
    }

    // ─── Listener ───

    private void OnStartListenerClick(object? sender, RoutedEventArgs e) => StartListener();
    private void OnStopListenerClick(object? sender, RoutedEventArgs e) => StopListener();

    private async void StartListener()
    {
        try
        {
            _cfg = CaptureConfigFromUi();
            if (string.IsNullOrWhiteSpace(_cfg.ListenerSecret))
            {
                await ShowWarning("Set a Secret token first.");
                return;
            }
            _listener.Start(_cfg.ListenerPort, _cfg.ListenerSecret);
            UpdateListenerStatus(true);
        }
        catch (Exception ex)
        {
            AppendLog("Listener start failed: " + ex.Message);
            await ShowError("Listener start failed: " + ex.Message + "\nTry running as admin or use a different port.");
        }
    }

    private void StopListener()
    {
        _listener.Stop();
        UpdateListenerStatus(false);
    }

    private void UpdateListenerStatus(bool listening)
    {
        var color = listening ? Color.Parse("#4ADE80") : Color.Parse("#52525B");
        var brush = new SolidColorBrush(color);
        ListenerDot.Fill = brush;
        ListenerDot2.Fill = brush;
        ListenerStatusText.Text = listening ? $"Listening on port {_listener.Port}" : "Stopped";
        ListenerHeaderText.Text = listening ? $"Listener active — port {_listener.Port}" : "Listener stopped";
        BtnStartListener.IsEnabled = !listening;
        BtnStopListener.IsEnabled = listening;
    }

    private void UpdateLastReceivedLabel()
    {
        if (_listener.LastReceivedUtc is { } t)
        {
            var ago = DateTime.UtcNow - t;
            string txt = ago.TotalSeconds < 60 ? $"{(int)ago.TotalSeconds}s ago"
                : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
                : $"{(int)ago.TotalHours} h ago";
            LastReceivedText.Text = $"Last data received: {txt}";
        }
    }

    private void OnSnapshotReceived(object? sender, EconomySnapshot snap)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            RebuildEconomyView(snap);
            UpdateRunButtonEnabled();
        });
    }

    private void RebuildEconomyView(EconomySnapshot snap)
    {
        EconomyGroup.IsVisible = true;
        var ts = DateTimeOffset.FromUnixTimeSeconds(snap.Timestamp).LocalDateTime;
        SnapshotTimeText.Text = ts.ToString("yyyy-MM-dd HH:mm");
        SnapshotPlayersText.Text = $"{snap.PlayersOnline}/{snap.PlayersMax}";
        SnapshotItemsText.Text = (snap.Items?.Count ?? 0).ToString();
        SnapshotZombiesText.Text = snap.Zombies != null ? $"{snap.Zombies.TotalAlive}/{snap.Zombies.TotalMax}" : "0/0";

        _allEconomy.Clear();
        if (snap.Items != null)
        {
            var history = _listener.RecentSnapshots;
            foreach (var it in snap.Items)
            {
                var trend = ComputeTrend(it.ClassName, history);
                var ratio = it.Nominal > 0 ? it.SpawnedCount * 100.0 / it.Nominal : 0;
                string status = it.Flags.Crafted ? "Crafted (skip)"
                    : ratio < 40 ? "Under-spawned"
                    : ratio > 160 ? "Over-spawned"
                    : ratio < 80 ? "Hoarded?"
                    : "Balanced";
                _allEconomy.Add(new EconomyRowViewModel
                {
                    ClassName = it.ClassName,
                    Category = it.Category,
                    SpawnedCount = it.SpawnedCount,
                    Nominal = it.Nominal,
                    Trend = trend,
                    Status = status,
                    LastSeen = ts.ToString("HH:mm:ss"),
                    IsCrafted = it.Flags.Crafted,
                });
            }
        }

        PopulateEconomyCategoryFilter();
        ApplyEconomyFilter();
        UpdateEconomySummary();
    }

    private static string ComputeTrend(string className, IReadOnlyList<EconomySnapshot> hist)
    {
        if (hist == null || hist.Count < 2) return "→";
        var values = new List<int>();
        foreach (var s in hist)
        {
            var it = s.Items?.FirstOrDefault(i => string.Equals(i.ClassName, className, StringComparison.OrdinalIgnoreCase));
            if (it != null) values.Add(it.SpawnedCount);
        }
        if (values.Count < 2) return "→";
        var first = values.Take(values.Count / 2).DefaultIfEmpty(0).Average();
        var second = values.Skip(values.Count / 2).DefaultIfEmpty(0).Average();
        var delta = second - first;
        var threshold = Math.Max(1, first * 0.15);
        if (delta > threshold * 2) return "↑";
        if (delta > threshold) return "↗";
        if (delta < -threshold * 2) return "↓";
        if (delta < -threshold) return "↘";
        return "→";
    }

    private void PopulateEconomyCategoryFilter()
    {
        var current = GetComboText(EconomyCategoryCombo) ?? "All";
        EconomyCategoryCombo.Items.Clear();
        EconomyCategoryCombo.Items.Add(new ComboBoxItem { Content = "All" });
        foreach (var cat in _allEconomy.Select(e => e.Category).Where(c => !string.IsNullOrEmpty(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c))
            EconomyCategoryCombo.Items.Add(new ComboBoxItem { Content = cat });
        SelectComboByText(EconomyCategoryCombo, current);
        if (EconomyCategoryCombo.SelectedIndex < 0) EconomyCategoryCombo.SelectedIndex = 0;
    }

    private void OnEconomyFilterChanged(object? sender, RoutedEventArgs e) => ApplyEconomyFilter();
    private void OnEconomyFilterChanged(object? sender, TextChangedEventArgs e) => ApplyEconomyFilter();
    private void OnEconomyFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyEconomyFilter();

    private void ApplyEconomyFilter()
    {
        var search = EconomySearchBox?.Text?.Trim() ?? string.Empty;
        var cat = GetComboText(EconomyCategoryCombo) ?? "All";
        var status = GetComboText(EconomyStatusCombo) ?? "All";
        var showCrafted = ShowCraftedCheck?.IsChecked ?? false;

        _viewEconomy.Clear();
        foreach (var row in _allEconomy)
        {
            if (!showCrafted && row.IsCrafted) continue;
            if (cat != "All" && !string.Equals(cat, row.Category, StringComparison.OrdinalIgnoreCase)) continue;
            if (status != "All" && !string.Equals(status, row.Status, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(search) && row.ClassName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _viewEconomy.Add(row);
        }
    }

    private void UpdateEconomySummary()
    {
        int bal = 0, over = 0, under = 0, severe = 0;
        foreach (var r in _allEconomy)
        {
            switch (r.Status)
            {
                case "Balanced": bal++; break;
                case "Over-spawned": over++; if (r.RatioPct > 200) severe++; break;
                case "Under-spawned": under++; if (r.RatioPct < 20) severe++; break;
            }
        }
        SummaryBalancedText.Text = $"Balanced: {bal}";
        SummaryOverText.Text = $"Over-spawned: {over}";
        SummaryUnderText.Text = $"Under-spawned: {under}";
        SummarySevereText.Text = $"Severe: {severe}";
    }

    // ─── Browse ───

    private void OnBrowseTypesXml(object? sender, RoutedEventArgs e) => _ = BrowseXmlAsync(TypesXmlPathBox);
    private void OnBrowseEventsXml(object? sender, RoutedEventArgs e) => _ = BrowseXmlAsync(EventsXmlPathBox);
    private void OnBrowseGlobalsXml(object? sender, RoutedEventArgs e) => _ = BrowseXmlAsync(GlobalsXmlPathBox);
    private void OnBrowseSpawnableTypesXml(object? sender, RoutedEventArgs e) => _ = BrowseXmlAsync(SpawnableTypesXmlPathBox);

    private async Task BrowseXmlAsync(TextBox target)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select XML file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("XML files") { Patterns = new[] { "*.xml" } }, FilePickerFileType.All },
        });
        if (files.Count > 0)
            target.Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
    }

    private async void OnLoadFromMergedOutput(object? sender, RoutedEventArgs e)
    {
        try
        {
            var store = AppConfigStore.Load();
            var merged = store.CombineOutFileText;
            if (string.IsNullOrWhiteSpace(merged))
            {
                await ShowInfo("No merged output path set yet. Run the Mods Folders tab first.");
                return;
            }
            var full = Path.IsPathRooted(merged) ? merged : Path.Combine(AppContext.BaseDirectory, merged);
            if (!File.Exists(full))
            {
                await ShowWarning($"Merged file not found:\n{full}");
                return;
            }
            TypesXmlPathBox.Text = full;
        }
        catch (Exception ex)
        {
            await ShowError("Load failed: " + ex.Message);
        }
    }

    // ─── Run balancer ───

    private void UpdateRunButtonEnabled()
    {
        var snap = _listener.RecentSnapshots.LastOrDefault();
        BtnRunBalancer.IsEnabled = snap != null && snap.Items?.Count > 0 && !string.IsNullOrWhiteSpace(ApiKeyBox.Text);
    }

    private async void OnRunBalancerClick(object? sender, RoutedEventArgs e)
    {
        var snap = _listener.RecentSnapshots.LastOrDefault();
        if (snap == null || snap.Items == null || snap.Items.Count == 0)
        {
            await ShowInfo("No economy snapshot yet. Start the listener and wait for the mod to POST data.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Text))
        {
            await ShowWarning("Enter your OpenAI API key.");
            return;
        }

        _cfg = CaptureConfigFromUi();
        var opts = new AiBalancerOptions
        {
            ApiKey = ApiKeyBox.Text,
            Model = _cfg.OpenAiModel,
            Concurrency = _cfg.Concurrency,
            BatchSize = _cfg.BatchSize,
            ServerType = _cfg.ServerType,
        };

        _allSuggestions.Clear();
        _viewSuggestions.Clear();
        SuggestionsGroup.IsVisible = false;
        _workers.Clear();
        for (var i = 0; i < opts.Concurrency; i++)
            _workers.Add(new WorkerCardViewModel { Title = $"Worker {i + 1}", Status = "idle", ProgressPct = 0 });

        ProgressPanel.IsVisible = true;
        OverallProgress.Value = 0;
        ProgressStatusText.Text = "Starting...";
        BtnRunBalancer.IsEnabled = false;
        BtnCancelBalancer.IsEnabled = true;

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        var progress = new Progress<BalancerProgress>(p => UpdateProgress(p, opts.Concurrency));

        AppendLog($"AI run started. Model={opts.Model}, concurrency={opts.Concurrency}, batchSize={opts.BatchSize}");

        try
        {
            var run = await Task.Run(() => _ai.RunAsync(snap, _listener.RecentSnapshots, opts, progress, token), token);
            PopulateSuggestions(run);
            AppendLog($"AI run finished. {_allSuggestions.Count} field-changes across {run.Suggestions.Count} items. Tokens={run.TotalTokensUsed}.");
            SaveHistoryEntry(run.Suggestions.Count, run.TotalTokensUsed, opts.Model, "Generated");
            RefreshHistoryList();
        }
        catch (OperationCanceledException) { AppendLog("AI run canceled."); }
        catch (Exception ex)
        {
            AppendLog("AI run failed: " + ex.Message);
            await ShowError("AI run failed: " + ex.Message);
        }
        finally
        {
            BtnRunBalancer.IsEnabled = true;
            BtnCancelBalancer.IsEnabled = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnCancelBalancerClick(object? sender, RoutedEventArgs e)
    {
        try { _runCts?.Cancel(); } catch { }
    }

    private void UpdateProgress(BalancerProgress p, int totalWorkers)
    {
        if (p.TotalBatches > 0)
            OverallProgress.Value = p.CompletedBatches * 100.0 / p.TotalBatches;
        ProgressStatusText.Text =
            $"Batch {p.CompletedBatches}/{p.TotalBatches}  |  Active: {p.ActiveWorkers}  |  Modified: {p.TotalModified}  |  Errors: {p.TotalErrors}  |  Tokens: {p.TotalTokensUsed}";

        if (!string.IsNullOrEmpty(p.LogMessage)) AppendLog(p.LogMessage);

        if (totalWorkers > 0)
        {
            var idx = ((p.WorkerIndex - 1) % totalWorkers + totalWorkers) % totalWorkers;
            if (idx < _workers.Count)
            {
                _workers[idx].Status = $"batch {p.CurrentBatchIndex + 1} {(p.WorkerStatus == WorkerStatus.Done ? "done" : "…")}";
                _workers[idx].ProgressPct = p.WorkerProgressPct;
            }
        }
    }

    private void PopulateSuggestions(AiBalancerRunResult run)
    {
        _allSuggestions.Clear();
        foreach (var sug in run.Suggestions)
        {
            foreach (var kv in sug.Changes)
            {
                _allSuggestions.Add(new SuggestionRowViewModel
                {
                    ClassName = sug.ClassName,
                    Category = sug.Category,
                    Field = kv.Key,
                    OldValue = kv.Value.OldValue,
                    NewValue = kv.Value.NewValue,
                    Reason = sug.AiReason,
                    Source = sug,
                });
            }
        }
        ApplySuggestionFilter();
        SuggestionsGroup.IsVisible = _allSuggestions.Count > 0;
        var cost = run.TotalTokensUsed * 0.00000005m;
        SuggestionsSummaryText.Text = $"AI suggests {_allSuggestions.Count} change(s) across {run.Suggestions.Count} items. Tokens used: {run.TotalTokensUsed:N0} (~${cost:F4}).";
    }

    private void OnSuggestionFilterChanged(object? sender, RoutedEventArgs e) => ApplySuggestionFilter();
    private void OnSuggestionFilterChanged(object? sender, TextChangedEventArgs e) => ApplySuggestionFilter();
    private void OnSuggestionFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplySuggestionFilter();

    private void ApplySuggestionFilter()
    {
        var search = SuggestionSearchBox?.Text?.Trim() ?? string.Empty;
        var field = GetComboText(SuggestionFieldCombo) ?? "All fields";
        var onlyInc = OnlyIncreasesCheck?.IsChecked ?? false;
        var onlyDec = OnlyDecreasesCheck?.IsChecked ?? false;
        var onlySev = OnlySevereCheck?.IsChecked ?? false;

        _viewSuggestions.Clear();
        foreach (var row in _allSuggestions)
        {
            if (field != "All fields" && !string.Equals(field, row.Field, StringComparison.OrdinalIgnoreCase)) continue;
            if (onlyInc && row.Delta <= 0) continue;
            if (onlyDec && row.Delta >= 0) continue;
            if (onlySev && !row.IsSevere) continue;
            if (!string.IsNullOrEmpty(search) && row.ClassName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _viewSuggestions.Add(row);
        }
    }

    // ─── Apply / Export ───

    private void OnSelectAll(object? sender, RoutedEventArgs e) { foreach (var r in _viewSuggestions) r.IsApproved = true; }
    private void OnSelectNone(object? sender, RoutedEventArgs e) { foreach (var r in _viewSuggestions) r.IsApproved = false; }
    private void OnSelectSevere(object? sender, RoutedEventArgs e) { foreach (var r in _allSuggestions) r.IsApproved = r.IsSevere; }
    private void OnRejectSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var r in _viewSuggestions.Where(s => s.IsApproved).ToList()) r.IsApproved = false;
    }

    private async void OnApplySelected(object? sender, RoutedEventArgs e)
    {
        var path = TypesXmlPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            await ShowWarning("Set a valid types.xml path first.");
            return;
        }

        var approvedRows = _viewSuggestions.Where(r => r.IsApproved).ToList();
        if (approvedRows.Count == 0)
        {
            await ShowInfo("No suggestions selected.");
            return;
        }

        var backupNote = BackupCheck.IsChecked == true ? "Original will be backed up." : "No backup will be created.";
        var result = await MessageBoxManager
            .GetMessageBoxStandard("Confirm apply",
                $"Apply {approvedRows.Count} field change(s) to types.xml?\n{backupNote}",
                ButtonEnum.YesNo, Icon.Question)
            .ShowWindowDialogAsync(GetParentWindow());
        if (result != ButtonResult.Yes) return;

        var byClass = approvedRows.GroupBy(r => r.ClassName, StringComparer.OrdinalIgnoreCase);
        var toApply = new List<BalanceSuggestion>();
        foreach (var g in byClass)
        {
            var sug = new BalanceSuggestion
            {
                ClassName = g.Key,
                Category = g.First().Category,
                AiReason = g.First().Reason,
                IsApproved = true,
            };
            foreach (var r in g)
                sug.Changes[r.Field] = new FieldChange { OldValue = r.OldValue, NewValue = r.NewValue };
            toApply.Add(sug);
        }

        try
        {
            var applyResult = _xml.Apply(toApply, path, BackupCheck.IsChecked == true);
            var msg = $"Applied: {applyResult.Applied}\nNot found: {applyResult.NotFound}";
            if (!string.IsNullOrEmpty(applyResult.BackupPath)) msg += $"\nBackup: {Path.GetFileName(applyResult.BackupPath)}";
            if (applyResult.Errors.Count > 0) msg += "\nErrors: " + applyResult.Errors.Count;
            await ShowInfo(msg);
            AppendLog($"Apply complete. {applyResult.Applied} applied, {applyResult.NotFound} not found, {applyResult.Errors.Count} errors.");

            SaveHistoryEntry(_allSuggestions.Count, 0, _cfg.OpenAiModel,
                approvedRows.Count == _allSuggestions.Count ? "Applied" : "Partially applied",
                approvedRows.Count, _allSuggestions.Count - approvedRows.Count, applyResult.BackupPath);
            RefreshHistoryList();
        }
        catch (Exception ex)
        {
            await ShowError("Apply failed: " + ex.Message);
        }
    }

    private async void OnExportSuggestions(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export suggestions",
            SuggestedFileName = $"balance-suggestions-{DateTime.Now:yyyyMMdd_HHmmss}.json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file == null) return;
        try
        {
            var payload = _allSuggestions.Select(r => new
            {
                r.ClassName, r.Category, r.Field, r.OldValue, r.NewValue, r.Delta, r.Reason, Approved = r.IsApproved,
            });
            var path = file.TryGetLocalPath() ?? file.Path.LocalPath;
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog("Suggestions exported to " + path);
        }
        catch (Exception ex)
        {
            await ShowError("Export failed: " + ex.Message);
        }
    }

    // ─── History ───

    private void SaveHistoryEntry(int suggestionCount, int tokens, string model, string status,
        int applied = 0, int rejected = 0, string? backupPath = null)
    {
        try
        {
            var entry = new BalanceHistoryEntry
            {
                Timestamp = DateTime.Now,
                SuggestionCount = suggestionCount,
                AppliedCount = applied,
                RejectedCount = rejected,
                TokensUsed = tokens,
                Model = model,
                Status = status,
                BackupPath = backupPath,
            };
            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_historyPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppendLog("History save failed: " + ex.Message);
        }
    }

    private void RefreshHistoryList()
    {
        _historyItems.Clear();
        try
        {
            if (!File.Exists(_historyPath)) return;
            var lines = File.ReadAllLines(_historyPath).Reverse().Take(30);
            foreach (var line in lines)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<BalanceHistoryEntry>(line);
                    if (entry == null) continue;
                    _historyItems.Add($"{entry.Timestamp:yyyy-MM-dd HH:mm} — {entry.SuggestionCount} suggestion(s) — {entry.Status}" +
                        (entry.AppliedCount > 0 ? $" ({entry.AppliedCount} accepted, {entry.RejectedCount} rejected)" : ""));
                }
                catch { /* skip malformed */ }
            }
        }
        catch (Exception ex) { AppendLog("History load failed: " + ex.Message); }
    }

    private void OnRefreshAiHistory(object? sender, RoutedEventArgs e) => RefreshHistoryList();

    // ─── Log ───

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            var lines = LogBox.Text?.Split('\n');
            if (lines != null && lines.Length > 60)
                LogBox.Text = string.Join('\n', lines.Skip(lines.Length - 50));
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }

    // ─── Server files / AI tasks ───

    private async void OnBrowseServerRoot(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select DayZ server folder (pick any file inside it)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DayZ Server exe") { Patterns = new[] { "DayZServer_x64.exe" } },
                FilePickerFileType.All,
            },
        });
        if (files.Count > 0)
        {
            var picked = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
            ServerRootPathBox.Text = Path.GetDirectoryName(picked) ?? picked;
        }
    }

    private async void OnBrowseMission(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select mission folder (pick init.c inside it)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mission init") { Patterns = new[] { "init.c" } },
                FilePickerFileType.All,
            },
        });
        if (files.Count > 0)
        {
            var picked = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
            MissionPathBox.Text = Path.GetDirectoryName(picked) ?? picked;
        }
    }

    private async void OnDiscoverServerFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var root = ServerRootPathBox.Text?.Trim() ?? string.Empty;
            var mission = MissionPathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(root) && string.IsNullOrEmpty(mission))
            {
                await ShowInfo("Set the server root or mission folder first.");
                return;
            }
            _serverFilesSnap = _serverFiles.Discover(root, mission);
            var found = _serverFilesSnap.Files.Count(f => f.Exists);
            ServerFilesSummaryText.Text = $"{found}/{_serverFilesSnap.Files.Count} server files found.";
            AppendLog($"Discovered {found} server file(s) under root='{root}', mission='{mission}'.");
        }
        catch (Exception ex)
        {
            AppendLog("Discover failed: " + ex.Message);
        }
    }

    private async void OnPlanTaskClick(object? sender, RoutedEventArgs e)
    {
        var prompt = AiTaskPromptBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await ShowInfo("Type a request first (e.g. 'set day to 2h and night to 15min').");
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Text))
        {
            await ShowWarning("Enter your OpenAI API key.");
            return;
        }
        if (_serverFilesSnap == null)
            OnDiscoverServerFiles(sender, e);
        if (_serverFilesSnap == null) return;

        _cfg = CaptureConfigFromUi();
        var opts = new AiTaskOptions
        {
            ApiKey = ApiKeyBox.Text,
            Model = _cfg.OpenAiModel,
            ServerType = _cfg.ServerType,
        };

        _taskActions.Clear();
        TaskNotesText.Text = string.Empty;
        TaskTokensText.Text = string.Empty;
        BtnPlanTask.IsEnabled = false;
        BtnApplyTaskPlan.IsEnabled = false;
        AppendLog("AI task planner: requesting plan...");

        try
        {
            var progress = new Progress<string>(AppendLog);
            var proposal = await Task.Run(() =>
                _taskAi.ProposeAsync(prompt, _serverFilesSnap, opts, progress, CancellationToken.None));

            foreach (var a in proposal.Actions) _taskActions.Add(a);
            TaskNotesText.Text = proposal.Notes ?? string.Empty;
            TaskTokensText.Text = proposal.TokensUsed > 0 ? $"Tokens: {proposal.TokensUsed:N0}" : string.Empty;
            BtnApplyTaskPlan.IsEnabled = _taskActions.Count > 0;
            AppendLog($"AI task planner: {_taskActions.Count} action(s) proposed.");
        }
        catch (Exception ex)
        {
            AppendLog("AI task planner failed: " + ex.Message);
            await ShowError("Plan failed: " + ex.Message);
        }
        finally
        {
            BtnPlanTask.IsEnabled = true;
        }
    }

    private async void OnApplyTaskPlanClick(object? sender, RoutedEventArgs e)
    {
        var approved = _taskActions.Where(a => a.IsApproved).ToList();
        if (approved.Count == 0)
        {
            await ShowInfo("Nothing approved.");
            return;
        }

        var summary = string.Join("\n", approved.Take(8).Select(a => "  • " + a.Summary));
        if (approved.Count > 8) summary += $"\n  …and {approved.Count - 8} more";
        var backupNote = TaskBackupCheck.IsChecked == true ? "Files will be backed up first." : "No backup will be created.";
        var result = await MessageBoxManager
            .GetMessageBoxStandard("Confirm AI task apply",
                $"Apply {approved.Count} approved action(s)?\n\n{summary}\n\n{backupNote}",
                ButtonEnum.YesNo, Icon.Question)
            .ShowWindowDialogAsync(GetParentWindow());
        if (result != ButtonResult.Yes) return;

        try
        {
            var proposal = new TaskProposal { Actions = _taskActions.ToList() };
            var applyResult = _taskApply.Apply(proposal, TaskBackupCheck.IsChecked == true);
            var msg = $"Applied: {applyResult.Applied}\nSkipped: {applyResult.Skipped}\nErrors: {applyResult.Errors.Count}";
            if (applyResult.BackupPaths.Count > 0) msg += "\nBackups: " + applyResult.BackupPaths.Count;
            if (applyResult.Errors.Count > 0) msg += "\nFirst error: " + applyResult.Errors[0];
            await ShowInfo(msg);
            AppendLog($"AI task apply: {applyResult.Applied} applied, {applyResult.Skipped} skipped, {applyResult.Errors.Count} errors.");

            if (approved.Any(a => a.Kind == TaskActionKind.RunBalance))
                OnRunBalancerClick(sender, e);
        }
        catch (Exception ex)
        {
            await ShowError("Apply failed: " + ex.Message);
        }
    }

    private void OnClearTaskPlanClick(object? sender, RoutedEventArgs e)
    {
        _taskActions.Clear();
        TaskNotesText.Text = string.Empty;
        TaskTokensText.Text = string.Empty;
        BtnApplyTaskPlan.IsEnabled = false;
    }

    // ─── Helpers ───

    private Window? GetParentWindow() => TopLevel.GetTopLevel(this) as Window;

    private Task ShowInfo(string msg) =>
        MessageBoxManager.GetMessageBoxStandard("AI Balancer", msg, ButtonEnum.Ok, Icon.Info)
            .ShowWindowDialogAsync(GetParentWindow());

    private Task ShowWarning(string msg) =>
        MessageBoxManager.GetMessageBoxStandard("AI Balancer", msg, ButtonEnum.Ok, Icon.Warning)
            .ShowWindowDialogAsync(GetParentWindow());

    private Task ShowError(string msg) =>
        MessageBoxManager.GetMessageBoxStandard("AI Balancer", msg, ButtonEnum.Ok, Icon.Error)
            .ShowWindowDialogAsync(GetParentWindow());
}
