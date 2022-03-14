using DgcReader.BlacklistProviders.Italy.LiteDb;
using DgcReader.Interfaces.BlacklistProviders;
using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Linq;

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Builder exposing methods for configuring the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service
    /// </summary>
    public class ItalianDrlBlacklistLiteDbProviderBuilder
    {
        static readonly Func<IServiceProvider, ItalianDrlBlacklistLiteDbProvider> _providerFactory = sp => sp.GetRequiredService<ItalianDrlBlacklistLiteDbProvider>();

        /// <summary>
        /// Returns the services collection
        /// </summary>
        private IServiceCollection Services { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="ItalianDrlBlacklistLiteDbProviderBuilder"/>
        /// </summary>
        /// <param name="services"></param>
        public ItalianDrlBlacklistLiteDbProviderBuilder(IServiceCollection services)
        {
            Services = services;

            Services.AddHttpClient();

            Services.TryAddSingleton<ItalianDrlBlacklistLiteDbProvider>();

            var sd = Services.FirstOrDefault(s => s.ServiceType == typeof(IBlacklistProvider) && s.ImplementationFactory == _providerFactory);
            if (sd == null)
                Services.AddSingleton<IBlacklistProvider, ItalianDrlBlacklistLiteDbProvider>(_providerFactory);
        }


        /// <summary>
        /// Configures the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service
        /// </summary>
        /// <param name="configuration">The delegate used to configure the options</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public ItalianDrlBlacklistLiteDbProviderBuilder Configure(Action<ItalianDrlBlacklistLiteDbProviderOptions> configuration)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            Services.Configure(configuration);

            return this;
        }
    }
}