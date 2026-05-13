using System.Collections.ObjectModel;
using System.Text;
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

            LocalFilePathTextBox.Text = AppPaths.ModsTxtPath;
            LocalApiKeyTextBox.Text = keyToUse;

            SearchApiKeyTextBox.Text = keyToUse;
            ModsApiKeyTextBox.Text = keyToUse;

            // Default mods root one level above publish folder.
            ModsRootTextBox.Text = System.IO.Path.Combine(AppContext.BaseDirectory, "..");

            CombineAllCheckBox.Checked += (_, _) => UpdateCombineEnabled();
            CombineAllCheckBox.Unchecked += (_, _) => UpdateCombineEnabled();
            UpdateCombineEnabled();

            _ = RefreshLocalAsync();
            _ = RefreshModsListAsync();
            _ = RefreshModFoldersAsync();
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
            var ids = ModStorage.LoadIds(path).OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                _localItems.Add("(file empty)");
                LocalFooterTextBlock.Text = "No IDs found.";
                return;
            }

            var doLookup = LocalLookupCheckBox.IsChecked == true;
            if (!doLookup)
            {
                foreach (var id in ids) _localItems.Add(id.ToString());
                LocalFooterTextBlock.Text = $"Loaded {ids.Length} IDs.";
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
                _localItems.Add(!string.IsNullOrWhiteSpace(title) ? $"{id} - {title}" : id.ToString());

            LocalFooterTextBlock.Text = $"Loaded {ids.Length} IDs.";
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

            var ok = ModStorage.RemoveId(LocalFilePathTextBox.Text.Trim(), id);
            LocalStatusTextBlock.Text = ok ? $"Removed {id}." : $"ID {id} not found.";
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
            foreach (var r in results) _searchResults.Add(r);
            AddStatusTextBlock.Text = $"Found {results.Count} results.";
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

            ModStorage.AddIds(AppPaths.ModsTxtPath, closure.Select(x => x.ToString()));
            AddStatusTextBlock.Text = AutoAddDepsCheckBox.IsChecked == true
                ? $"Added {closure.Count} mods (incl. dependencies)."
                : $"Added {item.PublishedFileId} to mods.txt.";

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

            var outFile = System.IO.Path.IsPathRooted(outVal) ? outVal : System.IO.Path.Combine(root, outVal);
            FolderDetailsTextBox.Text = "Generating combined types.xml...";
            await Task.Run(() => TypesXmlGenerator.Generate(root, outFile));
            FolderDetailsTextBox.Text = $"Generated: {outFile}";
        }
        catch (Exception ex)
        {
            FolderDetailsTextBox.Text = $"Generation failed: {ex.Message}";
        }
    }
}
