namespace LiveLengthManager;

// Small diagnostic log next to loader.log, for failures that are otherwise swallowed to keep AutoCAD alive.
internal static class PluginLog
{
    internal static void Write(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveLengthManager2027");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "plugin.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} v{Updater.CurrentText} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
