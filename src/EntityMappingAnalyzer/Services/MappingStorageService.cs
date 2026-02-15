using System.Text.Json;
using EntityMappingAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Handles saving and loading of mapping databases to/from JSON files
/// </summary>
public class MappingStorageService
{
    private readonly string _defaultFilePath;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public MappingStorageService(IOptions<MappingAnalyzerOptions> options)
    {
        _defaultFilePath = GetAbsolutePath(options.Value.DefaultOutputPath);
    }

    /// <summary>
    /// Gets the default file path for the mapping database
    /// </summary>
    public string GetDefaultFilePath() => _defaultFilePath;

    /// <summary>
    /// Saves a mapping database to a JSON file
    /// </summary>
    public async Task SaveAsync(MappingDatabase database, string filePath)
    {
        try
        {
            // Update last modified date
            database.LastModifiedDate = DateTime.UtcNow;

            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Serialize to JSON
            var json = JsonSerializer.Serialize(database, JsonOptions);

            // Write to file
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save mapping database to {filePath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads a mapping database from a JSON file
    /// </summary>
    public async Task<MappingDatabase?> LoadAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var database = JsonSerializer.Deserialize<MappingDatabase>(json, JsonOptions);

            // Validate version compatibility
            if (database != null && database.Version != "1.0")
            {
                throw new InvalidOperationException($"Unsupported mapping database version: {database.Version}");
            }

            return database;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse mapping database from {filePath}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load mapping database from {filePath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks if a mapping database file exists
    /// </summary>
    public bool Exists(string filePath)
    {
        return File.Exists(filePath);
    }

    /// <summary>
    /// Deletes a mapping database file
    /// </summary>
    public void Delete(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Creates a backup of the mapping database
    /// </summary>
    public string CreateBackup(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Mapping database not found: {filePath}");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{filePath}.backup-{timestamp}";

        File.Copy(filePath, backupPath, overwrite: true);
        
        return backupPath;
    }

    /// <summary>
    /// Exports mapping database to a human-readable markdown report
    /// </summary>
    public async Task ExportToMarkdownAsync(MappingDatabase database, string outputPath)
    {
        var lines = new List<string>
        {
            "# Entity Mapping Report",
            "",
            $"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"**Old Entities Path:** {database.OldEntitiesPath}",
            $"**Dacpac Entities Path:** {database.DacpacEntitiesPath}",
            "",
            "## Summary",
            "",
            $"- **Total Mappings:** {database.TotalMappings}",
            $"- **Verified:** {database.VerifiedMappings}",
            $"- **Unverified:** {database.UnverifiedMappings}",
            $"- **Completed Replacements:** {database.CompletedReplacements}",
            $"- **Average Confidence:** {database.AverageConfidence:P1}",
            "",
            "## Mappings",
            ""
        };

        foreach (var mapping in database.Mappings.OrderByDescending(m => m.ConfidenceScore))
        {
            var status = mapping.IsVerified ? "✓ VERIFIED" : "⚠ UNVERIFIED";
            var replacement = mapping.ReplacementStatus == ReplacementStatus.Completed ? " [REPLACED]" : "";
            
            lines.Add($"### {mapping.OldEntity?.ClassName ?? "Unknown"} → {mapping.NewEntity?.ClassName ?? "No Match"} {status}{replacement}");
            lines.Add("");
            lines.Add($"**Confidence:** {mapping.ConfidenceScore:P1}");
            lines.Add("");
            lines.Add("**Match Reasons:**");
            foreach (var reason in mapping.MatchReasons)
            {
                lines.Add($"- {reason}");
            }
            lines.Add("");
            
            if (mapping.PropertyMappings.Any())
            {
                lines.Add("**Property Mappings:**");
                lines.Add("");
                lines.Add("| Old Property | Old Type | New Property | New Type | Status |");
                lines.Add("|--------------|----------|--------------|----------|--------|");
                
                foreach (var prop in mapping.PropertyMappings)
                {
                    var propStatus = prop.IsMatched ? "✓" : (prop.Action == MappingAction.Manual ? "Manual" : "⚠");
                    lines.Add($"| {prop.OldPropertyName} | {prop.OldPropertyType} | {prop.NewPropertyName} | {prop.NewPropertyType} | {propStatus} |");
                }
                lines.Add("");
            }

            if (!string.IsNullOrEmpty(mapping.ManualNotes))
            {
                lines.Add($"**Notes:** {mapping.ManualNotes}");
                lines.Add("");
            }
        }

        await File.WriteAllLinesAsync(outputPath, lines);
    }

    /// <summary>
    /// Gets the absolute path from a relative or absolute path
    /// </summary>
    private string GetAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var currentDir = Directory.GetCurrentDirectory();
        if (currentDir.EndsWith("bin/Debug/net8.0") || currentDir.EndsWith("bin\\Debug\\net8.0"))
        {
            currentDir = Path.GetFullPath(Path.Combine(currentDir, "..", "..", ".."));
        }

        return Path.GetFullPath(Path.Combine(currentDir, path));
    }
}
