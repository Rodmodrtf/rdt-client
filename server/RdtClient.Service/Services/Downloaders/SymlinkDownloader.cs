using RdtClient.Service.Helpers;
using Serilog;

namespace RdtClient.Service.Services.Downloaders;

public class SymlinkDownloader : IDownloader
{
    public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    private readonly CancellationTokenSource _cancellationToken = new();
    private readonly ILogger _logger = Log.ForContext<SymlinkDownloader>();
    private const int MaxRetries = 10;
    
    private static readonly List<string> UnwantedExtensions = new()
    {
        ".zip",
        ".rar",
        ".tar"
    };

    private readonly string _uri;
    private readonly string _destinationPath;
    private readonly string _path;

    public SymlinkDownloader(string uri, string destinationPath, string path)
    {
        _uri = uri;
        _destinationPath = destinationPath;
        _path = path;
    }

    public async Task<string> Download()
{
    _logger.Debug($"Starting symlink resolving of {_uri}, writing to path: {_path}");

    try
    {
        var filePath = new FileInfo(_path);
        var rcloneMountPath = Settings.Get.DownloadClient.RcloneMountPath.TrimEnd('\\', '/');
        var searchSubDirectories = rcloneMountPath.EndsWith('*');
        rcloneMountPath = rcloneMountPath.TrimEnd('*').TrimEnd('\\', '/');

        if (!Directory.Exists(rcloneMountPath))
            throw new DirectoryNotFoundException($"Mount path {rcloneMountPath} does not exist!");

        var fileName = filePath.Name;
        var fileExtension = filePath.Extension;

        if (UnwantedExtensions.Contains(fileExtension))
            throw new InvalidOperationException("Cannot handle compressed files with symlink downloader.");

        DownloadProgress?.Invoke(this, new DownloadProgressEventArgs { BytesDone = 0, BytesTotal = 0, Speed = 0 });

        var potentialFilePaths = GetPotentialFilePaths(rcloneMountPath, filePath);
        string? foundFilePath = await FindFileInRcloneMount(rcloneMountPath, potentialFilePaths, fileName, searchSubDirectories);

        if (foundFilePath == null)
        {
            LogAvailableDirectories(rcloneMountPath);
            throw new FileNotFoundException("Could not find file from rclone mount!");
        }

        _logger.Debug($"Creating symbolic link from {foundFilePath} to {_destinationPath}");
        if (!TryCreateSymbolicLink(foundFilePath, _destinationPath))
            throw new InvalidOperationException("Could not create symbolic link!");

        DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs());
        return foundFilePath;
    }
    catch (Exception ex)
    {
        DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs { Error = ex.ToString() });
        throw;
    }
}

// Other methods remain mostly unchanged

    public Task Cancel()
    {
        _cancellationToken.Cancel(false);
        return Task.CompletedTask;
    }

    public Task Pause() => Task.CompletedTask;

    public Task Resume() => Task.CompletedTask;

    private List<string> GetPotentialFilePaths(string searchPath, string fileName, string fileNameWithoutExtension)
    {
        var potentialFilePaths = new List<string>();
        var directoryInfo = new DirectoryInfo(searchPath);

        while (directoryInfo.Parent != null)
        {
            potentialFilePaths.Add(directoryInfo.Name);
            directoryInfo = directoryInfo.Parent;
            if (directoryInfo.FullName.TrimEnd('\\', '/') == searchPath)
            {
                break;
            }
        }

        potentialFilePaths.Add(fileName);
        potentialFilePaths.Add(fileNameWithoutExtension);
        potentialFilePaths.Add(""); // Add an empty path to check for the new file in the base directory

        return potentialFilePaths.Distinct().ToList();
    }

    private async Task<string?> FindFileInRcloneMount(string rcloneMountPath, List<string> potentialFilePaths, string fileName, bool searchSubDirectories)
    {
        for (var retryCount = 0; retryCount < MaxRetries; retryCount++)
        {
            DownloadProgress?.Invoke(this, new DownloadProgressEventArgs { BytesDone = retryCount, BytesTotal = 10, Speed = 1 });
            _logger.Debug($"Searching {rcloneMountPath} for {fileName} (attempt #{retryCount})...");

            var file = FindFile(rcloneMountPath, potentialFilePaths, fileName);
            if (file != null)
            {
                return file;
            }

            if (searchSubDirectories)
            {
                var subDirectories = Directory.GetDirectories(rcloneMountPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var subDirectory in subDirectories)
                {
                    file = FindFile(Path.Combine(rcloneMountPath, subDirectory), potentialFilePaths, fileName);
                    if (file != null)
                    {
                        return file;
                    }
                }
            }

            await Task.Delay(1000 * retryCount);
        }

        return null;
    }

    private string? FindFile(string rootPath, List<string> filePaths, string fileName)
    {
        foreach (var potentialFilePath in filePaths)
        {
            var potentialFilePathWithFileName = Path.Combine(rootPath, potentialFilePath, fileName);
            _logger.Debug($"Searching {potentialFilePathWithFileName}...");

            if (File.Exists(potentialFilePathWithFileName))
            {
                return potentialFilePathWithFileName;
            }
        }

        return null;
    }

    private bool TryCreateSymbolicLink(string sourcePath, string symlinkPath)
    {
        try
        {
            File.CreateSymbolicLink(symlinkPath, sourcePath);
            if (File.Exists(symlinkPath)) // Double-check that the link was created
            {
                _logger.Information($"Created symbolic link from {sourcePath} to {symlinkPath}");
                return true;
            }

            _logger.Error($"Failed to create symbolic link from {sourcePath} to {symlinkPath}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating symbolic link from {sourcePath} to {symlinkPath}: {ex.Message}");
            return false;
        }
    }

    private void LogAvailableDirectories(string rcloneMountPath)
    {
        _logger.Debug($"Unable to find file in rclone mount. Folders available in {rcloneMountPath}:");
        try
        {
            var allFolders = FileHelper.GetDirectoryContents(rcloneMountPath);
            _logger.Debug(allFolders);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error listing directories: {ex.Message}");
        }
    }
}
