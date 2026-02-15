namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Tracks the status of a replacement operation for an entity mapping
/// </summary>
public enum ReplacementStatus
{
    NotStarted,
    InProgress,
    Completed,
    Failed,
    Skipped
}
