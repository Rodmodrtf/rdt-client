using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RDNET;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.TorrentClient;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Services.TorrentClients
{
    public class RealDebridTorrentClient : ITorrentClient
    {
        private readonly ILogger<RealDebridTorrentClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private TimeSpan? _offset;

        public RealDebridTorrentClient(ILogger<RealDebridTorrentClient> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        private RdNetClient GetClient()
        {
            try
            {
                var apiKey = Settings.Get.Provider.ApiKey;

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("Real-Debrid API Key not set in the settings");
                }

                var httpClient = _httpClientFactory.CreateClient(DiConfig.RD_CLIENT);
                httpClient.Timeout = TimeSpan.FromSeconds(Settings.Get.Provider.Timeout);

                var rdtNetClient = new RdNetClient(null, httpClient, 5);
                rdtNetClient.UseApiAuthentication(apiKey);

                // Get the server time to adjust timezones on results
                if (_offset == null)
                {
                    var serverTime = rdtNetClient.Api.GetIsoTimeAsync().Result;
                    _offset = serverTime.Offset;
                }

                return rdtNetClient;
            }
            catch (AggregateException ae)
            {
                foreach (var inner in ae.InnerExceptions)
                {
                    _logger.LogError(inner, $"The connection to RealDebrid has failed: {inner.Message}");
                }

                throw;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogError(ex, $"The connection to RealDebrid has timed out: {ex.Message}");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, $"The connection to RealDebrid has timed out: {ex.Message}");
                throw;
            }
        }

        private TorrentClientTorrent Map(Torrent torrent)
        {
            return new TorrentClientTorrent
            {
                Id = torrent.Id,
                Filename = torrent.Filename,
                OriginalFilename = torrent.OriginalFilename,
                Hash = torrent.Hash,
                Bytes = torrent.Bytes,
                OriginalBytes = torrent.OriginalBytes,
                Host = torrent.Host,
                Split = torrent.Split,
                Progress = torrent.Progress,
                Status = torrent.Status,
                Added = ChangeTimeZone(torrent.Added)!.Value,
                Files = torrent.Files?.Select(m => new TorrentClientFile
                {
                    Path = m.Path,
                    Bytes = m.Bytes,
                    Id = m.Id,
                    Selected = m.Selected
                }).ToList() ?? new List<TorrentClientFile>(),
                Links = torrent.Links,
                Ended = ChangeTimeZone(torrent.Ended),
                Speed = torrent.Speed,
                Seeders = torrent.Seeders,
            };
        }

        public async Task<IList<TorrentClientTorrent>> GetTorrents()
        {
            var results = new List<Torrent>();
            var offset = 0;

            while (true)
            {
                var pagedResults = await GetClient().Torrents.GetAsync(offset, 5000);
                if (pagedResults.Count == 0) break;

                results.AddRange(pagedResults);
                offset += 5000;
            }

            return results.Select(Map).ToList();
        }

        public async Task<TorrentClientUser> GetUser()
        {
            var user = await GetClient().User.GetAsync();
            return new TorrentClientUser
            {
                Username = user.Username,
                Expiration = user.Premium > 0 ? user.Expiration : null
            };
        }

        public async Task<string> AddMagnet(string magnetLink)
        {
            var result = await GetClient().Torrents.AddMagnetAsync(magnetLink);
            return result.Id;
        }

        public async Task<string> AddFile(byte[] bytes)
        {
            var result = await GetClient().Torrents.AddFileAsync(bytes);
            return result.Id;
        }

        public async Task<IList<TorrentClientAvailableFile>> GetAvailableFiles(string hash)
        {
            var result = await GetClient().Torrents.GetAvailableFiles(hash);
            var files = result.SelectMany(m => m.Value).SelectMany(m => m.Value).SelectMany(m => m.Values);

            var groups = files.Where(m => m.Filename != null)
                              .GroupBy(m => $"{m.Filename}-{m.Filesize}");

            return groups.Select(m => new TorrentClientAvailableFile
            {
                Filename = m.First().Filename!,
                Filesize = m.First().Filesize
            }).ToList();
        }

        public async Task SelectFiles(Data.Models.Data.Torrent torrent)
        {
            var files = torrent.Files;
            Log("Selecting files", torrent);

            files = torrent.DownloadAction switch
            {
                TorrentDownloadAction.DownloadAvailableFiles => await HandleAvailableFilesSelection(torrent, files),
                TorrentDownloadAction.DownloadAll => new List<TorrentClientFile>(torrent.Files),
                TorrentDownloadAction.DownloadManual => HandleManualFilesSelection(torrent, files),
                _ => files
            };

            files = FilterByMinSize(torrent, files);
            files = ApplyRegexFilters(torrent, files);

            if (files.Count == 0)
            {
                Log("Filtered all files out! Downloading ALL files instead!", torrent);
                files = torrent.Files;
            }

            var fileIds = files.Select(m => m.Id.ToString()).ToArray();
            Log("Selecting files:", torrent);

            foreach (var file in files)
            {
                Log($"{file.Id}: {file.Path} ({file.Bytes}b)");
            }

            await GetClient().Torrents.SelectFilesAsync(torrent.RdId!, fileIds);
        }

        public async Task Delete(string torrentId)
        {
            await GetClient().Torrents.DeleteAsync(torrentId);
        }

        public async Task<string> Unrestrict(string link)
        {
            var result = await GetClient().Unrestrict.LinkAsync(link);
            if (result.Download == null)
            {
                throw new InvalidOperationException("Unrestrict returned an invalid download");
            }

            return result.Download;
        }

        public async Task<Data.Models.Data.Torrent> UpdateData(Data.Models.Data.Torrent torrent, TorrentClientTorrent? torrentClientTorrent)
        {
            try
            {
                if (torrent.RdId == null)
                {
                    return torrent;
                }

                torrentClientTorrent ??= await GetInfo(torrent.RdId) ?? throw new InvalidOperationException("Resource not found");

                torrent.RdName = !string.IsNullOrWhiteSpace(torrentClientTorrent.Filename)
                    ? torrentClientTorrent.Filename
                    : torrentClientTorrent.OriginalFilename;

                torrent.RdSize = torrentClientTorrent.Bytes > 0 ? torrentClientTorrent.Bytes : torrentClientTorrent.OriginalBytes;

                if (torrentClientTorrent.Files != null && torrentClientTorrent.Files.Count > 0)
                {
                    torrent.RdFiles = JsonConvert.SerializeObject(torrentClientTorrent.Files);
                }

                UpdateTorrentStatus(torrent, torrentClientTorrent);
            }
            catch (Exception ex) when (ex.Message == "Resource not found")
            {
                torrent.RdStatusRaw = "deleted";
            }

            return torrent;
        }

        public async Task<IList<string>?> GetDownloadLinks(Data.Models.Data.Torrent torrent)
        {
            if (torrent.RdId == null)
            {
                return null;
            }

            var rdTorrent = await GetInfo(torrent.RdId);
            if (rdTorrent.Links == null)
            {
                return null;
            }

            var downloadLinks = rdTorrent.Links.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            Log($"Found {downloadLinks.Count} links", torrent);

            foreach (var link in downloadLinks)
            {
                Log($"{link}", torrent);
            }

            Log($"Torrent has {torrent.Files.Count(m => m.Selected)} selected files out of {torrent.Files.Count} files, found {downloadLinks.Count} links, torrent ended: {torrent.RdEnded}", torrent);
            
            return await CheckDownloadLinkConditions(torrent, downloadLinks);
        }

        private DateTimeOffset? ChangeTimeZone(DateTimeOffset? dateTimeOffset)
        {
            return _offset == null ? dateTimeOffset : dateTimeOffset?.Subtract(_offset.Value).ToOffset(_offset.Value);
        }

        private async Task<TorrentClientTorrent> GetInfo(string torrentId)
        {
            var result = await GetClient().Torrents.GetInfoAsync(torrentId);
            return Map(result);
        }

        private void Log(string message, Data.Models.Data.Torrent? torrent = null)
        {
            if (torrent != null)
            {
                message += $" {torrent.ToLog()}";
            }

            _logger.LogDebug(message);
        }

        private async Task<IList<TorrentClientFile>> HandleAvailableFilesSelection(Data.Models.Data.Torrent torrent, IList<TorrentClientFile> files)
        {
            Log("Determining which files are already available on RealDebrid", torrent);
            var availableFiles = await GetAvailableFiles(torrent.Hash);
            Log($"Found {files.Count}/{torrent.Files.Count} available files on RealDebrid", torrent);
            return files.Where(m => availableFiles.Any(f => m.Path.EndsWith(f.Filename))).ToList();
        }

        private IList<TorrentClientFile> HandleManualFilesSelection(Data.Models.Data.Torrent torrent, IList<TorrentClientFile> files)
        {
            Log("Selecting manually selected files", torrent);
            return files.Where(m => torrent.ManualFiles.Any(f => m.Path.EndsWith(f))).ToList();
        }

        private IList<TorrentClientFile> FilterByMinSize(Data.Models.Data.Torrent torrent, IList<TorrentClientFile> files)
        {
            if (torrent.DownloadMinSize > 0)
            {
                var minFileSize = torrent.DownloadMinSize * 1024 * 1024;
                Log($"Determining which files are over {minFileSize} bytes", torrent);
                files = files.Where(m => m.Bytes > minFileSize).ToList();
                Log($"Found {files.Count} files that match the minimum file size criteria", torrent);
            }

            return files;
        }

        private IList<TorrentClientFile> ApplyRegexFilters(Data.Models.Data.Torrent torrent, IList<TorrentClientFile> files)
        {
            if (!string.IsNullOrWhiteSpace(torrent.IncludeRegex))
            {
                Log($"Using regular expression {torrent.IncludeRegex} to include only files matching this regex", torrent);
                files = files.Where(file => Regex.IsMatch(file.Path, torrent.IncludeRegex)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(torrent.ExcludeRegex))
            {
                Log($"Using regular expression {torrent.ExcludeRegex} to ignore files matching this regex", torrent);
                files = files.Where(file => !Regex.IsMatch(file.Path, torrent.ExcludeRegex)).ToList();
            }

            Log($"Found {files.Count} files that match the criteria", torrent);
            return files;
        }

        private async Task<IList<string>?> CheckDownloadLinkConditions(Data.Models.Data.Torrent torrent, List<string> downloadLinks)
        {
            // Check if all selected files have matching links
            if (torrent.Files.Count(m => m.Selected) == downloadLinks.Count ||
                torrent.ManualFiles.Count == downloadLinks.Count)
            {
                Log($"Matched {downloadLinks.Count} files", torrent);
                return downloadLinks;
            }

            // Delay for 1 minute if only 1 link is available
            if (downloadLinks.Count == 1 && torrent.RdEnded.HasValue)
            {
                var expired = DateTime.UtcNow - torrent.RdEnded.Value.ToUniversalTime();
                Log($"Waiting to see if more links appear, checked for {expired.TotalSeconds} seconds", torrent);
                if (expired.TotalSeconds > 60.0)
                {
                    Log("Waited long enough", torrent);
                    return downloadLinks;
                }
            }

            Log("Did not find any suitable download links", torrent);
            return null;
        }

        private void UpdateTorrentStatus(Data.Models.Data.Torrent torrent, TorrentClientTorrent torrentClientTorrent)
        {
            torrent.RdHost = torrentClientTorrent.Host;
            torrent.RdSplit = torrentClientTorrent.Split;
            torrent.RdProgress = torrentClientTorrent.Progress;
            torrent.RdAdded = torrentClientTorrent.Added;
            torrent.RdEnded = torrentClientTorrent.Ended;
            torrent.RdSpeed = torrentClientTorrent.Speed;
            torrent.RdSeeders = torrentClientTorrent.Seeders;
            torrent.RdStatusRaw = torrentClientTorrent.Status;

            torrent.RdStatus = torrentClientTorrent.Status switch
            {
                "magnet_error" => TorrentStatus.Error,
                "magnet_conversion" => TorrentStatus.Processing,
                "waiting_files_selection" => TorrentStatus.WaitingForFileSelection,
                "queued" or "downloading" => TorrentStatus.Downloading,
                "downloaded" => TorrentStatus.Finished,
                "error" or "virus" or "dead" => TorrentStatus.Error,
                "compressing" => TorrentStatus.Downloading,
                "uploading" => TorrentStatus.Uploading,
                _ => TorrentStatus.Error
            };
        }
    }
}
