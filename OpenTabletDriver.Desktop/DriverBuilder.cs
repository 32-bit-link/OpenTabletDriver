using System;
using Microsoft.Extensions.DependencyInjection;

#nullable enable

namespace OpenTabletDriver.Desktop
{
    public class DriverBuilder
    {
        private readonly DesktopServiceCollection _driverServices;
        private bool _hasBuilt;

        public DriverBuilder()
        {
            _driverServices = new DesktopServiceCollection();
        }

        public DriverBuilder(DesktopServiceCollection serviceCollection)
        {
            _driverServices = serviceCollection;
        }

        public DriverBuilder ConfigureServices(Action<IServiceCollection> configure)
        {
            if (_hasBuilt)
                throw new DriverAlreadyBuiltException();

            configure(_driverServices);
            return this;
        }

        /// <summary>
        /// Builds an instance of <see cref="Driver"/>.
        /// </summary>
        /// <param name="serviceCollection">The final service collection associated to the driver.</param>
        /// <returns>The built Driver with its lifetime managed by <paramref name="serviceCollection"/>.</returns>
        public T Build<T>(out IServiceCollection serviceCollection) where T : class, IDriver
        {
            if (_hasBuilt)
                throw new DriverAlreadyBuiltException();

            _driverServices.AddSingleton<IDriver, T>();
#if DEBUG
            var serviceProvider = _driverServices.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
#else
            var serviceProvider = _driverServices.BuildServiceProvider();
#endif
            _hasBuilt = true;
            serviceCollection = _driverServices;

            if (serviceProvider.GetService<IDriver>() is not T driver)
                throw new InvalidOperationException();

            return driver;
        }
    }
}
