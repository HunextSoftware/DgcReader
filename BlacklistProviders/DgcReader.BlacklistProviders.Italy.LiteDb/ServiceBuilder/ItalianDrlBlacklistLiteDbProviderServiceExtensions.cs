using DgcReader.BlacklistProviders.Italy.LiteDb;
using System;

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Exposes extensions allowing to register the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service
    /// </summary>
    public static class ItalianDrlBlacklistLiteDbProviderServiceExtensions
    {
        /// <summary>
        /// Registers the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service in the DI container
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static ItalianDrlBlacklistLiteDbProviderBuilder AddItalianDrlBlacklistLiteDbProvider(this IServiceCollection services)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            return new ItalianDrlBlacklistLiteDbProviderBuilder(services);
        }

        /// <summary>
        /// Registers the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service in the DI container
        /// </summary>
        /// <param name="dgcBuilder"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static DgcReaderServiceBuilder AddItalianDrlBlacklistLiteDbProvider(this DgcReaderServiceBuilder dgcBuilder)
        {
            if (dgcBuilder is null)
            {
                throw new ArgumentNullException(nameof(dgcBuilder));
            }
            dgcBuilder.Services.AddItalianDrlBlacklistLiteDbProvider();
            return dgcBuilder;
        }

        /// <summary>
        /// Registers the <see cref="ItalianDrlBlacklistLiteDbProvider"/> service in the DI container
        /// </summary>
        /// <param name="dgcBuilder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static DgcReaderServiceBuilder AddItalianDrlBlacklistLiteDbProvider(this DgcReaderServiceBuilder dgcBuilder,
            Action<ItalianDrlBlacklistLiteDbProviderOptions> configuration)
        {
            if (dgcBuilder is null)
                throw new ArgumentNullException(nameof(dgcBuilder));

            if (configuration is null)
                throw new ArgumentNullException(nameof(configuration));


            dgcBuilder.AddItalianDrlBlacklistLiteDbProvider();
            dgcBuilder.Services.Configure(configuration);


            return dgcBuilder;
        }
    }
}