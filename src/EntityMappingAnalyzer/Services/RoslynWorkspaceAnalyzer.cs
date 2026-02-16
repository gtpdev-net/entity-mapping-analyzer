using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using EntityMappingAnalyzer.Models;
using Buildalyzer;
using Buildalyzer.Workspaces;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Analyzes a Roslyn workspace to find all references to entities that need replacement
/// </summary>
public class RoslynWorkspaceAnalyzer
{
    private readonly ILogger<RoslynWorkspaceAnalyzer> _logger;

    public RoslynWorkspaceAnalyzer(ILogger<RoslynWorkspaceAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Find all references to an entity within a workspace
    /// </summary>
    public async Task<List<CodeLocation>> FindEntityReferencesAsync(
        string workspacePath, 
        EntityInfo entity,
        CancellationToken cancellationToken = default)
    {
        var locations = new List<CodeLocation>();

        try
        {
            // Load the workspace
            var workspace = await LoadWorkspaceAsync(workspacePath, cancellationToken);
            if (workspace == null)
            {
                _logger.LogWarning("Failed to load workspace from path: {Path}", workspacePath);
                return locations;
            }

            // Find the symbol for the entity
            var symbol = await FindEntitySymbolAsync(workspace, entity, cancellationToken);
            if (symbol == null)
            {
                _logger.LogWarning("Could not find symbol for entity: {ClassName}", entity.ClassName);
                return locations;
            }

            // Find all references to the symbol
            var references = await SymbolFinder.FindReferencesAsync(symbol, workspace.CurrentSolution, cancellationToken);

            foreach (var reference in references)
            {
                foreach (var referenceLocation in reference.Locations)
                {
                    var location = referenceLocation.Location;
                    if (location.IsInSource)
                    {
                        var codeLocation = await CreateCodeLocationAsync(
                            location, 
                            workspace.CurrentSolution, 
                            cancellationToken);
                        
                        if (codeLocation != null)
                        {
                            locations.Add(codeLocation);
                        }
                    }
                }
            }

            _logger.LogInformation("Found {Count} references to entity {ClassName}", locations.Count, entity.ClassName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding entity references for {ClassName}", entity.ClassName);
        }

        return locations;
    }

    /// <summary>
    /// Find all files that reference the entity (faster than finding all exact locations)
    /// </summary>
    public async Task<List<string>> FindAffectedFilesAsync(
        string workspacePath,
        EntityInfo entity,
        CancellationToken cancellationToken = default)
    {
        var locations = await FindEntityReferencesAsync(workspacePath, entity, cancellationToken);
        return locations.Select(l => l.FilePath).Distinct().ToList();
    }

    /// <summary>
    /// Load a workspace from a path (can be a .sln, .csproj, or directory)
    /// </summary>
    public async Task<Workspace?> LoadWorkspaceAsync(string path, CancellationToken cancellationToken)
    {

        // Try to load as solution or project file
        if (File.Exists(path))
        {
            if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadSolutionAsync(path, cancellationToken);
            }
            else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadProjectAsync(path, cancellationToken);
            }
        }

        // Try to find solution or project files in directory
        if (Directory.Exists(path))
        {
            var slnFiles = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                return await LoadSolutionAsync(slnFiles[0], cancellationToken);
            }

            var projFiles = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projFiles.Length > 0)
            {
                return await LoadProjectAsync(projFiles[0], cancellationToken);
            }

