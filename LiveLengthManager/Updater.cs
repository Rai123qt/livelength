using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveLengthManager;

internal sealed record ReleaseInfo(Version Version, string PageUrl, string DllUrl, string? ShaUrl, string Notes = "");

internal enum UpdateState { None, Available, Downloading, Ready, Failed }

// Checks GitHub Releases for a newer plugin and stages it for the loader. The running DLL is
// never touched: the new build goes to %AppData%\LiveLengthManager2027\versions\<version>\
// and LiveLengthManager.Loader picks it on the next AutoCAD start.
internal static class Updater
{
    private const string Repository = "Rai123qt/livelength";
    private const string DllAsset = "LiveLengthManager.dll";
    // Must stay above Http: static initializers run in textual order and CreateClient reads it.
    internal static Version CurrentVersion { get; } =
        typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    private static HttpClient Http => LazyHttp.Value;
    private static int _checking;
    internal static string CurrentText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    // Without the loader, a downloaded build would never be picked up.
    internal static bool LoaderPresent => AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name == "LiveLengthManager.Loader");

    internal static UpdateState State { get; private set; }
    internal static ReleaseInfo? Latest { get; private set; }
    /// <summary>Newest published release, even when it is not newer than the running version.</summary>
    internal static ReleaseInfo? Published { get; private set; }
    internal static string Error { get; private set; } = "";
    internal static event Action? Changed;

    private static string DataDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveLengthManager2027");
    private static string SettingFile => System.IO.Path.Combine(DataDir, "auto-update.txt");
    private static string LastCheckFile => System.IO.Path.Combine(DataDir, "update-check.txt");
    private static string SkipFile => System.IO.Path.Combine(DataDir, "update-skip.txt");
    private static int _prompt;

    /// <summary>True once per check that should open the update popup; read on AutoCAD's main thread.</summary>
    internal static bool TakePrompt() => Interlocked.Exchange(ref _prompt, 0) == 1;

    internal static void Skip(Version version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDir);
            System.IO.File.WriteAllText(SkipFile, version.ToString());
        }
        catch { }
    }

    private static bool IsSkipped(Version version)
    {
        try { return System.IO.File.Exists(SkipFile) && System.IO.File.ReadAllText(SkipFile).Trim() == version.ToString(); }
        catch { return false; }
    }
    private static string VersionDir(Version v) => System.IO.Path.Combine(DataDir, "versions", v.ToString());

    internal static bool AutoCheck
    {
        get { try { return System.IO.File.ReadAllText(SettingFile).Trim() != "off"; } catch { return true; } }
        set
        {
            try
            {
                System.IO.Directory.CreateDirectory(DataDir);
                System.IO.File.WriteAllText(SettingFile, value ? "on" : "off");
            }
            catch { }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveLengthManager/" + CurrentText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Background check; at most once a day unless forced.</summary>
    internal static void CheckInBackground(bool force)
    {
        if (!force && !AutoCheck) return;
        if (!force)
        {
            try
            {
                if (System.IO.File.Exists(LastCheckFile) &&
                    DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(LastCheckFile) < TimeSpan.FromHours(20))
                {
                    RestoreStagedState();
                    return;
                }
            }
            catch { }
        }
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        Task.Run(async () =>
        {
            try { await CheckAsync(force); }
            catch (System.Exception ex) { Error = ex.Message; if (force) Set(UpdateState.Failed); }
            finally { Interlocked.Exchange(ref _checking, 0); }
        });
    }

    private static async Task CheckAsync(bool manual)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        // 404: the repository has no published release yet.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Set(UpdateState.None); return; }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("GitHub: " + (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return;
        string? dll = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == DllAsset) dll = url;
            else if (name == DllAsset + ".sha256") sha = url;
        }
        try
        {
            System.IO.Directory.CreateDirectory(DataDir);
            System.IO.File.WriteAllText(LastCheckFile, tag);
        }
        catch { }
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        Published = new ReleaseInfo(Normalize(version), root.GetProperty("html_url").GetString() ?? "", dll ?? "", sha, notes);
        if (Normalize(version) <= Normalize(CurrentVersion) || dll == null)
        {
            // A manual check still opens the popup to say the plugin is current and show its notes.
            if (manual) Interlocked.Exchange(ref _prompt, 1);
            Set(UpdateState.None);
            return;
        }
        Latest = Published;
        var ready = System.IO.File.Exists(System.IO.Path.Combine(VersionDir(Latest.Version), DllAsset));
        // Automatic checks pop up once per new version unless the user skipped it; manual checks always do.
        if (manual || (!ready && !IsSkipped(Latest.Version))) Interlocked.Exchange(ref _prompt, 1);
        Set(ready ? UpdateState.Ready : UpdateState.Available);
    }

    // After a skipped daily check, still show "ready" for a build staged earlier.
    private static void RestoreStagedState()
    {
        try
        {
            var dir = System.IO.Path.Combine(DataDir, "versions");
            if (!System.IO.Directory.Exists(dir)) return;
            var staged = System.IO.Directory.GetDirectories(dir)
                .Select(d => Version.TryParse(System.IO.Path.GetFileName(d), out var v) ? Normalize(v) : null)
                .Where(v => v != null && v > Normalize(CurrentVersion) &&
                            System.IO.File.Exists(System.IO.Path.Combine(VersionDir(v), DllAsset)))
                .Max();
            if (staged == null) return;
            Latest = new ReleaseInfo(staged, $"https://github.com/{Repository}/releases", "", null);
            Set(UpdateState.Ready);
        }
        catch { }
    }

    internal static void DownloadInBackground()
    {
        if (Latest is not { } release || State is UpdateState.Downloading or UpdateState.Ready ||
            release.DllUrl.Length == 0) return;
        Set(UpdateState.Downloading);
        Task.Run(async () =>
        {
            var dir = VersionDir(release.Version);
            var temp = System.IO.Path.Combine(dir, DllAsset + ".download");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var bytes = await Http.GetByteArrayAsync(release.DllUrl);
                if (release.ShaUrl != null)
                {
                    var expected = (await Http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t')[0];
                    var actual = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Sai mã kiểm tra SHA-256.");
                }
                await System.IO.File.WriteAllBytesAsync(temp, bytes);
                var name = AssemblyName.GetAssemblyName(temp);
                if (name.Name != "LiveLengthManager" || name.Version == null || Normalize(name.Version) != release.Version)
                    throw new InvalidOperationException("File tải về không đúng phiên bản.");
                System.IO.File.Move(temp, System.IO.Path.Combine(dir, DllAsset), true);
                Set(UpdateState.Ready);
            }
            catch (System.Exception ex)
            {
                try { System.IO.File.Delete(temp); } catch { }
                Error = ex.Message;
                Set(UpdateState.Failed);
            }
        });
    }

    /// <summary>Keeps the running version and the newest staged one; older downloads are removed.</summary>
    internal static void CleanupOldVersions()
    {
        try
        {
            var dir = System.IO.Path.Combine(DataDir, "versions");
            if (!System.IO.Directory.Exists(dir)) return;
            var current = Normalize(CurrentVersion);
            var versions = System.IO.Directory.GetDirectories(dir)
                .Select(d => (Dir: d, Ok: Version.TryParse(System.IO.Path.GetFileName(d), out var v), V: v))
                .Where(x => x.Ok && x.V != null).Select(x => (x.Dir, V: Normalize(x.V!))).ToList();
            var newest = versions.Where(x => x.V > current).Select(x => x.V).DefaultIfEmpty(current).Max();
            foreach (var (path, v) in versions)
                if (v < current) { try { System.IO.Directory.Delete(path, true); } catch { } }
            _ = newest;
        }
        catch { }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void Set(UpdateState state)
    {
        State = state;
        try { Changed?.Invoke(); } catch { }
    }
}
