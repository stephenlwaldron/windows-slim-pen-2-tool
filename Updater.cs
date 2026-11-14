using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SlimPenHaptics;

// Checks GitHub releases for a newer version. A downloaded update installs itself the next
// time the app starts; the portable copy has no installer to update, so it only reports.
public class Updater
{
    private const string RepoUrl = "https://github.com/stephenlwaldron/windows-slim-pen-haptics-unlocked";

    private UpdateManager? _manager;
    private UpdateInfo?    _ready;
    private bool           _busy;

    public string Status { get; private set; } = "";
    public bool   CanRestartToUpdate => _ready != null;
    public bool   IsInstalled { get; private set; }

    // Raised on a background thread.
    public event Action? Changed;

    public string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
            IsInstalled = _manager.IsInstalled;
            if (!IsInstalled)
            {
                Set("This is the portable copy, so it doesn't update itself. Use the installer for automatic updates.");
                return;
            }

            Set("Checking for updates…");
            var update = await _manager.CheckForUpdatesAsync();
            if (update == null)
            {
                Set("You're up to date.");
                return;
            }

            string version = update.TargetFullRelease.Version.ToString();
            Set($"Downloading version {version}…");
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            Log.Write($"update {version} downloaded");
            Set($"Version {version} is ready. It installs the next time the app starts.");
        }
        catch (Exception ex)
        {
            Log.Write($"update check failed: {ex.Message}");
            Set("Couldn't check for updates. Check your internet connection and try again.");
        }
        finally
        {
            _busy = false;
        }
    }

    public void RestartToUpdate()
    {
        if (_manager != null && _ready != null) _manager.ApplyUpdatesAndRestart(_ready);
    }

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
