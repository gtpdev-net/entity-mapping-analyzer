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
public class RoslynWorkspaceAnalyzer : IDisposable
{
    private readonly ILogger<RoslynWorkspaceAnalyzer> _logger;
    
    // Cache workspace to avoid reloading the same solution multiple times
    private Workspace? _cachedWorkspace;
    private string? _cachedWorkspacePath;
    private readonly object _cacheLock = new object();
    private bool _disposed = false;

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting entity reference search for {ClassName} in workspace: {Path}", 
                entity.ClassName, workspacePath);
            
            // Load the workspace
            var workspace = await LoadWorkspaceAsync(workspacePath, cancellationToken);
            if (workspace == null)
            {
                _logger.LogWarning("Failed to load workspace from path: {Path}", workspacePath);
                return locations;
            }

            _logger.LogDebug("Workspace loaded with {ProjectCount} projects, searching for symbol {ClassName}",
                workspace.CurrentSolution.Projects.Count(), entity.ClassName);

            // Find the symbol for the entity
            var symbol = await FindEntitySymbolAsync(workspace, entity, cancellationToken);
            if (symbol == null)
            {
                _logger.LogWarning("Could not find symbol for entity: {ClassName}", entity.ClassName);
                return locations;
            }

            _logger.LogDebug("Symbol found: {SymbolName}, searching for references...", symbol.ToDisplayString());

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

            stopwatch.Stop();
            _logger.LogInformation("Found {Count} references to entity {ClassName} in {ElapsedMs}ms", 
                locations.Count, entity.ClassName, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding entity references for {ClassName} after {ElapsedMs}ms", 
                entity.ClassName, stopwatch.ElapsedMilliseconds);
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
        // Normalize the path for consistent caching
        var normalizedPath = Path.GetFullPath(path);
        
        // Check cache first
        lock (_cacheLock)
        {
            if (_cachedWorkspace != null && _cachedWorkspacePath == normalizedPath)
            {
                _logger.LogInformation("Using cached workspace for: {Path}", normalizedPath);
                return _cachedWorkspace;
            }
        }

        // Try to load as solution or project file
        Workspace? workspace = null;
        
        if (File.Exists(normalizedPath))
        {
            if (normalizedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                workspace = await LoadSolutionAsync(normalizedPath, cancellationToken);
            }
            else if (normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                workspace = await LoadProjectAsync(normalizedPath, cancellationToken);
            }
        }
        else if (Directory.Exists(normalizedPath))
        {
            // Try to find solution or project files in directory
            var slnFiles = Directory.GetFiles(normalizedPath, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                workspace = await LoadSolutionAsync(slnFiles[0], cancellationToken);
            }
            else
            {
                var projFiles = Directory.GetFiles(normalizedPath, "*.csproj", SearchOption.TopDirectoryOnly);
                if (projFiles.Length > 0)
                {
                    workspace = await LoadProjectAsync(projFiles[0], cancellationToken);
                }
                else
                {
                    // Fallback: create adhoc workspace from C# files
                    workspace = await LoadDirectoryAsAdhocWorkspaceAsync(normalizedPath, cancellationToken);
                }
            }
        }

        // Cache the workspace if successfully loaded
        if (workspace != null)
        {
            lock (_cacheLock)
            {
                // Dispose old cached workspace if path changed
                if (_cachedWorkspace != null && _cachedWorkspacePath != normalizedPath)
                {
                    _cachedWorkspace.Dispose();
                }
                
                _cachedWorkspace = workspace;
                _cachedWorkspacePath = normalizedPath;
            }
        }

        return workspace;
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
            
            _logger.LogDebug("Initializing Buildalyzer for path: {Path}", path);
            var manager = new AnalyzerManager(path);
            
            var totalProjects = manager.Projects.Count;
            _logger.LogDebug("Found {TotalProjects} projects to analyze", totalProjects);
            
            // Create workspace with properly initialized host services
            var workspace = new AdhocWorkspace(hostServices);
            
            // Track added projects to avoid duplicates
            var addedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projectCount = 0;
            var buildStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Add all analyzed projects to the workspace
            var processedCount = 0;
            foreach (var project in manager.Projects.Values)
            {
                var projectPath = project.ProjectFile.Path;
                var projectName = Path.GetFileNameWithoutExtension(projectPath);
                
                try
                {
                    processedCount++;
                    
                    _logger.LogDebug("[{Current}/{Total}] Processing project: {ProjectName}", 
                        processedCount, totalProjects, projectName);
                    
                    // Skip if already processed this project
                    if (addedProjects.Contains(projectPath))
                    {
                        _logger.LogDebug("Project {ProjectName} already processed, skipping", projectName);
                        continue;
                    }
                    
                    var analyzerResults = project.Build();
                    
                    // Take only the first result for each project to avoid duplicates
                    // (multiple results can occur when a project has multiple target frameworks)
                    var result = analyzerResults.FirstOrDefault();
                    if (result != null)
                    {
                        try
                        {
                            result.AddToWorkspace(workspace);
                            addedProjects.Add(projectPath);
                            projectCount++;
                            _logger.LogDebug("Successfully added project {ProjectName} to workspace ({ProjectCount}/{Total})", 
                                projectName, projectCount, totalProjects);
                        }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("already contains the specified project"))
                        {
                            // Project already in workspace, this can happen with project references
                            _logger.LogDebug("Project {ProjectName} already in workspace, skipping", projectName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add project {ProjectName} to workspace", projectName);
                }
            }
            
            buildStopwatch.Stop();
            _logger.LogInformation("Successfully loaded {ProjectCount} projects with Buildalyzer in {ElapsedSeconds:F1}s", 
                projectCount, buildStopwatch.Elapsed.TotalSeconds);
            
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
    
    /// <summary>
    /// Clears the cached workspace to force a reload on next access
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            if (_cachedWorkspace != null)
            {
                _cachedWorkspace.Dispose();
                _cachedWorkspace = null;
                _cachedWorkspacePath = null;
                _logger.LogInformation("Workspace cache cleared");
            }
        }
    }
    
    /// <summary>
    /// Dispose the analyzer and any cached workspace
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
            
        ClearCache();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
