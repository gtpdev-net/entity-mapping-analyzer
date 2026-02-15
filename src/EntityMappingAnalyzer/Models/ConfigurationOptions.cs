namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Configuration options for the mapping analyzer
/// </summary>
public class MappingAnalyzerOptions
{
    public string DefaultOldEntitiesPath { get; set; } = "./existing-entities";
    public string DefaultDacpacEntitiesPath { get; set; } = "./output";
    public string DefaultOutputPath { get; set; } = "./entity-mapping.json";
    public double ConfidenceThresholdHigh { get; set; } = 0.8;
    public double ConfidenceThresholdMedium { get; set; } = 0.6;
}

/// <summary>
/// Configuration options for replacement operations
/// </summary>
public class ReplacementSettings
{
    public bool AutoBackup { get; set; } = true;
    public bool ValidateCompilation { get; set; } = true;
    public bool DeleteOldEntities { get; set; }
    public int BackupRetentionDays { get; set; } = 30;
    public int MaxConcurrentReplacements { get; set; } = 5;
}
