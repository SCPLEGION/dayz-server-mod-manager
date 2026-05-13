using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using System.Collections.Concurrent;

namespace DayZModManager;

public sealed class MainForm : Form
{
    // Baked-in Steam Web API key (Steam Web API / IPublishedFileService).
    // This is optional; the UI textbox still overrides it if you paste a different key.
    private const string BakedSteamWebApiKey = "A871AA9AB7D48FFD5C3A6E145EDCCC0B";

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabLocal = new("Local Mods");
    private readonly TabPage _tabAdd = new("Add to mods.txt");

    // ---- Local tab controls ----
    private readonly TextBox _localFilePath = new() { Dock = DockStyle.Fill };
    private readonly Button _localBrowse = new() { Text = "Browse..." };
    private readonly Button _localRefresh = new() { Text = "Refresh" };
    private readonly Button _localRemove = new() { Text = "Remove Selected" };
    private readonly ListBox _localList = new() { Dock = DockStyle.Fill };
    private readonly Label _localStatus = new() { Dock = DockStyle.Bottom, Height = 34, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
    private readonly CheckBox _localLookupCheck = new() { Text = "Lookup titles (Steam Web API key)" };
    private readonly TextBox _steamApiKeyInput = new() { PlaceholderText = "Steam Web API key (STEAM_API_KEY env or paste here)" };

    // ---- Add tab controls ----
    private readonly Label _modsTxtLabel = new() { Text = $"mods.txt: {AppPaths.ModsTxtPath}", AutoSize = true };
    private readonly ListBox _modsList = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _modsLookupCheck = new() { Text = "Lookup titles in mods.txt (optional)" };

    private readonly TextBox _appidInput = new() { Text = "221100", Width = 90 };
    private readonly TextBox _creatorAppidInput = new() { Text = "221100", Width = 90 };
    private readonly TextBox _searchTextInput = new() { Multiline = false, Dock = DockStyle.Top, Height = 24 };
    private readonly Button _searchButton = new() { Text = "Search Workshop" };
    private readonly Button _addSelectedButton = new() { Text = "Add Selected to mods.txt" };
    private readonly CheckBox _autoDepsCheck = new() { Text = "Auto-add dependencies", Checked = true, AutoSize = true };

    private readonly ListBox _searchResults = new()
    {
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 120
    };

    private readonly Label _addStatus = new() { Dock = DockStyle.Bottom, Height = 34, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
    private readonly TextBox _searchSteamApiKeyInput = new() { PlaceholderText = "Steam Web API key (STEAM_API_KEY env or paste here)" };
    private readonly CheckBox _searchLookupCheck = new() { Text = "Lookup titles/descriptions in results (requires key)" };

    // Cache workshop details so refreshes are fast.
    private readonly ConcurrentDictionary<ulong, string?> _titleCache = new();
    private readonly ConcurrentDictionary<ulong, List<ulong>> _depsCache = new();

    public MainForm()
    {
        Text = "DayZ Mod Manager";
        Width = 1200;
        Height = 900;

        Controls.Add(_tabs);
        _tabs.TabPages.Add(_tabLocal);
        _tabs.TabPages.Add(_tabAdd);

        BuildLocalTab();
        BuildAddTab();

        Load += (_, _) =>
        {
            _localFilePath.Text = AppPaths.ModsTxtPath;
            // Prefer baked key, but allow overriding via STEAM_API_KEY if you want.
            var envKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
            var keyToUse = string.IsNullOrWhiteSpace(envKey) ? BakedSteamWebApiKey : envKey!;
            _steamApiKeyInput.Text = keyToUse;
            _searchSteamApiKeyInput.Text = keyToUse;

            _ = RefreshLocalAsync();
            _ = RefreshModsListAsync();
        };
    }

    private void BuildLocalTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        _tabLocal.Controls.Add(root);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);

        left.Controls.Add(new Label { Text = "Local file (IDs one per line)", AutoSize = true, Padding = new Padding(6) });

        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.Controls.Add(_localFilePath, 0, 0);
        fileRow.Controls.Add(_localBrowse, 1, 0);
        left.Controls.Add(fileRow, 0, 1);

        _localBrowse.Click += async (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select local mods file",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _localFilePath.Text = ofd.FileName;
                await RefreshLocalAsync();
            }
        };

