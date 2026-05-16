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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using DayZModManager.Services;

namespace DayZModManager;

public partial class MainWindow : Window
{
    private const string BakedSteamWebApiKey = "A871AA9AB7D48FFD5C3A6E145EDCCC0B";

    public string ModsTxtLabel => $"mods.txt: {AppPaths.ModsTxtPath}";

    private readonly ObservableCollection<string> _localItems = new();
    private readonly ObservableCollection<WorkshopSearchResultItem> _searchResults = new();
    private readonly ObservableCollection<string> _modsListItems = new();
    private readonly ObservableCollection<string> _modFolders = new();

    private readonly Dictionary<ulong, string?> _titleCache = new();
    private readonly Dictionary<ulong, List<ulong>> _depsCache = new();

    // ---- Server tab ----
    private ServerProcessController? _server;
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
        DataContext = this;

        LocalIdsListBox.ItemsSource = _localItems;
        SearchResultsListBox.ItemsSource = _searchResults;
        ModsListBox.ItemsSource = _modsListItems;
        ModFoldersListBox.ItemsSource = _modFolders;

        LocalIdsListBox.SelectionChanged += OnLocalIdSelected;

        Loaded += (_, _) =>
        {
            var envKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
            var keyToUse = string.IsNullOrWhiteSpace(envKey) ? BakedSteamWebApiKey : envKey!;

            LocalApiKeyTextBox.Text = keyToUse;

            SearchApiKeyTextBox.Text = keyToUse;
            ModsApiKeyTextBox.Text = keyToUse;

            // Default mods root: parent of exe directory (parent1/parent2/exe layout).
            ModsRootTextBox.Text = AppPaths.DefaultModsRoot;
            LocalFilePathTextBox.Text = AppPaths.ModsTxtPath;

            // Populate preset dropdown.
            PresetComboBox.ItemsSource = XmlMergePresets.All;
            PresetComboBox.SelectedIndex = 0; // "types" by default

            // Load persisted config (paths + merge mode + preset).
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
            // First-run seed: if no saved exclusion list, populate textbox with sensible
            // defaults (stock DayZ server subfolders that aren't mod XML sources).
            if (cfg.ExcludedXmlGenDirs is { Count: > 0 } ex)
                XmlGenExcludeDirsTextBox.Text = string.Join(Environment.NewLine, ex);
            else if (cfg.ExcludedXmlGenDirs == null)
                XmlGenExcludeDirsTextBox.Text = string.Join(Environment.NewLine, XmlMergePresets.DefaultExcludedDirs);

            CombineAllCheckBox.Checked += (_, _) => UpdateCombineEnabled();
            CombineAllCheckBox.Unchecked += (_, _) => UpdateCombineEnabled();
            UpdateCombineEnabled();

            // ---- Server tab init ----
            PreStartPresetComboBox.ItemsSource = XmlMergePresets.All;
            PreStartPresetComboBox.SelectedIndex = 0;

            HydrateServerUiFromConfig(cfg.Server);
            InitServerController();
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

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Persist all configured dirs/paths even if the user never hit "SAVE SERVER SETTINGS".
        try { PersistAllDirsToConfig(); } catch { }

        // Detach (don't kill) the server: closing the manager window is not a stop request.
        try { _tail?.Dispose(); } catch { }
        try { _server?.Detach(); } catch { }
    }

    /// <summary>
    /// Returns the mods.txt path currently configured in the UI (LOCAL_MODS tab).
    /// Falls back to the default path next to the exe when the textbox is empty
    /// or hasn't been created yet (early-init / window-closing edge cases).
    /// </summary>
    private string CurrentModsTxtPath()
    {
        var fromUi = LocalFilePathTextBox?.Text?.Trim();
        return string.IsNullOrWhiteSpace(fromUi) ? AppPaths.ModsTxtPath : fromUi;
    }

    private void PersistAllDirsToConfig()
    {
        var cfg = new AppConfigStore.Config
        {
            ModsRootPath = ModsRootTextBox?.Text?.Trim(),
            LocalModsTxtPath = LocalFilePathTextBox?.Text?.Trim(),
            CombineOutFileText = CombineOutFileTextBox?.Text?.Trim(),
            MergeModeSelectedIndex = MergeModeComboBox?.SelectedIndex,
            SelectedPresetId = (PresetComboBox?.SelectedItem as XmlMergePreset)?.Id,
            ExcludedXmlGenDirs = ReadExcludedXmlGenDirs(),
            Server = ReadServerConfigFromUi()
        };
        AppConfigStore.Save(cfg);
    }

    /// <summary>
    /// Parse the EXCLUDE DIRS textbox: one folder name per line, blank lines and
    /// lines starting with '#' are ignored. Returns null when empty so the JSON stays clean.
    /// </summary>
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

