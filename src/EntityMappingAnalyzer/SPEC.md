# Entity Mapping Analyzer - Specification

## Overview

**Entity Mapping Analyzer** is a Blazor Server web application that automates the process of mapping existing entity classes to newly generated DACPAC entities, and then replacing the old entities throughout a codebase with their new counterparts. The application uses Roslyn-based static analysis to intelligently match entities, provides a web UI for manual verification and adjustment, and orchestrates safe code replacement operations.

## Problem Statement

When regenerating entity classes from database schemas (using tools like DACPACEntityGenerator), developers face the challenge of systematically replacing existing entities throughout large codebases. With 300+ entities used across repositories, contexts, mappers, services, and controllers, manual replacement is:

- **Time-consuming**: Identifying all usages and making consistent changes
- **Error-prone**: Risk of missing references, typos, or inconsistent updates
- **Tedious**: Repetitive work without added value
- **Risky**: Breaking changes without proper verification

## Solution

A two-phase automated system:

1. **Entity Mapping Phase**: Scan existing and new entities, compare them using fuzzy matching algorithms, and present mappings in a web UI for user review and verification
2. **Replacement Phase**: Use Roslyn to find all references to old entities, rewrite code to use new entities, and safely delete old entity files

## Target Users

- Backend developers refactoring entity layers
- Database-first development teams regenerating entities from schema changes
- DevOps engineers modernizing legacy codebases
- Teams migrating between ORM frameworks

## Key Features

### Phase 1: Entity Mapping & Verification

#### 1.1 Automatic Entity Discovery
- **Roslyn-based scanning**: Parse C# files to extract entity metadata
- **Heuristic classification**: Identify entity classes vs other classes using:
  - Public property patterns
  - Common attributes (`[Table]`, `[Key]`, etc.)
  - Inheritance patterns
  - POCO structure
- **Metadata extraction**:
  - Class name and namespace
  - File path
  - Table name (from attributes or conventions)
  - Properties with types and attributes
  - Full type information via semantic model

#### 1.2 Intelligent Entity Matching
- **Similarity scoring algorithm** with weighted factors:
  - Table name match: 40%
  - Class name similarity: 20%
  - Property overlap: 30%
  - Namespace pattern: 10%
- **Fuzzy string matching** using Levenshtein distance
- **Property-level comparison**:
  - Exact property name/type matches
  - Fuzzy matching for renamed properties
  - Type compatibility checking
- **Confidence thresholds**: Configurable minimum confidence (default: 60%)
- **Match reason tracking**: Detailed explanation for each suggested mapping

#### 1.3 Web-based Review Interface
- **Dashboard**:
  - Configuration form for scan paths
  - Progress indicators during scanning
  - Summary statistics
  - Quick navigation to mappings
- **Mapping Grid**:
  - Sortable/filterable table of all mappings
  - Color-coded confidence indicators:
    - Green: Verified mappings
    - Yellow: Low confidence (<0.7)
    - Red: Unmapped entities
  - Inline editing capabilities
  - Bulk operations
- **Statistics Dashboard**:
  - Total mappings count
  - Verified vs unverified breakdown
  - Average confidence score
  - Confidence distribution (high/medium/low)
- **Property Detail Modal**:
  - Side-by-side property comparison
  - Drag-and-drop or dropdown property mapping
  - Highlight renamed/unmapped properties
  - Mark properties to ignore

#### 1.4 Manual Mapping Creation
- **Custom mapping form**:
  - Select old entity from dropdown
  - Select new entity from dropdown
  - Auto-attempt property matching
  - Manual property adjustment
  - Confidence set to 1.0 (manual override)

#### 1.5 Persistent Storage
- **JSON database format**:
  - Human-readable and version-controllable
  - Stores mappings with metadata
  - Tracks verification status
  - Records replacement history
- **Auto-save functionality**: Changes persist immediately
- **Backup creation**: Timestamped backups before modifications
- **Import/export**: Markdown report generation

### Phase 2: Automated Code Replacement

