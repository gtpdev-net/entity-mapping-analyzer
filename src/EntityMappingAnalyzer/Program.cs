using EntityMappingAnalyzer.Services;
using EntityMappingAnalyzer.Models;
using Microsoft.Build.Locator;
using System.Reflection;
using System.Runtime.Loader;

// Add assembly resolver to help locate System.Composition assemblies at runtime
// This is necessary because Roslyn dynamically loads these assemblies
AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
{
    var assemblyName = new AssemblyName(args.Name);
    
    // Help locate System.Composition assemblies
    if (assemblyName.Name?.StartsWith("System.Composition") == true)
    {
        try
        {
            var applicationPath = AppContext.BaseDirectory;
            var assemblyPath = Path.Combine(applicationPath, $"{assemblyName.Name}.dll");
            
            if (File.Exists(assemblyPath))
            {
                Console.WriteLine($"Loading {assemblyName.Name} from {assemblyPath}");
                return Assembly.LoadFrom(assemblyPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to resolve {assemblyName.Name}: {ex.Message}");
        }
    }
    
    return null;
};

// Pre-load System.Composition assemblies required by Roslyn workspaces
// This ensures they're available in the AppDomain before Roslyn tries to load them
try
{
    var compositionAssemblies = new[]
    {
        "System.Composition.AttributedModel",
        "System.Composition.Convention",
        "System.Composition.Hosting",
        "System.Composition.Runtime",
        "System.Composition.TypedParts"
    };
    
    foreach (var assemblyName in compositionAssemblies)
    {
        var assembly = Assembly.Load($"{assemblyName}, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
        Console.WriteLine($"Pre-loaded: {assembly.GetName().Name}");
    }
    
    // Pre-load Roslyn language service assemblies for MEF composition
    var roslynAssemblies = new[]
    {
        "Microsoft.CodeAnalysis",
        "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.CodeAnalysis.VisualBasic",
        "Microsoft.CodeAnalysis.Workspaces",
        "Microsoft.CodeAnalysis.CSharp.Workspaces",
        "Microsoft.CodeAnalysis.VisualBasic.Workspaces"
    };
    
    foreach (var assemblyName in roslynAssemblies)
    {
        var assembly = Assembly.Load(assemblyName);
        Console.WriteLine($"Pre-loaded: {assembly.GetName().Name}");
    }
    
    Console.WriteLine("Successfully pre-loaded all required assemblies for Roslyn workspaces");
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: Failed to pre-load required assemblies: {ex.Message}");
}

// Register MSBuild locator once at startup to avoid issues with Roslyn workspace loading
if (!MSBuildLocator.IsRegistered)
{
    try
    {
        var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
        if (instances.Any())
        {
            // Prefer the newest version
            var instance = instances.OrderByDescending(i => i.Version).First();
            MSBuildLocator.RegisterInstance(instance);
            Console.WriteLine($"Registered MSBuild from: {instance.MSBuildPath}");
        }
        else
        {
            // No Visual Studio found, try to find .NET SDK's MSBuild
            Console.WriteLine("No Visual Studio instances found, looking for .NET SDK MSBuild...");
            
            // Try common .NET SDK locations
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") 
                ?? (OperatingSystem.IsWindows() 
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
                    : "/usr/share/dotnet");
            
            if (Directory.Exists(dotnetRoot))
            {
                var sdkPath = Path.Combine(dotnetRoot, "sdk");
                if (Directory.Exists(sdkPath))
                {
                    // Find the latest SDK version
                    var sdkVersions = Directory.GetDirectories(sdkPath)
                        .Select(d => new DirectoryInfo(d))
                        .OrderByDescending(d => d.Name)
                        .FirstOrDefault();
                    
                    if (sdkVersions != null)
                    {
                        var msbuildPath = sdkVersions.FullName;
                        Console.WriteLine($"Found .NET SDK at: {msbuildPath}");
                        MSBuildLocator.RegisterMSBuildPath(msbuildPath);
                        Console.WriteLine($"Registered MSBuild from .NET SDK: {msbuildPath}");
                    }
                    else
                    {
                        // Fallback to defaults as last resort
                        MSBuildLocator.RegisterDefaults();
                        Console.WriteLine("Registered MSBuild using defaults");
                    }
                }
                else
                {
                    MSBuildLocator.RegisterDefaults();
                    Console.WriteLine("Registered MSBuild using defaults");
                }
            }
            else
            {
                // Fallback to defaults
                MSBuildLocator.RegisterDefaults();
                Console.WriteLine("Registered MSBuild using defaults");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to register MSBuild locator: {ex.Message}");
        Console.WriteLine("The application will use Buildalyzer for workspace loading.");
    }
}

var builder = WebApplication.CreateBuilder(args);

// Ensure correct paths are set regardless of current working directory
// This is important because workspace operations may change the working directory
var appDirectory = AppContext.BaseDirectory;
builder.Environment.ContentRootPath = appDirectory;
builder.Environment.WebRootPath = Path.Combine(appDirectory, "wwwroot");

// Ensure wwwroot exists
if (!Directory.Exists(builder.Environment.WebRootPath))
{
    Directory.CreateDirectory(builder.Environment.WebRootPath);
    Console.WriteLine($"Created wwwroot directory at: {builder.Environment.WebRootPath}");
}

// Configure options
builder.Services.Configure<MappingAnalyzerOptions>(
    builder.Configuration.GetSection("MappingAnalyzer"));
builder.Services.Configure<ReplacementSettings>(
    builder.Configuration.GetSection("ReplacementSettings"));

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Register application services
builder.Services.AddScoped<MappingStorageService>();
builder.Services.AddScoped<RoslynEntityScanner>();
builder.Services.AddScoped<SimilarityAnalyzer>();
builder.Services.AddScoped<MappingGeneratorService>();
builder.Services.AddScoped<RoslynWorkspaceAnalyzer>();
builder.Services.AddScoped<CodeRewriterService>();
builder.Services.AddScoped<ReplacementOrchestratorService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<EntityCleanupService>();
builder.Services.AddScoped<CompilationValidator>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
