using EntityMappingAnalyzer.Services;
using EntityMappingAnalyzer.Models;

var builder = WebApplication.CreateBuilder(args);

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
