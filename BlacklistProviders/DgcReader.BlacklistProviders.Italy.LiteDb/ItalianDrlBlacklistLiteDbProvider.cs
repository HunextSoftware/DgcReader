using DgcReader.Interfaces.BlacklistProviders;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using DgcReader.Providers.Abstractions;
using DgcReader.BlacklistProviders.Italy.LiteDb.Entities;
using Microsoft.Extensions.Options;
using DgcReader.Interfaces.Deserializers;
using DgcReader.Deserializers.Italy;

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace DgcReader.BlacklistProviders.Italy.LiteDb
{
    /// <summary>
    /// Blacklist provider using the Italian backend
    /// </summary>
    public class ItalianDrlBlacklistLiteDbProvider : IBlacklistProvider, ICustomDeserializerDependentService, IDisposable
    {
        private readonly ItalianDrlBlacklistLiteDbProviderOptions Options;
        private readonly ILogger<ItalianDrlBlacklistLiteDbProvider>? Logger;
        private readonly ItalianDrlBlacklistLiteDbManager BlacklistManager;
        private readonly SingleTaskRunner<SyncStatus> RefreshBlacklistTaskRunner;
        private DateTime LastRefreshAttempt;

        /// <inheritdoc cref="ItalianDrlBlacklistLiteDbManager.DownloadProgressChanged"/>
        public event EventHandler<DownloadProgressEventArgs> DownloadProgressChanged
        {
            add { BlacklistManager.DownloadProgressChanged += value; }
            remove { BlacklistManager.DownloadProgressChanged -= value; }
        }

        #region Constructor
        /// <summary>
        /// Constructor for the provider
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        public ItalianDrlBlacklistLiteDbProvider(HttpClient httpClient,
            IOptions<ItalianDrlBlacklistLiteDbProviderOptions>? options = null,
            ILogger<ItalianDrlBlacklistLiteDbProvider>? logger = null)
        {
            Options = options?.Value ?? new ItalianDrlBlacklistLiteDbProviderOptions();
            Logger = logger;

            var drlClient = new ItalianDrlBlacklistLiteDbClient(httpClient, logger);
            BlacklistManager = new ItalianDrlBlacklistLiteDbManager(Options, drlClient, logger);
            RefreshBlacklistTaskRunner = new SingleTaskRunner<SyncStatus>(async ct =>
            {
                LastRefreshAttempt = DateTime.Now;
                return await BlacklistManager.UpdateFromServer(ct);
            }, Logger);
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="ItalianDrlBlacklistLiteDbProvider"/>
        /// whithout using the DI mechanism. Useful for legacy applications
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        /// <returns></returns>
        public static ItalianDrlBlacklistLiteDbProvider Create(HttpClient httpClient,
            ItalianDrlBlacklistLiteDbProviderOptions? options = null,
            ILogger<ItalianDrlBlacklistLiteDbProvider>? logger = null)
        {
            return new ItalianDrlBlacklistLiteDbProvider(httpClient,
                options == null ? null : Microsoft.Extensions.Options.Options.Create(options),
                logger);
        }
        #endregion

        #region Implementation of IBlacklistProvider

        /// <inheritdoc/>
        public async Task<bool> IsBlacklisted(string certificateIdentifier, CancellationToken cancellationToken = default)
        {
            // Get latest check datetime
            var status = await BlacklistManager.GetSyncStatus(true, cancellationToken);


            if (status.LastCheck.Add(Options.MaxFileAge) < DateTime.Now)
            {
                // MaxFileAge expired

                var refreshTask = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);

                // Wait for the task to complete
                await refreshTask;
            }
            else if (status.LastCheck.Add(Options.RefreshInterval) < DateTime.Now ||
                status.HasPendingDownload())
            {
                // Normal expiration

                // If min refresh expired
                if (LastRefreshAttempt.Add(Options.MinRefreshInterval) < DateTime.Now)
                {
                    var refreshTask = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);
                    if (!Options.UseAvailableValuesWhileRefreshing)
                    {
                        // Wait for the task to complete
                        await refreshTask;
                    }
                }
            }

            return await BlacklistManager.ContainsUCVI(certificateIdentifier, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task RefreshBlacklist(CancellationToken cancellationToken = default)
        {
            var task = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);
            await task;
        }
        #endregion

        #region Implementation of ICustomDeserializerDependentService
        /// <inheritdoc/>
        public IDgcDeserializer GetCustomDeserializer() => new ItalianDgcDeserializer();
        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            RefreshBlacklistTaskRunner.Dispose();
        }
    }
}