            // Fallback: create adhoc workspace from C# files
            return await LoadDirectoryAsAdhocWorkspaceAsync(path, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Load a solution file
    /// </summary>
    private async Task<Workspace?> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken)
    {
        // Try Buildalyzer first - it's more robust for complex project setups
        _logger.LogInformation("Loading solution with Buildalyzer: {Path}", solutionPath);
        var workspace = await LoadWithBuildalyzerAsync(solutionPath, cancellationToken);
        
        if (workspace != null && workspace.CurrentSolution.Projects.Any())
        {
            _logger.LogInformation("Successfully loaded {ProjectCount} projects with Buildalyzer", 
                workspace.CurrentSolution.Projects.Count());
            return workspace;
        }

        // Fallback to MSBuildWorkspace if Buildalyzer fails
        _logger.LogInformation("Buildalyzer failed, trying MSBuildWorkspace: {Path}", solutionPath);
        try
        {
            var msbuildWorkspace = MSBuildWorkspace.Create();
            var diagnostics = new List<WorkspaceDiagnostic>();
            
            msbuildWorkspace.WorkspaceFailed += (sender, e) =>
            {
                diagnostics.Add(e.Diagnostic);
                _logger.LogWarning("Workspace loading warning: {Message}", e.Diagnostic.Message);
            };

            await msbuildWorkspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            
            // Check if there were critical failures
            var criticalFailures = diagnostics.Where(d => 
                d.Kind == WorkspaceDiagnosticKind.Failure &&
                (d.Message.Contains("could not be found") || d.Message.Contains("failed")));
            
            if (criticalFailures.Any() && !msbuildWorkspace.CurrentSolution.Projects.Any())
            {
                _logger.LogWarning("MSBuildWorkspace had critical failures, using Buildalyzer result");
                return workspace; // Return Buildalyzer result even if it was null
            }
            
            return msbuildWorkspace;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load solution with MSBuildWorkspace: {Path}", solutionPath);
            return workspace; // Return Buildalyzer result even if it was null
        }
    }

    /// <summary>
    /// Load a project file
    /// </summary>
    private async Task<Workspace?> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        // Try Buildalyzer first - it's more robust for complex project setups
        _logger.LogInformation("Loading project with Buildalyzer: {Path}", projectPath);
        var workspace = await LoadWithBuildalyzerAsync(projectPath, cancellationToken);
        
        if (workspace != null && workspace.CurrentSolution.Projects.Any())
        {
            _logger.LogInformation("Successfully loaded project with Buildalyzer");
            return workspace;
        }

