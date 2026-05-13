using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        LocalIdsListBox.ItemsSource = _localItems;
        SearchResultsListBox.ItemsSource = _searchResults;
        ModsListBox.ItemsSource = _modsListItems;
        ModFoldersListBox.ItemsSource = _modFolders;

        Loaded += (_, _) =>
        {
            var envKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
            var keyToUse = string.IsNullOrWhiteSpace(envKey) ? BakedSteamWebApiKey : envKey!;

            LocalApiKeyTextBox.Text = keyToUse;

            SearchApiKeyTextBox.Text = keyToUse;
            ModsApiKeyTextBox.Text = keyToUse;

            // Default mods root one level above publish folder.
            ModsRootTextBox.Text = System.IO.Path.Combine(AppContext.BaseDirectory, "..");
            LocalFilePathTextBox.Text = AppPaths.ModsTxtPath;

            // Load persisted config (paths + merge mode).
            var cfg = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(cfg.LocalModsTxtPath))
                LocalFilePathTextBox.Text = cfg.LocalModsTxtPath!;
            if (!string.IsNullOrWhiteSpace(cfg.ModsRootPath))
                ModsRootTextBox.Text = cfg.ModsRootPath!;
            if (!string.IsNullOrWhiteSpace(cfg.CombineOutFileText))
                CombineOutFileTextBox.Text = cfg.CombineOutFileText!;
            if (cfg.MergeModeSelectedIndex is int idx)
                MergeModeComboBox.SelectedIndex = idx;

            CombineAllCheckBox.Checked += (_, _) => UpdateCombineEnabled();
            CombineAllCheckBox.Unchecked += (_, _) => UpdateCombineEnabled();
            UpdateCombineEnabled();

            _ = RefreshLocalAsync();
            _ = RefreshModsListAsync();
            _ = RefreshModFoldersAsync();
            _ = RefreshHistoryAsync();
        };
    }

    private void UpdateCombineEnabled()
    {
        var enabled = CombineAllCheckBox.IsChecked == true;
        CombineOutFileTextBox.IsEnabled = enabled;
    }

    // ---- Local mods tab ----
    private void OnBrowseLocalFile(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (ofd.ShowDialog() == true)
        {
            LocalFilePathTextBox.Text = ofd.FileName;
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
                foreach (var id in ids)
                {
                    var installed = modsRootExists && Directory.Exists(Path.Combine(modsRoot, id.ToString()));
                    _localItems.Add(installed ? $"{id} (installed)" : id.ToString());
                }
                LocalFooterTextBlock.Text = invalidCount > 0
                    ? $"Loaded {ids.Length} IDs (invalid lines: {invalidCount})."
                    : $"Loaded {ids.Length} IDs.";
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
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
            {
                var installed = modsRootExists && Directory.Exists(Path.Combine(modsRoot, id.ToString()));
                if (!string.IsNullOrWhiteSpace(title))
                    _localItems.Add(installed ? $"{id} - {title} (installed)" : $"{id} - {title}");
                else
                    _localItems.Add(installed ? $"{id} (installed)" : id.ToString());
            }

            LocalFooterTextBlock.Text = invalidCount > 0
                ? $"Loaded {ids.Length} IDs (invalid lines: {invalidCount})."
                : $"Loaded {ids.Length} IDs.";
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
                var existing = ModStorage.LoadIds(AppPaths.ModsTxtPath);
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

            var modsTxtPath = AppPaths.ModsTxtPath;
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

            var modsTxtPath = AppPaths.ModsTxtPath;
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

            var ids = ModStorage.LoadIds(AppPaths.ModsTxtPath).OrderBy(x => x).ToArray();
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

            // Requirement: default output should live next to the exe.
            var outFile = System.IO.Path.IsPathRooted(outVal)
                ? outVal
                : System.IO.Path.Combine(AppContext.BaseDirectory, outVal);
            var mergeMode = GetSelectedMergeMode();
            FolderDetailsTextBox.Text = "Generating combined types.xml...";
            await Task.Run(() => TypesXmlGenerator.Generate(root, outFile, mergeMode));
            FolderDetailsTextBox.Text = $"Generated: {outFile}";
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
            FolderDetailsTextBox.Text = "Previewing combined types.xml (dry-run)...";

            var stats = await Task.Run(() => TypesXmlGenerator.Preview(root, mergeMode));

            var sb = new StringBuilder();
            sb.AppendLine("Dry-run preview:");
            sb.AppendLine($"Mods scanned: {stats.ModDirsScanned}");
            sb.AppendLine($"types.xml files found: {stats.TypesXmlFilesFound}");
            sb.AppendLine($"Candidate <types> children found: {stats.CandidateTypesElementsFound}");
            sb.AppendLine($"Merged output children: {stats.MergedTypeChildren}");
            sb.AppendLine($"Unique type keys: {stats.UniqueTypeKeys}");
            sb.AppendLine($"Conflicts detected: {stats.ConflictCount}");

            if (stats.ConflictCount > 0 && stats.Conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("First conflicts:");
                foreach (var c in stats.Conflicts.Take(25))
                    sb.AppendLine($"- {c.Key}");
            }

            FolderDetailsTextBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            FolderDetailsTextBox.Text = $"Preview failed: {ex.Message}";
        }
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
            MergeModeSelectedIndex = MergeModeComboBox.SelectedIndex
        };

        AppConfigStore.Save(cfg);
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
                ModsTxtPath = AppPaths.ModsTxtPath,
                ModsRootPath = ModsRootTextBox.Text.Trim(),
                CombineOutFile = CombineOutFileTextBox.Text.Trim(),
                MergeMode = GetSelectedMergeMode(),
                AutoAddDeps = AutoAddDepsCheckBox.IsChecked == true,
                SearchApiKey = SearchApiKeyTextBox.Text.Trim(),
                LocalModsApiKey = LocalApiKeyTextBox.Text.Trim(),
                ModsIds = ModStorage.LoadIds(AppPaths.ModsTxtPath).ToList()
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

            ModStorage.SaveIdsFromSet(AppPaths.ModsTxtPath, profile.ModsIds ?? new List<ulong>());
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
}
