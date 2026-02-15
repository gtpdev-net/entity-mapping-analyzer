using Microsoft.CodeAnalysis;
using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Orchestrates replacement operations across multiple entity mappings
/// </summary>
public class ReplacementOrchestratorService
{
    private readonly RoslynWorkspaceAnalyzer _workspaceAnalyzer;
    private readonly CodeRewriterService _codeRewriter;
    private readonly BackupService _backupService;
    private readonly EntityCleanupService _cleanupService;
    private readonly CompilationValidator _compilationValidator;
    private readonly MappingStorageService _storageService;
    private readonly ILogger<ReplacementOrchestratorService> _logger;
    
    // Cache workspace to avoid reloading for each file
    private Workspace? _cachedWorkspace;
    private string? _cachedWorkspacePath;

    public ReplacementOrchestratorService(
        RoslynWorkspaceAnalyzer workspaceAnalyzer,
        CodeRewriterService codeRewriter,
        BackupService backupService,
        EntityCleanupService cleanupService,
        CompilationValidator compilationValidator,
        MappingStorageService storageService,
        ILogger<ReplacementOrchestratorService> logger)
    {
        _workspaceAnalyzer = workspaceAnalyzer;
        _codeRewriter = codeRewriter;
        _backupService = backupService;
        _cleanupService = cleanupService;
        _compilationValidator = compilationValidator;
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Execute replacement operation for selected mappings
    /// </summary>
    public async Task<ReplacementOperation> ExecuteReplacementAsync(
        ReplacementOperation operation, 
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        operation.StartTime = DateTime.UtcNow;
        progress?.Report("Starting replacement operation...");

        try
        {
            // Step 1: Create backup if enabled
            if (operation.CreateBackup && !operation.IsDryRun)
            {
                progress?.Report("Creating backup...");
                var backupPath = await _backupService.CreateBackupAsync(operation.TargetCodebasePath);
                progress?.Report($"Backup created: {backupPath}");
            }

            // Step 2: Load mappings from storage
            var database = await _storageService.LoadAsync(_storageService.GetDefaultFilePath());
            if (database == null)
            {
                progress?.Report("ERROR: Could not load mapping database");
                operation.EndTime = DateTime.UtcNow;
                return operation;
            }

            // Step 3: Process each selected mapping
            foreach (var mappingId in operation.SelectedMappingIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    progress?.Report("Operation cancelled by user");
                    break;
                }

                var mapping = database.Mappings.FirstOrDefault(m => m.Id == mappingId);
                if (mapping == null)
                {
                    progress?.Report($"WARNING: Mapping {mappingId} not found, skipping...");
                    continue;
                }

                if (mapping.OldEntity == null || mapping.NewEntity == null)
                {
                    progress?.Report($"WARNING: Mapping {mappingId} has missing entity info, skipping...");
                    continue;
                }

                progress?.Report($"Processing: {mapping.OldEntity.ClassName} → {mapping.NewEntity.ClassName}");

                var result = await ProcessSingleMappingAsync(
                    mapping, 
                    operation.TargetCodebasePath,
                    operation.IsDryRun,
                    operation.DeleteOldEntities,
                    progress,
                    cancellationToken);

                operation.Results.Add(result);

                // Update mapping status in database
                mapping.ReplacementStatus = result.Status;
                mapping.LastReplacedDate = result.Timestamp;
                mapping.ReplacementResult = result;

                progress?.Report($"Completed {mapping.OldEntity.ClassName}: {result.Status} ({result.FilesModified} files, {result.TotalReplacements} replacements)");
            }

            // Step 4: Save updated mapping database
            if (!operation.IsDryRun)
            {
                await _storageService.SaveAsync(database, _storageService.GetDefaultFilePath());
            }

            // Step 5: Validate compilation if enabled
            if (operation.ValidateCompilation && !operation.IsDryRun)
            {
                progress?.Report("Validating compilation... (this may take a while)");
                
                try
                {
                    var (success, errors) = await _compilationValidator.ValidateCompilationAsync(
                        operation.TargetCodebasePath,
                        timeoutSeconds: 300,
                        cancellationToken);

                    if (success)
                    {
                        progress?.Report("✓ Compilation validation successful - no errors detected");
                        _logger.LogInformation("Compilation validation passed");
                    }
                    else
                    {
                        var errorSummary = errors.Count > 5 
                            ? $"{errors.Count} errors detected (showing first 5)" 
                            : $"{errors.Count} errors detected";
                        
                        progress?.Report($"⚠ Compilation validation failed: {errorSummary}");
                        
                        // Report first few errors
                        var errorsToReport = errors.Take(5);
                        foreach (var error in errorsToReport)
                        {
                            progress?.Report($"  ERROR: {error}");
                        }
                        
                        _logger.LogWarning("Compilation validation failed with {ErrorCount} errors", errors.Count);
                        
                        // Store validation errors in operation
                        operation.ValidationErrors = errors;
                    }
                }
                catch (TimeoutException)
                {
                    progress?.Report("⚠ Compilation validation timed out after 5 minutes");
                    _logger.LogWarning("Compilation validation timed out");
                }
                catch (Exception ex)
                {
                    progress?.Report($"⚠ Compilation validation error: {ex.Message}");
                    _logger.LogError(ex, "Error during compilation validation");
                }
            }

            operation.EndTime = DateTime.UtcNow;
            
            var summary = GenerateSummary(operation);
            progress?.Report(summary);

            _logger.LogInformation("Replacement operation completed: {Completed}/{Total} successful", 
                operation.Results.Count(r => r.Status == ReplacementStatus.Completed),
                operation.Results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during replacement operation");
            progress?.Report($"ERROR: {ex.Message}");
            operation.EndTime = DateTime.UtcNow;
        }
        finally
        {
            // Clean up cached workspace
            if (_cachedWorkspace != null)
            {
                _cachedWorkspace.Dispose();
                _cachedWorkspace = null;
                _cachedWorkspacePath = null;
            }
        }

        return operation;
    }

    /// <summary>
    /// Process a single entity mapping
    /// </summary>
    private async Task<ReplacementResult> ProcessSingleMappingAsync(
        EntityMapping mapping,
        string targetCodebasePath,
        bool isDryRun,
        bool deleteOldEntities,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new ReplacementResult
        {
            MappingId = mapping.Id,
            Status = ReplacementStatus.InProgress,
            Timestamp = DateTime.UtcNow,
            OldEntityFilePath = mapping.OldEntity?.FilePath ?? string.Empty
        };

        var startTime = DateTime.UtcNow;

        try
        {
            // Step 1: Find all references to the old entity
            progress?.Report($"  Finding references to {mapping.OldEntity!.ClassName}...");
            var references = await _workspaceAnalyzer.FindEntityReferencesAsync(
                targetCodebasePath,
                mapping.OldEntity!,
                cancellationToken);

            if (references.Count == 0)
            {
                progress?.Report($"  No references found for {mapping.OldEntity!.ClassName}");
                result.Status = ReplacementStatus.Skipped;
                result.Duration = DateTime.UtcNow - startTime;
                return result;
            }

            progress?.Report($"  Found {references.Count} references in {references.Select(r => r.FilePath).Distinct().Count()} files");

            // Step 2: Load the workspace and rewrite code
            if (!isDryRun)
            {
                progress?.Report($"  Rewriting code...");
                
                var modifiedLocations = await RewriteCodeInWorkspaceAsync(
                    targetCodebasePath,
                    mapping,
                    references,
                    cancellationToken);

                result.LocationsModified = modifiedLocations;
                progress?.Report($"  Modified {result.FilesModified} files");
            }
            else
            {
                result.LocationsModified = references;
                progress?.Report($"  [DRY RUN] Would modify {references.Select(r => r.FilePath).Distinct().Count()} files");
            }

            // Step 3: Delete old entity file if requested
            if (deleteOldEntities && !isDryRun && !string.IsNullOrEmpty(mapping.OldEntity!.FilePath))
            {
                progress?.Report($"  Deleting old entity file...");
                var deleted = await _cleanupService.DeleteOldEntityAsync(mapping, result);
                result.OldEntityDeleted = deleted;
                
                if (deleted)
                {
                    progress?.Report($"  Deleted: {mapping.OldEntity!.FilePath}");
                }
            }

            result.Status = ReplacementStatus.Completed;
        }
        catch (Exception ex)
        {
            result.Status = ReplacementStatus.Failed;
            result.Errors.Add(ex.Message);
            _logger.LogError(ex, "Error processing mapping {MappingId}", mapping.Id);
            progress?.Report($"  ERROR: {ex.Message}");
        }

        result.Duration = DateTime.UtcNow - startTime;
        return result;
    }

    /// <summary>
    /// Rewrite code in workspace using Roslyn
    /// </summary>
    private async Task<List<CodeLocation>> RewriteCodeInWorkspaceAsync(
        string workspacePath,
        EntityMapping mapping,
        List<CodeLocation> references,
        CancellationToken cancellationToken)
    {
        var modifiedLocations = new List<CodeLocation>();

        // Group references by file for efficiency
        var fileGroups = references.GroupBy(r => r.FilePath);

        foreach (var fileGroup in fileGroups)
        {
            var filePath = fileGroup.Key;
            
            try
            {
                // Load the workspace containing this file
                var workspace = await LoadWorkspaceForFileAsync(workspacePath, filePath);
                if (workspace == null)
                {
                    _logger.LogWarning("Could not load workspace for file: {FilePath}", filePath);
                    continue;
                }

                // Find the document
                var document = workspace.CurrentSolution.Projects
                    .SelectMany(p => p.Documents)
                    .FirstOrDefault(d => d.FilePath == filePath);

                if (document == null)
                {
                    _logger.LogWarning("Could not find document in workspace: {FilePath}", filePath);
                    continue;
                }

                // Rewrite the document
                var newDocument = await _codeRewriter.RewriteDocumentAsync(document, mapping, cancellationToken);

                // Apply changes and save
                if (newDocument != document)
                {
                    var text = await newDocument.GetTextAsync(cancellationToken);
                    await File.WriteAllTextAsync(filePath, text.ToString(), cancellationToken);
                    
                    modifiedLocations.AddRange(fileGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rewriting file: {FilePath}", filePath);
                // Continue processing other files
            }
        }

        return modifiedLocations;
    }

    /// <summary>
    /// Load workspace for a specific file (with caching)
    /// </summary>
    private async Task<Workspace?> LoadWorkspaceForFileAsync(string workspacePath, string filePath)
    {
        // Check if we already have the workspace cached
        if (_cachedWorkspace != null && _cachedWorkspacePath == workspacePath)
        {
            return _cachedWorkspace;
        }

        // Clear the old cache if path changed
        if (_cachedWorkspace != null && _cachedWorkspacePath != workspacePath)
        {
            _cachedWorkspace.Dispose();
            _cachedWorkspace = null;
            _cachedWorkspacePath = null;
        }

        // Load the workspace using the analyzer
        _cachedWorkspace = await _workspaceAnalyzer.LoadWorkspaceAsync(workspacePath, default);
        _cachedWorkspacePath = workspacePath;

        return _cachedWorkspace;
    }

    /// <summary>
    /// Generate summary report of the operation
    /// </summary>
    private string GenerateSummary(ReplacementOperation operation)
    {
        var completed = operation.Results.Count(r => r.Status == ReplacementStatus.Completed);
        var failed = operation.Results.Count(r => r.Status == ReplacementStatus.Failed);
        var skipped = operation.Results.Count(r => r.Status == ReplacementStatus.Skipped);
        var totalFiles = operation.Results.Sum(r => r.FilesModified);
        var totalReplacements = operation.Results.Sum(r => r.TotalReplacements);
        var duration = (operation.EndTime ?? DateTime.UtcNow) - operation.StartTime;

        return $@"
=== Replacement Operation Summary ===
Total Mappings: {operation.TotalMappings}
Completed: {completed}
Failed: {failed}
Skipped: {skipped}
Files Modified: {totalFiles}
Total Replacements: {totalReplacements}
Duration: {duration:mm\:ss}
{(operation.IsDryRun ? "[DRY RUN - No changes were made]" : "")}
=====================================";
    }

    /// <summary>
    /// Get preview of changes without executing them
    /// </summary>
    public async Task<Dictionary<string, int>> GetReplacementPreviewAsync(
        string targetCodebasePath,
        List<string> mappingIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var preview = new Dictionary<string, int>();

        var database = await _storageService.LoadAsync(_storageService.GetDefaultFilePath());
        if (database == null)
        {
            return preview;
        }

        foreach (var mappingId in mappingIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var mapping = database.Mappings.FirstOrDefault(m => m.Id == mappingId);
            if (mapping?.OldEntity == null)
            {
                continue;
            }

            progress?.Report($"Analyzing {mapping.OldEntity.ClassName}...");

            var references = await _workspaceAnalyzer.FindEntityReferencesAsync(
                targetCodebasePath,
                mapping.OldEntity,
                cancellationToken);

            var affectedFiles = references.Select(r => r.FilePath).Distinct().ToList();
            
            foreach (var file in affectedFiles)
            {
                if (!preview.ContainsKey(file))
                {
                    preview[file] = 0;
                }
                preview[file] += references.Count(r => r.FilePath == file);
            }
        }

        return preview;
    }
}
