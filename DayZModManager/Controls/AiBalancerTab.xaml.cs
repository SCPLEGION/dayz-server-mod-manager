using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DayZModManager.Models;
using DayZModManager.Services;
using Microsoft.Win32;

namespace DayZModManager.Controls;

public partial class AiBalancerTab : UserControl
{
    private readonly EconomyApiListener _listener = new();
    private readonly AiBalancerService _ai = new();
    private readonly XmlApplyService _xml = new();
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    private readonly ObservableCollection<EconomyRowViewModel> _allEconomy = new();
    private readonly ObservableCollection<EconomyRowViewModel> _viewEconomy = new();
    private readonly ObservableCollection<SuggestionRowViewModel> _allSuggestions = new();
    private readonly ObservableCollection<SuggestionRowViewModel> _viewSuggestions = new();
    private readonly ObservableCollection<WorkerCardViewModel> _workers = new();

    private CancellationTokenSource? _runCts;
    private AiBalancerConfig _cfg = new();
    private string _historyPath = string.Empty;

    public AiBalancerTab()
    {
        InitializeComponent();

        EconomyGrid.ItemsSource = _viewEconomy;
        SuggestionsGrid.ItemsSource = _viewSuggestions;
        WorkersList.ItemsSource = _workers;

        _listener.SnapshotReceived += OnSnapshotReceived;
        _listener.LogMessage += (_, m) => AppendLog(m);

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateLastReceivedLabel();
        _statusTimer.Start();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
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

    // ───────────────── Settings UI ↔ Config ─────────────────

    private void ApplyConfigToUi(AiBalancerConfig cfg)
    {
        PortTextBox.Text = cfg.ListenerPort.ToString();
        SecretBox.Password = cfg.ListenerSecret ?? string.Empty;
        ApiKeyBox.Password = ApiKeyProtection.Unprotect(cfg.OpenAiApiKeyEncrypted ?? string.Empty);
        SelectComboByText(ModelCombo, cfg.OpenAiModel);
        SelectComboByText(ServerTypeCombo, cfg.ServerType);
        SelectComboByText(ConcurrencyCombo, cfg.Concurrency.ToString());
        SelectComboByText(BatchSizeCombo, cfg.BatchSize.ToString());
        TypesXmlPathBox.Text = cfg.TypesXmlPath ?? string.Empty;
        EventsXmlPathBox.Text = cfg.EventsXmlPath ?? string.Empty;
        GlobalsXmlPathBox.Text = cfg.GlobalsXmlPath ?? string.Empty;
        SpawnableTypesXmlPathBox.Text = cfg.SpawnableTypesXmlPath ?? string.Empty;
        BackupCheck.IsChecked = cfg.BackupBeforeApply;
    }

    private static void SelectComboByText(ComboBox combo, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private AiBalancerConfig CaptureConfigFromUi()
    {
        var cfg = new AiBalancerConfig
        {
            ListenerPort = int.TryParse(PortTextBox.Text, out var p) ? p : 7823,
            ListenerSecret = SecretBox.Password ?? string.Empty,
            OpenAiApiKeyEncrypted = ApiKeyProtection.Protect(ApiKeyBox.Password ?? string.Empty),
            OpenAiModel = (ModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "gpt-5.4-nano-2026-03-17",
            ServerType = (ServerTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PvE",
            Concurrency = int.TryParse((ConcurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var c) ? c : 3,
            BatchSize = int.TryParse((BatchSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var b) ? b : 30,
            TypesXmlPath = TypesXmlPathBox.Text?.Trim() ?? string.Empty,
            EventsXmlPath = EventsXmlPathBox.Text?.Trim() ?? string.Empty,
            GlobalsXmlPath = GlobalsXmlPathBox.Text?.Trim() ?? string.Empty,
            SpawnableTypesXmlPath = SpawnableTypesXmlPathBox.Text?.Trim() ?? string.Empty,
            BackupBeforeApply = BackupCheck.IsChecked ?? true,
            AutoStartListener = _cfg.AutoStartListener,
        };
        return cfg;
    }

    private void OnSaveAiSettings(object sender, RoutedEventArgs e)
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
            MessageBox.Show("Save failed: " + ex.Message, "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────── Listener ─────────────────

    private void OnStartListenerClick(object sender, RoutedEventArgs e) => StartListener();
    private void OnStopListenerClick(object sender, RoutedEventArgs e) => StopListener();

    private void StartListener()
    {
        try
        {
            _cfg = CaptureConfigFromUi();
            if (string.IsNullOrWhiteSpace(_cfg.ListenerSecret))
            {
                MessageBox.Show("Set a Secret token first.", "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _listener.Start(_cfg.ListenerPort, _cfg.ListenerSecret);
            UpdateListenerStatus(true);
        }
        catch (Exception ex)
        {
            AppendLog("Listener start failed: " + ex.Message);
            MessageBox.Show("Listener start failed: " + ex.Message + "\nTry running as admin or use a different port.",
                "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopListener()
    {
        _listener.Stop();
        UpdateListenerStatus(false);
    }

    private void UpdateListenerStatus(bool listening)
    {
        var brush = listening ? (Brush)Application.Current.Resources["Accent"] : (Brush)Application.Current.Resources["Muted"];
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
        Dispatcher.InvokeAsync(() =>
        {
            RebuildEconomyView(snap);
            UpdateRunButtonEnabled();
        });
    }

    private void RebuildEconomyView(EconomySnapshot snap)
    {
        EconomyGroup.Visibility = Visibility.Visible;
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
        if (hist == null || hist.Count < 2) return "\u2192";
        var values = new List<int>();
        foreach (var s in hist)
        {
            var it = s.Items?.FirstOrDefault(i => string.Equals(i.ClassName, className, StringComparison.OrdinalIgnoreCase));
            if (it != null) values.Add(it.SpawnedCount);
        }
        if (values.Count < 2) return "\u2192";
        var first = values.Take(values.Count / 2).DefaultIfEmpty(0).Average();
        var second = values.Skip(values.Count / 2).DefaultIfEmpty(0).Average();
        var delta = second - first;
        var threshold = Math.Max(1, first * 0.15);
        if (delta > threshold * 2) return "\u2191";
        if (delta > threshold) return "\u2197";
        if (delta < -threshold * 2) return "\u2193";
        if (delta < -threshold) return "\u2198";
        return "\u2192";
    }

    private void PopulateEconomyCategoryFilter()
    {
        var current = (EconomyCategoryCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        EconomyCategoryCombo.Items.Clear();
        EconomyCategoryCombo.Items.Add(new ComboBoxItem { Content = "All", IsSelected = true });
        foreach (var cat in _allEconomy.Select(e => e.Category).Where(c => !string.IsNullOrEmpty(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c))
            EconomyCategoryCombo.Items.Add(new ComboBoxItem { Content = cat });
        foreach (ComboBoxItem item in EconomyCategoryCombo.Items)
            if (string.Equals(item.Content?.ToString(), current, StringComparison.OrdinalIgnoreCase))
            { item.IsSelected = true; break; }
    }

    private void OnEconomyFilterChanged(object sender, RoutedEventArgs e) => ApplyEconomyFilter();
    private void OnEconomyFilterChanged(object sender, TextChangedEventArgs e) => ApplyEconomyFilter();
    private void OnEconomyFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyEconomyFilter();

    private void ApplyEconomyFilter()
    {
        if (_allEconomy == null) return;
        var search = EconomySearchBox?.Text?.Trim() ?? string.Empty;
        var cat = (EconomyCategoryCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var status = (EconomyStatusCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
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

    // ───────────────── Browse buttons ─────────────────

    private void OnBrowseTypesXml(object sender, RoutedEventArgs e) => Browse(TypesXmlPathBox);
    private void OnBrowseEventsXml(object sender, RoutedEventArgs e) => Browse(EventsXmlPathBox);
    private void OnBrowseGlobalsXml(object sender, RoutedEventArgs e) => Browse(GlobalsXmlPathBox);
    private void OnBrowseSpawnableTypesXml(object sender, RoutedEventArgs e) => Browse(SpawnableTypesXmlPathBox);

    private static void Browse(TextBox target)
    {
        var dlg = new OpenFileDialog { Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) target.Text = dlg.FileName;
    }

    private void OnLoadFromMergedOutput(object sender, RoutedEventArgs e)
    {
        try
        {
            var store = AppConfigStore.Load();
            var merged = store.CombineOutFileText;
            if (string.IsNullOrWhiteSpace(merged))
            {
                MessageBox.Show("No merged output path set yet. Run the Mods Folders tab first.",
                    "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var full = Path.IsPathRooted(merged) ? merged : Path.Combine(AppContext.BaseDirectory, merged);
            if (!File.Exists(full))
            {
                MessageBox.Show($"Merged file not found:\n{full}", "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TypesXmlPathBox.Text = full;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Load failed: " + ex.Message, "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────── Run AI Balancer ─────────────────

    private void UpdateRunButtonEnabled()
    {
        var snap = _listener.RecentSnapshots.LastOrDefault();
        BtnRunBalancer.IsEnabled = snap != null && snap.Items?.Count > 0 && !string.IsNullOrWhiteSpace(ApiKeyBox.Password);
    }

    private async void OnRunBalancerClick(object sender, RoutedEventArgs e)
    {
        var snap = _listener.RecentSnapshots.LastOrDefault();
        if (snap == null || snap.Items == null || snap.Items.Count == 0)
        {
            MessageBox.Show("No economy snapshot yet. Start the listener and wait for the mod to POST data.",
                "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            MessageBox.Show("Enter your OpenAI API key.", "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cfg = CaptureConfigFromUi();
        var opts = new AiBalancerOptions
        {
            ApiKey = ApiKeyBox.Password,
            Model = _cfg.OpenAiModel,
            Concurrency = _cfg.Concurrency,
            BatchSize = _cfg.BatchSize,
            ServerType = _cfg.ServerType,
        };

        _allSuggestions.Clear();
        _viewSuggestions.Clear();
        SuggestionsGroup.Visibility = Visibility.Collapsed;
        _workers.Clear();
        for (var i = 0; i < opts.Concurrency; i++)
            _workers.Add(new WorkerCardViewModel { Title = $"Worker {i + 1}", Status = "idle", ProgressPct = 0 });

        ProgressPanel.Visibility = Visibility.Visible;
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
            MessageBox.Show("AI run failed: " + ex.Message, "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnRunBalancer.IsEnabled = true;
            BtnCancelBalancer.IsEnabled = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnCancelBalancerClick(object sender, RoutedEventArgs e)
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
                _workers[idx].Status = $"batch {p.CurrentBatchIndex + 1} {(p.WorkerStatus == WorkerStatus.Done ? "✓" : "…")}";
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
        SuggestionsGroup.Visibility = _allSuggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var cost = run.TotalTokensUsed * 0.00000005m; // rough nano-tier estimate (informational only)
        SuggestionsSummaryText.Text = $"AI suggests {_allSuggestions.Count} change(s) across {run.Suggestions.Count} items. Tokens used: {run.TotalTokensUsed:N0} (~${cost:F4}).";
    }

    private void OnSuggestionFilterChanged(object sender, RoutedEventArgs e) => ApplySuggestionFilter();
    private void OnSuggestionFilterChanged(object sender, TextChangedEventArgs e) => ApplySuggestionFilter();
    private void OnSuggestionFilterChanged(object sender, SelectionChangedEventArgs e) => ApplySuggestionFilter();

    private void ApplySuggestionFilter()
    {
        if (_allSuggestions == null) return;
        var search = SuggestionSearchBox?.Text?.Trim() ?? string.Empty;
        var field = (SuggestionFieldCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All fields";
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

    // ───────────────── Apply / Export ─────────────────

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var r in _viewSuggestions) r.IsApproved = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var r in _viewSuggestions) r.IsApproved = false; }
    private void OnSelectSevere(object sender, RoutedEventArgs e)
    {
        foreach (var r in _allSuggestions) r.IsApproved = r.IsSevere;
    }

    private void OnRejectSelected(object sender, RoutedEventArgs e)
    {
        foreach (var r in _viewSuggestions.Where(s => s.IsApproved).ToList())
            r.IsApproved = false;
    }

    private void OnApplySelected(object sender, RoutedEventArgs e)
    {
        var path = TypesXmlPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("Set a valid types.xml path first.", "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var approvedRows = _viewSuggestions.Where(r => r.IsApproved).ToList();
        if (approvedRows.Count == 0)
        {
            MessageBox.Show("No suggestions selected.", "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply {approvedRows.Count} field change(s) to types.xml?\n{(BackupCheck.IsChecked == true ? "Original will be backed up." : "No backup will be created.")}",
            "Confirm apply", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        // Build per-className suggestions limited to approved field-rows.
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
            var result = _xml.Apply(toApply, path, BackupCheck.IsChecked == true);
            var msg = $"Applied: {result.Applied}\nNot found: {result.NotFound}";
            if (!string.IsNullOrEmpty(result.BackupPath)) msg += $"\nBackup: {Path.GetFileName(result.BackupPath)}";
            if (result.Errors.Count > 0) msg += "\nErrors: " + result.Errors.Count;
            MessageBox.Show(msg, "Apply complete", MessageBoxButton.OK, MessageBoxImage.Information);
            AppendLog($"Apply complete. {result.Applied} applied, {result.NotFound} not found, {result.Errors.Count} errors.");

            SaveHistoryEntry(_allSuggestions.Count, 0, _cfg.OpenAiModel,
                approvedRows.Count == _allSuggestions.Count ? "Applied" : "Partially applied",
                approvedRows.Count, _allSuggestions.Count - approvedRows.Count, result.BackupPath);
            RefreshHistoryList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Apply failed: " + ex.Message, "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportSuggestions(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"balance-suggestions-{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var payload = _allSuggestions.Select(r => new
            {
                r.ClassName, r.Category, r.Field, r.OldValue, r.NewValue, r.Delta, r.Reason, Approved = r.IsApproved,
            });
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog("Suggestions exported to " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed: " + ex.Message, "AI Balancer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────── History ─────────────────

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
            File.AppendAllText(_historyPath,
                JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppendLog("History save failed: " + ex.Message);
        }
    }

    private void RefreshHistoryList()
    {
        HistoryList.Items.Clear();
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
                    HistoryList.Items.Add($"{entry.Timestamp:yyyy-MM-dd HH:mm} — {entry.SuggestionCount} suggestion(s) — {entry.Status}" +
                        (entry.AppliedCount > 0 ? $" ({entry.AppliedCount} accepted, {entry.RejectedCount} rejected)" : ""));
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex) { AppendLog("History load failed: " + ex.Message); }
    }

    private void OnRefreshAiHistory(object sender, RoutedEventArgs e) => RefreshHistoryList();

    // ───────────────── Log ─────────────────

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            // Trim to ~50 lines
            var lines = LogBox.Text.Split('\n');
            if (lines.Length > 60)
                LogBox.Text = string.Join('\n', lines.Skip(lines.Length - 50));
            LogBox.ScrollToEnd();
        });
    }
}
