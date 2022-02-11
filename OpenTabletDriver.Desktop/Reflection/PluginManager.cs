using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OpenTabletDriver.Desktop.Interop.AppInfo;
using OpenTabletDriver.Desktop.Reflection.Metadata;

namespace OpenTabletDriver.Desktop.Reflection
{
    public class PluginManager : IPluginManager
    {
        public PluginManager(IAppInfo appInfo)
            : this(appInfo.PluginDirectory, appInfo.TrashDirectory, appInfo.TemporaryDirectory)
        {
        }

        public PluginManager(string pluginDirectory, string trashDirectory, string temporaryDirectory)
        {
            PluginDirectory = new DirectoryInfo(pluginDirectory);
            TrashDirectory = new DirectoryInfo(trashDirectory);
            TemporaryDirectory = new DirectoryInfo(temporaryDirectory);

            if (!PluginDirectory.Exists)
                PluginDirectory.Create();
        }

        private readonly List<DesktopPluginContext> _plugins = new List<DesktopPluginContext>();

        private readonly IEnumerable<Assembly> _coreAssemblies = new[]
        {
            Assembly.Load("OpenTabletDriver"),
            Assembly.Load("OpenTabletDriver.Desktop"),
            Assembly.Load("OpenTabletDriver.Configurations")
        };

        private DirectoryInfo PluginDirectory { get; }
        private DirectoryInfo TrashDirectory { get; }
        private DirectoryInfo TemporaryDirectory { get; }

        public IReadOnlyList<DesktopPluginContext> Plugins => _plugins;

        public IEnumerable<Assembly> Assemblies => Plugins.SelectMany(c => c.Assemblies).Concat(_coreAssemblies);

        public IEnumerable<Type> ExportedTypes => Assemblies.SelectMany(r => r.ExportedTypes);

        public event EventHandler AssembliesChanged;

        public void Clean()
        {
            try
            {
                if (PluginDirectory.Exists)
                {
                    foreach (var file in PluginDirectory.GetFiles())
                    {
                        Log.Write("Plugin", $"Unexpected file found: '{file.FullName}'", LogLevel.Warning);
                    }
                }

                if (TrashDirectory.Exists)
                    Directory.Delete(TrashDirectory.FullName, true);
                if (TemporaryDirectory.Exists)
                    Directory.Delete(TemporaryDirectory.FullName, true);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        public void Load()
        {
            foreach (var dir in PluginDirectory.GetDirectories())
                LoadPlugin(dir);

            AssembliesChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool InstallPlugin(string filePath)
        {
            var file = new FileInfo(filePath);
            if (!file.Exists)
                return false;

            var name = file.Name.Replace(file.Extension, string.Empty);
            var tempDir = new DirectoryInfo(Path.Join(TemporaryDirectory.FullName, name));
            if (!tempDir.Exists)
                tempDir.Create();

            var pluginPath = Path.Join(PluginDirectory.FullName, name);
            var pluginDir = new DirectoryInfo(pluginPath);
            switch (file.Extension)
            {
                case ".zip":
                {
                    ZipFile.ExtractToDirectory(file.FullName, tempDir.FullName, true);
                    break;
                }
                case ".dll":
                {
                    file.CopyTo(Path.Join(tempDir.FullName, file.Name));
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported archive type: {file.Extension}");
            }
            var context = Plugins.FirstOrDefault(ctx => ctx.Directory.FullName == pluginDir.FullName);
            var result = pluginDir.Exists ? UpdatePlugin(context, tempDir) : InstallPlugin(pluginDir, tempDir);

            if (!TemporaryDirectory.GetFileSystemInfos().Any())
                Directory.Delete(TemporaryDirectory.FullName, true);

            if (result)
                LoadPlugin(pluginDir);
            return result;
        }

        public async Task<bool> DownloadPlugin(PluginMetadata metadata)
        {
            string sourcePath = Path.Join(TemporaryDirectory.FullName, metadata.Name);
            string targetPath = Path.Join(PluginDirectory.FullName, metadata.Name);
            string metadataPath = Path.Join(targetPath, "metadata.json");

            var sourceDir = new DirectoryInfo(sourcePath);
            var targetDir = new DirectoryInfo(targetPath);

            await metadata.DownloadAsync(sourcePath);

            var context = Plugins.FirstOrDefault(ctx => ctx.Directory.FullName == targetDir.FullName);
            var result = targetDir.Exists ? UpdatePlugin(context, sourceDir) : InstallPlugin(targetDir, sourceDir);

            using (var fs = File.Create(metadataPath))
                Serialization.Serialize(fs, metadata);

            if (!TemporaryDirectory.GetFileSystemInfos().Any())
                Directory.Delete(TemporaryDirectory.FullName, true);
            return result;
        }

        public bool InstallPlugin(DirectoryInfo target, DirectoryInfo source)
        {
            Log.Write("Plugin", $"Installing plugin '{target.Name}'");
            CopyDirectory(source, target);
            LoadPlugin(target);
            return true;
        }

        public bool UninstallPlugin(DesktopPluginContext plugin)
        {
            if (plugin == null)
                return false;

            var random = new Random();
            if (!Directory.Exists(TrashDirectory.FullName))
                TrashDirectory.Create();

            Log.Write("Plugin", $"Uninstalling plugin '{plugin.FriendlyName}'");

            var trashPath = Path.Join(TrashDirectory.FullName, $"{plugin.FriendlyName}_{random.Next()}");
            Directory.Move(plugin.Directory.FullName, trashPath);

            return UnloadPlugin(plugin);
        }

        public bool UpdatePlugin(DesktopPluginContext plugin, DirectoryInfo source)
        {
            var targetDir = new DirectoryInfo(plugin.Directory.FullName);
            if (UninstallPlugin(plugin))
                return InstallPlugin(targetDir, source);
            return false;
        }

        public bool UnloadPlugin(DesktopPluginContext context)
        {
            Log.Write("Plugin", $"Unloading plugin '{context.FriendlyName}'", LogLevel.Debug);
            _plugins.Remove(context);
            AssembliesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private void LoadPlugin(DirectoryInfo directory)
        {
            // "Plugins" are directories that contain managed and unmanaged dll
            // These dlls are loaded into a PluginContext per directory
            directory.Refresh();
            if (Plugins.All(p => p.Directory.Name != directory.Name))
            {
                if (directory.Exists)
                {
                    Log.Write("Plugin", $"Loading plugin '{directory.Name}'", LogLevel.Debug);
                    var context = new DesktopPluginContext(directory);

                    _plugins.Add(context);
                }
                else
                {
                    Log.Write("Plugin", $"Tried to load a nonexistent plugin '{directory.Name}'", LogLevel.Warning);
                }
            }
            else
            {
                Log.Write("Plugin", $"Attempted to load the plugin {directory.Name} when it is already loaded.", LogLevel.Debug);
            }
        }

        private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination)
        {
            if (!source.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + source.FullName);
            }

            // If the destination directory doesn't exist, create it.
            destination.Create();

            // Get the files in the directory and copy them to the new location.
            foreach (var file in source.GetFiles())
            {
                string tempPath = Path.Combine(destination.FullName, file.Name);
                file.CopyTo(tempPath, false);
            }

            foreach (DirectoryInfo subdir in source.GetDirectories())
            {
                CopyDirectory(
                    new DirectoryInfo(subdir.FullName),
                    new DirectoryInfo(Path.Combine(destination.FullName, subdir.Name))
                );
            }
        }
    }
}
