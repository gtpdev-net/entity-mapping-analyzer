# Configuration - hardcoded paths
$EntityFolder = "./src/EntityMappingAnalyzer/Models"
$OutputFile = $null  # Set to a file path like "./dbcontext-setup.txt" to save output to file, or $null for console output

# Function to extract entity class names and their namespaces from C# files
function Get-EntityClasses {
    param(
        [string]$FolderPath
    )
    
    $entityInfo = @()
    
    # Get all .cs files in the folder
    $csFiles = Get-ChildItem -Path $FolderPath -Filter "*.cs" -Recurse
    
    foreach ($file in $csFiles) {
        $content = Get-Content -Path $file.FullName -Raw
        
        # Extract namespace
        $namespaceMatch = [regex]::Match($content, 'namespace\s+([\w\.]+)')
        $namespace = if ($namespaceMatch.Success) { $namespaceMatch.Groups[1].Value } else { $null }
        
        # Match public class declarations
        # Pattern matches: public class ClassName or public sealed class ClassName, etc.
        $matches = [regex]::Matches($content, 'public\s+(?:sealed\s+)?(?:abstract\s+)?class\s+(\w+)')
        
        foreach ($match in $matches) {
            $className = $match.Groups[1].Value
            
            # Filter out common non-entity classes (optional)
            if ($className -notmatch '(Controller|Service|Helper|Util|Config|Startup|Program)') {
                $entityInfo += [PSCustomObject]@{
                    ClassName = $className
                    Namespace = $namespace
                    FilePath = $file.FullName
                }
            }
        }
    }
    
    return $entityInfo | Sort-Object -Property ClassName -Unique
}

# Function to generate using statements from unique namespaces
function Generate-UsingStatements {
    param(
        [array]$EntityInfo
    )
    
    $namespaces = $EntityInfo | Where-Object { $_.Namespace -ne $null } | Select-Object -ExpandProperty Namespace -Unique | Sort-Object
    
    $statements = @()
    foreach ($ns in $namespaces) {
        $statements += "using $ns;"
    }
    
    return $statements
}

# Function to generate DbSet statements
function Generate-DbSetStatements {
    param(
        [array]$EntityInfo
    )
    
    $statements = @()
    
    foreach ($entity in $EntityInfo) {
        $className = $entity.ClassName
        
        # Pluralize entity name (simple approach - just add 's')
        $pluralName = "${className}s"
        
        # Handle special cases for pluralization
        if ($className -match 'y$') {
            $pluralName = $className -replace 'y$', 'ies'
        }
        elseif ($className -match '(s|x|z|ch|sh)$') {
            $pluralName = "${className}es"
        }
        
        $statement = "public DbSet<$className> $pluralName { get; set; } = null!;"
        $statements += $statement
    }
    
    return $statements
}

# Main execution
try {
    # Validate folder exists
    if (-not (Test-Path -Path $EntityFolder)) {
        Write-Error "Folder not found: $EntityFolder"
        exit 1
    }
    
    Write-Host "Scanning for entity classes in: $EntityFolder" -ForegroundColor Cyan
    
    # Get entity classes with namespace info
    $entities = Get-EntityClasses -FolderPath $EntityFolder
    
    if ($entities.Count -eq 0) {
        Write-Warning "No entity classes found in the specified folder."
        exit 0
    }
    
    Write-Host "Found $($entities.Count) entity class(es):" -ForegroundColor Green
    $entities | ForEach-Object { 
        Write-Host "  - $($_.ClassName)" -NoNewline
        if ($_.Namespace) {
            Write-Host " (namespace: $($_.Namespace))" -ForegroundColor DarkGray
        } else {
            Write-Host " (no namespace found)" -ForegroundColor DarkYellow
        }
    }
    
    Write-Host "`nGenerating using statements and DbSet declarations..." -ForegroundColor Cyan
    Write-Host ""
    
    # Generate statements
    $usingStatements = Generate-UsingStatements -EntityInfo $entities
    $dbSetStatements = Generate-DbSetStatements -EntityInfo $entities
    
    # Prepare output
    $output = @()
    
    if ($usingStatements.Count -gt 0) {
        $output += "// Using statements:"
        $output += $usingStatements
        $output += ""
    }
    
    $output += "// DbSet declarations:"
    $output += $dbSetStatements
    
    # Output results
    if ($OutputFile) {
        $output | Out-File -FilePath $OutputFile -Encoding utf8
        Write-Host "Statements written to: $OutputFile" -ForegroundColor Green
    }
    else {
        $output | ForEach-Object { 
            if ($_ -match '^//') {
                Write-Host $_ -ForegroundColor Yellow
            } else {
                Write-Host $_
            }
        }
    }
    
    Write-Host ""
    Write-Host "Done!" -ForegroundColor Green
}
catch {
    Write-Error "An error occurred: $_"
    exit 1
}
