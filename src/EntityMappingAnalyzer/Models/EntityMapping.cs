namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Represents the mapping between an old entity and a new dacpac entity
/// </summary>
public class EntityMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public EntityInfo? OldEntity { get; set; }
    public EntityInfo? NewEntity { get; set; }
    public double ConfidenceScore { get; set; }
    public List<string> MatchReasons { get; set; } = new();
    public List<PropertyMapping> PropertyMappings { get; set; } = new();
    public bool IsVerified { get; set; }
    public string ManualNotes { get; set; } = string.Empty;
    
    // Replacement tracking
    public ReplacementStatus ReplacementStatus { get; set; } = ReplacementStatus.NotStarted;
    public DateTime? LastReplacedDate { get; set; }
    public ReplacementResult? ReplacementResult { get; set; }

    /// <summary>
    /// Helper to check if this mapping is ready for replacement
    /// </summary>
    public bool IsReadyForReplacement => IsVerified && 
                                          OldEntity != null && 
                                          NewEntity != null &&
                                          ReplacementStatus == ReplacementStatus.NotStarted;
}
