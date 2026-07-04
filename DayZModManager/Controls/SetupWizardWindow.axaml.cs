using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DayZModManager.Services;

namespace DayZModManager.Controls;

/// <summary>
/// One-shot "run setup" dialog: creates the folders the app needs, resolves or bootstraps
/// SteamCMD, and optionally installs the DayZ Dedicated Server itself. Reopenable anytime, not
/// gated to first run. Returns a <see cref="ServerConfig"/> delta via <see cref="Result"/> (null
/// if cancelled/failed) rather than writing config itself - the caller merges only the fields
/// this wizard governs into the currently-active server profile, so unrelated settings (RCON
/// password, webhook URL, schedule config, ...) are never clobbered.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private CancellationTokenSource? _cts;

    public ServerConfig? Result { get; private set; }

    public SetupWizardWindow() : this(null) { }

    public SetupWizardWindow(ServerConfig? existing)
    {
        InitializeComponent();

        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(existing.ServerRootDir)) ServerFolderTextBox.Text = existing.ServerRootDir;
            if (!string.IsNullOrWhiteSpace(existing.SteamCmdPath)) SteamCmdPathTextBox.Text = existing.SteamCmdPath;
            if (!string.IsNullOrWhiteSpace(existing.SteamLogin)) SteamUsernameTextBox.Text = existing.SteamLogin;
            if (existing.LoginMode == SteamLoginMode.Anonymous) LoginAnonymousRadio.IsChecked = true;
        }

        if (string.IsNullOrWhiteSpace(SteamCmdPathTextBox.Text))
        {
            var probe = Path.Combine(AppPaths.DefaultSteamCmdDir, "steamcmd.exe");
            if (File.Exists(probe)) SteamCmdPathTextBox.Text = probe;
        }
        if (string.IsNullOrWhiteSpace(ServerFolderTextBox.Text))
            ServerFolderTextBox.Text = AppPaths.DefaultServerInstallDir;

        UpdateServerLocationHint();
        UpdateSteamCmdHint();
    }

    private void OnServerLocationModeChanged(object? sender, RoutedEventArgs e) => UpdateServerLocationHint();
    private void OnSteamCmdPathTextChanged(object? sender, TextChangedEventArgs e) => UpdateSteamCmdHint();

    private void UpdateServerLocationHint()
    {
        ServerFolderHintText.Text = AlreadyInstalledRadio.IsChecked == true
            ? "Pick the folder that already contains DayZServer_x64.exe."
            : "Pick (or create) an empty folder - the DayZ Dedicated Server will be downloaded here via SteamCMD.";
    }

    private void UpdateSteamCmdHint()
    {
        var path = SteamCmdPathTextBox.Text?.Trim() ?? string.Empty;
        SteamCmdHintText.Text = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? "Found."
            : "Not found - will be downloaded automatically when you run setup.";
    }

    private async void OnBrowseServerFolder(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { AllowMultiple = false, Title = "Select DayZ server folder" });
        if (folders.Count == 0) return;
        ServerFolderTextBox.Text = folders[0].Path.LocalPath;
    }

    private async void OnBrowseSteamCmd(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Result = null;
        Close();
    }

    private void AppendLog(string line) => LogTextBox.Text = (LogTextBox.Text ?? string.Empty) + line + "\r\n";

    private async void OnRunSetup(object? sender, RoutedEventArgs e)
    {
        var serverFolder = ServerFolderTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverFolder))
        {
            StatusText.Text = "Pick a server folder first.";
            return;
        }

        var installNew = InstallNewRadio.IsChecked == true;
        var interactive = LoginInteractiveRadio.IsChecked == true;
        var username = SteamUsernameTextBox.Text?.Trim() ?? string.Empty;

        if (installNew && !interactive)
        {
            StatusText.Text = "Anonymous login won't work for installing the DayZ Dedicated Server - switch to Interactive.";
            return;
        }
        if (installNew && string.IsNullOrWhiteSpace(username))
        {
            StatusText.Text = "Enter your Steam username first (a real login is required to install the server).";
            return;
        }

        RunSetupButton.IsEnabled = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Directory.CreateDirectory(serverFolder);
            AppendLog($"[setup] server folder ready: {serverFolder}");

            var profileDir = AppPaths.DefaultServerProfileDir;
            Directory.CreateDirectory(profileDir);
            AppendLog($"[setup] profile dir ready: {profileDir}");

            var steamCmdPath = SteamCmdPathTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(steamCmdPath) || !File.Exists(steamCmdPath))
            {
                StatusText.Text = "Downloading SteamCMD...";
                // DownloadAndExtractAsync awaits with ConfigureAwait(false) internally, so its
                // onLog callback can fire on a thread-pool thread - marshal back to the UI thread
                // before touching LogTextBox.
                steamCmdPath = await SteamCmdBootstrapper.DownloadAndExtractAsync(
                    AppPaths.DefaultSteamCmdDir,
                    line => Dispatcher.UIThread.Post(() => AppendLog(line)),
                    ct);
                SteamCmdPathTextBox.Text = steamCmdPath;

                StatusText.Text = "Running SteamCMD self-update...";
                var selfUpdateExit = 0;
                await foreach (var line in new SteamCmdClient(steamCmdPath).RunSelfUpdateAsync(ct))
                {
                    if (line.StartsWith("__EXIT__:"))
                    {
                        int.TryParse(line.AsSpan("__EXIT__:".Length), out selfUpdateExit);
                        break;
                    }
                    AppendLog(line);
                }
                if (selfUpdateExit != 0)
                {
                    StatusText.Text = $"SteamCMD self-update failed (exit {selfUpdateExit}).";
                    AppendLog($"[setup] self-update exited with code {selfUpdateExit}.");
                    return;
                }
            }
            UpdateSteamCmdHint();

            string exePath;
            if (!installNew)
            {
                StatusText.Text = "Looking for DayZServer_x64.exe...";
                var found = Directory.Exists(serverFolder)
                    ? Directory.EnumerateFiles(serverFolder, "DayZServer_x64.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    : null;
                if (found == null)
                {
                    StatusText.Text = "DayZServer_x64.exe not found in that folder.";
                    AppendLog("[setup] error: DayZServer_x64.exe not found top-level in " + serverFolder);
                    return;
                }
                exePath = found;
                AppendLog("[setup] using existing server: " + exePath);
            }
            else
            {
                StatusText.Text = "Installing DayZ Dedicated Server via SteamCMD (this can take a while)...";
                AppendLog("[setup] SteamCMD opened in its own console window - enter your password there.");
                AppendLog("[setup] (this window will stay quiet until SteamCMD exits)");

                var exitCode = await new SteamCmdClient(steamCmdPath)
                    .DownloadAppInteractiveAsync(SteamCmdClient.DayZDedicatedServerAppId, serverFolder, username, validate: true, ct);

                if (exitCode != 0)
                {
                    StatusText.Text = $"SteamCMD exited with code {exitCode} - check the console window output and try again.";
                    AppendLog($"[setup] app_update exited with code {exitCode}.");
                    return;
                }

                var found = Directory.EnumerateFiles(serverFolder, "DayZServer_x64.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (found == null)
                {
                    StatusText.Text = "Install finished but DayZServer_x64.exe was not found.";
                    AppendLog("[setup] error: install completed (exit 0) but DayZServer_x64.exe missing top-level.");
                    return;
                }
                exePath = found;
                AppendLog("[setup] server installed: " + exePath);
            }

            Result = new ServerConfig
            {
                ExePath = exePath,
                ServerRootDir = serverFolder,
                SteamCmdPath = steamCmdPath,
                ModCacheDir = Path.GetDirectoryName(Path.GetFullPath(steamCmdPath)) ?? AppPaths.DefaultSteamCmdDir,
                SteamLogin = string.IsNullOrWhiteSpace(username) ? null : username,
                LoginMode = interactive ? SteamLoginMode.Interactive : SteamLoginMode.Anonymous,
            };

            StatusText.Text = "Setup complete.";
            AppendLog("[setup] done.");
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Setup failed: " + ex.Message;
            AppendLog("[setup] error: " + ex.Message);
        }
        finally
        {
            RunSetupButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
