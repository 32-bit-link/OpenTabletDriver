﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Octokit;
using OpenTabletDriver.Components;
using OpenTabletDriver.Desktop;
using OpenTabletDriver.Desktop.Binding;
using OpenTabletDriver.Desktop.Contracts;
using OpenTabletDriver.Desktop.Diagnostics;
using OpenTabletDriver.Desktop.Interop.AppInfo;
using OpenTabletDriver.Desktop.Migration;
using OpenTabletDriver.Desktop.Profiles;
using OpenTabletDriver.Desktop.Reflection;
using OpenTabletDriver.Desktop.Reflection.Metadata;
using OpenTabletDriver.Desktop.RPC;
using OpenTabletDriver.Desktop.Updater;
using OpenTabletDriver.Devices;
using OpenTabletDriver.Logging;
using OpenTabletDriver.Output;
using OpenTabletDriver.SystemDrivers;
using OpenTabletDriver.Tablet;

#nullable enable

namespace OpenTabletDriver.Daemon
{
    public class DriverDaemon : IDriverDaemon
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IDriver _driver;
        private readonly ICompositeDeviceHub _deviceHub;
        private readonly IAppInfo _appInfo;
        private readonly ISettingsManager _settingsManager;
        private readonly IPluginManager _pluginManager;
        private readonly IPluginFactory _pluginFactory;
        private readonly IPresetManager _presetManager;
        private readonly IUpdater? _updater;

        public DriverDaemon(
            IServiceProvider serviceProvider,
            IDriver driver,
            ICompositeDeviceHub deviceHub,
            IAppInfo appInfo,
            ISettingsManager settingsManager,
            IPluginManager pluginManager,
            IPluginFactory pluginFactory,
            IPresetManager presetManager
        )
        {
            _serviceProvider = serviceProvider;
            _driver = driver;
            _deviceHub = deviceHub;
            _appInfo = appInfo;
            _settingsManager = settingsManager;
            _pluginManager = pluginManager;
            _pluginFactory = pluginFactory;
            _presetManager = presetManager;

            _updater = serviceProvider.GetService<IUpdater>();
        }

        public async Task Initialize()
        {
            Log.Output += (sender, message) =>
            {
                LogMessages.Add(message);
                Console.WriteLine(Log.GetStringFormat(message));
                Message?.Invoke(sender, message);
            };

            _driver.InputDevicesChanged += (sender, e) => TabletsChanged?.Invoke(sender, e.Select(c => c.Configuration));
            _deviceHub.DevicesChanged += async (sender, args) =>
            {
                if (args.Additions.Any())
                {
                    await DetectTablets();
                    await SetSettings(_settingsManager.Settings);
                }
            };

            foreach (var driverInfo in DriverInfo.GetDriverInfos())
            {
                Log.Write("Detect", $"Another tablet driver found: {driverInfo.Name}", LogLevel.Warning);
                if (driverInfo.IsBlockingDriver)
                    Log.Write("Detect", $"Detection for {driverInfo.Name} tablets might be impaired", LogLevel.Warning);
                else if (driverInfo.IsSendingInput)
                    Log.Write("Detect", $"Detected input coming from {driverInfo.Name} driver", LogLevel.Error);
            }

            _pluginManager.Clean();
            await LoadPlugins();
            await DetectTablets();

            LoadUserSettings();

#if !DEBUG
            SleepDetection = new(async () =>
            {
                Log.Write(nameof(SleepDetectionThread), "Sleep detected...", LogLevel.Debug);
                await DetectTablets();
            });

            SleepDetection.Start();
#endif
        }

        public event EventHandler<LogMessage>? Message;
        public event EventHandler<DebugReportData>? DeviceReport;
        public event EventHandler<IEnumerable<TabletConfiguration>>? TabletsChanged;

        private Collection<LogMessage> LogMessages { set; get; } = new Collection<LogMessage>();
        private Collection<ITool> Tools { set; get; } = new Collection<ITool>();
#if !DEBUG
        private readonly SleepDetectionThread SleepDetection;
#endif

        private bool _debugging;

        public Task WriteMessage(LogMessage message)
        {
            Log.Write(message);
            return Task.CompletedTask;
        }

        public Task LoadPlugins()
        {
            _pluginManager.Load();
            return Task.CompletedTask;
        }