#### 2.1 Impact Analysis & Preview
- **Usage discovery**: Find all references using Roslyn `SymbolFinder`
- **Reference categorization**:
  - Using directives
  - Type declarations
  - Base classes/interfaces
  - Property/field types
  - Method parameters and return types
  - Generic type arguments
  - Object instantiations
  - LINQ query references
- **Affected file list**: Complete inventory before changes
- **Code snippet preview**: Show before/after for each location
- **Impact statistics**:
  - Total files affected
  - Total line changes
  - Estimated processing time

#### 2.2 Roslyn-based Code Rewriting
- **Syntax tree manipulation**:
  - Update using directives
  - Replace type references
  - Handle qualified names
  - Update generic type parameters
- **Property renaming**: If properties changed names between old and new
- **Format preservation**: Maintain original whitespace and comments
- **Semantic verification**: Ensure type compatibility

#### 2.3 Safe Replacement Orchestration
- **Pre-replacement backup**:
  - ZIP archive of all C# files
  - Timestamped for easy identification
  - Stored in `./backups/` directory
- **Atomic operations**: Each mapping processed independently
- **Progress tracking**:
  - Real-time status updates
  - Current file being modified
  - Percentage complete
  - Success/failure per mapping
- **Error handling**:
  - Continue processing on individual failures
  - Log detailed error messages
  - Track which mappings succeeded/failed

#### 2.4 Post-Replacement Actions
- **Old entity cleanup**:
  - Delete old entity files after successful replacement
  - Optional: Move to archive instead of delete
  - Safety check: Ensure no remaining references
  - Clean empty directories
- **Compilation validation**:
  - Optional: Run `dotnet build` after replacement
  - Report compilation errors
  - Offer rollback if validation fails
- **Detailed results report**:
  - Summary statistics
  - Per-mapping breakdown
  - File change inventory
  - Error log
  - Markdown export

#### 2.5 Rollback Capability
- **Backup restoration**: Restore from timestamped backup ZIP
- **Revert to pre-replacement state**: One-click rollback
- **Backup management**: Configurable retention period

### Phase 3: Settings & Configuration

#### 3.1 Application Settings
- **Paths**:
  - Default old entities path
  - Default DACPAC entities path
  - Default output JSON path
- **Thresholds**:
  - Confidence threshold for auto-mapping
  - Fuzzy match sensitivity
- **Replacement options**:
  - Auto-create backups (default: true)
  - Delete old entities (default: false)
  - Validate compilation (default: true)
  - Backup retention days
  - Max concurrent replacements

#### 3.2 Persistent Configuration
- **appsettings.json**: Server-side defaults
- **LocalStorage**: User-specific preferences in browser
- **Settings UI**: Form-based configuration editor

## User Workflows

### Workflow 1: First-Time Mapping Setup

1. User opens application at `http://localhost:5000`
2. Enter paths on Dashboard:
   - Old entities: `./src/Data/Entities`
   - DACPAC entities: `./output/SQLPROD01/CustomerDB`
   - Output file: `./entity-mapping.json`
3. Adjust confidence threshold slider (60-100%)
4. Click **"Scan & Generate Mappings"**
5. Wait for progress indicator (scanning + analysis)
6. Navigate to **Mappings** page
7. Review auto-generated mappings:
   - Verify high-confidence matches (green rows)
   - Investigate low-confidence matches (yellow rows)
   - Handle unmapped entities (red rows)
8. Click row to open property detail modal
9. Adjust property mappings if needed
10. Click **"Verify"** for each correct mapping
11. Save changes (auto-saved)

### Workflow 2: Manual Mapping Addition

1. From Mappings page, click **"Add Manual Mapping"**
2. Select old entity from dropdown
3. Select new entity from dropdown
4. Review auto-suggested property mappings
5. Adjust property mappings manually
6. Click **"Add Mapping"**
7. Mapping appears in grid with confidence 100%
8. Verify and save

### Workflow 3: Selective Replacement