        // Fallback to MSBuildWorkspace if Buildalyzer fails
        _logger.LogInformation("Buildalyzer failed, trying MSBuildWorkspace: {Path}", projectPath);
        try
        {
            var msbuildWorkspace = MSBuildWorkspace.Create();
            var diagnostics = new List<WorkspaceDiagnostic>();
            
            msbuildWorkspace.WorkspaceFailed += (sender, e) =>
            {
                diagnostics.Add(e.Diagnostic);
                _logger.LogWarning("Workspace loading warning: {Message}", e.Diagnostic.Message);
            };

            await msbuildWorkspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            
            // Check if there were critical failures
            var criticalFailures = diagnostics.Where(d => 
                d.Kind == WorkspaceDiagnosticKind.Failure &&
                (d.Message.Contains("could not be found") || d.Message.Contains("failed")));
            
            if (criticalFailures.Any() && !msbuildWorkspace.CurrentSolution.Projects.Any())
            {
                _logger.LogWarning("MSBuildWorkspace had critical failures, using Buildalyzer result");
                return workspace; // Return Buildalyzer result even if it was null
            }
            
            return msbuildWorkspace;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project with MSBuildWorkspace: {Path}", projectPath);
            return workspace; // Return Buildalyzer result even if it was null
        }
    }

    /// <summary>
    /// Load workspace using Buildalyzer (more robust for complex project setups)
    /// </summary>
    private async Task<Workspace?> LoadWithBuildalyzerAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Ensure MEF host services are initialized with language service assemblies
            // This must be done before creating any workspace
            var hostServices = Microsoft.CodeAnalysis.Host.Mef.MefHostServices.Create(
                Microsoft.CodeAnalysis.Host.Mef.MefHostServices.DefaultAssemblies);
            
            var manager = new AnalyzerManager(path);
            
            // Create workspace with properly initialized host services
            var workspace = new AdhocWorkspace(hostServices);
            
            // Add all analyzed projects to the workspace
            foreach (var project in manager.Projects.Values)
            {
                try
                {
                    var analyzerResults = project.Build();
                    foreach (var result in analyzerResults)
                    {
                        if (result != null)
                        {
                            result.AddToWorkspace(workspace);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add project {ProjectPath} to workspace", project.ProjectFile.Path);
                }
            }
            
            await Task.CompletedTask; // Keep async signature consistent
            return workspace;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load workspace with Buildalyzer: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Load a directory as an adhoc workspace (fallback when no solution/project found)
    /// </summary>
    private async Task<Workspace> LoadDirectoryAsAdhocWorkspaceAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "AdhocProject",
            "AdhocProject",
            LanguageNames.CSharp);

        var project = workspace.AddProject(projectInfo);

        // Add all C# files in directory
        var csFiles = Directory.GetFiles(directoryPath, "*.cs", SearchOption.AllDirectories);
        foreach (var file in csFiles)
        {
            var documentInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id),
                Path.GetFileName(file),
                filePath: file,
                loader: new FileTextLoader(file, System.Text.Encoding.UTF8));

            workspace.AddDocument(documentInfo);
        }

        await Task.CompletedTask; // Keep async signature consistent
        return workspace;
    }

    /// <summary>
    /// Find the symbol for an entity in the workspace
    /// </summary>
    private async Task<INamedTypeSymbol?> FindEntitySymbolAsync(
        Workspace workspace, 
        EntityInfo entity,
        CancellationToken cancellationToken)
    {
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // Try to find the symbol by fully qualified name
            var fullyQualifiedName = string.IsNullOrEmpty(entity.Namespace) 
                ? entity.ClassName 
                : $"{entity.Namespace}.{entity.ClassName}";

            var symbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
            if (symbol != null)
            {
                return symbol;
            }

            // Fallback: search through all types
            var allTypes = compilation.GetSymbolsWithName(
                entity.ClassName, 
                SymbolFilter.Type, 
                cancellationToken);

            foreach (var typeSymbol in allTypes.OfType<INamedTypeSymbol>())
            {
                if (typeSymbol.ContainingNamespace?.ToDisplayString() == entity.Namespace)
                {
                    return typeSymbol;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Create a CodeLocation from a Roslyn location
    /// </summary>
    private async Task<CodeLocation?> CreateCodeLocationAsync(
        Location location,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(location.SourceTree);
        if (document == null)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
        var sourceText = await document.GetTextAsync(cancellationToken);
        var line = sourceText.Lines[lineSpan.StartLinePosition.Line];
        var snippet = line.ToString().Trim();

        // Determine the type of reference
        var root = await location.SourceTree!.GetRootAsync(cancellationToken);
        var node = root.FindNode(location.SourceSpan);
        var locationType = DetermineLocationType(node);

        return new CodeLocation
        {
            FilePath = location.SourceTree.FilePath,
            LineNumber = lineSpan.StartLinePosition.Line + 1, // 1-based
            Column = lineSpan.StartLinePosition.Character + 1, // 1-based
            CodeSnippet = snippet,
            Type = locationType
        };
    }

    /// <summary>
    /// Determine the type of code location based on syntax node
    /// </summary>
    private LocationType DetermineLocationType(SyntaxNode node)
    {
        // Walk up the tree to find the containing construct
        var current = node;
        while (current != null)
        {
            switch (current)
            {
                case UsingDirectiveSyntax:
                    return LocationType.UsingDirective;
                
                case ClassDeclarationSyntax:
                case StructDeclarationSyntax:
                case InterfaceDeclarationSyntax:
                    return LocationType.TypeDeclaration;
                
                case BaseListSyntax:
                    return LocationType.BaseClass;
                
                case PropertyDeclarationSyntax:
                case FieldDeclarationSyntax:
                    return LocationType.PropertyType;
                
                case ParameterSyntax:
                    return LocationType.MethodParameter;
                
                case MethodDeclarationSyntax method:
                    if (method.ReturnType.Span.Contains(node.Span))
                        return LocationType.ReturnType;
                    break;
                
                case GenericNameSyntax:
                case TypeArgumentListSyntax:
                    return LocationType.GenericTypeArgument;
                
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                    return LocationType.Instantiation;
                
                case QueryExpressionSyntax:
                case FromClauseSyntax:
                    return LocationType.LinqQuery;
            }

            current = current.Parent;
        }

        return LocationType.Other;
    }
}
