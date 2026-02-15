namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Configuration for a replacement operation
/// </summary>
public class ReplacementOperation
{
    public string TargetCodebasePath { get; set; } = string.Empty;
    public List<string> SelectedMappingIds { get; set; } = new();
    public bool IsDryRun { get; set; }
    public bool CreateBackup { get; set; } = true;
    public bool DeleteOldEntities { get; set; }
    public bool ValidateCompilation { get; set; } = true;
    public List<ReplacementResult> Results { get; set; } = new();
    public List<string> ValidationErrors { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Progress tracking
    /// </summary>
    public int TotalMappings => SelectedMappingIds.Count;
    public int ProcessedMappings => Results.Count;
    public int PercentComplete => TotalMappings > 0 ? (ProcessedMappings * 100) / TotalMappings : 0;
    public bool IsComplete => ProcessedMappings >= TotalMappings;
}
