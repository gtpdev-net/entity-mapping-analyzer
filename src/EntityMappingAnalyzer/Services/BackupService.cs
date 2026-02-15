using System.IO.Compression;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Creates and restores backups of codebase before replacement operations
/// </summary>
public class BackupService
{
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupDirectory;

    public BackupService(ILogger<BackupService> logger, IConfiguration configuration)
    {
        _logger = logger;
        
        // Get backup directory from configuration or use default
        _backupDirectory = configuration["BackupDirectory"] ?? "./backups";
        
        // Ensure backup directory exists
        if (!Directory.Exists(_backupDirectory))
        {
            Directory.CreateDirectory(_backupDirectory);
        }
    }

    /// <summary>
    /// Create a ZIP backup of all C# files in the target path
    /// </summary>
    public async Task<string> CreateBackupAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            throw new DirectoryNotFoundException($"Target path not found: {targetPath}");
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"backup_{Path.GetFileName(targetPath)}_{timestamp}.zip";
        var backupPath = Path.Combine(_backupDirectory, backupFileName);

        _logger.LogInformation("Creating backup: {BackupPath}", backupPath);

        try
        {
            // Create the ZIP archive
            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

            // If target is a single file, just add that file
            if (File.Exists(targetPath))
            {
                await AddFileToArchiveAsync(archive, targetPath, Path.GetFileName(targetPath), cancellationToken);
            }
            // If target is a directory, add all C# files
            else
            {
                var files = Directory.GetFiles(targetPath, "*.cs", SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Get relative path for entry name
                    var relativePath = Path.GetRelativePath(targetPath, file);
                    await AddFileToArchiveAsync(archive, file, relativePath, cancellationToken);
                }

                _logger.LogInformation("Backed up {Count} C# files", files.Length);
            }

            _logger.LogInformation("Backup created successfully: {BackupPath}", backupPath);
            return backupPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup");
            
            // Clean up partial backup if it exists
            if (File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Restore files from a backup ZIP
    /// </summary>
    public async Task RestoreBackupAsync(string backupPath, string? targetPath = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup file not found: {backupPath}");
        }

        _logger.LogInformation("Restoring backup: {BackupPath}", backupPath);

        try
        {
            using var archive = ZipFile.OpenRead(backupPath);

            foreach (var entry in archive.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Determine output path
                var outputPath = targetPath != null 
                    ? Path.Combine(targetPath, entry.FullName)
                    : entry.FullName;

                // Create directory if needed
                var directoryPath = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Extract file
                await Task.Run(() => entry.ExtractToFile(outputPath, overwrite: true), cancellationToken);
            }

            _logger.LogInformation("Backup restored successfully: {Count} files", archive.Entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup");
            throw;
        }
    }

    /// <summary>
    /// List all available backups
    /// </summary>
    public async Task<List<BackupInfo>> ListBackupsAsync()
    {
        var backups = new List<BackupInfo>();

        if (!Directory.Exists(_backupDirectory))
        {
            return backups;
        }

        var files = Directory.GetFiles(_backupDirectory, "backup_*.zip");

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            
            backups.Add(new BackupInfo
            {
                FilePath = file,
                FileName = fileInfo.Name,
                CreatedDate = fileInfo.CreationTimeUtc,
                SizeBytes = fileInfo.Length,
                SizeFormatted = FormatFileSize(fileInfo.Length)
            });
        }

        await Task.CompletedTask; // Keep async signature
        return backups.OrderByDescending(b => b.CreatedDate).ToList();
    }

    /// <summary>
    /// Delete old backups based on retention period
    /// </summary>
    public async Task CleanupOldBackupsAsync(int retentionDays = 30)
    {
        if (!Directory.Exists(_backupDirectory))
        {
            return;
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        var files = Directory.GetFiles(_backupDirectory, "backup_*.zip");
        var deletedCount = 0;

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.CreationTimeUtc < cutoffDate)
            {
                try
                {
                    File.Delete(file);
                    deletedCount++;
                    _logger.LogInformation("Deleted old backup: {FileName}", fileInfo.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old backup: {FileName}", fileInfo.Name);
                }
            }
        }

        _logger.LogInformation("Cleanup complete: {Count} old backups deleted", deletedCount);
        await Task.CompletedTask; // Keep async signature
    }

    /// <summary>
    /// Delete a specific backup
    /// </summary>
    public async Task DeleteBackupAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup file not found: {backupPath}");
        }

        File.Delete(backupPath);
        _logger.LogInformation("Deleted backup: {BackupPath}", backupPath);
        await Task.CompletedTask; // Keep async signature
    }

    /// <summary>
    /// Add a file to a ZIP archive
    /// </summary>
    private async Task AddFileToArchiveAsync(
        ZipArchive archive, 
        string sourceFilePath, 
        string entryName,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            // Use forward slashes in ZIP entries for cross-platform compatibility
            entryName = entryName.Replace('\\', '/');
            archive.CreateEntryFromFile(sourceFilePath, entryName, CompressionLevel.Optimal);
        }, cancellationToken);
    }

    /// <summary>
    /// Format file size in human-readable format
    /// </summary>
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Information about a backup file
/// </summary>
public class BackupInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public long SizeBytes { get; set; }
    public string SizeFormatted { get; set; } = string.Empty;
}
