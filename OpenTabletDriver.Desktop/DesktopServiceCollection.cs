using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using OpenTabletDriver.ComponentProviders;
using OpenTabletDriver.Components;
using OpenTabletDriver.Configurations;
using OpenTabletDriver.Desktop.Diagnostics;
using OpenTabletDriver.Desktop.Interop;
using OpenTabletDriver.Desktop.Reflection;
using OpenTabletDriver.Devices;
using OpenTabletDriver.Interop;

#nullable enable

namespace OpenTabletDriver.Desktop
{
    using static ServiceDescriptor;

    public class DesktopServiceCollection : ServiceCollection
    {
        private static readonly IEnumerable<ServiceDescriptor> RequiredServices = new[]
        {
            // Core Services
            Singleton<IDriver, Driver>(),
            Singleton<IReportParserProvider, ReportParserProvider>(),
            Singleton<IDeviceHubsProvider, DeviceHubsProvider>(p => new DeviceHubsProvider(p)),
            Singleton<ICompositeDeviceHub, RootHub>(RootHub.WithProvider),
            Singleton<IDeviceConfigurationProvider, DesktopDeviceConfigurationProvider>(),
            Singleton<IReportParserProvider, DesktopReportParserProvider>(),
            // Desktop Services
            Transient<EnvironmentDictionary, EnvironmentDictionary>(),
            Singleton<IPluginManager, PluginManager>(),
            Singleton<ISettingsManager, SettingsManager>(),
            Singleton<IPresetManager, PresetManager>(),
            Transient<IPluginFactory, PluginFactory>(),
            // TODO: null updater for Linux
        };

        public DesktopServiceCollection()
        {
            this.AddServices(RequiredServices);
        }

        protected DesktopServiceCollection(IEnumerable<ServiceDescriptor> overridingServices) : this()
        {
            this.AddServices(overridingServices);
        }

        public static DesktopServiceCollection GetPlatformServiceCollection()
        {
            return SystemInterop.CurrentPlatform switch
            {
                SystemPlatform.Windows => new DesktopWindowsServiceCollection(),
                SystemPlatform.Linux => new DesktopLinuxServiceCollection(),
                SystemPlatform.MacOS => new DesktopMacOSServiceCollection(),
                _ => throw new PlatformNotSupportedException("This platform is not supported by OpenTabletDriver.")
            };
        }
    }
}
