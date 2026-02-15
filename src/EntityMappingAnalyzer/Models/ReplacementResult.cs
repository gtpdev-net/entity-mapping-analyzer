namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Result of a replacement operation for a single entity mapping
/// </summary>
public class ReplacementResult
{
    public string MappingId { get; set; } = string.Empty;
    public ReplacementStatus Status { get; set; } = ReplacementStatus.NotStarted;
    public List<CodeLocation> LocationsModified { get; set; } = new();
    public string OldEntityFilePath { get; set; } = string.Empty;
    public bool OldEntityDeleted { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Summary statistics
    /// </summary>
    public int FilesModified => LocationsModified.Select(l => l.FilePath).Distinct().Count();
    public int TotalReplacements => LocationsModified.Count;
    public bool HasErrors => Errors.Any();
}
