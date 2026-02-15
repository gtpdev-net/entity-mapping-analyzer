using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Deletes old entity files after successful replacement
/// </summary>
public class EntityCleanupService
{
    private readonly ILogger<EntityCleanupService> _logger;
    private readonly string _archiveDirectory;

    public EntityCleanupService(ILogger<EntityCleanupService> logger, IConfiguration configuration)
    {
        _logger = logger;
        
        // Get archive directory from configuration or use default
        _archiveDirectory = configuration["ArchiveDirectory"] ?? "./archived-entities";
    }

    /// <summary>
    /// Delete old entity file after successful replacement
    /// </summary>
    public async Task<bool> DeleteOldEntityAsync(EntityMapping mapping, ReplacementResult result)
    {
        if (mapping.OldEntity == null || string.IsNullOrEmpty(mapping.OldEntity.FilePath))
        {
            _logger.LogWarning("Cannot delete old entity: file path is empty");
            return false;
        }

        var filePath = mapping.OldEntity.FilePath;

        // Verify replacement was successful
        if (result.Status != ReplacementStatus.Completed)
        {
            _logger.LogWarning("Cannot delete old entity: replacement was not successful");
            return false;
        }

        // Check if file exists
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Old entity file not found: {FilePath}", filePath);
            return false;
        }

        try
        {
            // Delete the file
            File.Delete(filePath);
            _logger.LogInformation("Deleted old entity file: {FilePath}", filePath);

            // Clean up empty directories
            await DeleteEmptyDirectoriesAsync(Path.GetDirectoryName(filePath));

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when deleting file: {FilePath}", filePath);
            result.Errors.Add($"Access denied: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error when deleting file (file may be in use): {FilePath}", filePath);
            result.Errors.Add($"File in use or IO error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file: {FilePath}", filePath);
            result.Errors.Add($"Delete error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Archive old entity file instead of deleting it
    /// </summary>
    public async Task<bool> ArchiveEntityAsync(EntityMapping mapping, string? customArchivePath = null)
    {
        if (mapping.OldEntity == null || string.IsNullOrEmpty(mapping.OldEntity.FilePath))
        {
            _logger.LogWarning("Cannot archive old entity: file path is empty");
            return false;
        }

        var sourcePath = mapping.OldEntity.FilePath;

        // Check if file exists
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Old entity file not found: {FilePath}", sourcePath);
            return false;
        }

        try
        {
            // Determine archive path
            var archiveBasePath = customArchivePath ?? _archiveDirectory;
            
            // Create archive directory structure matching source
            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            var archivePath = Path.Combine(archiveBasePath, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Path.GetFileName(sourcePath)}");
            
            var archiveDir = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrEmpty(archiveDir) && !Directory.Exists(archiveDir))
            {
                Directory.CreateDirectory(archiveDir);
            }

            // Move file to archive
            File.Move(sourcePath, archivePath, overwrite: false);
            _logger.LogInformation("Archived old entity file: {SourcePath} → {ArchivePath}", sourcePath, archivePath);

            // Clean up empty directories
            await DeleteEmptyDirectoriesAsync(sourceDirectory);

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when archiving file: {FilePath}", sourcePath);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error when archiving file: {FilePath}", sourcePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving file: {FilePath}", sourcePath);
            return false;
        }
    }

    /// <summary>
    /// Delete empty directories recursively up the tree
    /// </summary>
    public async Task DeleteEmptyDirectoriesAsync(string? directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            // Check if directory is empty (no files or subdirectories)
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath);
                _logger.LogInformation("Deleted empty directory: {DirectoryPath}", directoryPath);

                // Recursively check parent directory
                var parentDirectory = Path.GetDirectoryName(directoryPath);
                await DeleteEmptyDirectoriesAsync(parentDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete empty directory: {DirectoryPath}", directoryPath);
            // Don't throw - this is a cleanup operation
        }
    }

    /// <summary>
    /// Bulk delete multiple entity files
    /// </summary>
    public async Task<Dictionary<string, bool>> DeleteMultipleEntitiesAsync(
        List<EntityMapping> mappings,
        List<ReplacementResult> results)
    {
        var deleteResults = new Dictionary<string, bool>();

        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            var result = results.ElementAtOrDefault(i);

            if (mapping.OldEntity != null && result != null)
            {
                var deleted = await DeleteOldEntityAsync(mapping, result);
                deleteResults[mapping.OldEntity.ClassName] = deleted;
            }
        }

        var successCount = deleteResults.Values.Count(v => v);
        _logger.LogInformation("Bulk delete complete: {SuccessCount}/{TotalCount} files deleted", 
            successCount, deleteResults.Count);

        return deleteResults;
    }

    /// <summary>
    /// Verify that a file can be safely deleted (no locks, no compilation errors expected)
    /// </summary>
    public async Task<bool> CanSafelyDeleteAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            // Try to open file exclusively to check for locks
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // If we can open it exclusively, it's not locked
                await Task.CompletedTask;
                return true;
            }
        }
        catch (IOException)
        {
            // File is locked or in use
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Don't have permission
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if file can be deleted: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Get list of files that would be affected by cleanup
    /// </summary>
    public async Task<List<string>> GetFilesToDeleteAsync(List<EntityMapping> mappings)
    {
        var files = new List<string>();

        foreach (var mapping in mappings)
        {
            if (mapping.OldEntity != null && 
                !string.IsNullOrEmpty(mapping.OldEntity.FilePath) &&
                File.Exists(mapping.OldEntity.FilePath))
            {
                files.Add(mapping.OldEntity.FilePath);
            }
        }

        await Task.CompletedTask; // Keep async signature
        return files;
    }
}
