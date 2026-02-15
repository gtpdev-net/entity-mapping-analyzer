namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Represents a property within an entity class
/// </summary>
public class PropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public List<string> Attributes { get; set; } = new();

    /// <summary>
    /// Computed property showing full type representation
    /// </summary>
    public string FullType => IsNullable && !Type.EndsWith("?") ? $"{Type}?" : Type;
}
