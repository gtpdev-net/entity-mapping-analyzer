using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Orchestrates the scanning and matching process to generate entity mappings
/// </summary>
public class MappingGeneratorService
{
    private readonly RoslynEntityScanner _scanner;
    private readonly SimilarityAnalyzer _analyzer;

    public MappingGeneratorService(RoslynEntityScanner scanner, SimilarityAnalyzer analyzer)
    {
        _scanner = scanner;
        _analyzer = analyzer;
    }

    /// <summary>
    /// Generates entity mappings by scanning and comparing old and new entity directories
    /// </summary>
    public async Task<MappingDatabase> GenerateMappingsAsync(
        string oldEntitiesPath, 
        string dacpacEntitiesPath, 
        double confidenceThreshold = 0.6,
        IProgress<string>? progress = null)
    {
        progress?.Report($"Scanning old entities in: {oldEntitiesPath}");
        var oldEntities = await _scanner.ScanDirectoryAsync(oldEntitiesPath);
        progress?.Report($"Found {oldEntities.Count} old entities");

        progress?.Report($"Scanning dacpac entities in: {dacpacEntitiesPath}");
        var newEntities = await _scanner.ScanDirectoryAsync(dacpacEntitiesPath);
        progress?.Report($"Found {newEntities.Count} dacpac entities");

        var database = new MappingDatabase
        {
            OldEntitiesPath = oldEntitiesPath,
            DacpacEntitiesPath = dacpacEntitiesPath,
            CreatedDate = DateTime.UtcNow,
            LastModifiedDate = DateTime.UtcNow
        };

        progress?.Report("Analyzing similarities and generating mappings...");

        int processed = 0;
        foreach (var oldEntity in oldEntities)
        {
            processed++;
            progress?.Report($"Processing {processed}/{oldEntities.Count}: {oldEntity.ClassName}");

            // Find best match for this old entity
            EntityInfo? bestMatch = null;
            double bestConfidence = 0.0;
            List<string> bestReasons = new();

            foreach (var newEntity in newEntities)
            {
                var (confidence, reasons) = _analyzer.AnalyzeSimilarity(oldEntity, newEntity);
                
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestMatch = newEntity;
                    bestReasons = reasons;
                }
            }

            // Create mapping if confidence above threshold
            if (bestMatch != null && bestConfidence >= confidenceThreshold)
            {
                var mapping = new EntityMapping
                {
                    OldEntity = oldEntity,
                    NewEntity = bestMatch,
                    ConfidenceScore = bestConfidence,
                    MatchReasons = bestReasons,
                    IsVerified = false, // Requires manual verification
                    PropertyMappings = _analyzer.GeneratePropertyMappings(oldEntity, bestMatch)
                };

                database.Mappings.Add(mapping);
                progress?.Report($"  ✓ Matched with {bestMatch.ClassName} (confidence: {bestConfidence:P0})");
            }
            else
            {
                // Create unmatched mapping for manual review
                var mapping = new EntityMapping
                {
                    OldEntity = oldEntity,
                    NewEntity = null,
                    ConfidenceScore = bestConfidence,
                    MatchReasons = new List<string> { "No confident match found" },
                    IsVerified = false
                };

                database.Mappings.Add(mapping);
                progress?.Report($"  ⚠ No match found (best confidence: {bestConfidence:P0})");
            }
        }

        database.LastModifiedDate = DateTime.UtcNow;
        progress?.Report($"Completed! Generated {database.Mappings.Count} mappings");

        return database;
    }

    /// <summary>
    /// Updates an existing mapping database with new scan results
    /// </summary>
    public async Task<MappingDatabase> RefreshMappingsAsync(
        MappingDatabase existingDatabase,
        IProgress<string>? progress = null)
    {
        return await GenerateMappingsAsync(
            existingDatabase.OldEntitiesPath,
            existingDatabase.DacpacEntitiesPath,
            0.6,
            progress);
    }
}
