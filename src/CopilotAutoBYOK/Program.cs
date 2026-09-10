using copilot_auto_byok.Data;
using copilot_auto_byok.Middleware;
using copilot_auto_byok.Models;
using copilot_auto_byok.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Net;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddMemoryCache();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<HeaderParameterOperationFilter>();
});
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
Console.WriteLine($"SQLite database path: {dbPath}");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Register services
builder.Services.AddSingleton<IConfigService, ConfigService>();
// 必须复用同一个实例：AddHostedService<MetricsService>() 会另外创建一个实例，
// 导致 IMetricsService 写入的 channel 与 IHostedService 消费的 channel 不是同一个。
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<IMetricsService>(sp => sp.GetRequiredService<MetricsService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsService>());
builder.Services.AddSingleton<LogCleanupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogCleanupService>());
builder.Services.AddScoped<IProxyService, ProxyService>();
// 以下代码通过 appsettings.json 配置 "Urls"："http://*:15959" 实现
// // 监听0.0.0.0
// builder.WebHost.ConfigureKestrel(options =>
// {
//     options.ListenAnyIP(15959); // Listen on port 15959
// });

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

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "CopilotAutoBYOK API v1");
    options.RoutePrefix = "swagger";
    options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
    options.InjectJavascript("/js/swagger-ui.js");
});

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

        if (!string.IsNullOrWhiteSpace(oldConfig.AutoCopilot.OpenAICurrentModel) ||
            !string.IsNullOrWhiteSpace(oldConfig.AutoCopilot.AnthropicCurrentModel))
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

internal sealed class HeaderParameterOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            AddHeaderParameter(operation, "Authorization", "Bearer token for the OpenAI-compatible API");
            SetRequestBodyExample(operation, """
                {
                  "model": "auto-copilot",
                  "messages": [
                    {
                      "role": "user",
                      "content": "who are you?"
                    }
                  ],
                  "stream": false
                }
                """);
        }

        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            AddHeaderParameter(operation, "Authorization", "Bearer token for the OpenAI-compatible API");
            SetRequestBodyExample(operation, """
                {
                    "model": "auto-copilot",
                    "instructions": "You are an AI assistant developed by OpenAI. Today is date: 2026-08-05. Your knowledge cutoff date is December 2025.",
                    "input": "who are you?",
                    "max_output_tokens": 1024,
                    "stream": false,
                    "reasoning": {
                        "effort": "none"
                    }
                }
                """);
        }

        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            AddHeaderParameter(operation, "x-api-key", "API key for the Anthropic-compatible API");
            SetRequestBodyExample(operation, """
                {
                  "model": "auto-copilot",
                  "max_tokens": 1024,
                  "messages": [
                    {
                      "role": "user",
                      "content": "who are you?"
                    }
                  ],
                  "stream": false
                }
                """);
        }
    }

    private static void SetRequestBodyExample(OpenApiOperation operation, string json)
    {
        if (operation.RequestBody?.Content == null ||
            !operation.RequestBody.Content.TryGetValue("application/json", out var mediaType))
            return;

        // Microsoft.OpenApi 3.x 中 IOpenApiMediaType 接口的 Schema/Example 是只读的，
        // 但具体实现类 OpenApiMediaType 的属性是可写的，转换为具体类型即可赋值。
        if (mediaType is OpenApiMediaType concreteMediaType)
        {
            concreteMediaType.Schema = new OpenApiSchema { Type = JsonSchemaType.Object };
            concreteMediaType.Example = JsonNode.Parse(json);
        }
    }

    private static void AddHeaderParameter(OpenApiOperation operation, string name, string description)
    {
        if (operation == null || operation.Parameters == null) return;

        if (operation.Parameters.Any(p => 
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && 
                p.In == ParameterLocation.Header))
            return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Description = description,
            Required = false,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
