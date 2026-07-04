using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DayZModManager.Controls;
using DayZModManager.Helpers;
using DayZModManager.Services;

namespace DayZModManager;

public partial class MainWindow : Window
{
    private const string BakedSteamWebApiKey = "A871AA9AB7D48FFD5C3A6E145EDCCC0B";

    private readonly ObservableCollection<string> _localItems = new();
    private readonly ObservableCollection<WorkshopSearchResultItem> _searchResults = new();
    private readonly ObservableCollection<string> _modsListItems = new();
    private readonly ObservableCollection<string> _modFolders = new();
    private readonly ObservableCollection<string> _historyItems = new();

    private readonly Dictionary<ulong, string?> _titleCache = new();
    private readonly Dictionary<ulong, List<ulong>> _depsCache = new();
    private readonly HashSet<ulong> _modsWithUpdateAvailable = new();

    private ServerProcessController? _server;
    private ServerScheduleService? _schedule;
    private BattlEyeRconClient? _rcon;
    private ServerConfig? _lastAppliedServerConfig;
    private readonly ObservableCollection<AppConfigStore.ServerProfileEntry> _serverProfiles = new();
    private bool _suppressServerProfileSelection;
    private RptLogTail? _tail;
    private readonly LinkedList<string> _tailBuffer = new();
    private DispatcherTimer? _uptimeTimer;
    private bool _forceReauth;
    private int _tailLineCap = 2000;
    private enum LogSource { Rpt, SteamCmd }
    private LogSource _activeLogSource = LogSource.Rpt;