        var btnRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnRow.Controls.Add(_localRefresh, 0, 0);
        btnRow.Controls.Add(_localRemove, 1, 0);
        left.Controls.Add(btnRow, 0, 2);

        _localRefresh.Click += async (_, _) => await RefreshLocalAsync();
        _localRemove.Click += async (_, _) => await RemoveLocalSelectedAsync();

        left.Controls.Add(_localLookupCheck, 0, 3);
        _steamApiKeyInput.Height = 24;
        left.Controls.Add(_steamApiKeyInput, 0, 4);

        left.Controls.Add(new Panel(), 0, 5);
        left.Controls.Add(_localStatus, 0, 6);

        root.Controls.Add(_localList, 1, 0);
        _localList.SelectionMode = SelectionMode.One;
    }

    private void BuildAddTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        _tabAdd.Controls.Add(root);

        // Left: workshop search
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(10) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // title
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // key row
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // appid row
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // search row
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // results
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // add selected row
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // status
        root.Controls.Add(left, 0, 0);

        left.Controls.Add(new Label { Text = "Search Workshop (Steam Web API)", AutoSize = true });

        var keyRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        keyRow.Controls.Add(_searchLookupCheck);
        keyRow.Controls.Add(_searchSteamApiKeyInput);
        _searchSteamApiKeyInput.Width = 360;
        left.Controls.Add(keyRow, 0, 1);

        var appRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        appRow.Controls.Add(new Label { Text = "appid", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        appRow.Controls.Add(_appidInput);
        appRow.Controls.Add(new Label { Text = "creator_appid", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        appRow.Controls.Add(_creatorAppidInput);
        left.Controls.Add(appRow, 0, 2);

        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_searchTextInput, 0, 0);
        searchRow.Controls.Add(_searchButton, 1, 0);
        left.Controls.Add(searchRow, 0, 3);

        _searchResults.DrawItem += SearchResults_DrawItem;
        _searchResults.SelectionMode = SelectionMode.One;
        left.Controls.Add(_searchResults, 0, 4);

        var addRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        addRow.Controls.Add(_addSelectedButton);
        addRow.Controls.Add(_autoDepsCheck);
        left.Controls.Add(addRow, 0, 5);

        left.Controls.Add(_addStatus, 0, 6);

        _searchButton.Click += async (_, _) => await SearchWorkshopAsync();
        _addSelectedButton.Click += async (_, _) => await AddSelectedFromSearchAsync();

        // Right: mods.txt view
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(right, 1, 0);

        right.Controls.Add(_modsTxtLabel, 0, 0);
        _modsLookupCheck.CheckedChanged += async (_, _) => await RefreshModsListAsync();
        right.Controls.Add(_modsLookupCheck, 0, 1);
        right.Controls.Add(_modsList, 0, 2);

        _modsList.SelectionMode = SelectionMode.One;
    }

    private void SearchResults_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _searchResults.Items.Count) return;

        e.DrawBackground();
        var item = _searchResults.Items[e.Index] as WorkshopSearchResultItem;
        if (item == null) return;

        var bounds = e.Bounds;
        var x = bounds.X;
        var y = bounds.Y;
        var width = bounds.Width;

        var font = System.Drawing.SystemFonts.MessageBoxFont;
        var titleRect = new Rectangle(x + 4, y + 4, width - 8, 42);
        TextRenderer.DrawText(e.Graphics, item.Title ?? "", font, titleRect, System.Drawing.Color.Black,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var descRect = new Rectangle(x + 4, y + 48, width - 8, bounds.Height - 52);
        TextRenderer.DrawText(e.Graphics, item.Description ?? "", font, descRect, System.Drawing.Color.DimGray,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    private void SetLocalStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetLocalStatus(text))); return; }
        _localStatus.Text = text;
    }

    private void SetAddStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetAddStatus(text))); return; }
        _addStatus.Text = text;
    }

    private static string? GetApiKeyOrNull(TextBox box)
    {
        var pasted = box.Text.Trim();
        if (pasted.Length > 0) return pasted;
        var envKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey;
        return BakedSteamWebApiKey;
    }

    private static async Task<List<ulong>> LoadIdsForLookupAsync(string modsPath)
    {
        var set = ModStorage.LoadIds(modsPath);
        return await Task.FromResult(set.OrderBy(x => x).ToList());
    }

    private async Task RefreshLocalAsync()
    {
        try
        {
            SetLocalStatus("Loading...");
            _localList.Items.Clear();

            var ids = ModStorage.LoadIds(_localFilePath.Text).OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                _localList.Items.Add("(file empty)");
                SetLocalStatus("No IDs found.");
                return;
            }

            if (!_localLookupCheck.Checked)
            {
                foreach (var id in ids) _localList.Items.Add(id);
                SetLocalStatus($"Loaded {ids.Length} IDs.");
                return;
            }

            var apiKey = GetApiKeyOrNull(_steamApiKeyInput);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetLocalStatus("Missing Steam Web API key. Showing IDs only.");
                foreach (var id in ids) _localList.Items.Add(id);
                return;
            }

            var throttler = new SemaphoreSlim(5);
            var tasks = ids.Select(async id =>
            {
                await throttler.WaitAsync();
                try
                {
                    if (_titleCache.TryGetValue(id, out var cachedTitle))
                    {
                        return (id, cachedTitle);
                    }

                    var details = await SteamWorkshopClient.GetPublishedFileDetailsAsync(id, apiKey);
                    var title = details?.Title;
                    if (!string.IsNullOrWhiteSpace(title))
                        _titleCache[id] = title;

                    return (id, title);
                }
                catch
                {
                    return (id, (string?)null);
                }
                finally
                {
                    throttler.Release();
                }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
            {
                _localList.Items.Add(!string.IsNullOrWhiteSpace(title) ? $"{id} - {title}" : id);
            }

            SetLocalStatus($"Loaded {ids.Length} IDs.");
        }
        catch (Exception ex)
        {
            SetLocalStatus(ex.Message);
        }
    }

    private async Task RemoveLocalSelectedAsync()
    {
        try
        {
            var selected = _localList.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(selected)) return;

            var token = selected.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var id = ModStorage.ParseWorkshopId(token);

            var ok = ModStorage.RemoveId(_localFilePath.Text, id);
            SetLocalStatus(ok ? $"Removed {id}." : $"ID {id} not found.");
            await RefreshLocalAsync();
        }
        catch (Exception ex)
        {
            SetLocalStatus(ex.Message);
        }
    }

    private async Task RefreshModsListAsync()
    {
        try
        {
            _modsList.Items.Clear();
            _modsList.BeginUpdate();
            var ids = ModStorage.LoadIds(AppPaths.ModsTxtPath).OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                _modsList.Items.Add("(mods.txt empty)");
                return;
            }

            if (!_modsLookupCheck.Checked)
            {
                foreach (var id in ids) _modsList.Items.Add(id);
                return;
            }

            var apiKey = GetApiKeyOrNull(_steamApiKeyInput);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                foreach (var id in ids) _modsList.Items.Add(id);
                return;
            }

            var throttler = new SemaphoreSlim(5);
            var tasks = ids.Select(async id =>
            {
                await throttler.WaitAsync();
                try
                {
                    if (_titleCache.TryGetValue(id, out var cachedTitle))
                        return (id, cachedTitle);

                    var details = await SteamWorkshopClient.GetPublishedFileDetailsAsync(id, apiKey);
                    var title = details?.Title;
                    if (!string.IsNullOrWhiteSpace(title))
                        _titleCache[id] = title;

                    return (id, title);
                }
                catch
                {
                    return (id, (string?)null);
                }
                finally
                {
                    throttler.Release();
                }
            }).ToArray();

            var resolved = await Task.WhenAll(tasks);
            foreach (var (id, title) in resolved.OrderBy(x => x.id))
                _modsList.Items.Add(!string.IsNullOrWhiteSpace(title) ? $"{id} - {title}" : id.ToString());
        }
        finally
        {
            _modsList.EndUpdate();
        }
    }

    private static IEnumerable<string> ParseIdsFromRaw(string raw)
        => raw.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0);

    private async Task SearchWorkshopAsync()
    {
        SetAddStatus("Searching...");
        _searchResults.Items.Clear();

        try
        {
            var apiKey = GetApiKeyOrNull(_searchSteamApiKeyInput);
            if (!_searchLookupCheck.Checked)
            {
                // Still require key because Steam QueryFiles returns details; we allow running without lookup flag only if key is present.
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Steam API key is required for workshop search.");
            }
            else if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Missing Steam Web API key. Paste it into the field or set STEAM_API_KEY.");
            }

            var appid = uint.Parse(_appidInput.Text.Trim());
            var creatorAppid = uint.Parse(_creatorAppidInput.Text.Trim());

            var searchText = _searchTextInput.Text.Trim();
            if (searchText.Length == 0)
                throw new InvalidOperationException("Enter search text.");

            var results = await SteamWorkshopClient.QueryFilesAsync(
                apiKey!,
                searchText,
                appid,
                creatorAppid,
                20);

            foreach (var r in results)
                _searchResults.Items.Add(r);

            SetAddStatus($"Found {results.Count} results.");
        }
        catch (Exception ex)
        {
            _searchResults.Items.Clear();
            _searchResults.Items.Add(new WorkshopSearchResultItem { Title = "Error", Description = ex.Message });
            SetAddStatus(ex.Message);
        }
    }

    private async Task AddSelectedFromSearchAsync()
    {
        try
        {
            if (_searchResults.SelectedItem is not WorkshopSearchResultItem item)
                return;

            if (item.PublishedFileId == 0)
            {
                SetAddStatus("Select a valid result item first.");
                return;
            }

            var apiKey = GetApiKeyOrNull(_searchSteamApiKeyInput);
            if (_autoDepsCheck.Checked && string.IsNullOrWhiteSpace(apiKey))
            {
                SetAddStatus("Missing Steam Web API key. Paste it or set STEAM_API_KEY to resolve dependencies.");
                return;
            }

            var root = new HashSet<ulong> { item.PublishedFileId };
            HashSet<ulong> closure = root;

            if (_autoDepsCheck.Checked)
            {
                SetAddStatus("Resolving dependencies...");
                closure = await ResolveDependenciesClosureAsync(root, apiKey!);
            }

            ModStorage.AddIds(AppPaths.ModsTxtPath, closure.Select(x => x.ToString()));
            SetAddStatus(_autoDepsCheck.Checked ? $"Added {closure.Count} mods (incl. dependencies)." : $"Added {item.PublishedFileId} to mods.txt.");
            await RefreshModsListAsync();
        }
        catch (Exception ex)
        {
            SetAddStatus(ex.Message);
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
            if (visited.Count > maxNodes)
                break;

            var batch = new List<ulong>(batchSize);
            while (queue.Count > 0 && batch.Count < batchSize)
            {
                if (queue.TryDequeue(out var id))
                    batch.Add(id);
            }

            var tasks = batch.Select(async id =>
            {
                if (_depsCache.TryGetValue(id, out var cached))
                    return (id, cached);

                var children = await SteamWorkshopClient.GetChildrenPublishedFileIdsAsync(id, apiKey);
                _depsCache[id] = children;
                return (id, children);
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            foreach (var (_, children) in results)
            {
                foreach (var child in children)
                {
                    if (visited.Add(child))
                        queue.Enqueue(child);
                }
            }
        }

        return visited;
    }

    // Uses shared DTO: WorkshopSearchResultItem
}
