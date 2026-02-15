using FuzzySharp;
using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Analyzes similarity between entity classes to suggest mappings
/// </summary>
public class SimilarityAnalyzer
{
    /// <summary>
    /// Compares two entities and returns a confidence score (0.0 - 1.0) with match reasons
    /// Scoring: Table name (40%), Class name (20%), Property overlap (30%), Namespace pattern (10%)
    /// </summary>
    public (double confidence, List<string> reasons) AnalyzeSimilarity(EntityInfo oldEntity, EntityInfo newEntity)
    {
        var reasons = new List<string>();
        double totalScore = 0.0;

        // 1. Table name comparison (40% weight)
        double tableScore = CompareTableNames(oldEntity.TableName, newEntity.TableName, reasons);
        totalScore += tableScore * 0.4;

        // 2. Class name similarity (20% weight)
        double classScore = CompareClassNames(oldEntity.ClassName, newEntity.ClassName, reasons);
        totalScore += classScore * 0.2;

        // 3. Property overlap (30% weight)
        double propertyScore = CompareProperties(oldEntity.Properties, newEntity.Properties, reasons);
        totalScore += propertyScore * 0.3;

        // 4. Namespace pattern (10% weight)
        double namespaceScore = CompareNamespaces(oldEntity.Namespace, newEntity.Namespace, reasons);
        totalScore += namespaceScore * 0.1;

        return (Math.Round(totalScore, 3), reasons);
    }

    /// <summary>
    /// Compares table names for exact or fuzzy match
    /// </summary>
    private double CompareTableNames(string oldTable, string newTable, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(oldTable) || string.IsNullOrWhiteSpace(newTable))
        {
            return 0.0;
        }

        // Exact match (case-insensitive)
        if (oldTable.Equals(newTable, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("✓ Table name exact match");
            return 1.0;
        }

        // Fuzzy match using Levenshtein distance
        int similarity = Fuzz.Ratio(oldTable.ToLower(), newTable.ToLower());
        
        if (similarity >= 90)
        {
            reasons.Add($"✓ Table name very similar ({similarity}% match)");
            return similarity / 100.0;
        }
        else if (similarity >= 70)
        {
            reasons.Add($"⚠ Table name somewhat similar ({similarity}% match)");
            return similarity / 100.0;
        }
        else
        {
            reasons.Add($"✗ Table name mismatch ('{oldTable}' vs '{newTable}')");
            return 0.0;
        }
    }

    /// <summary>
    /// Compares class names for similarity
    /// </summary>
    private double CompareClassNames(string oldClass, string newClass, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(oldClass) || string.IsNullOrWhiteSpace(newClass))
        {
            return 0.0;
        }

        // Exact match
        if (oldClass.Equals(newClass, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("✓ Class name exact match");
            return 1.0;
        }

        // Fuzzy match
        int similarity = Fuzz.Ratio(oldClass.ToLower(), newClass.ToLower());
        
        if (similarity >= 85)
        {
            reasons.Add($"✓ Class name {similarity}% similar");
            return similarity / 100.0;
        }
        else if (similarity >= 60)
        {
            reasons.Add($"⚠ Class name {similarity}% similar");
            return similarity / 100.0;
        }
        else
        {
            reasons.Add($"✗ Class name different ('{oldClass}' vs '{newClass}')");
            return 0.0;
        }
    }

    /// <summary>
    /// Compares property lists to calculate overlap percentage
    /// </summary>
    private double CompareProperties(List<Models.PropertyInfo> oldProps, List<Models.PropertyInfo> newProps, List<string> reasons)
    {
        if (!oldProps.Any() || !newProps.Any())
        {
            return 0.0;
        }

        int matchedCount = 0;
        var unmatchedOld = new List<string>();
        var unmatchedNew = new List<string>();

        // Check how many old properties have matches in new properties
        foreach (var oldProp in oldProps)
        {
            var match = newProps.FirstOrDefault(np => 
                np.Name.Equals(oldProp.Name, StringComparison.OrdinalIgnoreCase) &&
                TypesMatch(oldProp.Type, np.Type));

            if (match != null)
            {
                matchedCount++;
            }
            else
            {
                unmatchedOld.Add(oldProp.Name);
            }
        }

        // Identify new properties that don't have matches
        foreach (var newProp in newProps)
        {
            var match = oldProps.FirstOrDefault(op => 
                op.Name.Equals(newProp.Name, StringComparison.OrdinalIgnoreCase) &&
                TypesMatch(op.Type, newProp.Type));

            if (match == null)
            {
                unmatchedNew.Add(newProp.Name);
            }
        }

        int totalUniqueProps = oldProps.Count + newProps.Count - matchedCount;
        double overlapScore = totalUniqueProps > 0 ? (double)matchedCount / totalUniqueProps : 0.0;

        if (matchedCount == oldProps.Count && matchedCount == newProps.Count)
        {
            reasons.Add($"✓ All {matchedCount} properties matched perfectly");
        }
        else if (matchedCount > 0)
        {
            reasons.Add($"⚠ {matchedCount}/{oldProps.Count} properties matched");
            
            if (unmatchedOld.Any() && unmatchedOld.Count <= 3)
            {
                reasons.Add($"  Missing in new: {string.Join(", ", unmatchedOld)}");
            }
            if (unmatchedNew.Any() && unmatchedNew.Count <= 3)
            {
                reasons.Add($"  Added in new: {string.Join(", ", unmatchedNew)}");
            }
        }
        else
        {
            reasons.Add("✗ No matching properties found");
        }

        return overlapScore;
    }