1. On Mappings page, select checkboxes for specific mappings
2. Click **"Replace Selected"** in toolbar
3. Preview page shows:
   - Selected mappings
   - Affected files and locations
   - Code snippets with changes
4. Enter target codebase path: `./src`
5. Configure options:
   - ☑ Create backup before replacement
   - ☐ Delete old entity files
   - ☑ Validate compilation after replacement
6. Click **"Continue to Replace"**
7. Execution page shows:
   - Real-time progress bar
   - Current mapping being processed
   - Live log messages
8. View results page:
   - Success/failure summary
   - Detailed change log
   - Option to rollback if needed

### Workflow 4: Bulk Replacement

1. On Mappings page, click **"Select All Verified"**
2. Click **"Replace Selected"** or use **"Start Replacement Wizard"**
3. Wizard guides through 5 steps:
   - Step 1: Confirm selected mappings
   - Step 2: Configure target path and options
   - Step 3: Review impact analysis
   - Step 4: Execute replacement with progress
   - Step 5: Review results
4. Download markdown report
5. Verify application builds successfully

### Workflow 5: Rollback After Issues

1. On Results page, note problems
2. Click **"Restore from Backup"**
3. Select backup from list
4. Confirm restoration
5. Files reverted to pre-replacement state
6. Investigate issues, adjust mappings
7. Retry replacement

## Technical Architecture

### Technology Stack

- **Framework**: ASP.NET Core 8.0 (Blazor Server)
- **Language**: C# 12
- **UI**: Blazor Server-Side Rendering
- **Static Analysis**: Microsoft.CodeAnalysis (Roslyn)
- **Project Loading**: Buildalyzer
- **String Matching**: FuzzySharp (Levenshtein distance)
- **Persistence**: JSON (System.Text.Json)
- **Backup**: System.IO.Compression (ZIP)

### Project Structure

```
EntityMappingAnalyzer/
├── Models/                      # Domain models
│   ├── EntityInfo.cs           # Entity metadata
│   ├── PropertyInfo.cs         # Property metadata
│   ├── EntityMapping.cs        # Mapping relationships
│   ├── PropertyMapping.cs      # Property-level mappings
│   ├── MappingDatabase.cs      # Root database object
│   ├── CodeLocation.cs         # Reference tracking
│   ├── ReplacementStatus.cs    # Status enum
│   ├── ReplacementResult.cs    # Operation results
│   ├── ReplacementOperation.cs # Operation configuration
│   └── ConfigurationOptions.cs # App settings models
├── Services/                    # Business logic
│   ├── RoslynEntityScanner.cs          # Entity scanning
│   ├── SimilarityAnalyzer.cs           # Fuzzy matching
│   ├── MappingGeneratorService.cs      # Mapping creation
│   ├── MappingStorageService.cs        # JSON persistence
│   ├── RoslynWorkspaceAnalyzer.cs      # Usage finding
│   ├── CodeRewriterService.cs          # Code transformation
│   ├── ReplacementOrchestratorService.cs # Coordination
│   ├── BackupService.cs                # Backup/restore
│   ├── EntityCleanupService.cs         # File deletion
│   └── CompilationValidator.cs         # Build verification
├── Pages/                       # Blazor pages
│   ├── Index.razor             # Dashboard
│   ├── Mappings.razor          # Mapping grid
│   ├── ManualMapping.razor     # Manual entry form
│   ├── Settings.razor          # Configuration
│   ├── ReplacementPreview.razor       # Impact analysis
│   ├── ReplacementExecution.razor     # Progress tracking
│   └── ReplacementResults.razor       # Results report
├── Components/                  # Reusable UI components
│   ├── PropertyMappingModal.razor     # Property detail view
│   ├── BulkOperationsToolbar.razor    # Bulk actions
│   ├── MappingStatistics.razor        # Stats cards
│   └── ReplacementWizard.razor        # Guided workflow
├── Utilities/                   # Helper classes
├── wwwroot/                     # Static assets
├── Program.cs                   # Application entry
└── appsettings.json            # Configuration

```