        public Task<bool> InstallPlugin(string filePath)
        {
            return Task.FromResult(_pluginManager.InstallPlugin(filePath));
        }

        public Task<bool> UninstallPlugin(string directoryPath)
        {
            var context = _pluginManager.Plugins.First(ctx => ctx.Directory.FullName == directoryPath);
            return Task.FromResult(_pluginManager.UninstallPlugin(context));
        }

        public Task<bool> DownloadPlugin(PluginMetadata metadata)
        {
            return _pluginManager.DownloadPlugin(metadata);
        }

        public Task<IEnumerable<TabletConfiguration>> GetTablets()
        {
            return Task.FromResult(_driver.InputDevices.Select(c => c.Configuration));
        }

        public async Task<IEnumerable<TabletConfiguration>> DetectTablets()
        {
            _driver.Detect();

            foreach (var tablet in _driver.InputDevices)
            {
                foreach (var dev in tablet.Endpoints)
                {
                    dev.RawReport += (_, report) => PostDebugReport(report);
                    dev.RawClone = _debugging;
                }
            }

            return await GetTablets();
        }

        public Task SetSettings(Settings? settings)
        {
            // Dispose filters that implement IDisposable interface
            foreach (var obj in _driver.InputDevices.SelectMany(d => d.OutputMode?.Elements ?? (IEnumerable<object>)Array.Empty<object>()))
                if (obj is IDisposable disposable)
                    disposable.Dispose();

            _settingsManager.Settings = settings ?? Settings.GetDefaults();

            foreach (var device in _driver.InputDevices)
            {
                var group = device.Configuration.Name;

                var profile = _settingsManager.Settings.Profiles.GetOrSetDefaults(_serviceProvider, device);
                device.OutputMode = _pluginFactory.Construct<IOutputMode>(profile.OutputMode, device);

                if (device.OutputMode != null)
                {
                    var outputModeName = _pluginFactory.GetName(profile.OutputMode);
                    Log.Write(group, $"Output mode: {outputModeName}");
                }

                if (device.OutputMode is IOutputMode outputMode)
                {
                    SetOutputModeSettings(device, outputMode, profile);
                    var bindingHandler = ActivatorUtilities.CreateInstance<BindingHandler>(
                        _serviceProvider,
                        device,
                        profile.BindingSettings
                    );
                }
            }

            Log.Write("Settings", "Driver is enabled.");

            SetToolSettings();

            return Task.CompletedTask;
        }

        public async Task ResetSettings()
        {
            await SetSettings(Settings.GetDefaults());
        }

        private async void LoadUserSettings()
        {
            _pluginManager.Clean();
            await LoadPlugins();
            await DetectTablets();

            var appdataDir = new DirectoryInfo(_appInfo.AppDataDirectory);
            if (!appdataDir.Exists)
            {
                appdataDir.Create();
                Log.Write("Settings", $"Created OpenTabletDriver application data directory: {appdataDir.FullName}");
            }

            var settingsFile = new FileInfo(_appInfo.SettingsFile);

            if (settingsFile.Exists)
            {
                var migrator = new SettingsMigrator(_serviceProvider);
                migrator.Migrate(_appInfo);

                var settings = Settings.Deserialize(settingsFile);
                if (settings != null)
                {
                    await SetSettings(settings);
                }
                else
                {
                    Log.Write("Settings", "Invalid settings detected. Attempting recovery.", LogLevel.Error);
                    settings = Settings.GetDefaults();

                    Settings.Recover(settingsFile, settings);
                    Log.Write("Settings", "Recovery complete");
                    await SetSettings(settings);
                }
            }
            else
            {
                await ResetSettings();
            }
        }

        private void SetOutputModeSettings(InputDevice dev, IOutputMode outputMode, Profile profile)
        {
            string group = dev.Configuration.Name;

            var elements = from store in profile.Filters
                           where store.Enable
                           let filter = _pluginFactory.Construct<IPositionedPipelineElement<IDeviceReport>>(store, dev)
                           where filter != null
                           select filter;

            outputMode.Elements = elements.ToList();

            if (outputMode.Elements.Any())
                Log.Write(group, $"Filters: {string.Join(", ", outputMode.Elements)}");
        }