    public MainWindow()
    {
        InitializeComponent();

        LocalIdsListBox.ItemsSource = _localItems;
        SearchResultsListBox.ItemsSource = _searchResults;
        ModsListBox.ItemsSource = _modsListItems;
        ModFoldersListBox.ItemsSource = _modFolders;
        HistoryListBox.ItemsSource = _historyItems;
        ServerProfileComboBox.ItemsSource = _serverProfiles;

        LocalIdsListBox.SelectionChanged += OnLocalIdSelected;

        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) OnWindowStateChanged(); };

        Loaded += (_, _) =>
        {
            var envKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
            var keyToUse = string.IsNullOrWhiteSpace(envKey) ? BakedSteamWebApiKey : envKey!;

            LocalApiKeyTextBox.Text = keyToUse;
            SearchApiKeyTextBox.Text = keyToUse;
            ModsApiKeyTextBox.Text = keyToUse;

            ModsRootTextBox.Text = AppPaths.DefaultModsRoot;
            LocalFilePathTextBox.Text = AppPaths.ModsTxtPath;

            PresetComboBox.ItemsSource = XmlMergePresets.All;
            PresetComboBox.SelectedIndex = 0;

            var cfg = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(cfg.LocalModsTxtPath))
                LocalFilePathTextBox.Text = cfg.LocalModsTxtPath!;
            if (!string.IsNullOrWhiteSpace(cfg.ModsRootPath))
                ModsRootTextBox.Text = cfg.ModsRootPath!;
            if (!string.IsNullOrWhiteSpace(cfg.CombineOutFileText))
                CombineOutFileTextBox.Text = cfg.CombineOutFileText!;
            if (cfg.MergeModeSelectedIndex is int idx)
                MergeModeComboBox.SelectedIndex = idx;
            if (!string.IsNullOrWhiteSpace(cfg.SelectedPresetId))
            {
                var saved = XmlMergePresets.FindById(cfg.SelectedPresetId!);
                if (saved != null) PresetComboBox.SelectedItem = saved;
            }
            if (cfg.ExcludedXmlGenDirs is { Count: > 0 } ex)
                XmlGenExcludeDirsTextBox.Text = string.Join(Environment.NewLine, ex);
            else if (cfg.ExcludedXmlGenDirs == null)
                XmlGenExcludeDirsTextBox.Text = string.Join(Environment.NewLine, XmlMergePresets.DefaultExcludedDirs);

            UpdateCombineEnabled();

            PreStartPresetComboBox.ItemsSource = XmlMergePresets.All;
            PreStartPresetComboBox.SelectedIndex = 0;

            _suppressServerProfileSelection = true;
            _serverProfiles.Clear();
            foreach (var p in cfg.ServerProfiles!) _serverProfiles.Add(p);
            var activeProfile = cfg.GetOrCreateActiveProfile();
            ServerProfileComboBox.SelectedItem = activeProfile;
            _suppressServerProfileSelection = false;

            HydrateServerUiFromConfig(activeProfile.Server);
            InitServerController();
            ApplyScheduleConfig(activeProfile.Server);
            UpdateServerModeVisibility();
            UpdateServerButtonsForState(ServerState.Stopped);

            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptimeTimer.Tick += (_, _) => RefreshUptimeText();

            Closing += OnWindowClosing;

            _ = RefreshLocalAsync();
            _ = RefreshModsListAsync();
            _ = RefreshModFoldersAsync();
            _ = RefreshHistoryAsync();
        };
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try { PersistAllDirsToConfig(); } catch { }
        try { _tail?.Dispose(); } catch { }
        try { _server?.Detach(); } catch { }
        try { _schedule?.Dispose(); } catch { }
        try { _rcon?.Dispose(); } catch { }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnWindowStateChanged()
    {
        if (MaximizeButton != null)
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

        if (WindowRootBorder != null)
        {
            WindowRootBorder.Padding = WindowState == WindowState.Maximized
                ? new Thickness(8, 8, 8, 8)
                : new Thickness(0);
            WindowRootBorder.BorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(1);
        }
    }

    private string CurrentModsTxtPath()
    {
        var fromUi = LocalFilePathTextBox?.Text?.Trim();
        return string.IsNullOrWhiteSpace(fromUi) ? AppPaths.ModsTxtPath : fromUi;
    }

    private void PersistAllDirsToConfig()
    {
        var cfg = AppConfigStore.Load();
        cfg.ModsRootPath = ModsRootTextBox?.Text?.Trim();
        cfg.LocalModsTxtPath = LocalFilePathTextBox?.Text?.Trim();
        cfg.CombineOutFileText = CombineOutFileTextBox?.Text?.Trim();
        cfg.MergeModeSelectedIndex = MergeModeComboBox?.SelectedIndex;
        cfg.SelectedPresetId = (PresetComboBox?.SelectedItem as XmlMergePreset)?.Id;
        cfg.ExcludedXmlGenDirs = ReadExcludedXmlGenDirs();

        var active = SelectedServerProfile ?? cfg.GetOrCreateActiveProfile();
        active.Server = ReadServerConfigFromUi();
        cfg.ServerProfiles = _serverProfiles.Count > 0 ? _serverProfiles.ToList() : cfg.ServerProfiles;
        cfg.ActiveServerProfileId = active.Id;

        AppConfigStore.Save(cfg);
    }

    private List<string>? ReadExcludedXmlGenDirs()
    {
        var raw = XmlGenExcludeDirsTextBox?.Text;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var list = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith("#"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private void UpdateCombineEnabled()
    {
        var enabled = CombineAllCheckBox.IsChecked == true;
        CombineOutFileTextBox.IsEnabled = enabled;
    }

    private void OnCombineAllChanged(object? sender, RoutedEventArgs e) => UpdateCombineEnabled();

    // ---- Topbar / UI sync ----
    private void OnMainTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender != MainTabs) return;
        if (TopCrumb == null || TopPathText == null) return;

        switch (MainTabs.SelectedIndex)
        {
            case 0:
                TopCrumb.Text = "LOCAL_MODS";
                TopPathText.Text = "// " + (LocalFilePathTextBox?.Text ?? "");
                break;
            case 1:
                TopCrumb.Text = "SEARCH_WORKSHOP";
                TopPathText.Text = "// steam workshop · appid " + (AppIdTextBox?.Text ?? "");
                break;
            case 2:
                TopCrumb.Text = "MOD_FOLDERS";
                TopPathText.Text = "// " + (ModsRootTextBox?.Text ?? "");
                break;
            case 3:
                TopCrumb.Text = "SERVER";
                TopPathText.Text = "// dayz server controls";
                break;
            case 4:
                TopCrumb.Text = "HISTORY";
                TopPathText.Text = "// mods.txt history log";
                break;
            case 5:
                TopCrumb.Text = "AI_BALANCER";
                TopPathText.Text = "// ai economy balancer";
                break;
            case 6:
                TopCrumb.Text = "SETTINGS";
                TopPathText.Text = "// app configuration";
                break;
        }
    }

    private void UpdateTopStats(int total, int installed)
    {
        if (TopMods != null)        TopMods.Text        = total.ToString();
        if (TopInstalled != null)   TopInstalled.Text   = installed.ToString();
        if (LocalCountPill != null) LocalCountPill.Text = total.ToString();
    }

    private static string ExtractIdToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private void OnLocalIdSelected(object? sender, SelectionChangedEventArgs e)
    {
        var raw = LocalIdsListBox?.SelectedItem?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("("))
        {
            if (DetailTitleText != null) DetailTitleText.Text = "(select a mod)";
            if (DetailIdText != null)    DetailIdText.Text    = "// no selection";
            if (DetailPathText != null)  DetailPathText.Text  = "—";
            if (DetailInstalledText != null) DetailInstalledText.Text = "—";
            if (DetailWsIdText != null)  DetailWsIdText.Text  = "—";
            return;
        }

        var idToken = ExtractIdToken(raw);
        var title = raw.Contains(" - ")
            ? raw.Substring(raw.IndexOf(" - ", StringComparison.Ordinal) + 3).Replace("(installed)", "").Trim()
            : idToken;

        var modsRoot = ModsRootTextBox?.Text?.Trim() ?? string.Empty;
        var installed = !string.IsNullOrWhiteSpace(modsRoot) && Directory.Exists(Path.Combine(modsRoot, idToken));

        if (DetailTitleText != null) DetailTitleText.Text = string.IsNullOrWhiteSpace(title) ? idToken : title;
        if (DetailIdText != null)    DetailIdText.Text    = "// " + idToken;
        if (DetailWsIdText != null)  DetailWsIdText.Text  = idToken;
        if (DetailPathText != null)  DetailPathText.Text  = installed
            ? Path.Combine(modsRoot, idToken)
            : "(not installed at " + (string.IsNullOrWhiteSpace(modsRoot) ? "<mods root unset>" : modsRoot) + ")";
        if (DetailInstalledText != null) DetailInstalledText.Text = installed ? "yes" : "no";
    }

    // ---- Window chrome buttons ----
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---- Local mods tab ----
    private async void OnBrowseLocalFile(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select mods.txt",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;
        LocalFilePathTextBox.Text = files[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
        _ = RefreshLocalAsync();
    }

    private async void OnRefreshLocal(object? sender, RoutedEventArgs e) => await RefreshLocalAsync();

    private async Task RefreshLocalAsync()
    {
        try
        {
            LocalStatusTextBlock.Text = "Loading...";
            _localItems.Clear();

            var path = LocalFilePathTextBox.Text.Trim();
            var load = ModStorage.LoadIdsWithValidation(path);
            var invalidCount = load.InvalidLines.Count;
            var ids = load.Ids.OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                _localItems.Add("(file empty)");
                LocalFooterTextBlock.Text = invalidCount > 0
                    ? $"No valid IDs found (invalid lines: {invalidCount})."
                    : "No IDs found.";
                return;
            }

            var doLookup = LocalLookupCheckBox.IsChecked == true;
            var showInstalled = LocalShowInstalledCheckBox.IsChecked == true;
            var modsRoot = ModsRootTextBox.Text.Trim();
            var modsRootExists = showInstalled && !string.IsNullOrWhiteSpace(modsRoot) && Directory.Exists(modsRoot);

            if (!doLookup)
            {
                var installedCount = 0;
                foreach (var id in ids)
                {
                    var installed = modsRootExists && Directory.Exists(Path.Combine(modsRoot, id.ToString()));
                    if (installed) installedCount++;
                    var updateTag = _modsWithUpdateAvailable.Contains(id) ? " [UPDATE AVAILABLE]" : "";
                    _localItems.Add((installed ? $"{id} (installed)" : id.ToString()) + updateTag);
                }
                LocalFooterTextBlock.Text = invalidCount > 0
                    ? $"Loaded {ids.Length} IDs (invalid lines: {invalidCount})."
                    : $"Loaded {ids.Length} IDs.";
                UpdateTopStats(ids.Length, installedCount);
                return;
            }

            var apiKey = LocalApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var throttler = new SemaphoreSlim(6);
            var tasks = ids.Select(async id =>
            {
                await throttler.WaitAsync();
                try
                {
                    if (_titleCache.TryGetValue(id, out var cached)) return (id, cached);
                    var details = await SteamWorkshopClient.GetPublishedFileDetailsAsync(id, apiKey);
                    var title = details?.Title;
                    if (!string.IsNullOrWhiteSpace(title)) _titleCache[id] = title;
                    return (id, title);
                }
                catch { return (id, (string?)null); }
                finally { throttler.Release(); }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            var installedTotal = 0;
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
            {
                var installed = modsRootExists && Directory.Exists(Path.Combine(modsRoot, id.ToString()));
                if (installed) installedTotal++;
                var updateTag = _modsWithUpdateAvailable.Contains(id) ? " [UPDATE AVAILABLE]" : "";
                if (!string.IsNullOrWhiteSpace(title))
                    _localItems.Add((installed ? $"{id} - {title} (installed)" : $"{id} - {title}") + updateTag);
                else
                    _localItems.Add((installed ? $"{id} (installed)" : id.ToString()) + updateTag);
            }

            LocalFooterTextBlock.Text = invalidCount > 0
                ? $"Loaded {ids.Length} IDs (invalid lines: {invalidCount})."
                : $"Loaded {ids.Length} IDs.";
            UpdateTopStats(ids.Length, installedTotal);
        }
        catch (Exception ex)
        {
            LocalFooterTextBlock.Text = ex.Message;
        }
        finally
        {
            LocalStatusTextBlock.Text = "";
        }
    }

    private async void OnCheckModUpdates(object? sender, RoutedEventArgs e)
    {
        try
        {
            LocalStatusTextBlock.Text = "Checking for mod updates...";
            var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
            if (ids.Count == 0) { LocalStatusTextBlock.Text = "No mods to check."; return; }

            var apiKey = LocalApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var details = await SteamWorkshopClient.GetPublishedFileDetailsBatchAsync(ids, apiKey);
            var deployState = ModDeployStateStore.LoadAll();

            _modsWithUpdateAvailable.Clear();
            foreach (var id in ids)
            {
                if (!details.TryGetValue(id, out var d) || d.TimeUpdated is not long updated) continue;
                if (deployState.TryGetValue(id, out var deployedAt) && updated > deployedAt)
                    _modsWithUpdateAvailable.Add(id);
            }

            await RefreshLocalAsync();
            LocalStatusTextBlock.Text = _modsWithUpdateAvailable.Count == 0
                ? "Checked - no updates found (note: only mods deployed at least once can be compared)."
                : $"Checked - {_modsWithUpdateAvailable.Count} mod(s) have updates available.";
        }
        catch (Exception ex)
        {
            LocalStatusTextBlock.Text = ex.Message;
        }
    }

    private async void OnRemoveSelected(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selected = LocalIdsListBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(selected)) return;

            var token = selected.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token == null) return;
            var id = ModStorage.ParseWorkshopId(token);

            var modsPath = LocalFilePathTextBox.Text.Trim();
            var existing = ModStorage.LoadIds(modsPath);
            if (!existing.Contains(id))
            {
                LocalStatusTextBlock.Text = $"ID {id} not found.";
                return;
            }

            if (!await MsgBox.Confirm(this, $"Remove {id} from mods.txt?", "Preview mods.txt change")) return;

            var removed = ModStorage.RemoveId(modsPath, id);
            LocalStatusTextBlock.Text = removed ? $"Removed {id}." : $"ID {id} not found.";
            if (removed)
                HistoryLogger.Append("remove", Array.Empty<ulong>(), new[] { id }, "local-remove");
            await RefreshLocalAsync();
        }
        catch (Exception ex)
        {
            LocalStatusTextBlock.Text = ex.Message;
        }
    }

    // ---- Search Workshop tab ----
    private async void OnSearchWorkshop(object? sender, RoutedEventArgs e) => await SearchWorkshopAsync();

    private async Task SearchWorkshopAsync()
    {
        try
        {
            AddStatusTextBlock.Text = "Searching...";
            _searchResults.Clear();

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var appid = uint.Parse(AppIdTextBox.Text.Trim());
            var creatorAppid = uint.Parse(CreatorAppIdTextBox.Text.Trim());
            var searchText = SearchTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(searchText))
                throw new InvalidOperationException("Enter search text.");

            var results = await SteamWorkshopClient.QueryFilesAsync(apiKey, searchText, appid, creatorAppid, 20);
            var list = results.AsEnumerable();

            if (HideInModsCheckBox.IsChecked == true)
            {
                var existing = ModStorage.LoadIds(CurrentModsTxtPath());
                list = list.Where(r => !existing.Contains(r.PublishedFileId));
            }

            list = WorkshopSortComboBox.SelectedIndex switch
            {
                1 => list.OrderBy(r => r.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase),
                2 => list.OrderByDescending(r => r.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase),
                3 => list.OrderBy(r => r.PublishedFileId),
                _ => list
            };

            var final = list.ToArray();
            foreach (var r in final) _searchResults.Add(r);
            AddStatusTextBlock.Text = $"Found {final.Length} results.";
        }
        catch (Exception ex)
        {
            _searchResults.Clear();
            _searchResults.Add(new WorkshopSearchResultItem { Title = "Error", Description = ex.Message, PublishedFileId = 0 });
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private async void OnAddSelected(object? sender, RoutedEventArgs e)
    {
        try
        {
            var item = SearchResultsListBox.SelectedItem as WorkshopSearchResultItem;
            if (item == null || item.PublishedFileId == 0) return;

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var root = new HashSet<ulong> { item.PublishedFileId };
            var closure = root;

            if (AutoAddDepsCheckBox.IsChecked == true)
            {
                AddStatusTextBlock.Text = "Resolving dependencies...";
                closure = await ResolveDependenciesClosureAsync(root, apiKey);
            }

            var modsTxtPath = CurrentModsTxtPath();
            var existing = ModStorage.LoadIds(modsTxtPath);
            var toAdd = closure.Where(x => !existing.Contains(x)).ToArray();

            if (toAdd.Length == 0)
            {
                AddStatusTextBlock.Text = "Nothing to add (already in mods.txt).";
                return;
            }

            var preview = BuildModsTxtAddPreview(toAdd, apiKey);
            if (!await MsgBox.Confirm(this, preview, "Preview mods.txt changes")) return;

            existing.UnionWith(toAdd);
            ModStorage.SaveIdsFromSet(modsTxtPath, existing);
            AddStatusTextBlock.Text = AutoAddDepsCheckBox.IsChecked == true
                ? $"Added {toAdd.Length} mods (incl. dependencies)."
                : $"Added {toAdd.Length} mods to mods.txt.";

            HistoryLogger.Append("add", toAdd, Array.Empty<ulong>(), "workshop-add");
            await RefreshModsListAsync();
        }
        catch (Exception ex)
        {
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private string BuildModsTxtAddPreview(IEnumerable<ulong> toAdd, string apiKey)
    {
        var arr = toAdd.ToArray();
        var head = arr.Take(30).Select(x => x.ToString());
        var more = arr.Length > 30 ? $" +{arr.Length - 30} more" : string.Empty;
        var srcLabel = apiKey?.Length > 0 ? "Steam API key set" : "baked-in key";
        return
            $"About to add {arr.Length} mods to mods.txt.\n\n" +
            string.Join(Environment.NewLine, head) + more +
            $"\n\nSource: {srcLabel}";
    }

    private async void OnAddBulk(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = BulkIdsTextBox.Text ?? string.Empty;
            var rawLines = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            if (rawLines.Length == 0)
            {
                AddStatusTextBlock.Text = "Paste IDs first.";
                return;
            }

            var root = new HashSet<ulong>();
            var invalid = new List<string>();
            foreach (var line in rawLines)
            {
                try { root.Add(ModStorage.ParseWorkshopId(line)); }
                catch { invalid.Add(line); }
            }

            if (invalid.Count > 0)
            {
                AddStatusTextBlock.Text = $"Invalid lines: {invalid.Count} (showing first 5).";
                await MsgBox.Info(this, "Invalid ID lines:\n" + string.Join(Environment.NewLine, invalid.Take(5)), "Bulk add - invalid IDs");
                return;
            }

            if (root.Count == 0) { AddStatusTextBlock.Text = "No valid IDs."; return; }

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var closure = root;
            if (AutoAddDepsCheckBox.IsChecked == true)
            {
                AddStatusTextBlock.Text = "Resolving dependencies...";
                closure = await ResolveDependenciesClosureAsync(root, apiKey);
            }

            var modsTxtPath = CurrentModsTxtPath();
            var existing = ModStorage.LoadIds(modsTxtPath);
            var toAdd = closure.Where(x => !existing.Contains(x)).ToArray();

            if (toAdd.Length == 0) { AddStatusTextBlock.Text = "Nothing to add (already in mods.txt)."; return; }

            var preview = BuildModsTxtAddPreview(toAdd, apiKey);
            if (!await MsgBox.Confirm(this, preview, "Preview mods.txt changes")) return;

            existing.UnionWith(toAdd);
            ModStorage.SaveIdsFromSet(modsTxtPath, existing);
            AddStatusTextBlock.Text = AutoAddDepsCheckBox.IsChecked == true
                ? $"Added {toAdd.Length} mods (incl. dependencies)."
                : $"Added {toAdd.Length} mods to mods.txt.";

            HistoryLogger.Append("add", toAdd, Array.Empty<ulong>(), "bulk-add");
            await RefreshModsListAsync();
        }
        catch (Exception ex)
        {
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private static ulong ParseCollectionIdOrUrl(string raw)
    {
        raw = raw.Trim();

        var idxQuery = raw.IndexOf("?id=", StringComparison.OrdinalIgnoreCase);
        if (idxQuery >= 0)
        {
            var tail = raw[(idxQuery + 4)..];
            var ampIdx = tail.IndexOf('&');
            if (ampIdx >= 0) tail = tail[..ampIdx];
            return ModStorage.ParseWorkshopId(tail);
        }

        // Covers path-shaped URLs without a query string, e.g.
        // steamcommunity.com/sharedfiles/collection/<id> or .../collection/<id>/ -
        // take the trailing run of digits.
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+)\D*$");
        if (match.Success)
            return ModStorage.ParseWorkshopId(match.Groups[1].Value);

        return ModStorage.ParseWorkshopId(raw);
    }

    private async void OnImportCollection(object? sender, RoutedEventArgs e)
    {
        try
        {
            var raw = CollectionIdTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                AddStatusTextBlock.Text = "Enter a collection ID or URL first.";
                return;
            }

            ulong collectionId;
            try { collectionId = ParseCollectionIdOrUrl(raw); }
            catch
            {
                AddStatusTextBlock.Text = "Invalid collection ID/URL.";
                return;
            }

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            AddStatusTextBlock.Text = "Fetching collection...";
            var children = await SteamWorkshopClient.GetCollectionChildrenAsync(collectionId, apiKey);
            if (children.Count == 0)
            {
                AddStatusTextBlock.Text = "Collection is empty or could not be read.";
                return;
            }

            var root = new HashSet<ulong>(children);

            var closure = root;
            if (AutoAddDepsCheckBox.IsChecked == true)
            {
                AddStatusTextBlock.Text = "Resolving dependencies...";
                closure = await ResolveDependenciesClosureAsync(root, apiKey);
            }

            var modsTxtPath = CurrentModsTxtPath();
            var existing = ModStorage.LoadIds(modsTxtPath);
            var toAdd = closure.Where(x => !existing.Contains(x)).ToArray();

            if (toAdd.Length == 0) { AddStatusTextBlock.Text = "Nothing to add (already in mods.txt)."; return; }

            var preview = BuildModsTxtAddPreview(toAdd, apiKey);
            if (!await MsgBox.Confirm(this, preview, "Preview mods.txt changes")) return;

            existing.UnionWith(toAdd);
            ModStorage.SaveIdsFromSet(modsTxtPath, existing);
            AddStatusTextBlock.Text = AutoAddDepsCheckBox.IsChecked == true
                ? $"Imported collection: added {toAdd.Length} mods (incl. dependencies)."
                : $"Imported collection: added {toAdd.Length} mods to mods.txt.";

            HistoryLogger.Append("add", toAdd, Array.Empty<ulong>(), "collection-import");
            await RefreshModsListAsync();
        }
        catch (Exception ex)
        {
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private async Task<HashSet<ulong>> ResolveDependenciesClosureAsync(HashSet<ulong> rootIds, string apiKey)
    {
        var visited = new HashSet<ulong>(rootIds);
        var queue = new Queue<ulong>(rootIds);
        const int maxNodes = 500, batchSize = 8;

        while (queue.Count > 0)
        {
            if (visited.Count > maxNodes) break;
            var batch = new List<ulong>(batchSize);
            while (queue.Count > 0 && batch.Count < batchSize) batch.Add(queue.Dequeue());

            var tasks = batch.Select(async id =>
            {
                if (_depsCache.TryGetValue(id, out var cached)) return (id, cached);
                var children = await SteamWorkshopClient.GetChildrenPublishedFileIdsAsync(id, apiKey);
                _depsCache[id] = children;
                return (id, children);
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            foreach (var (_, children) in results)
                foreach (var child in children)
                    if (visited.Add(child)) queue.Enqueue(child);
        }
        return visited;
    }

    private async void OnShowDependencyTree(object? sender, RoutedEventArgs e)
    {
        try
        {
            var item = SearchResultsListBox.SelectedItem as WorkshopSearchResultItem;
            if (item == null || item.PublishedFileId == 0) return;
            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;
            AddStatusTextBlock.Text = "Building dependency tree...";
            var tree = await BuildDependencyTreeTextAsync(item.PublishedFileId, apiKey, maxDepth: 8, maxNodes: 250);
            await MsgBox.Info(this, tree, $"Dependency tree: {item.PublishedFileId}");
            AddStatusTextBlock.Text = "Tree shown.";
        }
        catch (Exception ex) { AddStatusTextBlock.Text = ex.Message; }
    }

    private async Task<string> BuildDependencyTreeTextAsync(ulong root, string apiKey, int maxDepth, int maxNodes)
    {
        var visited = new HashSet<ulong>();
        var lines = new List<string>();
        var nodeCount = 0;

        async Task<List<ulong>> GetChildrenCachedAsync(ulong id)
        {
            if (_depsCache.TryGetValue(id, out var cached)) return cached;
            var children = await SteamWorkshopClient.GetChildrenPublishedFileIdsAsync(id, apiKey);
            _depsCache[id] = children;
            return children;
        }

        async Task WalkAsync(ulong id, int depth, string prefix)
        {
            if (nodeCount >= maxNodes) return;
            if (!visited.Add(id)) return;
            nodeCount++;
            lines.Add(depth == 0 ? id.ToString() : $"{prefix}{id}");
            if (depth >= maxDepth) return;
            List<ulong> children;
            try { children = await GetChildrenCachedAsync(id); }
            catch { return; }
            var list = children ?? new List<ulong>();
            for (var i = 0; i < list.Count; i++)
                await WalkAsync(list[i], depth + 1, i == list.Count - 1 ? "└─ " : "├─ ");
        }

        await WalkAsync(root, 0, "");
        return lines.Count == 0 ? "(no dependencies / none returned)" : string.Join(Environment.NewLine, lines);
    }

    // ---- mods.txt list ----
    private async void OnRefreshModsList(object? sender, RoutedEventArgs e) => await RefreshModsListAsync();

    private async Task RefreshModsListAsync()
    {
        try
        {
            ModsStatusTextBlock.Text = "Loading...";
            _modsListItems.Clear();
            var ids = ModStorage.LoadIds(CurrentModsTxtPath()).OrderBy(x => x).ToArray();
            if (ids.Length == 0) { _modsListItems.Add("(mods.txt empty)"); return; }

            var doLookup = ModsLookupCheckBox.IsChecked == true;
            if (!doLookup)
            {
                foreach (var id in ids) _modsListItems.Add(id.ToString());
                ModsStatusTextBlock.Text = $"Loaded {ids.Length} IDs.";
                return;
            }

            var apiKey = ModsApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var throttler = new SemaphoreSlim(6);
            var tasks = ids.Select(async id =>
            {
                await throttler.WaitAsync();
                try
                {
                    if (_titleCache.TryGetValue(id, out var cached)) return (id, cached);
                    var details = await SteamWorkshopClient.GetPublishedFileDetailsAsync(id, apiKey);
                    var title = details?.Title;
                    if (!string.IsNullOrWhiteSpace(title)) _titleCache[id] = title;
                    return (id, title);
                }
                catch { return (id, (string?)null); }
                finally { throttler.Release(); }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
                _modsListItems.Add(!string.IsNullOrWhiteSpace(title) ? $"{id} - {title}" : id.ToString());

            ModsStatusTextBlock.Text = $"Loaded {ids.Length} mods.";
        }
        catch (Exception ex) { ModsStatusTextBlock.Text = ex.Message; }
    }

    // ---- Mods Folders tab ----
    private async void OnBrowseModsRoot(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select mods root directory",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        ModsRootTextBox.Text = folders[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
        _ = RefreshModFoldersAsync();
    }

    private async void OnRefreshModFolders(object? sender, RoutedEventArgs e) => await RefreshModFoldersAsync();

    private async Task RefreshModFoldersAsync()
    {
        _modFolders.Clear();
        var root = ModsRootTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            FolderDetailsTextBox.Text = "Invalid mods root directory.";
            return;
        }

        var dirs = Directory.EnumerateDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .Where(di => !string.IsNullOrWhiteSpace(di.Name))
            .OrderBy(di => di.Name, StringComparer.OrdinalIgnoreCase)
            .Select(di => di.Name)
            .ToArray();

        foreach (var d in dirs) _modFolders.Add(d);
        if (_modFolders.Count > 0) await RefreshFolderDetailsAsync(_modFolders[0]);
    }

    private async void OnModFolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ModFoldersListBox.SelectedItem == null) return;
        await RefreshFolderDetailsAsync(ModFoldersListBox.SelectedItem.ToString()!);
    }

    private Task RefreshFolderDetailsAsync(string selected)
    {
        var root = ModsRootTextBox.Text.Trim();
        var modDir = Path.Combine(root, selected);
        var sb = new StringBuilder();
        sb.AppendLine($"Mod folder: {selected}");
        sb.AppendLine($"Path: {modDir}");

        if (!Directory.Exists(modDir))
        {
            sb.AppendLine("Directory missing.");
            FolderDetailsTextBox.Text = sb.ToString();
            return Task.CompletedTask;
        }

        const int maxTypesFilesToShow = 200, maxOtherXmlToShow = 80;

        var typesFiles = Directory.EnumerateFiles(modDir, "*types*.xml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(maxTypesFilesToShow).ToArray();

        sb.AppendLine();
        sb.AppendLine($"types.xml-related files (showing up to {maxTypesFilesToShow}):");
        if (typesFiles.Length == 0) sb.AppendLine("(none found under this folder)");
        else foreach (var f in typesFiles) { var fi = new FileInfo(f); sb.AppendLine($"- {fi.Name} ({fi.Length / 1024.0:0.0} KB)"); }

        var otherXmlFiles = Directory.EnumerateFiles(modDir, "*.xml", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(maxOtherXmlToShow).ToArray()!;

        sb.AppendLine();
        sb.AppendLine($"Other XML files (sample, up to {maxOtherXmlToShow}):");
        if (otherXmlFiles.Length == 0) sb.AppendLine("(none found under this folder)");
        else foreach (var n in otherXmlFiles) sb.AppendLine($"- {n}");

        FolderDetailsTextBox.Text = sb.ToString();
        return Task.CompletedTask;
    }

    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not XmlMergePreset preset) return;
        IncludePatternsTextBox.Text = string.Join(", ", preset.IncludePatterns);
        ExcludePatternsTextBox.Text = string.Join(", ", preset.ExcludePatterns);
        RootElementTextBox.Text = preset.RootElementName;
        KeyAttributeTextBox.Text = preset.KeyAttribute ?? string.Empty;
        CombineOutFileTextBox.Text = preset.OutputFileName;
        PresetDescriptionText.Text = preset.Description;
    }

    private XmlMergePreset BuildPresetFromUi()
    {
        var basePreset = PresetComboBox.SelectedItem as XmlMergePreset ?? XmlMergePresets.FindById("types")!;

        static string[] SplitList(string? raw) =>
            string.IsNullOrWhiteSpace(raw) ? Array.Empty<string>()
            : raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        var includes = SplitList(IncludePatternsTextBox.Text);
        var excludes = SplitList(ExcludePatternsTextBox.Text);
        var rootName = string.IsNullOrWhiteSpace(RootElementTextBox.Text) ? basePreset.RootElementName : RootElementTextBox.Text.Trim();
        var keyAttr  = string.IsNullOrWhiteSpace(KeyAttributeTextBox.Text) ? null : KeyAttributeTextBox.Text.Trim();

        return basePreset with
        {
            IncludePatterns = includes.Length > 0 ? includes : basePreset.IncludePatterns,
            ExcludePatterns = excludes,
            RootElementName = rootName,
            KeyAttribute    = keyAttr,
        };
    }

    private async void OnGenerateCombinedTypes(object? sender, RoutedEventArgs e)
    {
        try
        {
            var root = ModsRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                FolderDetailsTextBox.Text = "Invalid mods root directory.";
                return;
            }
            var outVal = CombineOutFileTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(outVal)) { FolderDetailsTextBox.Text = "Please set output file path."; return; }

            var outFile   = AppPaths.ResolveOutputPath(outVal);
            var mergeMode = GetSelectedMergeMode();
            var preset    = BuildPresetFromUi();

            FolderDetailsTextBox.Text = $"Combining {preset.DisplayName}...";
            var excluded = ReadExcludedXmlGenDirs();
            var stats = await Task.Run(() => XmlMergeService.Generate(root, preset, mergeMode, outFile, excluded));

            var sb = new StringBuilder();
            sb.AppendLine($"Generated: {outFile}");
            sb.AppendLine();
            sb.AppendLine(FormatStats(stats));
            FolderDetailsTextBox.Text = sb.ToString();
        }
        catch (Exception ex) { FolderDetailsTextBox.Text = $"Generation failed: {ex.Message}"; }
    }

    private void OnPreviewCombinedTypes(object? sender, RoutedEventArgs e) => _ = PreviewCombinedTypesAsync();

    private async Task PreviewCombinedTypesAsync()
    {
        try
        {
            var root = ModsRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                FolderDetailsTextBox.Text = "Invalid mods root directory.";
                return;
            }
            var mergeMode = GetSelectedMergeMode();
            var preset    = BuildPresetFromUi();
            FolderDetailsTextBox.Text = $"Previewing {preset.DisplayName} (dry-run)...";
            var excluded = ReadExcludedXmlGenDirs();
            var stats = await Task.Run(() => XmlMergeService.Preview(root, preset, mergeMode, excluded));
            FolderDetailsTextBox.Text = "Dry-run preview:\r\n\r\n" + FormatStats(stats);
        }
        catch (Exception ex) { FolderDetailsTextBox.Text = $"Preview failed: {ex.Message}"; }
    }

    /// <summary>Best-effort, non-blocking pre-deploy warning; never gates the actual deploy.</summary>
    private void LogModConflictsIfAny()
    {
        try
        {
            var root = ModsRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            var entries = ModConflictDetector.DetectConflicts(root, GetSelectedMergeMode(), ReadExcludedXmlGenDirs());
            if (entries.Count > 0)
                AppendLogLine($"[manager] warning: {entries.Count} mod conflict(s) detected across types/events/spawnable presets — see MOD_FOLDERS > CHECK MOD CONFLICTS for details.");
        }
        catch
        {
            // Pre-deploy convenience check only; never block deploy on it.
        }
    }

    private void OnCheckModConflicts(object? sender, RoutedEventArgs e) => _ = CheckModConflictsAsync();

    private async Task CheckModConflictsAsync()
    {
        try
        {
            var root = ModsRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                FolderDetailsTextBox.Text = "Invalid mods root directory.";
                return;
            }

            FolderDetailsTextBox.Text = "Checking for mod conflicts (types/events/spawnable presets)...";
            var mergeMode = GetSelectedMergeMode();
            var excluded = ReadExcludedXmlGenDirs();
            var entries = await Task.Run(() => ModConflictDetector.DetectConflicts(root, mergeMode, excluded));
            FolderDetailsTextBox.Text = ModConflictDetector.FormatReport(entries);
        }
        catch (Exception ex) { FolderDetailsTextBox.Text = $"Conflict check failed: {ex.Message}"; }
    }

    private static string FormatStats(XmlMergeStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Preset: {stats.PresetDisplayName}");
        sb.AppendLine($"Mod dirs scanned: {stats.ModDirsScanned}");
        sb.AppendLine($"Matching files found: {stats.FilesFound}");
        sb.AppendLine($"Candidate child elements: {stats.CandidateChildrenFound}");
        sb.AppendLine($"Merged children written: {stats.MergedChildren}");
        sb.AppendLine($"Unique keys: {stats.UniqueKeys}");
        sb.AppendLine($"Conflicts detected: {stats.ConflictCount}");
        if (stats.SkippedInvalidFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Skipped (invalid XML): {stats.SkippedInvalidFiles.Count}");
            foreach (var f in stats.SkippedInvalidFiles.Take(10)) sb.AppendLine($"  - {f}");
        }
        if (stats.ConflictCount > 0 && stats.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("First conflicts:");
            foreach (var c in stats.Conflicts.Take(25)) sb.AppendLine($"  - {c.Key}");
        }
        return sb.ToString();
    }

    private TypesXmlMergeMode GetSelectedMergeMode() => MergeModeComboBox.SelectedIndex switch
    {
        0 => TypesXmlMergeMode.DedupeFirstByKey,
        1 => TypesXmlMergeMode.Append,
        2 => TypesXmlMergeMode.DedupeLastByKey,
        _ => TypesXmlMergeMode.DedupeFirstByKey
    };

    private async void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        var cfg = AppConfigStore.Load();
        cfg.ModsRootPath = ModsRootTextBox.Text.Trim();
        cfg.LocalModsTxtPath = LocalFilePathTextBox.Text.Trim();
        cfg.CombineOutFileText = CombineOutFileTextBox.Text.Trim();
        cfg.MergeModeSelectedIndex = MergeModeComboBox.SelectedIndex;
        cfg.SelectedPresetId = (PresetComboBox.SelectedItem as XmlMergePreset)?.Id;
        cfg.ExcludedXmlGenDirs = ReadExcludedXmlGenDirs();

        var active = SelectedServerProfile ?? cfg.GetOrCreateActiveProfile();
        var serverCfg = ReadServerConfigFromUi();
        active.Server = serverCfg;
        cfg.ServerProfiles = _serverProfiles.Count > 0 ? _serverProfiles.ToList() : cfg.ServerProfiles;
        cfg.ActiveServerProfileId = active.Id;

        AppConfigStore.Save(cfg);
        ApplyServerConfigToController(serverCfg);
        await MsgBox.Info(this, "Settings saved.", "Saved");
    }

    private async void OnExportProfile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = (ProfileNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "default";

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export profile",
                SuggestedFileName = $"{name}.json",
                FileTypeChoices = new[] { new FilePickerFileType("Profile json") { Patterns = new[] { "*.json" } } }
            });
            if (file == null) return;

            var profile = new AppProfile
            {
                Name = name,
                ModsTxtPath = CurrentModsTxtPath(),
                ModsRootPath = ModsRootTextBox.Text.Trim(),
                CombineOutFile = CombineOutFileTextBox.Text.Trim(),
                MergeMode = GetSelectedMergeMode(),
                AutoAddDeps = AutoAddDepsCheckBox.IsChecked == true,
                SearchApiKey = SearchApiKeyTextBox.Text.Trim(),
                LocalModsApiKey = LocalApiKeyTextBox.Text.Trim(),
                ModsIds = ModStorage.LoadIds(CurrentModsTxtPath()).ToList(),
                Server = ReadServerConfigFromUi()
            };

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(file.Path.LocalPath, json, Encoding.UTF8);
            await MsgBox.Info(this, "Profile exported.", "Export");
        }
        catch (Exception ex)
        {
            await MsgBox.Info(this, ex.Message, "Export failed");
        }
    }

    private async void OnImportProfile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import profile",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Profile json") { Patterns = new[] { "*.json" } } }
            });
            if (files.Count == 0) return;

            var json = File.ReadAllText(files[0].Path.LocalPath);
            var profile = JsonSerializer.Deserialize<AppProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (profile == null) throw new InvalidOperationException("Invalid profile json.");

            ProfileNameTextBox.Text = profile.Name;
            ModsRootTextBox.Text = profile.ModsRootPath ?? ModsRootTextBox.Text;
            CombineOutFileTextBox.Text = profile.CombineOutFile ?? CombineOutFileTextBox.Text;
            AutoAddDepsCheckBox.IsChecked = profile.AutoAddDeps;
            SearchApiKeyTextBox.Text = profile.SearchApiKey ?? SearchApiKeyTextBox.Text;
            LocalApiKeyTextBox.Text = profile.LocalModsApiKey ?? LocalApiKeyTextBox.Text;
            MergeModeComboBox.SelectedIndex = profile.MergeMode switch
            {
                TypesXmlMergeMode.DedupeFirstByKey => 0,
                TypesXmlMergeMode.Append => 1,
                TypesXmlMergeMode.DedupeLastByKey => 2,
                _ => 0
            };

            if (profile.Server != null)
            {
                HydrateServerUiFromConfig(profile.Server);
                ApplyServerConfigToController(profile.Server);
                UpdateServerModeVisibility();
            }

            ModStorage.SaveIdsFromSet(CurrentModsTxtPath(), profile.ModsIds ?? new List<ulong>());
            _ = RefreshLocalAsync();
            _ = RefreshModsListAsync();
            _ = RefreshModFoldersAsync();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            await MsgBox.Info(this, ex.Message, "Import failed");
        }
    }

    private void OnRefreshHistory(object? sender, RoutedEventArgs e) => _ = RefreshHistoryAsync();

    private Task RefreshHistoryAsync()
    {
        try
        {
            var entries = HistoryLogger.LoadRecent(30);
            _historyItems.Clear();
            foreach (var e in entries)
            {
                var added = e.Added.Count > 0 ? string.Join(",", e.Added.Take(5)) + (e.Added.Count > 5 ? "…" : "") : "[]";
                var removed = e.Removed.Count > 0 ? string.Join(",", e.Removed.Take(5)) + (e.Removed.Count > 5 ? "…" : "") : "[]";
                _historyItems.Add($"[{e.Timestamp:u}] {e.Action} (add:{added} rem:{removed}) - {e.Source}");
            }
        }
        catch (Exception ex)
        {
            _historyItems.Clear();
            _historyItems.Add(ex.Message);
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVER TAB
    // ═══════════════════════════════════════════════════════════════════

    private void HydrateServerUiFromConfig(ServerConfig? cfg)
    {
        cfg ??= new ServerConfig();
        if (cfg.Mode == ServerLaunchMode.Ps1) ModePs1Radio.IsChecked = true;
        else ModeDirectRadio.IsChecked = true;

        ServerPs1PathTextBox.Text = cfg.Ps1Path ?? string.Empty;
        Ps1LaunchParamComboBox.SelectedIndex = string.Equals(cfg.Ps1LaunchParam, "user", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Ps1AppBranchComboBox.SelectedIndex = string.Equals(cfg.Ps1AppBranch, "exp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Ps1UpdateTargetComboBox.SelectedIndex = cfg.Ps1UpdateTarget?.ToLowerInvariant() switch { "server" => 0, "mod" => 1, _ => 2 };
        ServerExePathTextBox.Text = cfg.ExePath ?? string.Empty;
        ServerProfileDirTextBox.Text = cfg.ProfileDir ?? AppPaths.DefaultServerProfileDir;
        ServerRootDirTextBox.Text = cfg.ServerRootDir
            ?? (string.IsNullOrWhiteSpace(cfg.ExePath) ? string.Empty : (Path.GetDirectoryName(cfg.ExePath) ?? string.Empty));
        ServerPortTextBox.Text = (cfg.Port ?? 2302).ToString();
        ServerExtraArgsTextBox.Text = cfg.ExtraArgs ?? string.Empty;
        ServerBattlEyeCheckBox.IsChecked = cfg.BattlEye;
        SteamCmdPathTextBox.Text = cfg.SteamCmdPath ?? string.Empty;
        ModCacheDirTextBox.Text = !string.IsNullOrWhiteSpace(cfg.SteamCmdPath)
            ? (Path.GetDirectoryName(Path.GetFullPath(cfg.SteamCmdPath)) ?? string.Empty)
            : (cfg.ModCacheDir ?? AppPaths.DefaultModCacheDir);
        SteamLoginTextBox.Text = cfg.SteamLogin ?? string.Empty;
        if (cfg.LoginMode == SteamLoginMode.Anonymous) LoginAnonymousRadio.IsChecked = true;
        else LoginInteractiveRadio.IsChecked = true;
        WorkshopAppIdTextBox.Text = cfg.WorkshopAppId.ToString();
        DeployModeComboBox.SelectedIndex = (int)cfg.DeployMode;
        ValidateModsCheckBox.IsChecked = cfg.ValidateMods;
        AutoUpdateBeforeStartCheckBox.IsChecked = cfg.AutoUpdateModsBeforeStart;
        AutoRestartCheckBox.IsChecked = cfg.AutoRestartOnCrash;
        AutoRestartBackoffTextBox.Text = cfg.AutoRestartBackoffSeconds.ToString();
        AutoRestartMaxRetriesTextBox.Text = cfg.AutoRestartMaxRetries.ToString();
        PreStartMergeCheckBox.IsChecked = cfg.RunPreStartMerge;
        if (!string.IsNullOrWhiteSpace(cfg.PreStartPresetId))
        {
            var preset = XmlMergePresets.FindById(cfg.PreStartPresetId!);
            if (preset != null) PreStartPresetComboBox.SelectedItem = preset;
        }
        _tailLineCap = cfg.TailLineCap > 0 ? cfg.TailLineCap : 2000;
        WebhookUrlTextBox.Text = cfg.WebhookUrl ?? string.Empty;
        NotifyOnCrashCheckBox.IsChecked = cfg.NotifyOnCrash;
        NotifyOnRestartCheckBox.IsChecked = cfg.NotifyOnRestart;
        NotifyOnModUpdateCheckBox.IsChecked = cfg.NotifyOnModUpdate;
        ScheduledRestartCheckBox.IsChecked = cfg.ScheduledRestartEnabled;
        ScheduledRestartTimeTextBox.Text = string.IsNullOrWhiteSpace(cfg.ScheduledRestartTimeOfDay) ? "04:00" : cfg.ScheduledRestartTimeOfDay;
        ScheduledUpdateCheckCheckBox.IsChecked = cfg.ScheduledUpdateCheckEnabled;
        ScheduledUpdateIntervalTextBox.Text = cfg.ScheduledUpdateIntervalHours > 0 ? cfg.ScheduledUpdateIntervalHours.ToString() : "6";
        ApplyScheduleConfig(cfg);

        RconEnabledCheckBox.IsChecked = cfg.RconEnabled;
        RconPortTextBox.Text = (cfg.RconPort > 0 ? cfg.RconPort : 2306).ToString();
        RconPasswordTextBox.Text = ApiKeyProtection.Unprotect(cfg.RconPasswordEncrypted ?? string.Empty);
    }

    private ServerConfig ReadServerConfigFromUi()
    {
        var mode = (ModePs1Radio.IsChecked == true) ? ServerLaunchMode.Ps1 : ServerLaunchMode.DirectExe;
        var loginMode = (LoginAnonymousRadio.IsChecked == true) ? SteamLoginMode.Anonymous : SteamLoginMode.Interactive;
        int? port = int.TryParse(ServerPortTextBox.Text.Trim(), out var p) ? p : (int?)null;
        uint appId = uint.TryParse(WorkshopAppIdTextBox.Text.Trim(), out var a) ? a : 221100u;
        int backoff = int.TryParse(AutoRestartBackoffTextBox.Text.Trim(), out var b) ? b : 10;
        int maxRetries = int.TryParse(AutoRestartMaxRetriesTextBox.Text.Trim(), out var m) ? m : 3;

        return new ServerConfig
        {
            Mode = mode,
            Ps1Path = NullIfEmpty(ServerPs1PathTextBox.Text),
            Ps1LaunchParam = Ps1LaunchParamComboBox.SelectedIndex == 1 ? "user" : "default",
            Ps1AppBranch   = Ps1AppBranchComboBox.SelectedIndex == 1 ? "exp" : "stable",
            Ps1UpdateTarget = Ps1UpdateTargetComboBox.SelectedIndex switch { 0 => "server", 1 => "mod", _ => "all" },
            ExePath = NullIfEmpty(ServerExePathTextBox.Text),
            ProfileDir = NullIfEmpty(ServerProfileDirTextBox.Text),
            ServerRootDir = NullIfEmpty(ServerRootDirTextBox.Text),
            Port = port,
            ExtraArgs = NullIfEmpty(ServerExtraArgsTextBox.Text),
            BattlEye = ServerBattlEyeCheckBox.IsChecked == true,
            SteamCmdPath = NullIfEmpty(SteamCmdPathTextBox.Text),
            ModCacheDir = NullIfEmpty(ModCacheDirTextBox.Text),
            LoginMode = loginMode,
            SteamLogin = NullIfEmpty(SteamLoginTextBox.Text),
            WorkshopAppId = appId,
            DeployMode = (ModDeployMode)Math.Max(0, DeployModeComboBox.SelectedIndex),
            ValidateMods = ValidateModsCheckBox.IsChecked == true,
            AutoUpdateModsBeforeStart = AutoUpdateBeforeStartCheckBox.IsChecked == true,
            AutoRestartOnCrash = AutoRestartCheckBox.IsChecked == true,
            AutoRestartBackoffSeconds = backoff,
            AutoRestartMaxRetries = maxRetries,
            RunPreStartMerge = PreStartMergeCheckBox.IsChecked == true,
            PreStartPresetId = (PreStartPresetComboBox.SelectedItem as XmlMergePreset)?.Id,
            TailLineCap = _tailLineCap,
            WebhookUrl = NullIfEmpty(WebhookUrlTextBox.Text),
            NotifyOnCrash = NotifyOnCrashCheckBox.IsChecked == true,
            NotifyOnRestart = NotifyOnRestartCheckBox.IsChecked == true,
            NotifyOnModUpdate = NotifyOnModUpdateCheckBox.IsChecked == true,
            ScheduledRestartEnabled = ScheduledRestartCheckBox.IsChecked == true,
            ScheduledRestartTimeOfDay = ParseScheduleTimeOfDayOrDefault(ScheduledRestartTimeTextBox.Text),
            ScheduledUpdateCheckEnabled = ScheduledUpdateCheckCheckBox.IsChecked == true,
            ScheduledUpdateIntervalHours = double.TryParse(ScheduledUpdateIntervalTextBox.Text.Trim(), out var hrs) && hrs > 0 ? hrs : 6,
            RconEnabled = RconEnabledCheckBox.IsChecked == true,
            RconPort = int.TryParse(RconPortTextBox.Text.Trim(), out var rp) && rp > 0 ? rp : 2306,
            RconPasswordEncrypted = ApiKeyProtection.Protect(RconPasswordTextBox.Text ?? string.Empty)
        };
    }

    private static string ParseScheduleTimeOfDayOrDefault(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return TimeSpan.TryParseExact(trimmed, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out _)
            ? trimmed
            : "04:00";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void InitServerController()
    {
        _server = new ServerProcessController();
        _server.StateChanged += s => Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateServerButtonsForState(s);
            UpdateServerStatusPill(s);
            if (s == ServerState.Running) _uptimeTimer?.Start();
            else _uptimeTimer?.Stop();
            RefreshUptimeText();
        });
        _server.Exited += (code, crashed) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            ServerActionStatusText.Text = crashed ? $"crashed (exit {code})" : $"exited (exit {code})";
            if (crashed && _lastAppliedServerConfig is { NotifyOnCrash: true } cfg)
                _ = WebhookNotifier.NotifyAsync(cfg.WebhookUrl, $"⚠️ DayZ server crashed (exit code {code}).");
        });

        _schedule = new ServerScheduleService();
        _schedule.RestartDue += () => Dispatcher.UIThread.InvokeAsync(() => { _ = OnScheduledRestartDueAsync(); });
        _schedule.UpdateCheckDue += () => Dispatcher.UIThread.InvokeAsync(() => { _ = OnScheduledUpdateCheckDueAsync(); });
    }

    private async Task OnScheduledRestartDueAsync()
    {
        if (_server == null || _server.State != ServerState.Running) return;
        try
        {
            var cfg = ReadServerConfigFromUi();
            ApplyServerConfigToController(cfg);
            SyncBattlEyeRconCfgIfEnabled(cfg);
            ServerHistoryLogger.Append("scheduled-restart", cfg.Mode.ToString());
            AppendLogLine("[manager] scheduled restart firing…");

            var (deployed, deployedServer) = cfg.Mode == ServerLaunchMode.DirectExe
                ? DeployMods(cfg) : (new List<string>(), new List<string>());
            var spec = new ServerLaunchSpec(cfg.Mode, cfg.Ps1Path, cfg.ExePath, cfg.ProfileDir,
                cfg.ServerRootDir, cfg.Port, cfg.ExtraArgs, cfg.BattlEye, deployed,
                cfg.Ps1LaunchParam, cfg.Ps1AppBranch, deployedServer);
            await _server.RestartAsync(spec);
            ServerActionStatusText.Text = _server.State == ServerState.Running ? "running" : "restart failed";
            UpdateServerButtonsForState(_server.State);

            if (cfg.NotifyOnRestart)
                _ = WebhookNotifier.NotifyAsync(cfg.WebhookUrl, "🔄 DayZ server scheduled restart complete.");
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] scheduled restart failed: {ex.Message}");
            ServerHistoryLogger.Append("scheduled-restart-failed", "?", detail: ex.Message);
        }
    }

    private async Task OnScheduledUpdateCheckDueAsync()
    {
        var cfg = ReadServerConfigFromUi();
        if (cfg.Mode != ServerLaunchMode.DirectExe) return; // SteamCMD update path only applies here

        try
        {
            AppendLogLine("[manager] scheduled mod-update check running…");
            var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
            if (ids.Count == 0) return;

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var details = await SteamWorkshopClient.GetPublishedFileDetailsBatchAsync(ids, apiKey);
            var deployState = ModDeployStateStore.LoadAll();
            var pending = ids.Count(id =>
                details.TryGetValue(id, out var d) && d.TimeUpdated is long updated &&
                deployState.TryGetValue(id, out var deployedAt) && updated > deployedAt);

            if (pending == 0)
            {
                AppendLogLine("[manager] scheduled check: no mod updates found.");
                return;
            }

            AppendLogLine($"[manager] scheduled check: {pending} mod(s) updated — downloading via SteamCMD…");
            var ok = await RunSteamCmdUpdateAsync(cfg);
            if (!ok)
            {
                AppendLogLine("[manager] scheduled mod update failed.");
                ServerHistoryLogger.Append("scheduled-update-failed", cfg.Mode.ToString());
                return;
            }

            DeployMods(cfg);
            ServerHistoryLogger.Append("scheduled-update-done", cfg.Mode.ToString(), detail: $"pending={pending}");
            if (cfg.NotifyOnModUpdate)
                _ = WebhookNotifier.NotifyAsync(cfg.WebhookUrl, $"📦 Scheduled check found and deployed {pending} mod update(s).");

            if (_server != null && _server.State == ServerState.Running)
                await OnScheduledRestartDueAsync();
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] scheduled mod-update check failed: {ex.Message}");
            ServerHistoryLogger.Append("scheduled-update-failed", cfg.Mode.ToString(), detail: ex.Message);
        }
    }

    private void ApplyServerConfigToController(ServerConfig? cfg)
    {
        if (_server == null || cfg == null) return;
        _lastAppliedServerConfig = cfg;
        _server.AutoRestartOnCrash = cfg.AutoRestartOnCrash;
        _server.AutoRestartBackoffSeconds = cfg.AutoRestartBackoffSeconds;
        _server.AutoRestartMaxRetries = cfg.AutoRestartMaxRetries;
        ApplyScheduleConfig(cfg);
    }

    private void ApplyScheduleConfig(ServerConfig cfg)
    {
        if (_schedule == null) return;
        _schedule.RestartEnabled = cfg.ScheduledRestartEnabled;
        _schedule.RestartTimeOfDay = TimeSpan.TryParseExact(cfg.ScheduledRestartTimeOfDay, "hh\\:mm",
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(4, 0, 0);
        _schedule.UpdateCheckEnabled = cfg.ScheduledUpdateCheckEnabled;
        _schedule.UpdateCheckIntervalHours = cfg.ScheduledUpdateIntervalHours > 0 ? cfg.ScheduledUpdateIntervalHours : 6;
    }

    /// <summary>Keeps BEServer_x64.cfg in sync before each start/restart. Best-effort - never blocks the launch.</summary>
    private void SyncBattlEyeRconCfgIfEnabled(ServerConfig cfg)
    {
        if (!cfg.RconEnabled || cfg.Mode != ServerLaunchMode.DirectExe) return;
        if (string.IsNullOrWhiteSpace(cfg.ServerRootDir)) return;

        try
        {
            var password = ApiKeyProtection.Unprotect(cfg.RconPasswordEncrypted);
            if (string.IsNullOrWhiteSpace(password))
            {
                AppendLogLine("[manager] RCON enabled but no password set - skipping BEServer_x64.cfg sync.");
                return;
            }
            BattlEyeServerCfgWriter.EnsureConfigured(cfg.ServerRootDir!, cfg.RconPort, password);
            AppendLogLine("[manager] BEServer_x64.cfg synced with configured RCON port/password.");
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] RCON config sync failed: {ex.Message}");
        }
    }

    // ---- RCON (BattlEye admin console) ----

    private async void OnRconConnect(object? sender, RoutedEventArgs e)
    {
        try
        {
            var port = int.TryParse(RconPortTextBox.Text.Trim(), out var p) && p > 0 ? p : 2306;
            var password = RconPasswordTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(password))
            {
                RconStatusText.Text = "rcon: set a password first.";
                return;
            }

            _rcon?.Dispose();
            _rcon = new BattlEyeRconClient();
            _rcon.MessageReceived += m => Dispatcher.UIThread.InvokeAsync(() => AppendRconOutput("[msg] " + m));
            _rcon.Disconnected += () => Dispatcher.UIThread.InvokeAsync(() =>
            {
                RconStatusText.Text = "rcon: disconnected";
                RconConnectButton.IsEnabled = true;
                RconDisconnectButton.IsEnabled = false;
            });

            RconStatusText.Text = "rcon: connecting...";
            RconConnectButton.IsEnabled = false;
            var ok = await _rcon.ConnectAsync("127.0.0.1", port, password);
            RconStatusText.Text = ok ? "rcon: connected" : "rcon: login failed";
            RconConnectButton.IsEnabled = !ok;
            RconDisconnectButton.IsEnabled = ok;
        }
        catch (Exception ex)
        {
            RconStatusText.Text = "rcon: " + ex.Message;
            RconConnectButton.IsEnabled = true;
        }
    }

    private void OnRconDisconnect(object? sender, RoutedEventArgs e)
    {
        _rcon?.Disconnect();
        RconStatusText.Text = "rcon: disconnected";
        RconConnectButton.IsEnabled = true;
        RconDisconnectButton.IsEnabled = false;
    }

    private void AppendRconOutput(string text)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {text}\r\n" + RconOutputTextBox.Text;
        RconOutputTextBox.Text = stamped.Length > 20000 ? stamped.Substring(0, 20000) : stamped;
    }

    private async Task RunRconCommandAsync(Func<Task<string>> action)
    {
        if (_rcon == null || !_rcon.IsConnected)
        {
            AppendRconOutput("Not connected.");
            return;
        }
        try
        {
            var result = await action();
            AppendRconOutput(string.IsNullOrWhiteSpace(result) ? "(no response)" : result);
        }
        catch (Exception ex)
        {
            AppendRconOutput("Error: " + ex.Message);
        }
    }

    private async void OnRconPlayers(object? sender, RoutedEventArgs e) =>
        await RunRconCommandAsync(() => _rcon!.GetPlayersAsync(TimeSpan.FromSeconds(8)));

    private async void OnRconBans(object? sender, RoutedEventArgs e) =>
        await RunRconCommandAsync(() => _rcon!.GetBansAsync(TimeSpan.FromSeconds(8)));

    private async void OnRconBroadcast(object? sender, RoutedEventArgs e)
    {
        var msg = RconBroadcastTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(msg)) return;
        await RunRconCommandAsync(() => _rcon!.BroadcastAsync(msg, TimeSpan.FromSeconds(8)));
    }

    private async void OnRconKick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RconPlayerIdTextBox.Text.Trim(), out var id))
        {
            AppendRconOutput("Invalid player id.");
            return;
        }
        var reason = RconReasonTextBox.Text?.Trim();
        await RunRconCommandAsync(() => _rcon!.KickAsync(id, reason, TimeSpan.FromSeconds(8)));
    }

    private async void OnRconBan(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RconPlayerIdTextBox.Text.Trim(), out var id))
        {
            AppendRconOutput("Invalid player id.");
            return;
        }
        var minutes = int.TryParse(RconBanMinutesTextBox.Text.Trim(), out var m) ? m : 0;
        var reason = RconReasonTextBox.Text?.Trim();
        await RunRconCommandAsync(() => _rcon!.BanAsync(id, minutes, reason, TimeSpan.FromSeconds(8)));
    }

    private void UpdateServerModeVisibility()
    {
        if (ModePs1Radio == null || ModePs1Panel == null || ModeDirectPanel == null
            || ServerUpdateModsButton == null || ServerReauthButton == null) return;

        var isPs1 = ModePs1Radio.IsChecked == true;
        ModePs1Panel.IsVisible = isPs1;
        ModeDirectPanel.IsVisible = !isPs1;
        ServerUpdateModsButton.IsVisible = true;
        ServerReauthButton.IsVisible = !isPs1;
    }

    private void OnServerModeChanged(object? sender, RoutedEventArgs e) => UpdateServerModeVisibility();

    // ---- Server profiles (multiple named launch configs, one active at a time) ----

    private AppConfigStore.ServerProfileEntry? SelectedServerProfile =>
        ServerProfileComboBox.SelectedItem as AppConfigStore.ServerProfileEntry;

    private async void OnServerProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressServerProfileSelection) return;
        var profile = SelectedServerProfile;
        if (profile == null) return;

        if (_server != null && (_server.State == ServerState.Running || _server.State == ServerState.Starting))
        {
            await MsgBox.Info(this, "Stop the running server before switching profiles.", "Server profiles");
            _suppressServerProfileSelection = true;
            ServerProfileComboBox.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : profile;
            _suppressServerProfileSelection = false;
            return;
        }

        HydrateServerUiFromConfig(profile.Server);
        ApplyServerConfigToController(profile.Server);
        UpdateServerModeVisibility();

        var store = AppConfigStore.Load();
        store.ServerProfiles = _serverProfiles.ToList();
        store.ActiveServerProfileId = profile.Id;
        AppConfigStore.Save(store);
    }

    private void OnAddServerProfile(object? sender, RoutedEventArgs e)
    {
        var name = (ServerProfileNameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"Profile {_serverProfiles.Count + 1}";

        var entry = new AppConfigStore.ServerProfileEntry { Name = name, Server = new ServerConfig() };
        _serverProfiles.Add(entry);

        _suppressServerProfileSelection = true;
        ServerProfileComboBox.SelectedItem = entry;
        _suppressServerProfileSelection = false;

        HydrateServerUiFromConfig(entry.Server);
        UpdateServerModeVisibility();
        ServerProfileNameTextBox.Text = string.Empty;

        var store = AppConfigStore.Load();
        store.ServerProfiles = _serverProfiles.ToList();
        store.ActiveServerProfileId = entry.Id;
        AppConfigStore.Save(store);
    }

    private async void OnRenameServerProfile(object? sender, RoutedEventArgs e)
    {
        var profile = SelectedServerProfile;
        if (profile == null) return;
        var name = (ServerProfileNameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await MsgBox.Info(this, "Enter a new name first.", "Rename profile");
            return;
        }

        profile.Name = name;
        _suppressServerProfileSelection = true;
        // Force the ComboBox to re-render the bound Name.
        var idx = _serverProfiles.IndexOf(profile);
        if (idx >= 0) { _serverProfiles.RemoveAt(idx); _serverProfiles.Insert(idx, profile); }
        ServerProfileComboBox.SelectedItem = profile;
        _suppressServerProfileSelection = false;
        ServerProfileNameTextBox.Text = string.Empty;

        var store = AppConfigStore.Load();
        store.ServerProfiles = _serverProfiles.ToList();
        store.ActiveServerProfileId = profile.Id;
        AppConfigStore.Save(store);
    }

    private async void OnDeleteServerProfile(object? sender, RoutedEventArgs e)
    {
        var profile = SelectedServerProfile;
        if (profile == null) return;

        if (_serverProfiles.Count <= 1)
        {
            await MsgBox.Info(this, "At least one server profile must remain.", "Delete profile");
            return;
        }

        if (_server != null && (_server.State == ServerState.Running || _server.State == ServerState.Starting))
        {
            await MsgBox.Info(this, "Stop the running server before deleting profiles.", "Delete profile");
            return;
        }

        if (!await MsgBox.Confirm(this, $"Delete profile \"{profile.Name}\"?", "Delete profile")) return;

        _serverProfiles.Remove(profile);
        var next = _serverProfiles[0];

        _suppressServerProfileSelection = true;
        ServerProfileComboBox.SelectedItem = next;
        _suppressServerProfileSelection = false;

        HydrateServerUiFromConfig(next.Server);
        ApplyServerConfigToController(next.Server);
        UpdateServerModeVisibility();

        var store = AppConfigStore.Load();
        store.ServerProfiles = _serverProfiles.ToList();
        store.ActiveServerProfileId = next.Id;
        AppConfigStore.Save(store);
    }

    private async void OnOpenSetupWizard(object? sender, RoutedEventArgs e)
    {
        var profile = SelectedServerProfile;
        var existing = profile?.Server ?? ReadServerConfigFromUi();

        var dlg = new SetupWizardWindow(existing);
        await dlg.ShowDialog(this);
        if (dlg.Result == null) return;

        // Merge only the fields the wizard governs into the same live profile object, so
        // unrelated settings (RCON password, webhook URL, schedule config, ...) are preserved.
        var cfg = profile?.Server ?? existing;
        cfg.ExePath = dlg.Result.ExePath;
        cfg.ServerRootDir = dlg.Result.ServerRootDir;
        cfg.SteamCmdPath = dlg.Result.SteamCmdPath;
        cfg.ModCacheDir = dlg.Result.ModCacheDir;
        cfg.SteamLogin = dlg.Result.SteamLogin;
        cfg.LoginMode = dlg.Result.LoginMode;

        HydrateServerUiFromConfig(cfg);
        ApplyServerConfigToController(cfg);
        UpdateServerModeVisibility();
        PersistAllDirsToConfig();
    }

    private void UpdateServerButtonsForState(ServerState s)
    {
        ServerStartButton.IsEnabled = s == ServerState.Stopped || s == ServerState.Crashed;
        ServerStopButton.IsEnabled = s == ServerState.Running || s == ServerState.Starting;
        ServerRestartButton.IsEnabled = s == ServerState.Running;
        ServerUpdateModsButton.IsEnabled = s != ServerState.Running && s != ServerState.Starting;
    }

    private void UpdateServerStatusPill(ServerState s)
    {
        ServerStateText.Text = s.ToString().ToUpperInvariant();
        var (dot, text) = s switch
        {
            ServerState.Running  => ("#4ADE80", "#22C55E"),
            ServerState.Starting => ("#FACC15", "#FACC15"),
            ServerState.Stopping => ("#FACC15", "#FACC15"),
            ServerState.Crashed  => ("#EF4444", "#EF4444"),
            _                    => ("#52525B", "#52525B"),
        };
        ServerStatusDot.Fill = new SolidColorBrush(Color.Parse(dot));
        ServerStateText.Foreground = new SolidColorBrush(Color.Parse(text));
        ServerPidText.Text = _server?.Pid is int pid ? $"pid {pid}" : "";
    }

    private void RefreshUptimeText()
    {
        if (_server?.StartedAt is DateTime started && _server.State == ServerState.Running)
        {
            var d = DateTime.UtcNow - started;
            ServerUptimeText.Text = d.TotalHours >= 1
                ? $"up {(int)d.TotalHours}h{d.Minutes:00}m"
                : $"up {d.Minutes:00}m{d.Seconds:00}s";
        }
        else
        {
            ServerUptimeText.Text = string.Empty;
        }
    }

    // ---- File pickers ----

    private async void OnServerBrowsePs1(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select servermanager.ps1",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PowerShell scripts") { Patterns = new[] { "*.ps1" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;
        ServerPs1PathTextBox.Text = files[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
    }

    private async void OnServerBrowseExe(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select DayZServer_x64.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables") { Patterns = new[] { "*.exe", "*" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;
        ServerExePathTextBox.Text = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(ServerRootDirTextBox.Text))
            ServerRootDirTextBox.Text = Path.GetDirectoryName(files[0].Path.LocalPath) ?? string.Empty;
        try { PersistAllDirsToConfig(); } catch { }
    }

    private async void OnServerBrowseProfile(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Select server profile dir" });
        if (folders.Count == 0) return;
        ServerProfileDirTextBox.Text = folders[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
    }

    private async void OnServerBrowseRoot(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Select server root dir" });
        if (folders.Count == 0) return;
        ServerRootDirTextBox.Text = folders[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
    }

    private async void OnServerBrowseSteamCmd(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select steamcmd.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("steamcmd") { Patterns = new[] { "steamcmd.exe", "steamcmd" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;
        SteamCmdPathTextBox.Text = files[0].Path.LocalPath;
        try { ModCacheDirTextBox.Text = Path.GetDirectoryName(Path.GetFullPath(files[0].Path.LocalPath)) ?? string.Empty; } catch { }
        try { PersistAllDirsToConfig(); } catch { }
    }

    private async void OnServerBrowseModCache(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Select mod cache dir" });
        if (folders.Count == 0) return;
        ModCacheDirTextBox.Text = folders[0].Path.LocalPath;
        try { PersistAllDirsToConfig(); } catch { }
    }

    // ---- Log routing ----

    private void OnLogSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_server == null) return;
        _activeLogSource = LogSourceComboBox.SelectedIndex == 1 ? LogSource.SteamCmd : LogSource.Rpt;
        ServerLogTextBox.Text = string.Empty;
        _tailBuffer.Clear();
        if (_activeLogSource == LogSource.Rpt) StartRptTail();
        else _tail?.Stop();
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        ServerLogTextBox.Text = string.Empty;
        _tailBuffer.Clear();
    }

    private void StartRptTail()
    {
        try { _tail?.Dispose(); } catch { }
        _tail = new RptLogTail();
        _tail.LineAppended += line => Dispatcher.UIThread.InvokeAsync(() => AppendLogLine(line));
        _tail.ActiveFileChanged += path => Dispatcher.UIThread.InvokeAsync(() => ActiveLogFileText.Text = path);

        var dir = NullIfEmpty(ServerProfileDirTextBox.Text) ?? AppPaths.DefaultRptDir;
        if (!Directory.Exists(dir))
        {
            ActiveLogFileText.Text = $"(directory not found: {dir})";
            return;
        }
        _tail.Start(dir, new[] { "*.RPT", "*.ADM" });
    }

    private void AppendLogLine(string line)
    {
        _tailBuffer.AddLast(line);
        while (_tailBuffer.Count > _tailLineCap) _tailBuffer.RemoveFirst();

        ServerLogTextBox.Text += line + Environment.NewLine;
        if ((ServerLogTextBox.Text?.Split('\n').Length ?? 0) > _tailLineCap + 200)
            ServerLogTextBox.Text = string.Join(Environment.NewLine, _tailBuffer);

        if (PauseAutoscrollCheckBox.IsChecked != true)
            ServerLogTextBox.CaretIndex = ServerLogTextBox.Text?.Length ?? 0;
    }

    // ---- Start / Stop / Restart ----

    private async void OnServerStart(object? sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var cfg = ReadServerConfigFromUi();
        ApplyServerConfigToController(cfg);
        ServerActionStatusText.Text = "preparing…";
        ServerStartButton.IsEnabled = false;

        SyncBattlEyeRconCfgIfEnabled(cfg);

        try
        {
            var deployed = new List<string>();
            var deployedServer = new List<string>();
            if (cfg.Mode == ServerLaunchMode.DirectExe)
            {
                if (cfg.AutoUpdateModsBeforeStart)
                {
                    var ok = await RunSteamCmdUpdateAsync(cfg);
                    if (!ok)
                    {
                        ServerActionStatusText.Text = "SteamCMD update failed — aborting start.";
                        UpdateServerButtonsForState(_server.State);
                        return;
                    }
                }
                (deployed, deployedServer) = DeployMods(cfg);
            }

            if (cfg.RunPreStartMerge)
            {
                var mergeOk = await RunPreStartMergeAsync(cfg.PreStartPresetId ?? "types");
                if (!mergeOk)
                {
                    ServerActionStatusText.Text = "pre-start merge failed — aborting start.";
                    ServerHistoryLogger.Append("prestart-merge-failed", cfg.Mode.ToString());
                    UpdateServerButtonsForState(_server.State);
                    return;
                }
                ServerHistoryLogger.Append("prestart-merge", cfg.Mode.ToString());
            }

            var spec = new ServerLaunchSpec(
                Mode: cfg.Mode, Ps1Path: cfg.Ps1Path, ExePath: cfg.ExePath,
                ProfileDir: cfg.ProfileDir, ServerRootDir: cfg.ServerRootDir,
                Port: cfg.Port, ExtraArgs: cfg.ExtraArgs, BattlEye: cfg.BattlEye,
                DeployedAtNames: deployed, Ps1LaunchParam: cfg.Ps1LaunchParam,
                Ps1AppBranch: cfg.Ps1AppBranch, DeployedServerModNames: deployedServer);

            ServerActionStatusText.Text = "starting…";
            await _server.StartAsync(spec);
            ServerActionStatusText.Text = _server.State == ServerState.Running ? "running" : "start failed";
            LogSourceComboBox.SelectedIndex = 0;
            StartRptTail();
        }
        catch (Exception ex)
        {
            ServerActionStatusText.Text = ex.Message;
            ServerHistoryLogger.Append("start-failed", cfg.Mode.ToString(), detail: ex.Message);
        }
        finally { UpdateServerButtonsForState(_server.State); }
    }

    private async void OnServerStop(object? sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        ServerActionStatusText.Text = "stopping…";
        ServerStopButton.IsEnabled = false;
        try
        {
            await _server.StopAsync(TimeSpan.FromSeconds(5));
            ServerActionStatusText.Text = "stopped";
        }
        catch (Exception ex) { ServerActionStatusText.Text = ex.Message; }
        finally { UpdateServerButtonsForState(_server.State); }
    }

    private async void OnServerRestart(object? sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var cfg = ReadServerConfigFromUi();
        ApplyServerConfigToController(cfg);
        SyncBattlEyeRconCfgIfEnabled(cfg);
        try
        {
            ServerActionStatusText.Text = "restarting…";
            var (deployed, deployedServer) = cfg.Mode == ServerLaunchMode.DirectExe
                ? DeployMods(cfg) : (new List<string>(), new List<string>());
            var spec = new ServerLaunchSpec(cfg.Mode, cfg.Ps1Path, cfg.ExePath, cfg.ProfileDir,
                cfg.ServerRootDir, cfg.Port, cfg.ExtraArgs, cfg.BattlEye, deployed,
                cfg.Ps1LaunchParam, cfg.Ps1AppBranch, deployedServer);
            await _server.RestartAsync(spec);
            ServerActionStatusText.Text = _server.State == ServerState.Running ? "running" : "restart failed";
            if (cfg.NotifyOnRestart)
                _ = WebhookNotifier.NotifyAsync(cfg.WebhookUrl, $"🔄 DayZ server restarted ({ServerActionStatusText.Text}).");
        }
        catch (Exception ex) { ServerActionStatusText.Text = ex.Message; }
        finally { UpdateServerButtonsForState(_server.State); }
    }

    // ---- SteamCMD update ----

    private async void OnUpdateModsClicked(object? sender, RoutedEventArgs e)
    {
        var cfg = ReadServerConfigFromUi();
        ServerActionStatusText.Text = "updating…";
        ServerUpdateModsButton.IsEnabled = false;
        try
        {
            bool ok;
            if (cfg.Mode == ServerLaunchMode.Ps1)
            {
                ok = await RunPs1UpdateAsync(cfg);
                ServerActionStatusText.Text = ok ? "ps1 update done" : "ps1 update failed";
            }
            else
            {
                ok = await RunSteamCmdUpdateAsync(cfg);
                if (ok)
                {
                    DeployMods(cfg);
                    ServerActionStatusText.Text = "mods updated";
                    if (cfg.NotifyOnModUpdate)
                        _ = WebhookNotifier.NotifyAsync(cfg.WebhookUrl, "📦 DayZ server mods updated.");
                }
                else ServerActionStatusText.Text = "update failed";
            }
        }
        catch (Exception ex) { ServerActionStatusText.Text = ex.Message; }
        finally { UpdateServerButtonsForState(_server?.State ?? ServerState.Stopped); }
    }

    private async Task<bool> RunPs1UpdateAsync(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Ps1Path) || !File.Exists(cfg.Ps1Path))
        {
            ServerHistoryLogger.Append("ps1-update-failed", cfg.Mode.ToString(), detail: "servermanager.ps1 path not set or missing");
            return false;
        }

        var target = string.IsNullOrWhiteSpace(cfg.Ps1UpdateTarget) ? "all" : cfg.Ps1UpdateTarget;
        var app    = string.IsNullOrWhiteSpace(cfg.Ps1AppBranch)    ? "stable" : cfg.Ps1AppBranch;

        LogSourceComboBox.SelectedIndex = 1;
        _activeLogSource = LogSource.SteamCmd;
        _tail?.Stop();
        ActiveLogFileText.Text = "servermanager.ps1";
        ServerLogTextBox.Text = string.Empty;
        _tailBuffer.Clear();

        ServerHistoryLogger.Append("ps1-update-start", cfg.Mode.ToString(), detail: $"target={target} app={app}");

        var pwsh = ResolvePowerShellExe();
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{cfg.Ps1Path}\" -u {target} -app {app}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(cfg.Ps1Path) ?? string.Empty,
        };

        int exitCode;
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                AppendLogLine("[manager] failed to start powershell.");
                ServerHistoryLogger.Append("ps1-update-failed", cfg.Mode.ToString(), detail: "Process.Start returned null");
                return false;
            }
            proc.OutputDataReceived += (_, ev) => { if (ev.Data != null) Dispatcher.UIThread.InvokeAsync(() => AppendLogLine(ev.Data)); };
            proc.ErrorDataReceived  += (_, ev) => { if (ev.Data != null) Dispatcher.UIThread.InvokeAsync(() => AppendLogLine("[err] " + ev.Data)); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] error: {ex.Message}");
            ServerHistoryLogger.Append("ps1-update-failed", cfg.Mode.ToString(), detail: ex.Message);
            return false;
        }

        var ok = exitCode == 0;
        ServerHistoryLogger.Append(ok ? "ps1-update-done" : "ps1-update-failed", cfg.Mode.ToString(), exitCode: exitCode);
        return ok;
    }

    private static string ResolvePowerShellExe()
    {
        var whereCmd = OperatingSystem.IsWindows() ? "where" : "which";
        foreach (var candidate in new[] { "pwsh", "pwsh.exe", "powershell.exe" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(whereCmd, candidate)
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                });
                if (p != null)
                {
                    var line = p.StandardOutput.ReadLine();
                    p.WaitForExit();
                    if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim())) return line.Trim();
                }
            }
            catch { }
        }
        return "powershell.exe";
    }

    private void OnReauthClicked(object? sender, RoutedEventArgs e)
    {
        _forceReauth = true;
        ServerActionStatusText.Text = "next update will open SteamCMD console for re-auth";
    }

    private async Task<bool> RunSteamCmdUpdateAsync(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.SteamCmdPath) || !File.Exists(cfg.SteamCmdPath))
        {
            ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: "steamcmd.exe path not set or missing");
            return false;
        }

        string cacheDir;
        try { cacheDir = SteamCmdInstallRoot(cfg); }
        catch (Exception ex) { AppendLogLine($"[manager] {ex.Message}"); ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: ex.Message); return false; }
        Directory.CreateDirectory(cacheDir);

        var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
        if (ids.Count == 0) { ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: "mods.txt empty"); return false; }

        var client = new SteamCmdClient(cfg.SteamCmdPath);
        ServerHistoryLogger.Append("steamcmd-update-start", cfg.Mode.ToString(), detail: $"ids={ids.Count} validate={cfg.ValidateMods}");

        LogSourceComboBox.SelectedIndex = 1;
        _activeLogSource = LogSource.SteamCmd;
        _tail?.Stop();
        ActiveLogFileText.Text = "SteamCMD";
        ServerLogTextBox.Text = string.Empty;
        _tailBuffer.Clear();

        var login = cfg.LoginMode == SteamLoginMode.Anonymous ? "anonymous" : (cfg.SteamLogin ?? "anonymous");
        var useInteractive = cfg.LoginMode == SteamLoginMode.Interactive && (_forceReauth || !client.HasCachedCredential(login));

        int exitCode;
        try
        {
            if (useInteractive)
            {
                AppendLogLine("[manager] SteamCMD opened in its own console window — enter password there.");
                AppendLogLine("[manager] (this window will stay quiet until SteamCMD exits)");
                exitCode = await client.DownloadModsInteractiveAsync(ids, cfg.WorkshopAppId, cacheDir, login, cfg.ValidateMods);
                _forceReauth = false;
            }
            else
            {
                exitCode = 0;
                await foreach (var line in client.DownloadModsStreamedAsync(ids, cfg.WorkshopAppId, cacheDir, login, cfg.ValidateMods))
                {
                    if (line.StartsWith("__EXIT__:"))
                    {
                        if (int.TryParse(line.AsSpan("__EXIT__:".Length), out var ec)) exitCode = ec;
                        break;
                    }
                    AppendLogLine(line);
                    if (line.IndexOf("Login Failure", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("Invalid Password", StringComparison.OrdinalIgnoreCase) >= 0)
                        _forceReauth = true;
                }
            }
        }
        catch (Exception ex) { AppendLogLine($"[manager] error: {ex.Message}"); ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: ex.Message); return false; }

        var ok = exitCode == 0;
        ServerHistoryLogger.Append(ok ? "steamcmd-update-done" : "steamcmd-update-failed", cfg.Mode.ToString(), exitCode: exitCode);
        return ok;
    }

    private (List<string> ClientMods, List<string> ServerMods) DeployMods(ServerConfig cfg)
    {
        var empty = (new List<string>(), new List<string>());
        if (string.IsNullOrWhiteSpace(cfg.ServerRootDir)) { AppendLogLine("[manager] server root dir not set — skipping deploy."); return empty; }
        if (string.IsNullOrWhiteSpace(cfg.SteamCmdPath)) { AppendLogLine("[manager] steamcmd.exe path not set — skipping deploy."); return empty; }

        LogModConflictsIfAny();

        string workshopRoot;
        try { workshopRoot = SteamCmdInstallRoot(cfg); }
        catch (Exception ex) { AppendLogLine($"[manager] {ex.Message}"); return empty; }

        var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
        var names = new List<string>();
        try
        {
            var result = SteamCmdClient.DeployMods(workshopRoot, cfg.WorkshopAppId, ids, cfg.ServerRootDir!, cfg.DeployMode);
            names = result.Select(r => r.AtName).ToList();
            ModDeployStateStore.RecordDeployed(result.Select(r => r.Id), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ServerHistoryLogger.Append("mods-deployed", cfg.Mode.ToString(), detail: $"count={names.Count} mode={cfg.DeployMode}");
            AppendLogLine($"[manager] deployed {names.Count} mods to {cfg.ServerRootDir}");
        }
        catch (Exception ex) { AppendLogLine($"[manager] deploy failed: {ex.Message}"); ServerHistoryLogger.Append("mods-deploy-failed", cfg.Mode.ToString(), detail: ex.Message); return empty; }

        var serverMods = new List<string>();
        if (cfg.AutoDeployManagerMod)
        {
            var atName = DeployManagerMod(cfg);
            if (!string.IsNullOrEmpty(atName)) serverMods.Add(atName!);
        }
        return (names, serverMods);
    }

    private string? DeployManagerMod(ServerConfig cfg)
    {
        var source = ResolveManagerModSource(cfg);
        if (source == null) { AppendLogLine("[manager] manager mod source not found — skipping companion deploy."); return null; }

        var atName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(atName)) return null;
        if (!atName.StartsWith("@", StringComparison.Ordinal)) atName = "@" + atName;

        var target = Path.Combine(cfg.ServerRootDir!, atName);
        try
        {
            if (Directory.Exists(target))
            {
                var info = new DirectoryInfo(target);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(target, recursive: false);
                else
                    Directory.Delete(target, recursive: true);
            }

            switch (cfg.DeployMode)
            {
                case ModDeployMode.Junction:
                    var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{target}\" \"{source}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var proc = Process.Start(psi))
                    {
                        if (proc == null) throw new IOException("mklink launch failed");
                        proc.WaitForExit();
                        if (proc.ExitCode != 0) throw new IOException($"mklink /J failed: {proc.StandardError.ReadToEnd()}");
                    }
                    break;
                case ModDeployMode.Symlink:
                    Directory.CreateSymbolicLink(target, source);
                    break;
                default:
                    CopyDirectoryRecursive(source, target);
                    break;
            }
            AppendLogLine($"[manager] staged companion mod {atName} ({cfg.DeployMode}) <- {source}");
            ServerHistoryLogger.Append("manager-mod-deployed", cfg.Mode.ToString(), detail: $"name={atName} mode={cfg.DeployMode}");
            return atName;
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] companion mod deploy failed: {ex.Message}");
            ServerHistoryLogger.Append("manager-mod-deploy-failed", cfg.Mode.ToString(), detail: ex.Message);
            return null;
        }
    }

    private static string SteamCmdInstallRoot(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.SteamCmdPath)) throw new InvalidOperationException("SteamCMD path is not set.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.SteamCmdPath));
        if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("Cannot resolve SteamCMD directory from path.");
        return dir;
    }

    private static string? ResolveManagerModSource(ServerConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ManagerModSourceDir) && Directory.Exists(cfg.ManagerModSourceDir))
            return Path.GetFullPath(cfg.ManagerModSourceDir!);

        var candidates = new[]
        {
            Path.Combine(AppPaths.ExeDir, "@DayZAIBalancer"),
            Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "artifacts", "pbo", "@DayZAIBalancer")),
            Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "..", "artifacts", "pbo", "@DayZAIBalancer")),
        };
        foreach (var c in candidates) if (Directory.Exists(c)) return c;
        return null;
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }

    private async Task<bool> RunPreStartMergeAsync(string presetId)
    {
        var preset = XmlMergePresets.FindById(presetId) ?? XmlMergePresets.FindById("types");
        if (preset == null) return false;

        var root = ModsRootTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            AppendLogLine($"[manager] pre-start merge: invalid mods root '{root}'");
            return false;
        }

        var outFile = AppPaths.ResolveOutputPath(
            string.IsNullOrWhiteSpace(CombineOutFileTextBox.Text) ? preset.OutputFileName : CombineOutFileTextBox.Text.Trim());
        var mergeMode = GetSelectedMergeMode();

        try
        {
            var excluded = ReadExcludedXmlGenDirs();
            if (excluded is { Count: > 0 }) AppendLogLine($"[manager] pre-start merge: excluding {excluded.Count} dir(s)");
            AppendLogLine($"[manager] pre-start merge: {preset.DisplayName} -> {outFile}");
            await Task.Run(() => XmlMergeService.Generate(root, preset, mergeMode, outFile, excluded));
            return true;
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] pre-start merge failed: {ex.Message}");
            return false;
        }
    }
}
