using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Scans C# files using Roslyn to extract entity class metadata
/// </summary>
public class RoslynEntityScanner
{
    /// <summary>
    /// Scans a directory for C# entity files and extracts metadata
    /// </summary>
    public async Task<List<EntityInfo>> ScanDirectoryAsync(string path)
    {
        var entities = new List<EntityInfo>();

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var csFiles = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);

        foreach (var filePath in csFiles)
        {
            try
            {
                var fileEntities = await ScanFileAsync(filePath);
                entities.AddRange(fileEntities);
            }
            catch (Exception ex)
            {
                // Log error and continue with other files
                Console.WriteLine($"[WARNING] Failed to parse {filePath}: {ex.Message}");
            }
        }

        return entities;
    }

    /// <summary>
    /// Scans a single C# file for entity classes
    /// </summary>
    public async Task<List<EntityInfo>> ScanFileAsync(string filePath)
    {
        var entities = new List<EntityInfo>();
        var sourceCode = await File.ReadAllTextAsync(filePath);

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = await syntaxTree.GetRootAsync();

        // Create a compilation to get semantic model
        var compilation = CSharpCompilation.Create("TempCompilation")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);

        // Find all class declarations
        var classDeclarations = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(IsLikelyEntityClass);

        foreach (var classDecl in classDeclarations)
        {
            var entity = ExtractEntityInfo(classDecl, semanticModel, filePath);
            if (entity != null)
            {
                entities.Add(entity);
            }
        }

        return entities;
    }

    /// <summary>
    /// Heuristic to determine if a class is likely an entity
    /// </summary>
    private bool IsLikelyEntityClass(ClassDeclarationSyntax classDecl)
    {
        // Check if class is public
        if (!classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            return false;
        }

        // Must have at least one public property
        var hasPublicProperties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Any(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));

        if (!hasPublicProperties)
        {
            return false;
        }

        // Check for common entity indicators
        var attributes = classDecl.AttributeLists.SelectMany(al => al.Attributes).ToList();
        var hasTableAttribute = attributes.Any(a => a.Name.ToString().Contains("Table"));
        
        // If has [Table] attribute, definitely an entity
        if (hasTableAttribute)
        {
            return true;
        }

        // Check if it looks like a POCO entity (has properties with common entity patterns)
        var propertyCount = classDecl.Members.OfType<PropertyDeclarationSyntax>().Count();
        return propertyCount >= 2; // At least 2 properties to be considered an entity
    }

    /// <summary>
    /// Extracts entity metadata from a class declaration
    /// </summary>
    private EntityInfo? ExtractEntityInfo(ClassDeclarationSyntax classDecl, SemanticModel semanticModel, string filePath)
    {
        var entity = new EntityInfo
        {
            ClassName = classDecl.Identifier.Text,
            FilePath = filePath
        };

        // Extract namespace
        var namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDecl != null)
        {
            entity.Namespace = namespaceDecl.Name.ToString();
        }
        else
        {
            // Check for file-scoped namespace (C# 10+)
            var fileScopedNamespace = classDecl.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
            if (fileScopedNamespace != null)
            {
                entity.Namespace = fileScopedNamespace.Name.ToString();
            }
        }

        // Extract table name from [Table] attribute if present
        var tableAttribute = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Contains("Table"));

        if (tableAttribute != null && tableAttribute.ArgumentList?.Arguments.Count > 0)
        {
            var tableName = tableAttribute.ArgumentList.Arguments[0].Expression.ToString().Trim('"');
            entity.TableName = tableName;
        }
        else
        {
            // Default to class name if no [Table] attribute
            entity.TableName = entity.ClassName;
        }

        // Extract properties
        var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));

        foreach (var prop in properties)
        {
            var propInfo = ExtractPropertyInfo(prop, semanticModel);
            entity.Properties.Add(propInfo);
        }

        return entity;
    }

    /// <summary>
    /// Extracts property metadata including type and attributes
    /// </summary>
    private Models.PropertyInfo ExtractPropertyInfo(PropertyDeclarationSyntax propDecl, SemanticModel semanticModel)
    {
        var propInfo = new Models.PropertyInfo
        {
            Name = propDecl.Identifier.Text
        };

        // Get type information
        var typeInfo = semanticModel.GetTypeInfo(propDecl.Type);
        if (typeInfo.Type != null)
        {
            propInfo.Type = typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            propInfo.IsNullable = typeInfo.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                  (typeInfo.Type.ToString()?.EndsWith("?") ?? false);
        }
        else
        {
            // Fallback to syntax-based type name
            propInfo.Type = propDecl.Type.ToString() ?? "object";
            propInfo.IsNullable = propInfo.Type.EndsWith("?");
        }

        // Extract attributes
        foreach (var attrList in propDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                var attrArgs = attr.ArgumentList?.Arguments.Select(a => a.Expression.ToString()).ToList() ?? new List<string>();
                
                var attrString = attrArgs.Any() 
                    ? $"[{attrName}({string.Join(", ", attrArgs)})]"
                    : $"[{attrName}]";
                
                propInfo.Attributes.Add(attrString);
            }
        }

        return propInfo;
    }
}
