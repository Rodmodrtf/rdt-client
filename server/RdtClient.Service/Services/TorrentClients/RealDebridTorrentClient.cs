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

        private async Task<RdNetClient> GetClientAsync()
        {
            var apiKey = Settings.Get.Provider.ApiKey ?? throw new InvalidOperationException("Real-Debrid API Key not set in the settings");

            var httpClient = _httpClientFactory.CreateClient(DiConfig.RD_CLIENT);
            httpClient.Timeout = TimeSpan.FromSeconds(Settings.Get.Provider.Timeout);

            var rdtNetClient = new RdNetClient(null, httpClient, 5);
            rdtNetClient.UseApiAuthentication(apiKey);

            if (_offset == null)
            {
                var serverTime = await rdtNetClient.Api.GetIsoTimeAsync();
                _offset = serverTime.Offset;
            }

            return rdtNetClient;
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
            var offset = 0;
            var results = new List<Torrent>();

            while (true)
            {
                var pagedResults = await (await GetClientAsync()).Torrents.GetAsync(offset, 5000);
                results.AddRange(pagedResults);

                if (!pagedResults.Any()) break;

                offset += 5000;
            }

            return results.Select(Map).ToList();
        }

        public async Task<TorrentClientUser> GetUser()
        {
            var user = await (await GetClientAsync()).User.GetAsync();

            return new TorrentClientUser
            {
                Username = user.Username,
                Expiration = user.Premium > 0 ? user.Expiration : (DateTimeOffset?)null
            };
        }

        public async Task<string> AddMagnet(string magnetLink)
        {
            var result = await (await GetClientAsync()).Torrents.AddMagnetAsync(magnetLink);
            return result.Id;
        }

        public async Task<string> AddFile(byte[] bytes)
        {
            var result = await (await GetClientAsync()).Torrents.AddFileAsync(bytes);
            return result.Id;
        }

        public async Task<IList<TorrentClientAvailableFile>> GetAvailableFiles(string hash)
        {
            var result = await (await GetClientAsync()).Torrents.GetAvailableFiles(hash);
            var files = result.SelectMany(m => m.Value).SelectMany(m => m.Value).SelectMany(m => m.Values);

            var groups = files.Where(m => m.Filename != null).GroupBy(m => $"{m.Filename}-{m.Filesize}");

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
                TorrentDownloadAction.DownloadAvailableFiles => await FilterAvailableFiles(torrent, files.ToList()),
                TorrentDownloadAction.DownloadAll => files.ToList(),
                TorrentDownloadAction.DownloadManual => files.Where(m => torrent.ManualFiles.Any(f => m.Path.EndsWith(f))).ToList(),
                _ => files
            };

            Log($"Selecting {files.Count}/{torrent.Files.Count} files", torrent);

            if (torrent.DownloadMinSize > 0)
            {
                var minFileSize = torrent.DownloadMinSize * 1024 * 1024;
                files = files.Where(m => m.Bytes > minFileSize).ToList();
                Log($"Found {files.Count} files that match the minimum file size criteria", torrent);
            }

            files = FilterByRegex(torrent, files);

            if (!files.Any())
            {
                Log("Filtered all files out! Downloading ALL files instead!", torrent);
                files = torrent.Files;
            }

            var fileIds = files.Select(m => m.Id.ToString()).ToArray();
            Log($"Selecting files: {string.Join(", ", files.Select(f => $"{f.Id}: {f.Path} ({f.Bytes}b)"))}", torrent);

            await (await GetClientAsync()).Torrents.SelectFilesAsync(torrent.RdId!, fileIds);
        }

        private async Task<IList<TorrentClientFile>> FilterAvailableFiles(Data.Models.Data.Torrent torrent, List<TorrentClientFile> files)
        {
            Log("Determining which files are already available on RealDebrid", torrent);
            var availableFiles = await GetAvailableFiles(torrent.Hash);
            Log($"Found {files.Count}/{torrent.Files.Count} available files on RealDebrid", torrent);

            return files.Where(m => availableFiles.Any(f => m.Path.EndsWith(f.Filename))).ToList();
        }

        private List<TorrentClientFile> FilterByRegex(Data.Models.Data.Torrent torrent, List<TorrentClientFile> files)
        {
            if (!string.IsNullOrWhiteSpace(torrent.IncludeRegex))
            {
                files = files.Where(file => Regex.IsMatch(file.Path, torrent.IncludeRegex)).ToList();
                Log($"Found {files.Count} files that match the include regex", torrent);
            }
            else if (!string.IsNullOrWhiteSpace(torrent.ExcludeRegex))
            {
                files = files.Where(file => !Regex.IsMatch(file.Path, torrent.ExcludeRegex)).ToList();
                Log($"Found {files.Count} files that match the exclude regex", torrent);
            }
            return files;
        }

        public async Task Delete(string torrentId)
        {
            await (await GetClientAsync()).Torrents.DeleteAsync(torrentId);
        }

        public async Task<string> Unrestrict(string link)
        {
            var result = await (await GetClientAsync()).Unrestrict.LinkAsync(link);
            return result.Download ?? throw new InvalidOperationException("Unrestrict returned an invalid download");
        }

        public async Task<Data.Models.Data.Torrent> UpdateData(Data.Models.Data.Torrent torrent, TorrentClientTorrent? torrentClientTorrent)
        {
            if (torrent.RdId == null) return torrent;

            if (torrentClientTorrent == null || torrentClientTorrent.Ended == null || string.IsNullOrEmpty(torrentClientTorrent.Filename))
            {
                torrentClientTorrent = await GetInfo(torrent.RdId) ?? throw new InvalidOperationException("Resource not found");
            }

            UpdateTorrentFields(torrent, torrentClientTorrent);

            return torrent;
        }

        private void UpdateTorrentFields(Data.Models.Data.Torrent torrent, TorrentClientTorrent torrentClientTorrent)
        {
            if (!string.IsNullOrWhiteSpace(torrentClientTorrent.Filename))
                torrent.RdName = torrentClientTorrent.Filename;

            if (!string.IsNullOrWhiteSpace(torrentClientTorrent.OriginalFilename))
                torrent.RdName = torrentClientTorrent.OriginalFilename;

            if (torrentClientTorrent.Bytes > 0)
                torrent.RdSize = torrentClientTorrent.Bytes;
            else if (torrentClientTorrent.OriginalBytes > 0)
                torrent.RdSize = torrentClientTorrent.OriginalBytes;

            if (torrentClientTorrent.Files != null && torrentClientTorrent.Files.Count > 0)
                torrent.RdFiles = JsonConvert.SerializeObject(torrentClientTorrent.Files);

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
                "queued" => TorrentStatus.Downloading,
                "downloading" => TorrentStatus.Downloading,
                "downloaded" => TorrentStatus.Finished,
                "error" => TorrentStatus.Error,
                "virus" => TorrentStatus.Error,
                "compressing" => TorrentStatus.Downloading,
                "uploading" => TorrentStatus.Uploading,
                _ => TorrentStatus.Unknown
            };
        }
        public async Task<IList<String>?> GetDownloadLinks(Data.Models.Data.Torrent torrent)
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

        var downloadLinks = rdTorrent.Links.Where(m => !String.IsNullOrWhiteSpace(m)).ToList();

        Log($"Found {downloadLinks.Count} links", torrent);

        foreach (var link in downloadLinks)
        {
            Log($"{link}", torrent);
        }

        Log($"Torrent has {torrent.Files.Count(m => m.Selected)} selected files out of {torrent.Files.Count} files, found {downloadLinks.Count} links, torrent ended: {torrent.RdEnded}", torrent);
        
        // Check if all the links are set that have been selected
        if (torrent.Files.Count(m => m.Selected) == downloadLinks.Count)
        {
            Log($"Matched {torrent.Files.Count(m => m.Selected)} selected files expected files to {downloadLinks.Count} found files", torrent);

            return downloadLinks;
        }

        // Check if all all the links are set for manual selection
        if (torrent.ManualFiles.Count == downloadLinks.Count)
        {
            Log($"Matched {torrent.ManualFiles.Count} manual files expected files to {downloadLinks.Count} found files", torrent);

            return downloadLinks;
        }

        // If there is only 1 link, delay for 1 minute to see if more links pop up.
        if (downloadLinks.Count == 1 && torrent.RdEnded.HasValue)
        {
            var expired = DateTime.UtcNow - torrent.RdEnded.Value.ToUniversalTime();

            Log($"Waiting to see if more links appear, checked for {expired.TotalSeconds} seconds", torrent);

            if (expired.TotalSeconds > 60.0)
            {
                Log($"Waited long enough", torrent);

                return downloadLinks;
            }
        }

        Log($"Did not find any suiteable download links", torrent);
            
        return null;
    }

    private DateTimeOffset? ChangeTimeZone(DateTimeOffset? dateTimeOffset)
    {
        if (_offset == null)
        {
            return dateTimeOffset;
        }

        return dateTimeOffset?.Subtract(_offset.Value).ToOffset(_offset.Value);
    }

        public async Task<TorrentClientTorrent?> GetInfo(string id)
        {
            var result = await (await GetClientAsync()).Torrents.GetAsync(id);
            return result == null ? null : Map(result);
        }

        private DateTimeOffset? ChangeTimeZone(DateTimeOffset? time)
        {
            if (time == null || _offset == null) return time;

            return time.Value.AddSeconds(_offset.Value.TotalSeconds);
        }

        private void Log(string message, Data.Models.Data.Torrent torrent)
        {
            _logger.LogInformation($"[{torrent.Id}] {message}");
        }
    }
}
