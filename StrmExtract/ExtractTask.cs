using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.Providers;
using System.Collections;

namespace StrmExtract
{
    /// <summary>
    /// Fork of faush01/StrmExtract (https://github.com/faush01/StrmExtract).
    ///
    /// Change: when a folder holds several .strm files for the same title that
    /// only differ by a quality suffix (e.g. "Movie - 01 - 2160p.strm" /
    /// "Movie - 02 - 1080p.strm" - the naming convention written by the
    /// Dispatcharr "vod_manager" plugin), only the first one is probed; the
    /// rest are skipped instead of being probed back-to-back.
    ///
    /// Why: probing two .strm URLs for the same underlying title in quick
    /// succession, from the same Emby server (same client IP + user-agent
    /// for every request this task makes), can make Dispatcharr's VOD proxy
    /// serve the SECOND request the FIRST one's already-open stream instead
    /// of opening its own - Dispatcharr's idle-session reuse matches purely
    /// on (content uuid, client ip, user-agent), never on the requested
    /// stream_id (apps/proxy/vod_proxy/multi_worker_connection_manager.py,
    /// find_matching_idle_session). The result: Emby permanently caches the
    /// wrong technical info (resolution/codec/bitrate) for every secondary
    /// version of every multi-version title this task touches - confirmed
    /// happening in practice, not theoretical. See the matching Dispatcharr
    /// bug report this fork's README links to.
    ///
    /// Skipping the secondary files avoids the collision entirely. It's a
    /// real trade-off: only the first (best, since the source plugin ranks
    /// filenames best-quality-first) version gets Emby-native MediaInfo:
    /// resolution/codec/bitrate for the others stay unpopulated in Emby.
    /// Their quality is still visible in the filename itself.
    /// </summary>
    public class ExtractTask : IScheduledTask
    {
        // Matches this plugin's own filename suffix convention exactly:
        // " - 01 - 2160p", " - 02 - v3", " - 1080p" (single-version, no rank),
        // " - unprobed" - see vod_manager's strm.py plan_suffixes(). Case
        // sensitive on purpose: these are always written exactly this way.
        private static readonly Regex VersionSuffixPattern = new Regex(
            @"( - \d{2})? - (2160p|1080p|720p|480p|sd|unknown|unprobed|v\d+)$",
            RegexOptions.Compiled);

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IMediaProbeManager _mediaProbeManager;

        public ExtractTask(ILibraryManager libraryManager, 
            ILogger logger, 
            IFileSystem fileSystem,
            ILibraryMonitor libraryMonitor,
            IMediaProbeManager prob)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _libraryMonitor = libraryMonitor;
            _mediaProbeManager = prob;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("StrmExtract - Task Execute");

            InternalItemsQuery query = new InternalItemsQuery();

            query.HasPath = true;
            query.HasContainer = false;
            query.ExcludeItemTypes = new string[] { "Folder", "CollectionFolder", "UserView", "Series", "Season", "Trailer", "Playlist" };

            BaseItem[] results = _libraryManager.GetItemList(query);
            _logger.Info("StrmExtract - Number of items before : " + results.Length);
            List<BaseItem> candidates = new List<BaseItem>();
            foreach(BaseItem item in  results)
            {
                if(!string.IsNullOrEmpty(item.Path) &&
                    item.Path.EndsWith(".strm", StringComparison.InvariantCultureIgnoreCase) &&
                    item.GetMediaStreams().Count == 0)
                {
                    candidates.Add(item);
                }
                else
                {
                    _logger.Info("StrmExtract - Item dropped : " + item.Name + " - " + item.Path + " - " + item.GetType() + " - " + item.GetMediaStreams().Count);
                }
            }

            _logger.Info("StrmExtract - Number of candidate items : " + candidates.Count);

            // Only probe the first (alphabetically, which is also the best
            // quality with vod_manager's naming) file per title/episode group
            // - see the class-level comment for why probing the rest would
            // corrupt their metadata instead of just being redundant.
            List<BaseItem> items = new List<BaseItem>();
            int skippedAsAlternateVersion = 0;
            foreach (var group in candidates
                .GroupBy(i => GetVersionGroupKey(i))
                .Select(g => g.OrderBy(i => Path.GetFileName(i.Path), StringComparer.OrdinalIgnoreCase).ToList()))
            {
                items.Add(group[0]);
                if (group.Count > 1)
                {
                    skippedAsAlternateVersion += group.Count - 1;
                    for (int i = 1; i < group.Count; i++)
                    {
                        _logger.Info("StrmExtract - Skipping alternate version (same title, avoids VOD session collision): "
                            + group[i].Name + " - " + group[i].Path);
                    }
                }
            }

            _logger.Info("StrmExtract - Number of items after : " + items.Count
                + " (skipped " + skippedAsAlternateVersion + " alternate version(s))");

            double total = items.Count;
            int current = 0;
            foreach(BaseItem item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("StrmExtract - Task Cancelled");
                    break;
                }
                double percent_done = (current / total) * 100;
                progress.Report(percent_done);

                MetadataRefreshOptions options = new MetadataRefreshOptions(_fileSystem);
                options.EnableRemoteContentProbe = true;
                options.ReplaceAllMetadata = true;
                options.EnableThumbnailImageExtraction = false;
                options.ImageRefreshMode = MetadataRefreshMode.ValidationOnly;
                options.MetadataRefreshMode = MetadataRefreshMode.ValidationOnly;
                options.ReplaceAllImages = false;

                ItemUpdateType resp = await item.RefreshMetadata(options, cancellationToken);

                _logger.Info("StrmExtract - " + current + "/" + total + " - " + item.Path);

                //Thread.Sleep(5000);
                current++;
            }

            progress.Report(100.0);
            _logger.Info("StrmExtract - Task Complete");
        }

        /// <summary>
        /// Groups by parent folder + filename with the quality suffix
        /// stripped, so "Movie - 01 - 2160p" and "Movie - 02 - 1080p" (or an
        /// episode's "Show - S01E01 - 01 - 2160p" / "- 02 - 1080p") land in
        /// the same group, while different episodes/movies sharing just a
        /// parent folder (e.g. two episodes in the same season folder) do
        /// not - each has its own distinct stem once the suffix is removed.
        /// Falls back to the plain filename (no grouping) for anything that
        /// doesn't match the known suffix pattern, so unrelated .strm files
        /// are never accidentally skipped.
        /// </summary>
        private static string GetVersionGroupKey(BaseItem item)
        {
            string dir = Path.GetDirectoryName(item.Path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(item.Path) ?? item.Name ?? "";
            string baseName = VersionSuffixPattern.Replace(stem, "");
            return dir + "|" + baseName;
        }

        public string Category
        {
            get { return "Strm Extract"; }
        }

        public string Key
        {
            get { return "StrmExtractTask"; }
        }

        public string Description
        {
            get { return "Run Strm Media Info Extraction"; }
        }

        public string Name
        {
            get { return "Process Strm targets"; }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
                {
                    new TaskTriggerInfo
                    {
                        Type = TaskTriggerInfo.TriggerDaily,
                        TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                        MaxRuntimeTicks = TimeSpan.FromHours(24).Ticks
                    }
                };
        }
    }
}
