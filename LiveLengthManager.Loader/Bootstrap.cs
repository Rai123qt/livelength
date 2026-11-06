using System.Reflection;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(LiveLengthManager.Loader.Bootstrap))]

namespace LiveLengthManager.Loader;

// Loads the newest healthy LiveLengthManager.dll from:
//   <bundle>\Contents\app\                      (shipped by the installer)
//   %AppData%\LiveLengthManager2027\versions\X\ (downloaded by the in-app updater)
// A version that failed (or crashed AutoCAD) while loading is skipped on later starts.
public sealed class Bootstrap : IExtensionApplication
{
    private const string MainDll = "LiveLengthManager.dll";

    internal static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveLengthManager2027");
    private static string VersionsDir => Path.Combine(DataDir, "versions");
    private static string BadFile => Path.Combine(VersionsDir, "bad.txt");
    private static string LoadingFile => Path.Combine(VersionsDir, "loading.txt");

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(VersionsDir);
            var bad = ReadBad();
            // A leftover marker means the previous start never finished loading that version.
            if (File.Exists(LoadingFile))
            {
                var crashed = File.ReadAllText(LoadingFile).Trim();
                if (crashed.Length > 0 && bad.Add(crashed)) File.AppendAllText(BadFile, crashed + Environment.NewLine);
                File.Delete(LoadingFile);
            }
            foreach (var (version, path) in Candidates())
            {
                if (bad.Contains(version.ToString())) { Log($"skip {version} (marked bad)"); continue; }
                try
                {
                    File.WriteAllText(LoadingFile, version.ToString());
                    ExtensionLoader.Load(path);
                    File.Delete(LoadingFile);
                    Log($"loaded {version} from {path}");
                    return;
                }
                catch (System.Exception ex)
                {
                    Log($"failed {version}: {ex.Message}");
                    try
                    {
                        File.Delete(LoadingFile);
                        File.AppendAllText(BadFile, version + Environment.NewLine);
                    }
                    catch { }
                }
            }
            Log("no loadable LiveLengthManager.dll found");
        }
        catch (System.Exception ex) { Log("loader error: " + ex.Message); }
    }

    public void Terminate() { }

    private static IEnumerable<(Version Version, string Path)> Candidates()
    {
        var found = new List<(Version, string, int)>();
        var contents = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location) ?? "";
        Add(Path.Combine(contents, "app", MainDll), 1);
        if (Directory.Exists(VersionsDir))
            foreach (var dir in Directory.GetDirectories(VersionsDir))
                Add(Path.Combine(dir, MainDll), 0);
        // Newest first; on a tie the installer's copy wins.
        return found.OrderByDescending(c => c.Item1).ThenByDescending(c => c.Item3).Select(c => (c.Item1, c.Item2));

        void Add(string path, int priority)
        {
            try
            {
                if (!File.Exists(path)) return;
                var name = AssemblyName.GetAssemblyName(path);
                if (name.Name == "LiveLengthManager" && name.Version != null) found.Add((name.Version, path, priority));
            }
            catch { }
        }
    }

    private static HashSet<string> ReadBad()
    {
        try
        {
            return File.Exists(BadFile)
                ? File.ReadAllLines(BadFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
                : new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "loader.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch { }
    }
}
