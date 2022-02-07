using DgcReader.BlacklistProviders.Italy.LiteDb.Entities;
using DgcReader.BlacklistProviders.Italy.LiteDb.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace DgcReader.BlacklistProviders.Italy.LiteDb
{

    /// <summary>
    /// Class for manage the local blacklist database
    /// </summary>
    public class ItalianDrlBlacklistLiteDbManager
    {
        private const int MaxConsistencyTryCount = 3;

        internal static readonly string ProviderDataFolder = Path.Combine("DgcReaderData", "Blacklist", "Italy");
        private const string FileName = "italian-drl.ldb";

        private ItalianDrlBlacklistLiteDbProviderOptions Options { get; }
        private ItalianDrlBlacklistLiteDbClient Client { get; }

        private ILogger? Logger { get; }

        private SyncStatus? _syncStatus;

        /// <summary>
        /// Notify subscribers about the download progress
        /// </summary>
        public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options"></param>
        /// <param name="client"></param>
        /// <param name="logger"></param>
        public ItalianDrlBlacklistLiteDbManager(ItalianDrlBlacklistLiteDbProviderOptions options, ItalianDrlBlacklistLiteDbClient client, ILogger? logger)
        {
            Options = options;
            Client = client;
            Logger = logger;
        }

        #region Public methods
        /// <summary>
        /// Check if the specified UCVI is blacklisted
        /// </summary>
        /// <param name="ucvi"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<bool> ContainsUCVI(string ucvi, CancellationToken cancellationToken = default)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedUCVI = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(ucvi)));

                using (var db = await GetDbContext(cancellationToken))
                {
                    var col = db.GetCollection<BlacklistEntry>();
                    return col.Query().Where(r => r.HashedUCVI == hashedUCVI).Exists();
                }
            }
        }

        /// <summary>
        /// Return the currently saved SyncStatus from the DB
        /// </summary>
        /// <returns></returns>
        public async Task<SyncStatus> GetSyncStatus(bool useCache, CancellationToken cancellationToken = default)
        {
            if (useCache && _syncStatus != null)
                return _syncStatus;

            using (var db = await GetDbContext(cancellationToken))
            {
                return _syncStatus = await GetOrCreateSyncStatus(db, cancellationToken);
            }
        }

        /// <summary>
        /// Get the datetime of the latest update check
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<DateTime> GetLastCheck(CancellationToken cancellationToken = default)
        {
            var status = await GetSyncStatus(true, cancellationToken);
            return status.LastCheck;
        }

        /// <summary>
        /// Method that clears all the UCVIs downloaded resetting the sync status
        /// </summary>
        /// <returns></returns>
        public async Task<SyncStatus> ClearDb(CancellationToken cancellationToken = default)
        {
            Logger?.LogInformation("Clearing database");

            using (var db = await GetDbContext(cancellationToken))
            {
                var status = await GetOrCreateSyncStatus(db, cancellationToken);

                db.DropCollection("BlacklistEntry");

                status.CurrentVersion = 0;
                status.CurrentVersionId = "";
                status.LastChunkSaved = 0;

                var col = db.GetCollection<SyncStatus>();

                col.Update(status);

                return status;
            }
        }

        /// <summary>
        /// Updates the local blacklist if a new version is available from the remote server
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<SyncStatus> UpdateFromServer(CancellationToken cancellationToken = default)
        {
            return this.UpdateFromServer(0, cancellationToken);
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Updates the local blacklist if a new version is available from the remote server
        /// </summary>
        /// <param name="tryCount">Execution try count</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<SyncStatus> UpdateFromServer(int tryCount = 0, CancellationToken cancellationToken = default)
        {
            if (tryCount > MaxConsistencyTryCount)
            {
                throw new Exception($"Unable to get a consistent state of the Dlr after {tryCount} attempts");
            }

            // Read the status of the local drl from DB
            var localStatus = await GetSyncStatus(false, cancellationToken);

            // Get the updated status from server
            var remoteStatus = await Client.GetDrlStatus(localStatus.CurrentVersion, cancellationToken);

            if (localStatus.IsSameVersion(remoteStatus))
            {
                // Version is not outdated, checking if data from server matches local data
                if (await IsCurrentVersionConsistent(localStatus, remoteStatus))
                {
                    // Updates the last check datetime
                    await SetLastCheck(DateTime.Now, cancellationToken);
                }
                else
                {
                    Logger?.LogWarning($"Database is in an inconsistent state, clearing DB and restarting download");
                    await ClearDb();

                    return await UpdateFromServer(tryCount + 1, cancellationToken);
                }
            }
            else
            {
                if (localStatus.IsTargetVersionConsistent(remoteStatus))
                {
                    if (localStatus.HasPendingDownload() || !localStatus.CurrentVersionMatchTarget())
                    {
                        // Target version info matches the remote version. Resume download
                        Logger?.LogInformation($"Resuming download of version {remoteStatus.Version} from chunk {localStatus.LastChunkSaved}");
                    }
                }
                else
                {
                    // If target version does not match the remote info and a download was already started, the db is in an inconsistent state
                    if (localStatus.HasPendingDownload() && localStatus.AnyChunkDownloaded())
                    {
                        Logger?.LogWarning($"Database is in an inconsistent state, clearing DB and restarting download");
                        await ClearDb();
                        return await UpdateFromServer(tryCount + 1, cancellationToken);
                    }

                    // Otherwise, if the previous download was not started, updates the info with the new target and continues the download
                    if (!localStatus.CurrentVersionMatchTarget())
                        Logger?.LogWarning($"Target version {remoteStatus.Version} changed, setting new target version");
                    localStatus = await SetTargetVersion(remoteStatus, cancellationToken);
                }

                // Notify initial progress
                NotifyProgress(new DownloadProgressEventArgs(localStatus));

                // Downloading chunks
                while (localStatus.HasPendingDownload())
                {
                    Logger?.LogInformation($"Downloading chunk {localStatus.LastChunkSaved + 1} of {localStatus.TargetChunksCount} " +
                        $"for updating Drl from version {localStatus.CurrentVersion} to version {localStatus.TargetVersion}");

                    var chunk = await Client.GetDrlChunk(localStatus.CurrentVersion, localStatus.LastChunkSaved + 1, cancellationToken);

                    if (chunk.RevokedUcviList?.Length > 0 && chunk.Chunk == 1)
                    {
                        // If the update is not a differential update, clear the DB before saving the first chunk
                        Logger?.LogInformation($"The update is not incremental, clearing db before saving new data");
                        await ClearDb();
                    }

                    if (!localStatus.IsTargetVersionConsistent(chunk))
                    {
                        // Update the target version
                        // This will cause the localStatus to change, resetting the while cycle and downloading the whole new version from chunk 1
                        Logger?.LogWarning($"Target version {chunk.Version} changed, setting new target version");
                        localStatus = await SetTargetVersion(chunk, cancellationToken);

                        if (!localStatus.AnyChunkDownloaded() && chunk.Chunk == 1)
                        {
                            // If no chunks where downloaded for the inconsistent version, continue the download keeping the downloaded chunk
                            Logger?.LogWarning($"The downloaded chunk is the first for version {chunk.Version}, download can proceed");
                            localStatus = await SaveChunk(chunk, cancellationToken);
                        }
                    }
                    else
                    {
                        // Everything is good, save chunk data
                        localStatus = await SaveChunk(chunk, cancellationToken);
                    }

                    // If last chunk, apply the latest version
                    if (!localStatus.HasPendingDownload())
                    {
                        localStatus = await FinalizeUpdate(localStatus, chunk);
                        if (!localStatus.HasCurrentVersion())
                        {
                            // If failed, db is resetted and a new download attempt will be made
                            return await UpdateFromServer(tryCount + 1, cancellationToken);
                        }
                    }
                    NotifyProgress(new DownloadProgressEventArgs(localStatus));
                }

                // If finalization is missing, finalize the update
                if (!localStatus.CurrentVersionMatchTarget())
                {
                    // Consistency check:
                    // Getting updated status from server
                    remoteStatus = await Client.GetDrlStatus(localStatus.CurrentVersion, cancellationToken);

                    // If still same version, finalize
                    if (localStatus.IsTargetVersion(remoteStatus))
                    {
                        localStatus = await FinalizeUpdate(localStatus, remoteStatus);
                        NotifyProgress(new DownloadProgressEventArgs(localStatus));

                        if (!localStatus.HasCurrentVersion())
                        {
                            // If failed, db is resetted and a new download attempt will be made
                            return await UpdateFromServer(tryCount + 1, cancellationToken);
                        }
                    }
                }
            }

            return localStatus;
        }


        /// <summary>
        /// Update the datetime of last check for new versions
        /// </summary>
        /// <param name="lastCheck"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<SyncStatus> SetLastCheck(DateTime lastCheck, CancellationToken cancellationToken = default)
        {
            using (var db = await GetDbContext(cancellationToken))
            {
                var status = await GetOrCreateSyncStatus(db, cancellationToken);

                var col = db.GetCollection<SyncStatus>();

                status.LastCheck = lastCheck;
                col.Update(status);

                return status;
            }
        }

        /// <summary>
        /// Updates the target version info with the specified entry
        /// </summary>
        /// <param name="statusEntry"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<SyncStatus> SetTargetVersion(IDrlVersionInfo statusEntry, CancellationToken cancellationToken = default)
        {
            Logger?.LogInformation($"Updating target version to {statusEntry.Version} ({statusEntry.Id})");
            using (var db = await GetDbContext(cancellationToken))
            {
                var status = await GetOrCreateSyncStatus(db);

                if (!status.IsTargetVersionConsistent(statusEntry))
                {
                    // Copy target data to current status
                    SetTargetData(status, statusEntry);

                    var col = db.GetCollection<SyncStatus>();
                    col.Update(status);
                }

                return status;
            }
        }

        /// <summary>
        /// Saves the provided chunk of data, adding or deleting blacklist entries and updating the SyncStatus
        /// </summary>
        /// <param name="chunkData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<SyncStatus> SaveChunk(DrlChunkData chunkData, CancellationToken cancellationToken = default)
        {
            Logger?.LogInformation($"Saving chunk {chunkData.Chunk} of {chunkData.TotalChunks} for Drl version {chunkData.Version}");
            using (var db = await GetDbContext())
            {
                var status = await GetOrCreateSyncStatus(db, cancellationToken);
                if (!status.IsTargetVersionConsistent(chunkData))
                {

                    if (!status.AnyChunkDownloaded() && chunkData.Chunk == 1)
                    {
                        // If no chunks where downloaded for the inconsistent version, continue the download keeping the downloaded chunk
                        Logger?.LogWarning($"The downloaded chunk is the first for version {chunkData.Version}, download can proceed");

                        // Updating target with data from chunk
                        SetTargetData(status, chunkData);
                    }
                    else
                    {
                        // Version is changed and at least one chunk was downloaded, restart download of chunks targeting the new version
                        Logger?.LogWarning($"Version changed to {chunkData.Version} while downloading chunks for version {status.TargetVersion}. Restarting the download for the new version detected");

                        // Updating target with data from chunk
                        SetTargetData(status, chunkData);

                        var col = db.GetCollection<SyncStatus>();
                        col.Update(status);
                        return status;
                    }
                }

                // Full list of additions
                if (chunkData.RevokedUcviList != null)
                {
                    await AddMissingUcvis(db, chunkData.RevokedUcviList, cancellationToken);
                }
                else if (chunkData.Delta != null)
                {
                    // Add the new UCVIs
                    await AddMissingUcvis(db, chunkData.Delta.Insertions, cancellationToken);

                    // Removes deleted UCVIs
                    await RemoveUcvis(db, chunkData.Delta.Deletions, cancellationToken);
                }

                // Update status
                status.TargetTotalNumberUCVI = chunkData.TotalNumberUCVI;
                status.LastChunkSaved = chunkData.Chunk;

                var colup = db.GetCollection<SyncStatus>();
                colup.Update(status);

                return status;
            }
        }

        private async Task<SyncStatus> FinalizeUpdate(SyncStatus status, IDrlVersionInfo chunkData, CancellationToken cancellationToken = default)
        {
            // If last chunk, apply the latest version
            if (status.IsTargetVersionConsistent(chunkData) &&
                !status.HasPendingDownload())
            {
                using (var db = await GetDbContext())
                {
                    var col = db.GetCollection<BlacklistEntry>();
                    var count = col.Count();
                    if (count == status.TargetTotalNumberUCVI)
                    {
                        Logger?.LogInformation($"Finalizing update for version {status.TargetVersion}");
                        // Apply target version as current version
                        status.CurrentVersion = status.TargetVersion;
                        status.CurrentVersionId = status.TargetVersionId;
                        status.LastCheck = DateTime.Now;

                        var cls = db.GetCollection<SyncStatus>();
                        cls.Update(status);

                        Logger?.LogInformation($"Version {status.TargetVersion} finalized for {count} total blacklist entries");

                        return status;
                    }
                    else
                    {
                        Logger?.LogWarning($"Consistency check failed when finalizing update for version {status.TargetVersion}: " +
                            $"expected count {status.TargetTotalNumberUCVI} differs from actual count {count}. Resetting DB");
                        return await ClearDb();
                    }

                }

            }
            return status;
        }

        /// <summary>
        /// Copy Version info from status to the Target properties of current resetting the LastChunkSaved property
        /// </summary>
        /// <param name="current"></param>
        /// <param name="status"></param>
        private void SetTargetData(SyncStatus current, IDrlVersionInfo status)
        {
            // Reset chunk download status
            current.LastChunkSaved = 0;

            // Copy target data to current status
            current.TargetVersion = status.Version;
            current.TargetVersionId = status.Id;
            current.TargetTotalNumberUCVI = status.TotalNumberUCVI;
            current.TargetChunksCount = status.TotalChunks;
            current.TargetChunkSize = status.SingleChunkSize;
        }

        /// <summary>
        /// Check if the remote version equals the currently stored version, and matches the size
        /// </summary>
        /// <param name="remoteStatus"></param>
        /// <param name="localStatus"></param>
        /// <returns></returns>
        private async Task<bool> IsCurrentVersionConsistent(SyncStatus localStatus, IDrlVersionInfo remoteStatus)
        {
            if (!localStatus.IsSameVersion(remoteStatus))
                return false;
            // Check the actual count on the DB
            var currentCount = await GetActualBlacklistCount();
            return remoteStatus.TotalNumberUCVI == currentCount;
        }

        /// <summary>
        /// Get the actual count of entries in the blacklist
        /// </summary>
        /// <returns></returns>
        private async Task<int> GetActualBlacklistCount()
        {
            using (var db = await GetDbContext())
            {
                var col = db.GetCollection<BlacklistEntry>();
                return col.Count();
            }
        }

        private async Task<SyncStatus> GetOrCreateSyncStatus(LiteDatabase db, CancellationToken cancellationToken = default)
        {
            var col = db.GetCollection<SyncStatus>();

            var syncStatus = col.Query()
                    .OrderByDescending(r => r.CurrentVersion)
                    .FirstOrDefault();

            if (syncStatus == null)
            {
                syncStatus = new SyncStatus()
                {
                    CurrentVersion = 0,
                    TargetTotalNumberUCVI = 0,
                };
                col.Insert(syncStatus);
            }
            return syncStatus;
        }

        private async Task<LiteDatabase> GetDbContext(CancellationToken cancellationToken = default)
        {
            // Check directory
            if (!Directory.Exists(GetCacheFolder()))
                Directory.CreateDirectory(GetCacheFolder());

            //TODO: Verificare se da eliminare
            //// Configuring db context options
            //if (!Options.DbContext.IsConfigured)
            //{
            //    var connString = $"Data Source={GetCacheFilePath()}";
            //    Options.DbContext.UseSqlite(connString);
            //}
            var db = new LiteDatabase(GetCacheFilePath());

            return db;
        }

        private string GetCacheFolder() => Path.Combine(Options.BasePath, ProviderDataFolder);
        private string GetCacheFilePath() => Path.Combine(GetCacheFolder(), FileName);

        /// <summary>
        /// Add the missing UCVIs passed to the context tracked entries in Add
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="ucvis"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task AddMissingUcvis(LiteDatabase db, string[] ucvis, CancellationToken cancellationToken = default)
        {
            if (ucvis?.Any() != true)
                return;

            int pageSize = 1000;
            var pages = (int)Math.Ceiling(((decimal)ucvis.Length) / pageSize);

            Logger?.LogInformation($"Adding {ucvis.Length} UCVIs to the blacklist");

            for (int i = 0; i < pages; i++)
            {
                var pageData = ucvis.Skip(i * pageSize).Take(pageSize).ToArray();
                var col = db.GetCollection<BlacklistEntry>();

                col.EnsureIndex(r => r.HashedUCVI);

                var existing = col.Query()
                    .Where(r => pageData.Contains(r.HashedUCVI))
                    .Select(r => r.HashedUCVI).ToArray();

                if (existing.Any())
                {
                    Logger?.LogWarning($"{existing.Count()} UCVIs entries already in database, skipping add for these entries");
                }

                var newEntries = pageData.Except(existing).Distinct().Select(r => new BlacklistEntry() { HashedUCVI = r }).ToArray();
                Logger?.LogDebug($"Adding {newEntries.Count()} of {ucvis.Length} (page {i + 1} of {pages}) UCVIs to the blacklist");
                col.InsertBulk(newEntries);
            }
        }

        /// <summary>
        /// Add the missing UCVIs passed to the context tracked entries in Add
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="ucvis"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task RemoveUcvis(LiteDatabase db, string[] ucvis, CancellationToken cancellationToken = default)
        {
            if (ucvis?.Any() != true)
                return;

            int pageSize = 1000;
            var pages = (int)Math.Ceiling(((decimal)ucvis.Length) / pageSize);

            Logger?.LogInformation($"Removing {ucvis.Length} UCVIs from the blacklist");

            for (int i = 0; i < pages; i++)
            {
                var pageData = ucvis.Skip(i * pageSize).Take(pageSize).ToArray();

                var col = db.GetCollection<BlacklistEntry>();
                var deleting = col.Query().Where(r => pageData.Contains(r.HashedUCVI)).ToArray();

                if (deleting.Length != pageData.Length)
                {
                    Logger?.LogWarning($"Found {deleting.Length} out of {pageData.Length} deleted UCVIs entries");
                }

                Logger?.LogDebug($"Removing {deleting.Count()} of {ucvis.Length} (page {i + 1} of {pages}) UCVIs from the blacklist");

                foreach (var d in deleting)
                {
                    col.DeleteMany(r => r.HashedUCVI == d.HashedUCVI);
                }
            }
        }

        private void NotifyProgress(DownloadProgressEventArgs eventArgs)
        {
            try
            {
                DownloadProgressChanged?.Invoke(this, eventArgs);
            }
            catch (Exception e)
            {
                Logger?.LogError(e, $"Error while notifying download progress: {e.Message}");
            }
        }

        #endregion
    }
}