namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Root object containing all entity mappings and metadata
/// </summary>
public class MappingDatabase
{
    public string Version { get; set; } = "1.0";
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedDate { get; set; } = DateTime.UtcNow;
    public string OldEntitiesPath { get; set; } = string.Empty;
    public string DacpacEntitiesPath { get; set; } = string.Empty;
    public List<EntityMapping> Mappings { get; set; } = new();

    /// <summary>
    /// Statistics for display
    /// </summary>
    public int TotalMappings => Mappings.Count;
    public int VerifiedMappings => Mappings.Count(m => m.IsVerified);
    public int UnverifiedMappings => Mappings.Count(m => !m.IsVerified);
    public int CompletedReplacements => Mappings.Count(m => m.ReplacementStatus == ReplacementStatus.Completed);
    public double AverageConfidence => Mappings.Any() ? Mappings.Average(m => m.ConfidenceScore) : 0;
}
