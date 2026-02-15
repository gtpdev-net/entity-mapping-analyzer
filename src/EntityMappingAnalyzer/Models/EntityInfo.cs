namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Represents metadata about a C# entity class discovered via Roslyn analysis
/// </summary>
public class EntityInfo
{
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<PropertyInfo> Properties { get; set; } = new();

    /// <summary>
    /// Computed property for display purposes
    /// </summary>
    public string FullyQualifiedName => string.IsNullOrEmpty(Namespace) 
        ? ClassName 
        : $"{Namespace}.{ClassName}";
}
