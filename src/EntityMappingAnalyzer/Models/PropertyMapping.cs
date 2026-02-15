namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Represents how a property from an old entity maps to a new entity
/// </summary>
public class PropertyMapping
{
    public string OldPropertyName { get; set; } = string.Empty;
    public string OldPropertyType { get; set; } = string.Empty;
    public string NewPropertyName { get; set; } = string.Empty;
    public string NewPropertyType { get; set; } = string.Empty;
    public bool IsMatched { get; set; }
    public MappingAction Action { get; set; } = MappingAction.Auto;
}

/// <summary>
/// Defines how a property mapping was created
/// </summary>
public enum MappingAction
{
    Auto,      // Automatically matched by analyzer
    Manual,    // Manually specified by user
    Ignored    // Property should not be mapped
}
