using copilot_auto_byok.Data;
using copilot_auto_byok.Middleware;
using copilot_auto_byok.Models;
using copilot_auto_byok.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddMemoryCache();
builder.Services.AddControllers();
// Configure the proxy HttpClient with automatic decompression so upstream
// gzip/deflate/brotli responses are transparently decoded. Without this the
// proxy would forward compressed bytes as if they were plaintext and the
// upstream Content-Encoding header (a content header) would never be passed
// through to the client, corrupting both streaming and non-streaming responses.
builder.Services.AddHttpClient("ProxyClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli
    });
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Register EF Core
//var parentDir = Directory.GetParent(builder.Environment.ContentRootPath)?.FullName
//                ?? builder.Environment.ContentRootPath;
//var dbPath = Path.Combine(parentDir, "Data", "app.db");
// 数据库存放位置，恢复到 /app/Data/app.db
var currentDir = Directory.GetCurrentDirectory();
var dataDir = Path.Combine(currentDir, "Data");
var dbPath = Path.Combine(dataDir, "app.db");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Register services
builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<IMetricsService, MetricsService>();
builder.Services.AddHostedService<MetricsService>();
builder.Services.AddScoped<IProxyService, ProxyService>();
// 监听0.0.0.0
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(15959); // Listen on port 15959
});

var app = builder.Build();

// Ensure Data directory exists
//var dataDir = Path.Combine(parentDir, "Data");
if (!Directory.Exists(dataDir))
{
    Directory.CreateDirectory(dataDir);
}

// Migrate JSON data to SQLite (one-time)
var configPath = Path.Combine(dataDir, "models.json");
if (File.Exists(configPath))
{
    using var scope = app.Services.CreateScope();
    var configService = scope.ServiceProvider.GetRequiredService<IConfigService>();
    MigrateJsonToSqlite(configPath, configService);
    File.Move(configPath, configPath + ".backup", overwrite: true);
}

// Configure HTTP pipeline
app.UseMiddleware<copilot_auto_byok.Middleware.GlobalExceptionMiddleware>();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Auth middleware
app.UseMiddleware<AuthMiddleware>();

app.UseRouting();
app.MapControllers();

// Fallback to index.html for SPA
app.MapFallbackToFile("/index.html");

app.Run();

static void MigrateJsonToSqlite(string configPath, IConfigService configService)
{
    try
    {
        var json = File.ReadAllText(configPath);
        var oldConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfiguration>(json);
        if (oldConfig == null) return;

        foreach (var provider in oldConfig.Providers)
        {
            if (configService.GetProvider(provider.Id) == null)
                configService.AddProvider(provider);
        }

        foreach (var key in oldConfig.ApiKeys)
        {
            var existing = configService.GetApiKeys().FirstOrDefault(k => k.Id == key.Id);
            if (existing == null)
                configService.AddApiKey(key);
        }

        if (!string.IsNullOrWhiteSpace(oldConfig.AutoCopilot.CurrentModel))
        {
            configService.UpdateAutoCopilotBinding(oldConfig.AutoCopilot);
        }

        if (!string.IsNullOrWhiteSpace(oldConfig.ByokEnv.ProviderBaseUrl))
        {
            configService.UpdateByokEnv(oldConfig.ByokEnv);
        }
    }
    catch
    {
        // Ignore migration errors
    }
}
