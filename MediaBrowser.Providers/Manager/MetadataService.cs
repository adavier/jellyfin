#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Manager
{
    public abstract class MetadataService<TItemType, TIdType> : IMetadataService
        where TItemType : BaseItem, IHasLookupInfo<TIdType>, new()
        where TIdType : ItemLookupInfo, new()
    {
        protected MetadataService(IServerConfigurationManager serverConfigurationManager, ILogger<MetadataService<TItemType, TIdType>> logger, IProviderManager providerManager, IFileSystem fileSystem, ILibraryManager libraryManager)
        {
            ServerConfigurationManager = serverConfigurationManager;
            Logger = logger;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;
            ImageProvider = new ItemImageProvider(Logger, ProviderManager, FileSystem);
        }

        protected ItemImageProvider ImageProvider { get; }

        protected IServerConfigurationManager ServerConfigurationManager { get; }

        protected ILogger<MetadataService<TItemType, TIdType>> Logger { get; }

        protected IProviderManager ProviderManager { get; }

        protected IFileSystem FileSystem { get; }

        protected ILibraryManager LibraryManager { get; }

        protected virtual bool EnableUpdatingPremiereDateFromChildren => false;

        protected virtual bool EnableUpdatingGenresFromChildren => false;

        protected virtual bool EnableUpdatingStudiosFromChildren => false;

        protected virtual bool EnableUpdatingOfficialRatingFromChildren => false;

        public virtual int Order => 0;

        private FileSystemMetadata TryGetFile(string path, IDirectoryService directoryService)
        {
            try
            {
                return directoryService.GetFile(path);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting file {Path}", path);
                return null;
            }
        }

        public async Task<ItemUpdateType> RefreshMetadata(BaseItem item, MetadataRefreshOptions refreshOptions, CancellationToken cancellationToken)
        {
            var itemOfType = (TItemType)item;

            var updateType = ItemUpdateType.None;
            var requiresRefresh = false;

            var libraryOptions = LibraryManager.GetLibraryOptions(item);

            if (!requiresRefresh && libraryOptions.AutomaticRefreshIntervalDays > 0 && (DateTime.UtcNow - item.DateLastRefreshed).TotalDays >= libraryOptions.AutomaticRefreshIntervalDays)
            {
                requiresRefresh = true;
            }

            if (!requiresRefresh && refreshOptions.MetadataRefreshMode != MetadataRefreshMode.None)
            {
                // TODO: If this returns true, should we instead just change metadata refresh mode to Full?
                requiresRefresh = item.RequiresRefresh();

                if (requiresRefresh)
                {
                    Logger.LogDebug("Refreshing {0} {1} because item.RequiresRefresh() returned true", typeof(TItemType).Name, item.Path ?? item.Name);
                }
            }

            var localImagesFailed = false;

            var allImageProviders = ((ProviderManager)ProviderManager).GetImageProviders(item, refreshOptions).ToList();

            // Start by validating images
            try
            {
                // Always validate images and check for new locally stored ones.
                if (ImageProvider.ValidateImages(item, allImageProviders.OfType<ILocalImageProvider>(), refreshOptions.DirectoryService))
                {
                    updateType |= ItemUpdateType.ImageUpdate;
                }
            }
            catch (Exception ex)
            {
                localImagesFailed = true;
                Logger.LogError(ex, "Error validating images for {0}", item.Path ?? item.Name ?? "Unknown name");
            }

            var metadataResult = new MetadataResult<TItemType>
            {
                Item = itemOfType
            };

            bool hasRefreshedMetadata = true;
            bool hasRefreshedImages = true;
            var isFirstRefresh = item.DateLastRefreshed == default;

            // Next run metadata providers
            if (refreshOptions.MetadataRefreshMode != MetadataRefreshMode.None)
            {
                var providers = GetProviders(item, libraryOptions, refreshOptions, isFirstRefresh, requiresRefresh)
                    .ToList();

                if (providers.Count > 0 || isFirstRefresh || requiresRefresh)
                {
                    if (item.BeforeMetadataRefresh(refreshOptions.ReplaceAllMetadata))
                    {
                        updateType |= ItemUpdateType.MetadataImport;
                    }
                }

                if (providers.Count > 0)
                {
                    var id = itemOfType.GetLookupInfo();

                    if (refreshOptions.SearchResult != null)
                    {
                        ApplySearchResult(id, refreshOptions.SearchResult);
                    }

                    // await FindIdentities(id, cancellationToken).ConfigureAwait(false);
                    id.IsAutomated = refreshOptions.IsAutomated;

                    var result = await RefreshWithProviders(metadataResult, id, refreshOptions, providers, ImageProvider, cancellationToken).ConfigureAwait(false);

                    updateType |= result.UpdateType;
                    if (result.Failures > 0)
                    {
                        hasRefreshedMetadata = false;
                    }
                }
            }

            // Next run remote image providers, but only if local image providers didn't throw an exception
            if (!localImagesFailed && refreshOptions.ImageRefreshMode != MetadataRefreshMode.ValidationOnly)
            {
                var providers = GetNonLocalImageProviders(item, allImageProviders, refreshOptions).ToList();

                if (providers.Count > 0)
                {
                    var result = await ImageProvider.RefreshImages(itemOfType, libraryOptions, providers, refreshOptions, cancellationToken).ConfigureAwait(false);

                    updateType |= result.UpdateType;
                    if (result.Failures > 0)
                    {
                        hasRefreshedImages = false;
                    }
                }
            }

            var beforeSaveResult = BeforeSave(itemOfType, isFirstRefresh || refreshOptions.ReplaceAllMetadata || refreshOptions.MetadataRefreshMode == MetadataRefreshMode.FullRefresh || requiresRefresh || refreshOptions.ForceSave, updateType);
            updateType |= beforeSaveResult;

            // Save if changes were made, or it's never been saved before
            if (refreshOptions.ForceSave || updateType > ItemUpdateType.None || isFirstRefresh || refreshOptions.ReplaceAllMetadata || requiresRefresh)
            {
                if (item.IsFileProtocol)
                {
                    var file = TryGetFile(item.Path, refreshOptions.DirectoryService);
                    if (file != null)
                    {
                        item.DateModified = file.LastWriteTimeUtc;
                    }
                }

                // If any of these properties are set then make sure the updateType is not None, just to force everything to save
                if (refreshOptions.ForceSave || refreshOptions.ReplaceAllMetadata)
                {
                    updateType |= ItemUpdateType.MetadataDownload;
                }

                if (hasRefreshedMetadata && hasRefreshedImages)
                {
                    item.DateLastRefreshed = DateTime.UtcNow;
                }
                else
                {
                    item.DateLastRefreshed = default;
                }

                // Save to database
                await SaveItemAsync(metadataResult, updateType, cancellationToken).ConfigureAwait(false);
            }

            await AfterMetadataRefresh(itemOfType, refreshOptions, cancellationToken).ConfigureAwait(false);

            return updateType;
        }

        private void ApplySearchResult(ItemLookupInfo lookupInfo, RemoteSearchResult result)
        {
            // Episode and Season do not support Identify, so the search results are the Series'
            switch (lookupInfo)
            {
                case EpisodeInfo episodeInfo:
                    episodeInfo.SeriesProviderIds = result.ProviderIds;
                    episodeInfo.ProviderIds.Clear();
                    break;
                case SeasonInfo seasonInfo:
                    seasonInfo.SeriesProviderIds = result.ProviderIds;
                    seasonInfo.ProviderIds.Clear();
                    break;
                default:
                    lookupInfo.ProviderIds = result.ProviderIds;
                    lookupInfo.Name = result.Name;
                    lookupInfo.Year = result.ProductionYear;
                    break;
            }
        }

        protected async Task SaveItemAsync(MetadataResult<TItemType> result, ItemUpdateType reason, CancellationToken cancellationToken)
        {
            if (result.Item.SupportsPeople && result.People != null)
            {
                var baseItem = result.Item;

                await LibraryManager.UpdatePeopleAsync(baseItem, result.People, cancellationToken).ConfigureAwait(false);
            }

            await result.Item.UpdateToRepositoryAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        protected virtual Task AfterMetadataRefresh(TItemType item, MetadataRefreshOptions refreshOptions, CancellationToken cancellationToken)
        {
            item.AfterMetadataRefresh();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Before the save.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="isFullRefresh">if set to <c>true</c> [is full refresh].</param>
        /// <param name="currentUpdateType">Type of the current update.</param>
        /// <returns>ItemUpdateType.</returns>
        private ItemUpdateType BeforeSave(TItemType item, bool isFullRefresh, ItemUpdateType currentUpdateType)
        {
            var updateType = BeforeSaveInternal(item, isFullRefresh, currentUpdateType);

            updateType |= item.OnMetadataChanged();

            return updateType;
        }

        protected virtual ItemUpdateType BeforeSaveInternal(TItemType item, bool isFullRefresh, ItemUpdateType updateType)
        {
            if (EnableUpdateMetadataFromChildren(item, isFullRefresh, updateType))
            {
                if (isFullRefresh || updateType > ItemUpdateType.None)
                {
                    var children = GetChildrenForMetadataUpdates(item);

                    updateType = UpdateMetadataFromChildren(item, children, isFullRefresh, updateType);
                }
            }

            var presentationUniqueKey = item.CreatePresentationUniqueKey();
            if (!string.Equals(item.PresentationUniqueKey, presentationUniqueKey, StringComparison.Ordinal))
            {
                item.PresentationUniqueKey = presentationUniqueKey;
                updateType |= ItemUpdateType.MetadataImport;
            }

            return updateType;
        }

        protected virtual bool EnableUpdateMetadataFromChildren(TItemType item, bool isFullRefresh, ItemUpdateType currentUpdateType)
        {
            if (isFullRefresh || currentUpdateType > ItemUpdateType.None)
            {
                if (EnableUpdatingPremiereDateFromChildren || EnableUpdatingGenresFromChildren || EnableUpdatingStudiosFromChildren || EnableUpdatingOfficialRatingFromChildren)
                {
                    return true;
                }

                if (item is Folder folder)
                {
                    return folder.SupportsDateLastMediaAdded || folder.SupportsCumulativeRunTimeTicks;
                }
            }

            return false;
        }

        protected virtual IList<BaseItem> GetChildrenForMetadataUpdates(TItemType item)
        {
            if (item is Folder folder)
            {
                return folder.GetRecursiveChildren();
            }

            return Array.Empty<BaseItem>();
        }

        protected virtual ItemUpdateType UpdateMetadataFromChildren(TItemType item, IList<BaseItem> children, bool isFullRefresh, ItemUpdateType currentUpdateType)
        {
            var updateType = ItemUpdateType.None;

            if (isFullRefresh || currentUpdateType > ItemUpdateType.None)
            {
                updateType |= UpdateCumulativeRunTimeTicks(item, children);
                updateType |= UpdateDateLastMediaAdded(item, children);

                if (EnableUpdatingPremiereDateFromChildren)
                {
                    updateType |= UpdatePremiereDate(item, children);
                }

                if (EnableUpdatingGenresFromChildren)
                {
                    updateType |= UpdateGenres(item, children);
                }

                if (EnableUpdatingStudiosFromChildren)
                {
                    updateType |= UpdateStudios(item, children);
                }

                if (EnableUpdatingOfficialRatingFromChildren)
                {
                    updateType |= UpdateOfficialRating(item, children);
                }
            }

            return updateType;
        }

        private ItemUpdateType UpdateCumulativeRunTimeTicks(TItemType item, IList<BaseItem> children)
        {
            if (item is Folder folder && folder.SupportsCumulativeRunTimeTicks)
            {
                long ticks = 0;

                foreach (var child in children)
                {
                    if (!child.IsFolder)
                    {
                        ticks += child.RunTimeTicks ?? 0;
                    }
                }

                if (!folder.RunTimeTicks.HasValue || folder.RunTimeTicks.Value != ticks)
                {
                    folder.RunTimeTicks = ticks;
                    return ItemUpdateType.MetadataEdit;
                }
            }

            return ItemUpdateType.None;
        }

        private ItemUpdateType UpdateDateLastMediaAdded(TItemType item, IList<BaseItem> children)
        {
            var updateType = ItemUpdateType.None;

            var folder = item as Folder;
            if (folder != null && folder.SupportsDateLastMediaAdded)
            {
                var dateLastMediaAdded = DateTime.MinValue;
                var any = false;

                foreach (var child in children)
                {
                    if (!child.IsFolder)
                    {
                        var childDateCreated = child.DateCreated;
                        if (childDateCreated > dateLastMediaAdded)
                        {
                            dateLastMediaAdded = childDateCreated;
                        }

                        any = true;
                    }
                }

                if ((!folder.DateLastMediaAdded.HasValue && any) || folder.DateLastMediaAdded != dateLastMediaAdded)
                {
                    folder.DateLastMediaAdded = dateLastMediaAdded;
                    updateType = ItemUpdateType.MetadataImport;
                }
            }

            return updateType;
        }

        private ItemUpdateType UpdatePremiereDate(TItemType item, IList<BaseItem> children)
        {
            var updateType = ItemUpdateType.None;

            if (children.Count == 0)
            {
                return updateType;
            }

            var date = children.Select(i => i.PremiereDate ?? DateTime.MaxValue).Min();

            var originalPremiereDate = item.PremiereDate;
            var originalProductionYear = item.ProductionYear;

            if (date > DateTime.MinValue && date < DateTime.MaxValue)
            {
                item.PremiereDate = date;
                item.ProductionYear = date.Year;
            }
            else
            {
                var year = children.Select(i => i.ProductionYear ?? 0).Min();

                if (year > 0)
                {
                    item.ProductionYear = year;
                }
            }

            if ((originalPremiereDate ?? DateTime.MinValue) != (item.PremiereDate ?? DateTime.MinValue) ||
                (originalProductionYear ?? -1) != (item.ProductionYear ?? -1))
            {
                updateType |= ItemUpdateType.MetadataEdit;
            }

            return updateType;
        }

        private ItemUpdateType UpdateGenres(TItemType item, IList<BaseItem> children)
        {
            var updateType = ItemUpdateType.None;

            if (!item.LockedFields.Contains(MetadataField.Genres))
            {
                var currentList = item.Genres;

                item.Genres = children.SelectMany(i => i.Genres)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (currentList.Length != item.Genres.Length || !currentList.OrderBy(i => i).SequenceEqual(item.Genres.OrderBy(i => i), StringComparer.OrdinalIgnoreCase))
                {
                    updateType |= ItemUpdateType.MetadataEdit;
                }
            }

            return updateType;
        }

        private ItemUpdateType UpdateStudios(TItemType item, IList<BaseItem> children)
        {
            var updateType = ItemUpdateType.None;

            if (!item.LockedFields.Contains(MetadataField.Studios))
            {
                var currentList = item.Studios;

                item.Studios = children.SelectMany(i => i.Studios)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (currentList.Length != item.Studios.Length || !currentList.OrderBy(i => i).SequenceEqual(item.Studios.OrderBy(i => i), StringComparer.OrdinalIgnoreCase))
                {
                    updateType |= ItemUpdateType.MetadataEdit;
                }
            }

            return updateType;
        }

        private ItemUpdateType UpdateOfficialRating(TItemType item, IList<BaseItem> children)
        {
            var updateType = ItemUpdateType.None;

            if (!item.LockedFields.Contains(MetadataField.OfficialRating))
            {
                if (item.UpdateRatingToItems(children))
                {
                    updateType |= ItemUpdateType.MetadataEdit;
                }
            }

            return updateType;
        }

        /// <summary>
        /// Gets the providers.
        /// </summary>
        /// <param name="item">A media item.</param>
        /// <param name="libraryOptions">The LibraryOptions to use.</param>
        /// <param name="options">The MetadataRefreshOptions to use.</param>
        /// <param name="isFirstRefresh">Specifies first refresh mode.</param>
        /// <param name="requiresRefresh">Specifies refresh mode.</param>
        /// <returns>IEnumerable{`0}.</returns>
        protected IEnumerable<IMetadataProvider> GetProviders(BaseItem item, LibraryOptions libraryOptions, MetadataRefreshOptions options, bool isFirstRefresh, bool requiresRefresh)
        {
            // Get providers to refresh
            var providers = ((ProviderManager)ProviderManager).GetMetadataProviders<TItemType>(item, libraryOptions).ToList();

            var metadataRefreshMode = options.MetadataRefreshMode;

            // Run all if either of these flags are true
            var runAllProviders = options.ReplaceAllMetadata ||
                metadataRefreshMode == MetadataRefreshMode.FullRefresh ||
                (isFirstRefresh && metadataRefreshMode >= MetadataRefreshMode.Default) ||
                (requiresRefresh && metadataRefreshMode >= MetadataRefreshMode.Default);

            if (!runAllProviders)
            {
                var providersWithChanges = providers
                    .Where(i =>
                    {
                        if (i is IHasItemChangeMonitor hasFileChangeMonitor)
                        {
                            return HasChanged(item, hasFileChangeMonitor, options.DirectoryService);
                        }

                        return false;
                    })
                    .ToList();

                if (providersWithChanges.Count == 0)
                {
                    providers = new List<IMetadataProvider<TItemType>>();
                }
                else
                {
                    var anyRemoteProvidersChanged = providersWithChanges.OfType<IRemoteMetadataProvider>()
                        .Any();

                    var anyLocalProvidersChanged = providersWithChanges.OfType<ILocalMetadataProvider>()
                        .Any();

                    var anyLocalPreRefreshProvidersChanged = providersWithChanges.OfType<IPreRefreshProvider>()
                        .Any();

                    providers = providers.Where(i =>
                    {
                        // If any provider reports a change, always run local ones as well
                        if (i is ILocalMetadataProvider)
                        {
                            return anyRemoteProvidersChanged || anyLocalProvidersChanged || anyLocalPreRefreshProvidersChanged;
                        }

                        // If any remote providers changed, run them all so that priorities can be honored
                        if (i is IRemoteMetadataProvider)
                        {
                            if (options.MetadataRefreshMode == MetadataRefreshMode.ValidationOnly)
                            {
                                return false;
                            }

                            return anyRemoteProvidersChanged;
                        }

                        // Run custom refresh providers if they report a change or any remote providers change
                        return anyRemoteProvidersChanged || providersWithChanges.Contains(i);
                    }).ToList();
                }
            }

            return providers;
        }

        protected virtual IEnumerable<IImageProvider> GetNonLocalImageProviders(BaseItem item, IEnumerable<IImageProvider> allImageProviders, ImageRefreshOptions options)
        {
            // Get providers to refresh
            var providers = allImageProviders.Where(i => i is not ILocalImageProvider);

            var dateLastImageRefresh = item.DateLastRefreshed;

            // Run all if either of these flags are true
            var runAllProviders = options.ImageRefreshMode == MetadataRefreshMode.FullRefresh || dateLastImageRefresh == default(DateTime);

            if (!runAllProviders)
            {
                providers = providers
                    .Where(i =>
                    {
                        if (i is IHasItemChangeMonitor hasFileChangeMonitor)
                        {
                            return HasChanged(item, hasFileChangeMonitor, options.DirectoryService);
                        }

                        return false;
                    });
            }

            return providers;
        }

        public bool CanRefresh(BaseItem item)
        {
            return item is TItemType;
        }

        public bool CanRefreshPrimary(Type type)
        {
            return type == typeof(TItemType);
        }

        protected virtual async Task<RefreshResult> RefreshWithProviders(
            MetadataResult<TItemType> metadata,
            TIdType id,
            MetadataRefreshOptions options,
            ICollection<IMetadataProvider> providers,
            ItemImageProvider imageService,
            CancellationToken cancellationToken)
        {
            var refreshResult = new RefreshResult
            {
                UpdateType = ItemUpdateType.None
            };

            var item = metadata.Item;

            var customProviders = providers.OfType<ICustomMetadataProvider<TItemType>>().ToList();
            var logName = !item.IsFileProtocol ? item.Name ?? item.Path : item.Path ?? item.Name;

            foreach (var provider in customProviders.Where(i => i is IPreRefreshProvider))
            {
                await RunCustomProvider(provider, item, logName, options, refreshResult, cancellationToken).ConfigureAwait(false);
            }

            var temp = new MetadataResult<TItemType>
            {
                Item = CreateNew()
            };
            temp.Item.Path = item.Path;

            var userDataList = new List<UserItemData>();

            // If replacing all metadata, run internet providers first
            if (options.ReplaceAllMetadata)
            {
                var remoteResult = await ExecuteRemoteProviders(temp, logName, id, providers.OfType<IRemoteMetadataProvider<TItemType, TIdType>>(), cancellationToken)
                    .ConfigureAwait(false);

                refreshResult.UpdateType |= remoteResult.UpdateType;
                refreshResult.ErrorMessage = remoteResult.ErrorMessage;
                refreshResult.Failures += remoteResult.Failures;
            }

            var hasLocalMetadata = false;

            foreach (var provider in providers.OfType<ILocalMetadataProvider<TItemType>>().ToList())
            {
                var providerName = provider.GetType().Name;
                Logger.LogDebug("Running {0} for {1}", providerName, logName);

                var itemInfo = new ItemInfo(item);

                try
                {
                    var localItem = await provider.GetMetadata(itemInfo, options.DirectoryService, cancellationToken).ConfigureAwait(false);

                    if (localItem.HasMetadata)
                    {
                        foreach (var remoteImage in localItem.RemoteImages)
                        {
                            try
                            {
                                await ProviderManager.SaveImage(item, remoteImage.Url, remoteImage.Type, null, cancellationToken).ConfigureAwait(false);
                                refreshResult.UpdateType |= ItemUpdateType.ImageUpdate;
                            }
                            catch (HttpRequestException ex)
                            {
                                Logger.LogError(ex, "Could not save {ImageType} image: {Url}", Enum.GetName(remoteImage.Type), remoteImage.Url);
                            }
                        }

                        if (imageService.MergeImages(item, localItem.Images))
                        {
                            refreshResult.UpdateType |= ItemUpdateType.ImageUpdate;
                        }

                        if (localItem.UserDataList != null)
                        {
                            userDataList.AddRange(localItem.UserDataList);
                        }

                        MergeData(localItem, temp, Array.Empty<MetadataField>(), !options.ReplaceAllMetadata, true);
                        refreshResult.UpdateType |= ItemUpdateType.MetadataImport;

                        // Only one local provider allowed per item
                        if (item.IsLocked || localItem.Item.IsLocked || IsFullLocalMetadata(localItem.Item))
                        {
                            hasLocalMetadata = true;
                        }

                        break;
                    }

                    Logger.LogDebug("{0} returned no metadata for {1}", providerName, logName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error in {Provider}", provider.Name);

                    // If a local provider fails, consider that a failure
                    refreshResult.ErrorMessage = ex.Message;
                }
            }

            // Local metadata is king - if any is found don't run remote providers
            if (!options.ReplaceAllMetadata && (!hasLocalMetadata || options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh || !item.StopRefreshIfLocalMetadataFound))
            {
                var remoteResult = await ExecuteRemoteProviders(temp, logName, id, providers.OfType<IRemoteMetadataProvider<TItemType, TIdType>>(), cancellationToken)
                    .ConfigureAwait(false);

                refreshResult.UpdateType |= remoteResult.UpdateType;
                refreshResult.ErrorMessage = remoteResult.ErrorMessage;
                refreshResult.Failures += remoteResult.Failures;
            }

            if (providers.Any(i => i is not ICustomMetadataProvider))
            {
                if (refreshResult.UpdateType > ItemUpdateType.None)
                {
                    if (hasLocalMetadata)
                    {
                        MergeData(temp, metadata, item.LockedFields, true, true);
                    }
                    else
                    {
                        // TODO: If the new metadata from above has some blank data, this can cause old data to get filled into those empty fields
                        MergeData(metadata, temp, Array.Empty<MetadataField>(), false, false);
                        MergeData(temp, metadata, item.LockedFields, true, false);
                    }
                }
            }

            // var isUnidentified = failedProviderCount > 0 && successfulProviderCount == 0;

            foreach (var provider in customProviders.Where(i => i is not IPreRefreshProvider))
            {
                await RunCustomProvider(provider, item, logName, options, refreshResult, cancellationToken).ConfigureAwait(false);
            }

            // ImportUserData(item, userDataList, cancellationToken);

            return refreshResult;
        }

        protected virtual bool IsFullLocalMetadata(TItemType item)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return false;
            }

            return true;
        }

        private async Task RunCustomProvider(ICustomMetadataProvider<TItemType> provider, TItemType item, string logName, MetadataRefreshOptions options, RefreshResult refreshResult, CancellationToken cancellationToken)
        {
            Logger.LogDebug("Running {0} for {1}", provider.GetType().Name, logName);

            try
            {
                refreshResult.UpdateType |= await provider.FetchAsync(item, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                refreshResult.ErrorMessage = ex.Message;
                Logger.LogError(ex, "Error in {Provider}", provider.Name);
            }
        }

        protected virtual TItemType CreateNew()
        {
            return new TItemType();
        }

        private async Task<RefreshResult> ExecuteRemoteProviders(MetadataResult<TItemType> temp, string logName, TIdType id, IEnumerable<IRemoteMetadataProvider<TItemType, TIdType>> providers, CancellationToken cancellationToken)
        {
            var refreshResult = new RefreshResult();

            var tmpDataMerged = false;

            foreach (var provider in providers)
            {
                var providerName = provider.GetType().Name;
                Logger.LogDebug("Running {0} for {1}", providerName, logName);

                if (id != null && !tmpDataMerged)
                {
                    MergeNewData(temp.Item, id);
                    tmpDataMerged = true;
                }

                try
                {
                    var result = await provider.GetMetadata(id, cancellationToken).ConfigureAwait(false);

                    if (result.HasMetadata)
                    {
                        result.Provider = provider.Name;

                        MergeData(result, temp, Array.Empty<MetadataField>(), false, false);
                        MergeNewData(temp.Item, id);

                        refreshResult.UpdateType |= ItemUpdateType.MetadataDownload;
                    }
                    else
                    {
                        Logger.LogDebug("{0} returned no metadata for {1}", providerName, logName);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    refreshResult.Failures++;
                    refreshResult.ErrorMessage = ex.Message;
                    Logger.LogError(ex, "Error in {Provider}", provider.Name);
                }
            }

            return refreshResult;
        }

        private void MergeNewData(TItemType source, TIdType lookupInfo)
        {
            // Copy new provider id's that may have been obtained
            foreach (var providerId in source.ProviderIds)
            {
                var key = providerId.Key;

                // Don't replace existing Id's.
                if (!lookupInfo.ProviderIds.ContainsKey(key))
                {
                    lookupInfo.ProviderIds[key] = providerId.Value;
                }
            }
        }

        protected abstract void MergeData(
            MetadataResult<TItemType> source,
            MetadataResult<TItemType> target,
            MetadataField[] lockedFields,
            bool replaceData,
            bool mergeMetadataSettings);

        private bool HasChanged(BaseItem item, IHasItemChangeMonitor changeMonitor, IDirectoryService directoryService)
        {
            try
            {
                var hasChanged = changeMonitor.HasChanged(item, directoryService);

                if (hasChanged)
                {
                    Logger.LogDebug("{0} reports change to {1}", changeMonitor.GetType().Name, item.Path ?? item.Name);
                }

                return hasChanged;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in {0}.HasChanged", changeMonitor.GetType().Name);
                return false;
            }
        }
    }
}
