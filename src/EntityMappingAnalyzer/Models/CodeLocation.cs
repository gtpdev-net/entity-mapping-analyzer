namespace EntityMappingAnalyzer.Models;

/// <summary>
/// Represents a specific location in code where an entity is referenced
/// </summary>
public class CodeLocation
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int Column { get; set; }
    public string CodeSnippet { get; set; } = string.Empty;
    public LocationType Type { get; set; }
}

/// <summary>
/// Type of code reference
/// </summary>
public enum LocationType
{
    UsingDirective,
    TypeDeclaration,
    BaseClass,
    PropertyType,
    MethodParameter,
    ReturnType,
    GenericTypeArgument,
    Instantiation,
    LinqQuery,
    Other
}
