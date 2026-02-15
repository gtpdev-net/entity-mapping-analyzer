using System.Diagnostics;
using System.Text;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Validates that the target codebase compiles after replacement
/// </summary>
public class CompilationValidator
{
    private readonly ILogger<CompilationValidator> _logger;

    public CompilationValidator(ILogger<CompilationValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validate compilation by running dotnet build on the target path
    /// </summary>
    /// <param name="targetPath">Path to solution, project, or directory containing projects</param>
    /// <param name="timeoutSeconds">Timeout in seconds (default: 300 = 5 minutes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of success status and list of error messages</returns>
    public async Task<(bool success, List<string> errors)> ValidateCompilationAsync(
        string targetPath,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return (false, new List<string> { "Target path is empty" });
        }

        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            return (false, new List<string> { $"Target path not found: {targetPath}" });
        }

        try
        {
            _logger.LogInformation("Validating compilation for: {TargetPath}", targetPath);

            // Determine what to build
            var buildTarget = DetermineBuildTarget(targetPath);
            if (buildTarget == null)
            {
                return (false, new List<string> { "No solution or project file found in target path" });
            }

            _logger.LogInformation("Building: {BuildTarget}", buildTarget);

            // Run dotnet build
            var (exitCode, output, errors) = await RunDotnetBuildAsync(buildTarget, timeoutSeconds, cancellationToken);

            // Parse results
            var errorMessages = ParseBuildErrors(output, errors);

            bool success = exitCode == 0 && errorMessages.Count == 0;

            if (success)
            {
                _logger.LogInformation("Compilation validation successful");
            }
            else
            {
                _logger.LogWarning("Compilation validation failed with {ErrorCount} errors", errorMessages.Count);
            }

            return (success, errorMessages);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Compilation validation was cancelled");
            return (false, new List<string> { "Compilation validation was cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during compilation validation");
            return (false, new List<string> { $"Validation error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Determine the best build target (solution or project file)
    /// </summary>
    private string? DetermineBuildTarget(string targetPath)
    {
        // If it's a file, return it directly (should be .sln or .csproj)
        if (File.Exists(targetPath))
        {
            var ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (ext == ".sln" || ext == ".csproj" || ext == ".vbproj" || ext == ".fsproj")
            {
                return targetPath;
            }
            return null;
        }

        // If it's a directory, look for solution files first, then project files
        if (Directory.Exists(targetPath))
        {
            // Look for solution files
            var solutionFiles = Directory.GetFiles(targetPath, "*.sln", SearchOption.TopDirectoryOnly);
            if (solutionFiles.Length > 0)
            {
                return solutionFiles[0]; // Use the first solution file found
            }

            // Look for project files
            var projectFiles = Directory.GetFiles(targetPath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projectFiles.Length > 0)
            {
                return projectFiles[0]; // Use the first project file found
            }

            // Try looking in subdirectories
            projectFiles = Directory.GetFiles(targetPath, "*.csproj", SearchOption.AllDirectories);
            if (projectFiles.Length > 0)
            {
                return projectFiles[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Run dotnet build command
    /// </summary>
    private async Task<(int exitCode, string output, string errors)> RunDotnetBuildAsync(
        string buildTarget,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{buildTarget}\" --no-incremental",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(buildTarget) ?? Directory.GetCurrentDirectory()
        };

        using var process = new Process { StartInfo = processStartInfo };
        
        // Capture output streams
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for completion with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill the process if it times out or is cancelled
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore errors killing the process
            }

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Build process exceeded timeout of {timeoutSeconds} seconds");
            }

            throw;
        }

        var output = outputBuilder.ToString();
        var errors = errorBuilder.ToString();

        return (process.ExitCode, output, errors);
    }

    /// <summary>
    /// Parse build output to extract error messages
    /// </summary>
    private List<string> ParseBuildErrors(string output, string errors)
    {
        var errorMessages = new List<string>();
        var allOutput = output + "\n" + errors;

        // Split by lines and look for error patterns
        var lines = allOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Look for compiler errors (CS####) and other error patterns
            if (line.Contains(" error CS", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(" error MSB", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var cleanedLine = line.Trim();
                if (!string.IsNullOrWhiteSpace(cleanedLine))
                {
                    errorMessages.Add(cleanedLine);
                }
            }
        }

        // If no specific errors found but build failed (checking for failure indicators)
        if (errorMessages.Count == 0 && 
            (allOutput.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
             allOutput.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessages.Add("Build failed (see build output for details)");
        }

        return errorMessages;
    }

    /// <summary>
    /// Quick validation to check if dotnet CLI is available
    /// </summary>
    public async Task<bool> IsDotnetAvailableAsync()
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
