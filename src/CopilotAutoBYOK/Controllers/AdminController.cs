using Microsoft.AspNetCore.Mvc;
using copilot_auto_byok.Services;
using copilot_auto_byok.Models;
using System;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace copilot_auto_byok.Controllers;

public class FetchModelsRequest
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool FreeOnly { get; set; } = false;
}

[ApiController]
[Route("api")]
public class AdminController : ControllerBase
{
    private readonly IConfigService _configService;
    private readonly LogCleanupService _logCleanupService;

    public AdminController(IConfigService configService, LogCleanupService logCleanupService)
    {
        _configService = configService;
        _logCleanupService = logCleanupService;
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(_configService.GetConfiguration());
    }

    [HttpPut("config")]
    public IActionResult UpdateConfig([FromBody] AppConfiguration config)
    {
        _configService.SaveConfiguration(config);
        return Ok(new { message = "Configuration saved" });
    }

    // Provider management
    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        return Ok(_configService.GetProviders());
    }

    [HttpPost("providers")]
    public IActionResult AddProvider([FromBody] ProviderConfig provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            return BadRequest(new { error = "API key is required" });
        if (string.IsNullOrWhiteSpace(provider.Name))
            return BadRequest(new { error = "Provider name is required" });

        try
        {
            _configService.AddProvider(provider);
            return Ok(provider);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("providers/{id}")]
    public IActionResult UpdateProvider(string id, [FromBody] ProviderConfig provider)
    {
        provider.Id = id;

        try
        {
            _configService.UpdateProvider(provider);
            return Ok(new { message = "Provider updated" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("providers/{id}")]
    public IActionResult DeleteProvider(string id)
    {
        _configService.DeleteProvider(id);
        return Ok(new { message = "Provider deleted" });
    }

    [HttpPost("providers/fetch-models")]
    public async Task<IActionResult> FetchModels([FromBody] FetchModelsRequest request)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.ApiKey);
            client.Timeout = TimeSpan.FromSeconds(15);

            var baseUrl = request.BaseUrl.TrimEnd('/');

            // Try multiple endpoints that different providers use
            var urlsToTry = new[]
            {
                $"{baseUrl}/models",
                $"{baseUrl}/v1/models",
                baseUrl.Replace("/v1", "") + "/models"
            };

            HttpResponseMessage? response = null;
            string? lastError = null;

            foreach (var url in urlsToTry.Distinct())
            {
                try
                {
                    response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode) break;
                    lastError = await response.Content.ReadAsStringAsync();
                }
                catch { /* try next URL */ }
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                return Ok(new
                {
                    models = new List<string>(),
                    error = $"无法自动获取模型列表。该提供商可能不支持 /models 端点，请手动输入模型名称。{(lastError != null ? $" 最后错误: {lastError}" : "")}"
                });
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                        addModel(request.FreeOnly, models, idProp.GetString() ?? "", item);

                }
            }
            else if (doc.RootElement.TryGetProperty("models", out var modelsArray) && 
                     modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArray.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                        addModel(request.FreeOnly, models, idProp.GetString() ?? "", item);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Some providers return a plain array
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                        addModel(request.FreeOnly, models, idProp.GetString() ?? "", item);
                    else if (item.ValueKind == JsonValueKind.String)
                        addModel(request.FreeOnly, models, idProp.GetString() ?? "", item);
                }
            }

            var result = models.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            if (result.Count == 0)
            {
                return Ok(new
                {
                    models = new List<string>(),
                    error = "获取到的响应中未找到模型列表，请手动输入模型名称。"
                });
            }

            return Ok(new { models = result });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                models = new List<string>(),
                error = $"获取模型列表失败: {ex.Message}，请手动输入模型名称。"
            });
        }

        void addModel(bool freeOnly, List<string> models, string modelId, JsonElement element)
        {
            if (!freeOnly)
                models.Add(modelId);
            else if (isFree(element))
                models.Add(modelId);
        }

        bool isFree(JsonElement element)
        {
            // 免费模型判定条件:
            // 1. id 以 /free 或 :free 结尾
            // 2. isFree 为 true
            // 3. pricing:prompt 为 "0"
            if (element.TryGetProperty("id", out var idProp) && 
                (idProp.GetString()?.EndsWith("/free")==true || 
                 idProp.GetString()?.EndsWith(":free")==true))
                return true;
            if (element.TryGetProperty("isFree", out var isFreeProp) && 
                isFreeProp.GetBoolean())
                return true;
            if (element.TryGetProperty("pricing", out var pricingProp) && 
                pricingProp.TryGetProperty("prompt", out var promptProp) && 
                promptProp.GetString() == "0")
                return true;
            return false;
        }
    }


    [HttpGet("autocopilot")]
    public IActionResult GetAutoCopilot()
    {
        return Ok(_configService.GetAutoCopilotBinding());
    }

    [HttpPut("autocopilot")]
    public IActionResult SetAutoCopilot([FromBody] AutoCopilotBinding binding)
    {
        _configService.UpdateAutoCopilotBinding(binding);
        return Ok(new { message = "AutoCopilot binding updated" });
    }

    [Obsolete("访问密钥功能已弃用，不再从 UI 暴露，仅保留接口兼容性。")]
    [HttpGet("keys")]
    public IActionResult GetApiKeys()
    {
        var keys = _configService.GetApiKeys();
        return Ok(keys.Select(k => new { k.Id, k.Name, k.CreatedAt }));
    }

    [Obsolete("访问密钥功能已弃用，不再从 UI 暴露，仅保留接口兼容性。")]
    [HttpPost("keys")]
    public IActionResult AddApiKey([FromBody] ApiKeyConfig key)
    {
        if (string.IsNullOrWhiteSpace(key.Key))
            return BadRequest(new { error = "API key is required" });

        key.Id = Guid.NewGuid().ToString("N")[..8];
        key.CreatedAt = DateTime.UtcNow;
        _configService.AddApiKey(key);
        return Ok(new { key.Id, key.Name, key.CreatedAt });
    }

    [Obsolete("访问密钥功能已弃用，不再从 UI 暴露，仅保留接口兼容性。")]
    [HttpDelete("keys/{id}")]
    public IActionResult RemoveApiKey(string id)
    {
        _configService.RemoveApiKey(id);
        return Ok(new { message = "API key removed" });
    }

    [HttpGet("byok")]
    public IActionResult GetByokEnv()
    {
        return Ok(_configService.GetByokEnv());
    }

    [HttpPut("byok")]
    public IActionResult UpdateByokEnv([FromBody] ByokEnvConfig config)
    {
        _configService.UpdateByokEnv(config);
        return Ok(new { message = "BYOK environment configuration saved" });
    }

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        // 去掉 SDK 追加的提交哈希后缀（如 "+2fb572..."）
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];
        return Ok(new { version });
    }

    /// <summary>
    /// 增加一个 Ollama 的专用 API 接口，
    /// 骗过 vs2026 18.9.0+ 的 Copilot
    /// </summary>
    /// <returns></returns>
    [HttpGet("tags")]
    public IActionResult GetTags()
    {
        var response = new
        {
            models = new[]
            {
                new
                {
                    name = "ollama",
                    model = "gemma4",
                    modified_at = "2025-10-03T23:34:03.409490317-07:00",
                    size = 9608350245L,
                    digest = "c6eb396dbd5992bbe3f5cdb947e8bbc0ee413d7c17e2beaae69f5d569cf982eb",
                    details = new
                    {
                        format = "custom",
                        family = "auto-copilot",
                        families = new[] { "auto-copilot" },
                        parameter_size = "BYOK",
                        quantization_level = "Q4_K_M"
                    }
                }
            }
        };
        return Ok(response);
    }

    [HttpPost("logs/cleanup")]
    public async Task<IActionResult> CleanupLogs()
    {
        var deleted = await _logCleanupService.CleanupAsync();
        return Ok(new { deleted, message = $"已清理 {deleted} 条过期日志记录" });
    }

    [HttpPost("db/vacuum")]
    public async Task<IActionResult> VacuumDatabase()
    {
        var freed = await _logCleanupService.VacuumAsync();
        return Ok(new { freedBytes = freed, message = $"数据库压缩完成，释放 {freed / 1024} KB" });
    }
}