        private void SetToolSettings()
        {
            foreach (var runningTool in Tools)
                runningTool.Dispose();
            Tools.Clear();

            foreach (var settings in _settingsManager.Settings.Tools)
            {
                if (settings.Enable == false)
                    continue;

                var tool = _pluginFactory.Construct<ITool>(settings);

                if (tool?.Initialize() ?? false)
                {
                    Tools.Add(tool);
                }
                else
                {
                    var name = _pluginFactory.GetName(settings);
                    Log.Write("Tool", $"Failed to initialize {name} tool.", LogLevel.Error);
                }
            }
        }

        public Task<Settings> GetSettings()
        {
            return Task.FromResult(_settingsManager.Settings);
        }

        public async Task SetPreset(string name)
        {
            _presetManager.Refresh();
            if (_presetManager.FindPreset(name) is Preset preset)
                await SetSettings(preset.Settings);
            else
                Log.Write("Presets", $"Unable apply preset \"{name}\" as it could not be found.");
        }

        public Task<IEnumerable<string>> GetPresets()
        {
            _presetManager.Refresh();
            return Task.FromResult(_presetManager.GetPresets().Select(p => p.Name));
        }

        public Task SavePreset(string name, Settings settings)
        {
            _presetManager.Save(name, settings);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<IDeviceEndpoint>> GetDevices()
        {
            return Task.FromResult(_deviceHub.GetDevices());
        }

        public Task<AppInfo> GetApplicationInfo()
        {
            return Task.FromResult(_appInfo as AppInfo)!;
        }

        public async Task<IDiagnosticInfo> GetDiagnostics()
        {
            var devices = await GetDevices();
            return ActivatorUtilities.CreateInstance<DiagnosticInfo>(_serviceProvider, LogMessages, devices);
        }

        public Task SetTabletDebug(bool enabled)
        {
            _debugging = enabled;
            foreach (var endpoint in _driver.InputDevices.SelectMany(d => d.Endpoints))
            {
                endpoint.RawClone = _debugging;
            }

            Log.Debug("Tablet", $"Tablet debugging is {(_debugging ? "enabled" : "disabled")}");

            return Task.CompletedTask;
        }

        public Task<string?> RequestDeviceString(int vid, int pid, int index)
        {
            var tablet = _deviceHub.GetDevices().FirstOrDefault(d => d.VendorID == vid && d.ProductID == pid);
            if (tablet == null)
                throw new IOException($"Device not found ({vid:X2}:{pid:X2})");

            return Task.FromResult(tablet.GetDeviceString((byte)index));
        }

        public Task<IEnumerable<LogMessage>> GetCurrentLog()
        {
            return Task.FromResult((IEnumerable<LogMessage>)LogMessages);
        }

        public Task<PluginSettings> GetDefaults(string path)
        {
            var type = _pluginFactory.GetPluginType(path)!;
            var settings = type.GetDefaultSettings(_serviceProvider, this);
            return Task.FromResult(settings);
        }

        public Task<TypeProxy> GetProxiedType(string typeName)
        {
            var type = _pluginManager.ExportedTypes.First(t => t.GetFullName() == typeName);
            var proxy = ActivatorUtilities.CreateInstance<TypeProxy>(_serviceProvider, type);
            return Task.FromResult(proxy);
        }

        public Task<IEnumerable<TypeProxy>> GetMatchingTypes(string typeName)
        {
            var baseType = _pluginManager.ExportedTypes.First(t => t.GetFullName() == typeName);
            var matchingTypes = from type in _pluginFactory.GetMatchingTypes(baseType)
                select ActivatorUtilities.CreateInstance<TypeProxy>(_serviceProvider, type);
            return Task.FromResult(matchingTypes);
        }

        private void PostDebugReport(IDeviceReport report)
        {
            DeviceReport?.Invoke(this, new DebugReportData(report));
        }

        public Task<bool> HasUpdate()
        {
            return _updater?.CheckForUpdates() ?? Task.FromResult(false);
        }

        public async Task<Release> GetUpdateInfo()
        {
            return await _updater?.GetRelease()!;
        }

        public Task InstallUpdate()
        {
            return _updater?.InstallUpdate() ?? Task.CompletedTask;
        }
    }
}