### Data Models

#### EntityInfo
```csharp
{
  "namespace": "OldSystem.Data.Entities",
  "className": "UserProfile",
  "filePath": "./existing-entities/UserProfile.cs",
  "tableName": "user_profile",
  "properties": [...]
}
```

#### EntityMapping
```csharp
{
  "oldEntity": { EntityInfo },
  "newEntity": { EntityInfo },
  "confidenceScore": 0.95,
  "matchReasons": ["Table name exact match", "15/16 properties matched"],
  "propertyMappings": [...],
  "isVerified": false,
  "replacementStatus": "NotStarted",
  "lastReplacedDate": null
}
```

#### MappingDatabase
```csharp
{
  "version": "1.0",
  "createdDate": "2026-02-15T10:30:00Z",
  "lastModifiedDate": "2026-02-15T14:20:00Z",
  "oldEntitiesPath": "./src/Data/Entities",
  "dacpacEntitiesPath": "./output/SQLPROD01/CustomerDB",
  "mappings": [...]
}
```

### Confidence Scoring Algorithm

```
Confidence Score = 
  (TableNameScore × 0.40) +
  (ClassNameScore × 0.20) +
  (PropertyScore × 0.30) +
  (NamespaceScore × 0.10)

Where:
- TableNameScore: 1.0 for exact match, 0.0 for mismatch
- ClassNameScore: Levenshtein similarity (0.0-1.0)
- PropertyScore: (MatchingProperties / TotalUniqueProperties)
- NamespaceScore: (CommonParts / MaxParts)
```

## Security Considerations

- **Local execution only**: No external data transmission
- **Backup before destructive operations**: Always create backups
- **No credential storage**: No database or API credentials needed
- **File system access**: Limited to configured directories
- **Validation before actions**: User must verify before replacement

## Performance Considerations

- **Lazy loading**: Scan only when requested
- **Cached semantic models**: Reuse Roslyn compilations
- **Async operations**: Non-blocking UI during long operations
- **Configurable concurrency**: Limit parallel file processing
- **Incremental saves**: Auto-save doesn't block UI

## Future Enhancements

### Potential Features
- **CLI mode**: Headless execution for CI/CD pipelines
- **Diff visualization**: Side-by-side code comparison UI
- **Undo history**: Multi-level undo for mapping changes
- **Batch processing**: Process multiple projects at once
- **Custom matching rules**: User-defined heuristics
- **Integration with Git**: Auto-commit changes with detailed messages
- **Analytics dashboard**: Track replacement success rates
- **Export to CSV/Excel**: Alternative data formats
- **Migration scripts**: Generate SQL migration scripts alongside code changes

### Extensibility Points
- **Custom scanners**: Plugin system for different entity types
- **Custom rewriters**: Support for non-C# languages
- **Custom storage**: Database backends (SQLite, SQL Server)
- **Notification system**: Email/Slack notifications on completion

## Success Criteria

The application is considered successful when:

1. ✅ It correctly identifies 90%+ of entity mappings automatically
2. ✅ Users can verify and adjust all mappings via web UI
3. ✅ Replacement operations complete without breaking builds
4. ✅ Users can process 300+ entities in under 30 minutes
5. ✅ Zero data loss (backups always work)
6. ✅ Clear visibility into what changes will be made
7. ✅ Easy rollback when issues are discovered

## Glossary

- **DACPAC Entity**: Entity class generated from database schema
- **Old Entity**: Existing entity class in current codebase
- **Mapping**: Relationship between old and new entity
- **Confidence Score**: Algorithmic similarity measure (0.0-1.0)
- **Verification**: User confirmation that a mapping is correct
- **Replacement**: Process of updating code to use new entity
- **Roslyn**: .NET compiler platform for code analysis
- **Semantic Model**: Roslyn's type system representation
- **Syntax Tree**: Abstract syntax tree of C# code

---

**Document Version**: 1.0  
**Last Updated**: February 15, 2026  
**Status**: Active Development