    /// <summary>
    /// Compares two type strings to see if they match (handles nullable differences)
    /// </summary>
    private bool TypesMatch(string type1, string type2)
    {
        // Normalize types (remove nullable markers for comparison)
        var normalized1 = type1.TrimEnd('?');
        var normalized2 = type2.TrimEnd('?');

        return normalized1.Equals(normalized2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares namespaces for pattern similarity
    /// </summary>
    private double CompareNamespaces(string oldNamespace, string newNamespace, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(oldNamespace) || string.IsNullOrWhiteSpace(newNamespace))
        {
            return 0.5; // Neutral score if namespace info missing
        }

        // Exact match
        if (oldNamespace.Equals(newNamespace, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("✓ Namespace exact match");
            return 1.0;
        }

        // Check if they share common parts
        var oldParts = oldNamespace.Split('.');
        var newParts = newNamespace.Split('.');

        int commonParts = 0;
        int maxLength = Math.Min(oldParts.Length, newParts.Length);

        for (int i = 0; i < maxLength; i++)
        {
            if (oldParts[i].Equals(newParts[i], StringComparison.OrdinalIgnoreCase))
            {
                commonParts++;
            }
            else
            {
                break; // Stop at first difference
            }
        }

        double namespaceScore = (double)commonParts / Math.Max(oldParts.Length, newParts.Length);

        if (namespaceScore > 0.3)
        {
            reasons.Add($"⚠ Namespace partially matches ({commonParts} common parts)");
        }

        return namespaceScore;
    }

    /// <summary>
    /// Compare properties specifically to suggest property-level mappings
    /// </summary>
    public List<PropertyMapping> GeneratePropertyMappings(EntityInfo oldEntity, EntityInfo newEntity)
    {
        var mappings = new List<PropertyMapping>();

        foreach (var oldProp in oldEntity.Properties)
        {
            var mapping = new PropertyMapping
            {
                OldPropertyName = oldProp.Name,
                OldPropertyType = oldProp.FullType
            };

            // Find exact match first
            var exactMatch = newEntity.Properties.FirstOrDefault(np => 
                np.Name.Equals(oldProp.Name, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                mapping.NewPropertyName = exactMatch.Name;
                mapping.NewPropertyType = exactMatch.FullType;
                mapping.IsMatched = TypesMatch(oldProp.Type, exactMatch.Type);
                mapping.Action = MappingAction.Auto;
            }
            else
            {
                // Try fuzzy match for renamed properties
                var fuzzyMatch = FindFuzzyPropertyMatch(oldProp, newEntity.Properties);
                
                if (fuzzyMatch.HasValue && fuzzyMatch.Value.similarity >= 70)
                {
                    mapping.NewPropertyName = fuzzyMatch.Value.property.Name;
                    mapping.NewPropertyType = fuzzyMatch.Value.property.FullType;
                    mapping.IsMatched = false; // Fuzzy match, needs review
                    mapping.Action = MappingAction.Auto;
                }
                else
                {
                    // No match found
                    mapping.NewPropertyName = string.Empty;
                    mapping.NewPropertyType = string.Empty;
                    mapping.IsMatched = false;
                    mapping.Action = MappingAction.Manual; // Requires manual mapping
                }
            }

            mappings.Add(mapping);
        }

        return mappings;
    }

    /// <summary>
    /// Find the best fuzzy match for a property
    /// </summary>
    private (Models.PropertyInfo property, int similarity)? FindFuzzyPropertyMatch(
        Models.PropertyInfo oldProp, List<Models.PropertyInfo> newProps)
    {
        int bestScore = 0;
        Models.PropertyInfo? bestMatch = null;

        foreach (var newProp in newProps)
        {
            int score = Fuzz.Ratio(oldProp.Name.ToLower(), newProp.Name.ToLower());
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = newProp;
            }
        }

        return bestMatch != null ? (bestMatch, bestScore) : null;
    }
}