    // ---- Topbar / UI sync ----
    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
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
                TopCrumb.Text = "SETTINGS";
                TopPathText.Text = "// app configuration";
                break;
        }
    }

    private void UpdateTopStats(int total, int installed)
    {
        if (TopMods != null)      TopMods.Text      = total.ToString();
        if (TopInstalled != null) TopInstalled.Text = installed.ToString();
        if (LocalCountPill != null) LocalCountPill.Text = total.ToString();
    }

    private static string ExtractIdToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return token;
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

    // ---- Local mods tab ----
    private void OnBrowseLocalFile(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (ofd.ShowDialog() == true)
        {
            LocalFilePathTextBox.Text = ofd.FileName;
            try { PersistAllDirsToConfig(); } catch { }
            _ = RefreshLocalAsync();
        }
    }

    private async void OnRefreshLocal(object sender, RoutedEventArgs e) => await RefreshLocalAsync();

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
                    _localItems.Add(installed ? $"{id} (installed)" : id.ToString());
                }
                LocalFooterTextBlock.Text = invalidCount > 0
                    ? $"Loaded {ids.Length} IDs (invalid lines: {invalidCount})."
                    : $"Loaded {ids.Length} IDs.";
                UpdateTopStats(ids.Length, installedCount);
                return;
            }

            var apiKey = LocalApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var throttler = new System.Threading.SemaphoreSlim(6);
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
                catch
                {
                    return (id, (string?)null);
                }
                finally { throttler.Release(); }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            var installedTotal = 0;
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
            {
                var installed = modsRootExists && Directory.Exists(Path.Combine(modsRoot, id.ToString()));
                if (installed) installedTotal++;
                if (!string.IsNullOrWhiteSpace(title))
                    _localItems.Add(installed ? $"{id} - {title} (installed)" : $"{id} - {title}");
                else
                    _localItems.Add(installed ? $"{id} (installed)" : id.ToString());
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

    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
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

            var confirm = System.Windows.MessageBox.Show(
                $"Remove {id} from mods.txt?",
                "Preview mods.txt change",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning
            ) == MessageBoxResult.OK;

            if (!confirm) return;

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

    // ---- Add tab ----
    private async void OnSearchWorkshop(object sender, RoutedEventArgs e)
    {
        await SearchWorkshopAsync();
    }

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
                _ => list // relevance (as returned)
            };

            var final = list.ToArray();
            foreach (var r in final)
                _searchResults.Add(r);
            AddStatusTextBlock.Text = $"Found {final.Length} results.";
        }
        catch (Exception ex)
        {
            _searchResults.Clear();
            _searchResults.Add(new WorkshopSearchResultItem { Title = "Error", Description = ex.Message, PublishedFileId = 0 });
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private async void OnAddSelected(object sender, RoutedEventArgs e)
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
            var ok = System.Windows.MessageBox.Show(
                preview,
                "Preview mods.txt changes",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information
            ) == MessageBoxResult.OK;

            if (!ok) return;

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
        // Only show IDs (fast); names are looked up separately when refreshing UI.
        var arr = toAdd.ToArray();
        var head = arr.Take(30).Select(x => x.ToString());
        var more = arr.Length > 30 ? $" +{arr.Length - 30} more" : string.Empty;
        var srcLabel = apiKey?.Length > 0 ? "Steam API key set" : "baked-in key";
        return
            $"About to add {arr.Length} mods to mods.txt.\n\n" +
            string.Join(Environment.NewLine, head) + more +
            $"\n\nSource: {srcLabel}";
    }

    // ---- Bulk add ----
    private async void OnAddBulk(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = BulkIdsTextBox.Text ?? string.Empty;
            var rawLines = text
                .Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
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
                try
                {
                    root.Add(ModStorage.ParseWorkshopId(line));
                }
                catch
                {
                    invalid.Add(line);
                }
            }

            if (invalid.Count > 0)
            {
                AddStatusTextBlock.Text = $"Invalid lines: {invalid.Count} (showing first 5).";
                System.Windows.MessageBox.Show(
                    "Invalid ID lines:\n" + string.Join(Environment.NewLine, invalid.Take(5)),
                    "Bulk add - invalid IDs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            if (root.Count == 0)
            {
                AddStatusTextBlock.Text = "No valid IDs.";
                return;
            }

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

            if (toAdd.Length == 0)
            {
                AddStatusTextBlock.Text = "Nothing to add (already in mods.txt).";
                return;
            }

            var preview = BuildModsTxtAddPreview(toAdd, apiKey);
            var ok = System.Windows.MessageBox.Show(
                preview,
                "Preview mods.txt changes",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information
            ) == MessageBoxResult.OK;

            if (!ok) return;

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

    private async Task<HashSet<ulong>> ResolveDependenciesClosureAsync(HashSet<ulong> rootIds, string apiKey)
    {
        var visited = new HashSet<ulong>(rootIds);
        var queue = new Queue<ulong>(rootIds);

        const int maxNodes = 500;
        const int batchSize = 8;

        while (queue.Count > 0)
        {
            if (visited.Count > maxNodes) break;

            var batch = new List<ulong>(batchSize);
            while (queue.Count > 0 && batch.Count < batchSize)
            {
                var id = queue.Dequeue();
                batch.Add(id);
            }

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

    // ---- Dependency tree ----
    private async void OnShowDependencyTree(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = SearchResultsListBox.SelectedItem as WorkshopSearchResultItem;
            if (item == null || item.PublishedFileId == 0) return;

            var apiKey = SearchApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            AddStatusTextBlock.Text = "Building dependency tree...";
            var tree = await BuildDependencyTreeTextAsync(item.PublishedFileId, apiKey, maxDepth: 8, maxNodes: 250);
            System.Windows.MessageBox.Show(tree, $"Dependency tree: {item.PublishedFileId}", MessageBoxButton.OK, MessageBoxImage.Information);
            AddStatusTextBlock.Text = "Tree shown.";
        }
        catch (Exception ex)
        {
            AddStatusTextBlock.Text = ex.Message;
        }
    }

    private async Task<string> BuildDependencyTreeTextAsync(ulong root, string apiKey, int maxDepth, int maxNodes)
    {
        var visited = new HashSet<ulong>();
        var lines = new List<string>();
        var nodeCount = 0;

        async Task<List<ulong>> GetChildrenCachedAsync(ulong id)
        {
            if (_depsCache.TryGetValue(id, out var cached))
                return cached;
            var children = await SteamWorkshopClient.GetChildrenPublishedFileIdsAsync(id, apiKey);
            _depsCache[id] = children;
            return children;
        }

        async Task WalkAsync(ulong id, int depth, string prefix)
        {
            if (nodeCount >= maxNodes) return;
            if (!visited.Add(id)) return;
            nodeCount++;

            var label = id.ToString();
            if (depth == 0)
                lines.Add($"{label}");
            else
                lines.Add($"{prefix}{label}");

            if (depth >= maxDepth) return;

            List<ulong> children;
            try
            {
                children = await GetChildrenCachedAsync(id);
            }
            catch
            {
                return;
            }

            var list = children ?? new List<ulong>();
            for (var i = 0; i < list.Count; i++)
            {
                var child = list[i];
                var isLast = i == list.Count - 1;
                var nextPrefix = depth == 0 ? (isLast ? "└─ " : "├─ ") : (isLast ? "└─ " : "├─ ");
                await WalkAsync(child, depth + 1, nextPrefix);
            }
        }

        await WalkAsync(root, 0, "");
        return lines.Count == 0 ? "(no dependencies / none returned)" : string.Join(Environment.NewLine, lines);
    }

    // ---- mods.txt list ----
    private async void OnRefreshModsList(object sender, RoutedEventArgs e) => await RefreshModsListAsync();

    private async Task RefreshModsListAsync()
    {
        try
        {
            ModsStatusTextBlock.Text = "Loading...";
            _modsListItems.Clear();

            var ids = ModStorage.LoadIds(CurrentModsTxtPath()).OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                _modsListItems.Add("(mods.txt empty)");
                return;
            }

            var doLookup = ModsLookupCheckBox.IsChecked == true;
            if (!doLookup)
            {
                foreach (var id in ids) _modsListItems.Add(id.ToString());
                ModsStatusTextBlock.Text = $"Loaded {ids.Length} IDs.";
                return;
            }

            var apiKey = ModsApiKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = BakedSteamWebApiKey;

            var throttler = new System.Threading.SemaphoreSlim(6);
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
                catch
                {
                    return (id, (string?)null);
                }
                finally { throttler.Release(); }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
                _modsListItems.Add(!string.IsNullOrWhiteSpace(title) ? $"{id} - {title}" : id.ToString());

            ModsStatusTextBlock.Text = $"Loaded {ids.Length} mods.";
        }
        catch (Exception ex)
        {
            ModsStatusTextBlock.Text = ex.Message;
        }
    }

    // ---- Mods Folders tab ----
    private void OnBrowseModsRoot(object sender, RoutedEventArgs e)
    {
        // WPF has no built-in FolderBrowserDialog; we use OpenFileDialog as a pragmatic fallback:
        // select any file inside the mods root, then we take its directory as the root.
        var ofd = new OpenFileDialog
        {
            Filter = "Any file (*.*)|*.*",
            Title = "Select any file inside the mods root directory"
        };
        if (ofd.ShowDialog() == true)
        {
            ModsRootTextBox.Text = System.IO.Path.GetDirectoryName(ofd.FileName) ?? ofd.FileName;
            try { PersistAllDirsToConfig(); } catch { }
            _ = RefreshModFoldersAsync();
        }
    }

    private async void OnRefreshModFolders(object sender, RoutedEventArgs e) => await RefreshModFoldersAsync();

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

    private async void OnModFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModFoldersListBox.SelectedItem == null) return;
        await RefreshFolderDetailsAsync(ModFoldersListBox.SelectedItem.ToString()!);
    }

    private Task RefreshFolderDetailsAsync(string selected)
    {
        var root = ModsRootTextBox.Text.Trim();
        var modDir = System.IO.Path.Combine(root, selected);

        var sb = new StringBuilder();
        sb.AppendLine($"Mod folder: {selected}");
        sb.AppendLine($"Path: {modDir}");

        if (!Directory.Exists(modDir))
        {
            sb.AppendLine("Directory missing.");
            FolderDetailsTextBox.Text = sb.ToString();
            return Task.CompletedTask;
        }

        const int maxTypesFilesToShow = 200;
        const int maxOtherXmlToShow = 80;

        var typesFiles = Directory.EnumerateFiles(modDir, "*types*.xml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(maxTypesFilesToShow)
            .ToArray();

        sb.AppendLine();
        sb.AppendLine($"types.xml-related files (showing up to {maxTypesFilesToShow}):");
        if (typesFiles.Length == 0) sb.AppendLine("(none found under this folder)");
        else
            foreach (var f in typesFiles)
            {
                var fi = new FileInfo(f);
                sb.AppendLine($"- {fi.Name} ({fi.Length / 1024.0:0.0} KB)");
            }

        var otherXmlFiles = Directory.EnumerateFiles(modDir, "*.xml", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(maxOtherXmlToShow)
            .ToArray()!;

        sb.AppendLine();
        sb.AppendLine($"Other XML files (sample, up to {maxOtherXmlToShow}):");
        if (otherXmlFiles.Length == 0) sb.AppendLine("(none found under this folder)");
        else
            foreach (var n in otherXmlFiles)
                sb.AppendLine($"- {n}");

        FolderDetailsTextBox.Text = sb.ToString();
        return Task.CompletedTask;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not XmlMergePreset preset) return;

        IncludePatternsTextBox.Text = string.Join(", ", preset.IncludePatterns);
        ExcludePatternsTextBox.Text = string.Join(", ", preset.ExcludePatterns);
        RootElementTextBox.Text = preset.RootElementName;
        KeyAttributeTextBox.Text = preset.KeyAttribute ?? string.Empty;
        CombineOutFileTextBox.Text = preset.OutputFileName;
        PresetDescriptionText.Text = preset.Description;
    }

    /// <summary>Build a preset from the currently-edited UI fields, falling back to defaults.</summary>
    private XmlMergePreset BuildPresetFromUi()
    {
        var basePreset = PresetComboBox.SelectedItem as XmlMergePreset
                         ?? XmlMergePresets.FindById("types")!;

        static string[] SplitList(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0)
                     .ToArray();

        var includes = SplitList(IncludePatternsTextBox.Text);
        var excludes = SplitList(ExcludePatternsTextBox.Text);
        var rootName = string.IsNullOrWhiteSpace(RootElementTextBox.Text)
            ? basePreset.RootElementName
            : RootElementTextBox.Text.Trim();
        var keyAttr  = string.IsNullOrWhiteSpace(KeyAttributeTextBox.Text)
            ? null
            : KeyAttributeTextBox.Text.Trim();

        return basePreset with
        {
            IncludePatterns = includes.Length > 0 ? includes : basePreset.IncludePatterns,
            ExcludePatterns = excludes,
            RootElementName = rootName,
            KeyAttribute    = keyAttr,
        };
    }

    private async void OnGenerateCombinedTypes(object sender, RoutedEventArgs e)
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
            if (string.IsNullOrWhiteSpace(outVal))
            {
                FolderDetailsTextBox.Text = "Please set output file path.";
                return;
            }

            var outFile   = AppPaths.ResolveOutputPath(outVal);
            var mergeMode = GetSelectedMergeMode();
            var preset    = BuildPresetFromUi();

            FolderDetailsTextBox.Text = $"Combining {preset.DisplayName}...";
            var excluded = ReadExcludedXmlGenDirs();
            var stats = await Task.Run(() =>
                XmlMergeService.Generate(root, preset, mergeMode, outFile, excluded));

            var sb = new StringBuilder();
            sb.AppendLine($"Generated: {outFile}");
            sb.AppendLine();
            sb.AppendLine(FormatStats(stats));
            FolderDetailsTextBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            FolderDetailsTextBox.Text = $"Generation failed: {ex.Message}";
        }
    }

    private void OnPreviewCombinedTypes(object sender, RoutedEventArgs e) => _ = PreviewCombinedTypesAsync();

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
        catch (Exception ex)
        {
            FolderDetailsTextBox.Text = $"Preview failed: {ex.Message}";
        }
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
            foreach (var f in stats.SkippedInvalidFiles.Take(10))
                sb.AppendLine($"  - {f}");
        }

        if (stats.ConflictCount > 0 && stats.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("First conflicts:");
            foreach (var c in stats.Conflicts.Take(25))
                sb.AppendLine($"  - {c.Key}");
        }

        return sb.ToString();
    }

    private TypesXmlMergeMode GetSelectedMergeMode()
    {
        // Keep mapping in sync with XAML SelectedIndex in MainWindow.xaml.
        return MergeModeComboBox.SelectedIndex switch
        {
            0 => TypesXmlMergeMode.DedupeFirstByKey,
            1 => TypesXmlMergeMode.Append,
            2 => TypesXmlMergeMode.DedupeLastByKey,
            _ => TypesXmlMergeMode.DedupeFirstByKey
        };
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var cfg = new AppConfigStore.Config
        {
            ModsRootPath = ModsRootTextBox.Text.Trim(),
            LocalModsTxtPath = LocalFilePathTextBox.Text.Trim(),
            CombineOutFileText = CombineOutFileTextBox.Text.Trim(),
            MergeModeSelectedIndex = MergeModeComboBox.SelectedIndex,
            SelectedPresetId = (PresetComboBox.SelectedItem as XmlMergePreset)?.Id,
            ExcludedXmlGenDirs = ReadExcludedXmlGenDirs(),
            Server = ReadServerConfigFromUi()
        };

        AppConfigStore.Save(cfg);
        ApplyServerConfigToController(cfg.Server);
        System.Windows.MessageBox.Show("Settings saved to config.json.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnExportProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = (ProfileNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "default";

            var profilesDir = Path.Combine(AppContext.BaseDirectory, "profiles");
            Directory.CreateDirectory(profilesDir);

            var defaultPath = Path.Combine(profilesDir, $"{name}.json");

            var sfd = new SaveFileDialog
            {
                Filter = "Profile json (*.json)|*.json",
                FileName = $"{name}.json",
                InitialDirectory = profilesDir
            };

            if (sfd.ShowDialog() != true)
                return;

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
            File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
            System.Windows.MessageBox.Show("Profile exported.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    private async void OnImportProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Profile json (*.json)|*.json"
            };

            if (ofd.ShowDialog() != true)
                return;

            var json = File.ReadAllText(ofd.FileName);
            var profile = JsonSerializer.Deserialize<AppProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (profile == null)
                throw new InvalidOperationException("Invalid profile json.");

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
            System.Windows.MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefreshHistory(object sender, RoutedEventArgs e) => _ = RefreshHistoryAsync();

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton != null)
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

        // When maximized with WindowStyle=None, WPF over-extends past the work area.
        // Pad the inner border so content isn't clipped by taskbar / screen edges.
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

    private Task RefreshHistoryAsync()
    {
        try
        {
            var entries = HistoryLogger.LoadRecent(30);
            HistoryListBox.Items.Clear();

            foreach (var e in entries)
            {
                var added = e.Added.Count > 0 ? string.Join(",", e.Added.Take(5)) + (e.Added.Count > 5 ? "…" : "") : "[]";
                var removed = e.Removed.Count > 0 ? string.Join(",", e.Removed.Take(5)) + (e.Removed.Count > 5 ? "…" : "") : "[]";
                HistoryListBox.Items.Add($"[{e.Timestamp:u}] {e.Action} (add:{added} rem:{removed}) - {e.Source}");
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            HistoryListBox.Items.Clear();
            HistoryListBox.Items.Add(ex.Message);
            return Task.CompletedTask;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVER TAB — controller wiring, log tail, SteamCMD update flow
    // ═══════════════════════════════════════════════════════════════════

    private void HydrateServerUiFromConfig(ServerConfig? cfg)
    {
        cfg ??= new ServerConfig();

        if (cfg.Mode == ServerLaunchMode.Ps1) ModePs1Radio.IsChecked = true;
        else ModeDirectRadio.IsChecked = true;

        ServerPs1PathTextBox.Text = cfg.Ps1Path ?? string.Empty;
        Ps1LaunchParamComboBox.SelectedIndex = string.Equals(cfg.Ps1LaunchParam, "user", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Ps1AppBranchComboBox.SelectedIndex = string.Equals(cfg.Ps1AppBranch, "exp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Ps1UpdateTargetComboBox.SelectedIndex = cfg.Ps1UpdateTarget?.ToLowerInvariant() switch
        {
            "server" => 0,
            "mod"    => 1,
            _        => 2, // "all"
        };
        ServerExePathTextBox.Text = cfg.ExePath ?? string.Empty;
        ServerProfileDirTextBox.Text = cfg.ProfileDir ?? AppPaths.DefaultServerProfileDir;
        ServerRootDirTextBox.Text = cfg.ServerRootDir
            ?? (string.IsNullOrWhiteSpace(cfg.ExePath) ? string.Empty : (Path.GetDirectoryName(cfg.ExePath) ?? string.Empty));
        ServerPortTextBox.Text = (cfg.Port ?? 2302).ToString();
        ServerExtraArgsTextBox.Text = cfg.ExtraArgs ?? string.Empty;
        ServerBattlEyeCheckBox.IsChecked = cfg.BattlEye;

        SteamCmdPathTextBox.Text = cfg.SteamCmdPath ?? string.Empty;
        ModCacheDirTextBox.Text = cfg.ModCacheDir ?? AppPaths.DefaultModCacheDir;
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
            Ps1LaunchParam = (Ps1LaunchParamComboBox.SelectedIndex == 1) ? "user" : "default",
            Ps1AppBranch   = (Ps1AppBranchComboBox.SelectedIndex == 1) ? "exp" : "stable",
            Ps1UpdateTarget = Ps1UpdateTargetComboBox.SelectedIndex switch
            {
                0 => "server",
                1 => "mod",
                _ => "all",
            },
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
            TailLineCap = _tailLineCap
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void InitServerController()
    {
        _server = new ServerProcessController();
        _server.StateChanged += s => Dispatcher.InvokeAsync(() =>
        {
            UpdateServerButtonsForState(s);
            UpdateServerStatusPill(s);
            if (s == ServerState.Running) _uptimeTimer?.Start();
            else _uptimeTimer?.Stop();
            RefreshUptimeText();
        });

        _server.Exited += (code, crashed) => Dispatcher.InvokeAsync(() =>
        {
            ServerActionStatusText.Text = crashed
                ? $"crashed (exit {code})"
                : $"exited (exit {code})";
        });
    }

    private void ApplyServerConfigToController(ServerConfig? cfg)
    {
        if (_server == null || cfg == null) return;
        _server.AutoRestartOnCrash = cfg.AutoRestartOnCrash;
        _server.AutoRestartBackoffSeconds = cfg.AutoRestartBackoffSeconds;
        _server.AutoRestartMaxRetries = cfg.AutoRestartMaxRetries;
    }

    private void UpdateServerModeVisibility()
    {
        // Called from RadioButton.Checked which can fire during XAML parsing
        // before sibling named elements have been created. Bail out until ready.
        if (ModePs1Radio == null || ModePs1Panel == null || ModeDirectPanel == null
            || ServerUpdateModsButton == null || ServerReauthButton == null)
            return;

        var isPs1 = ModePs1Radio.IsChecked == true;
        ModePs1Panel.Visibility = isPs1 ? Visibility.Visible : Visibility.Collapsed;
        ModeDirectPanel.Visibility = isPs1 ? Visibility.Collapsed : Visibility.Visible;
        // UPDATE MODS is wired for both modes:
        //   - Ps1: invokes servermanager.ps1 -u <target> -app <branch>
        //   - DirectExe: drives SteamCMD download + deploy
        ServerUpdateModsButton.Visibility = Visibility.Visible;
        // Re-auth is SteamCMD-only.
        ServerReauthButton.Visibility = isPs1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnServerModeChanged(object sender, RoutedEventArgs e) => UpdateServerModeVisibility();

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
        ServerStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dot));
        ServerStateText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
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

    private void OnServerBrowsePs1(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*" };
        if (ofd.ShowDialog() == true)
        {
            ServerPs1PathTextBox.Text = ofd.FileName;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    private void OnServerBrowseExe(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "DayZ server exe (*.exe)|*.exe|All files (*.*)|*.*" };
        if (ofd.ShowDialog() == true)
        {
            ServerExePathTextBox.Text = ofd.FileName;
            if (string.IsNullOrWhiteSpace(ServerRootDirTextBox.Text))
                ServerRootDirTextBox.Text = Path.GetDirectoryName(ofd.FileName) ?? string.Empty;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    private void OnServerBrowseProfile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            ServerProfileDirTextBox.Text = dlg.FolderName;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    private void OnServerBrowseRoot(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            ServerRootDirTextBox.Text = dlg.FolderName;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    private void OnServerBrowseSteamCmd(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "steamcmd.exe|steamcmd.exe|All files (*.*)|*.*" };
        if (ofd.ShowDialog() == true)
        {
            SteamCmdPathTextBox.Text = ofd.FileName;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    private void OnServerBrowseModCache(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            ModCacheDirTextBox.Text = dlg.FolderName;
            try { PersistAllDirsToConfig(); } catch { }
        }
    }

    // ---- Log routing ----

    private void OnLogSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore the spurious fire from XAML init before Loaded runs.
        if (_server == null) return;
        _activeLogSource = LogSourceComboBox.SelectedIndex == 1 ? LogSource.SteamCmd : LogSource.Rpt;
        ServerLogTextBox.Clear();
        _tailBuffer.Clear();
        if (_activeLogSource == LogSource.Rpt) StartRptTail();
        else _tail?.Stop();
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        ServerLogTextBox.Clear();
        _tailBuffer.Clear();
    }

    private void StartRptTail()
    {
        try { _tail?.Dispose(); } catch { }
        _tail = new RptLogTail();
        _tail.LineAppended += line => Dispatcher.InvokeAsync(() => AppendLogLine(line));
        _tail.ActiveFileChanged += path => Dispatcher.InvokeAsync(() => ActiveLogFileText.Text = path);

        var dir = NullIfEmpty(ServerProfileDirTextBox.Text)
                  ?? AppPaths.DefaultRptDir;
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

        ServerLogTextBox.AppendText(line + Environment.NewLine);
        // Trim front of textbox if it grows huge
        if (ServerLogTextBox.LineCount > _tailLineCap + 200)
        {
            ServerLogTextBox.Text = string.Join(Environment.NewLine, _tailBuffer);
        }
        if (PauseAutoscrollCheckBox.IsChecked != true)
            ServerLogTextBox.ScrollToEnd();
    }

    // ---- Start / Stop / Restart ----

    private async void OnServerStart(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var cfg = ReadServerConfigFromUi();
        ApplyServerConfigToController(cfg);

        ServerActionStatusText.Text = "preparing…";
        ServerStartButton.IsEnabled = false;

        try
        {
            var deployed = new List<string>();
            if (cfg.Mode == ServerLaunchMode.DirectExe)
            {
                // Auto-update + deploy if requested.
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

                deployed = DeployMods(cfg);
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
                Mode: cfg.Mode,
                Ps1Path: cfg.Ps1Path,
                ExePath: cfg.ExePath,
                ProfileDir: cfg.ProfileDir,
                ServerRootDir: cfg.ServerRootDir,
                Port: cfg.Port,
                ExtraArgs: cfg.ExtraArgs,
                BattlEye: cfg.BattlEye,
                DeployedAtNames: deployed,
                Ps1LaunchParam: cfg.Ps1LaunchParam,
                Ps1AppBranch: cfg.Ps1AppBranch
            );

            ServerActionStatusText.Text = "starting…";
            await _server.StartAsync(spec);
            ServerActionStatusText.Text = _server.State == ServerState.Running ? "running" : "start failed";

            // Switch log to RPT once server is running.
            LogSourceComboBox.SelectedIndex = 0;
            StartRptTail();
        }
        catch (Exception ex)
        {
            ServerActionStatusText.Text = ex.Message;
            ServerHistoryLogger.Append("start-failed", cfg.Mode.ToString(), detail: ex.Message);
        }
        finally
        {
            UpdateServerButtonsForState(_server.State);
        }
    }

    private async void OnServerStop(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        ServerActionStatusText.Text = "stopping…";
        ServerStopButton.IsEnabled = false;
        try
        {
            await _server.StopAsync(TimeSpan.FromSeconds(5));
            ServerActionStatusText.Text = "stopped";
        }
        catch (Exception ex)
        {
            ServerActionStatusText.Text = ex.Message;
        }
        finally
        {
            UpdateServerButtonsForState(_server.State);
        }
    }

    private async void OnServerRestart(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var cfg = ReadServerConfigFromUi();
        ApplyServerConfigToController(cfg);

        try
        {
            ServerActionStatusText.Text = "restarting…";
            var deployed = cfg.Mode == ServerLaunchMode.DirectExe ? DeployMods(cfg) : new List<string>();
            var spec = new ServerLaunchSpec(
                cfg.Mode, cfg.Ps1Path, cfg.ExePath, cfg.ProfileDir, cfg.ServerRootDir,
                cfg.Port, cfg.ExtraArgs, cfg.BattlEye, deployed,
                cfg.Ps1LaunchParam, cfg.Ps1AppBranch);
            await _server.RestartAsync(spec);
            ServerActionStatusText.Text = _server.State == ServerState.Running ? "running" : "restart failed";
        }
        catch (Exception ex)
        {
            ServerActionStatusText.Text = ex.Message;
        }
        finally
        {
            UpdateServerButtonsForState(_server.State);
        }
    }

    // ---- SteamCMD update ----

    private async void OnUpdateModsClicked(object sender, RoutedEventArgs e)
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
                }
                else
                {
                    ServerActionStatusText.Text = "update failed";
                }
            }
        }
        catch (Exception ex)
        {
            ServerActionStatusText.Text = ex.Message;
        }
        finally
        {
            UpdateServerButtonsForState(_server?.State ?? ServerState.Stopped);
        }
    }

    private async Task<bool> RunPs1UpdateAsync(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Ps1Path) || !File.Exists(cfg.Ps1Path))
        {
            ServerHistoryLogger.Append("ps1-update-failed", cfg.Mode.ToString(),
                detail: "servermanager.ps1 path not set or missing");
            return false;
        }

        var target = string.IsNullOrWhiteSpace(cfg.Ps1UpdateTarget) ? "all" : cfg.Ps1UpdateTarget;
        var app    = string.IsNullOrWhiteSpace(cfg.Ps1AppBranch)    ? "stable" : cfg.Ps1AppBranch;

        // Surface ps1 stdout in the manager log pane.
        LogSourceComboBox.SelectedIndex = 1;
        _activeLogSource = LogSource.SteamCmd;
        _tail?.Stop();
        ActiveLogFileText.Text = "servermanager.ps1";
        ServerLogTextBox.Clear();
        _tailBuffer.Clear();

        ServerHistoryLogger.Append("ps1-update-start", cfg.Mode.ToString(),
            detail: $"target={target} app={app}");

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

            proc.OutputDataReceived += (_, ev) => { if (ev.Data != null) Dispatcher.InvokeAsync(() => AppendLogLine(ev.Data)); };
            proc.ErrorDataReceived  += (_, ev) => { if (ev.Data != null) Dispatcher.InvokeAsync(() => AppendLogLine("[err] " + ev.Data)); };
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
        ServerHistoryLogger.Append(ok ? "ps1-update-done" : "ps1-update-failed",
            cfg.Mode.ToString(), exitCode: exitCode);
        return ok;
    }

    private static string ResolvePowerShellExe()
    {
        // Prefer PowerShell 7 if present, fall back to Windows PowerShell.
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("where", candidate)
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                });
                if (p != null)
                {
                    var line = p.StandardOutput.ReadLine();
                    p.WaitForExit();
                    if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                        return line.Trim();
                }
            }
            catch { }
        }
        return "powershell.exe";
    }

    private void OnReauthClicked(object sender, RoutedEventArgs e)
    {
        _forceReauth = true;
        ServerActionStatusText.Text = "next update will open SteamCMD console for re-auth";
    }

    private async Task<bool> RunSteamCmdUpdateAsync(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.SteamCmdPath) || !File.Exists(cfg.SteamCmdPath))
        {
            ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(),
                detail: "steamcmd.exe path not set or missing");
            return false;
        }

        var cacheDir = cfg.ModCacheDir ?? AppPaths.DefaultModCacheDir;
        Directory.CreateDirectory(cacheDir);

        var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
        if (ids.Count == 0)
        {
            ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: "mods.txt empty");
            return false;
        }

        var client = new SteamCmdClient(cfg.SteamCmdPath);
        ServerHistoryLogger.Append("steamcmd-update-start", cfg.Mode.ToString(),
            detail: $"ids={ids.Count} validate={cfg.ValidateMods}");

        // Switch log view so the user sees what's happening.
        LogSourceComboBox.SelectedIndex = 1;
        _activeLogSource = LogSource.SteamCmd;
        _tail?.Stop();
        ActiveLogFileText.Text = "SteamCMD";
        ServerLogTextBox.Clear();
        _tailBuffer.Clear();

        var login = cfg.LoginMode == SteamLoginMode.Anonymous
            ? "anonymous"
            : (cfg.SteamLogin ?? "anonymous");

        var useInteractive =
            cfg.LoginMode == SteamLoginMode.Interactive &&
            (_forceReauth || !client.HasCachedCredential(login));

        int exitCode;
        try
        {
            if (useInteractive)
            {
                AppendLogLine("[manager] SteamCMD opened in its own console window — enter password there.");
                AppendLogLine("[manager] (this window will stay quiet until SteamCMD exits)");
                exitCode = await client.DownloadModsInteractiveAsync(
                    ids, cfg.WorkshopAppId, cacheDir, login, cfg.ValidateMods);
                _forceReauth = false;
            }
            else
            {
                exitCode = 0;
                await foreach (var line in client.DownloadModsStreamedAsync(
                                   ids, cfg.WorkshopAppId, cacheDir, login, cfg.ValidateMods))
                {
                    if (line.StartsWith("__EXIT__:"))
                    {
                        if (int.TryParse(line.AsSpan("__EXIT__:".Length), out var ec))
                            exitCode = ec;
                        break;
                    }
                    AppendLogLine(line);

                    // Auto-detect login failure → switch to interactive on next attempt.
                    if (line.IndexOf("Login Failure", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("Invalid Password", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _forceReauth = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] error: {ex.Message}");
            ServerHistoryLogger.Append("steamcmd-update-failed", cfg.Mode.ToString(), detail: ex.Message);
            return false;
        }

        var ok = exitCode == 0;
        ServerHistoryLogger.Append(
            ok ? "steamcmd-update-done" : "steamcmd-update-failed",
            cfg.Mode.ToString(),
            exitCode: exitCode);
        return ok;
    }

    private List<string> DeployMods(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerRootDir))
        {
            AppendLogLine("[manager] server root dir not set — skipping deploy.");
            return new List<string>();
        }
        if (string.IsNullOrWhiteSpace(cfg.ModCacheDir))
        {
            AppendLogLine("[manager] mod cache dir not set — skipping deploy.");
            return new List<string>();
        }

        var ids = ModStorage.LoadIds(CurrentModsTxtPath()).ToList();
        try
        {
            var result = SteamCmdClient.DeployMods(
                cfg.ModCacheDir!,
                cfg.WorkshopAppId,
                ids,
                cfg.ServerRootDir!,
                cfg.DeployMode);

            var names = result.Select(r => r.AtName).ToList();
            ServerHistoryLogger.Append("mods-deployed", cfg.Mode.ToString(),
                detail: $"count={names.Count} mode={cfg.DeployMode}");
            AppendLogLine($"[manager] deployed {names.Count} mods to {cfg.ServerRootDir}");
            return names;
        }
        catch (Exception ex)
        {
            AppendLogLine($"[manager] deploy failed: {ex.Message}");
            ServerHistoryLogger.Append("mods-deploy-failed", cfg.Mode.ToString(), detail: ex.Message);
            return new List<string>();
        }
    }

    // ---- Pre-start XML merge (reuses XmlMergeService — same path as the Combine button) ----

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
            if (excluded is { Count: > 0 })
                AppendLogLine($"[manager] pre-start merge: excluding {excluded.Count} dir(s)");
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
