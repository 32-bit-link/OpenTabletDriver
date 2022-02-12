#nullable enable

namespace OpenTabletDriver.Desktop.Interop.AppInfo
{
    public interface IAppInfo
    {
        string ConfigurationDirectory { set; get; }
        string SettingsFile { set; get; }
        string PluginDirectory { set; get; }
        string PresetDirectory { set; get; }
        string TemporaryDirectory { set; get; }
        string CacheDirectory { set; get; }
        string BackupDirectory { set; get; }
        string TrashDirectory { set; get; }
        string AppDataDirectory { set; get; }
    }
}
