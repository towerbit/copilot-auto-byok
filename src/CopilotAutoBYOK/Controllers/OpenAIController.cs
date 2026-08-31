using copilot_auto_byok.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text.Json;

namespace copilot_auto_byok.Controllers;

[ApiController]
[Route("v1")]
public class OpenAIController : ControllerBase
{
    private readonly IProxyService _proxyService;
    private readonly IConfigService _configService;
    private readonly ILogger<OpenAIController> _logger;

    public OpenAIController(IProxyService proxyService, 
                            IConfigService configService, 
                            ILogger<OpenAIController> logger)
    {
        _proxyService = proxyService;
        _configService = configService;
        _logger = logger;
    }

    [HttpPost("responses")]
    [Consumes("application/json")]
    public async Task ProxyResponses(
        [FromHeader(Name = "Authorization")] string? authorization,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body)
    {
        // Expose Authorization header in Swagger UI as a Bearer token.
        if (!string.IsNullOrWhiteSpace(authorization))
        { 
            if(!authorization.StartsWith("Bearer "))
                authorization = "Bearer " + authorization;
            Request.Headers["Authorization"] = authorization;
        }

        try
        {
            string bodyText = string.Empty;
            string model = "gpt-3.5-turbo";
            bool isStreaming = false;

            if (body is JsonElement jsonBody)
            {
                bodyText = jsonBody.GetRawText();

                try
                {
                    if (jsonBody.ValueKind == JsonValueKind.Object)
                    {
                        if (jsonBody.TryGetProperty("model", out var modelProp))
                            model = modelProp.GetString() ?? model;
                        if (jsonBody.TryGetProperty("stream", out var streamProp))
                            isStreaming = streamProp.GetBoolean();
                    }
                }
                catch { /* ignore parse errors, use defaults */ }
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, Request.Path + Request.QueryString)
            {
                Content = new StringContent(bodyText, System.Text.Encoding.UTF8, "application/json")
            };

            foreach (var header in Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            var response = await _proxyService.ForwardAsync(requestMessage, Request.Path + Request.QueryString, bodyText, "openai", model, isStreaming);

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            foreach (var header in response.Headers)
            {
                if (!Response.Headers.ContainsKey(header.Key) &&
                    !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            if (!isStreaming)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                await Response.WriteAsync(responseContent);
                await Response.Body.FlushAsync();
                return;
            }

            Response.Headers.Remove("Content-Length");
            var stream = await response.Content.ReadAsStreamAsync();
            try
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await Response.Body.WriteAsync(buffer, 0, read);
                    await Response.Body.FlushAsync();
                }
            }
            finally
            {
                // 必须对 stream 调用 DisposeAsync：
                // HttpContent.DisposeAsync() 的基类实现只同步调用 Dispose(true)，
                // 不会把 DisposeAsync 传播到内层流，MetricsCollectingStream 就拿不到 await 的机会。
                await stream.DisposeAsync();
                // 兜底清理，此时内层流已被 _disposed 标记为已释放，为空操作
                response.Content.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OpenAI responses proxy");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                Response.ContentType = "application/json";
                await Response.WriteAsync(JsonSerializer.Serialize(new { 
                        error = new { 
                            message = ex.Message, 
                            type = "proxy_error" }}));
            }
        }
    }

    [HttpPost("chat/completions")]
    [Consumes("application/json")]
    public async Task ProxyChatCompletions(
        [FromHeader(Name = "Authorization")] string? authorization,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body)
    {
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            if (!authorization.StartsWith("Bearer "))
                authorization = "Bearer " + authorization;
            Request.Headers["Authorization"] = authorization;
        }

        try
        {
            string bodyText = string.Empty;
            string model = "gpt-3.5-turbo";
            bool isStreaming = false;

            if (body is JsonElement jsonBody)
            {
                bodyText = jsonBody.GetRawText();

                try
                {
                    if (jsonBody.ValueKind == JsonValueKind.Object)
                    {
                        if (jsonBody.TryGetProperty("model", out var modelProp))
                            model = modelProp.GetString() ?? model;
                        if (jsonBody.TryGetProperty("stream", out var streamProp))
                            isStreaming = streamProp.GetBoolean();
                    }
                }
                catch { /* ignore parse errors, use defaults */ }
            }

            // Rebuild the original request message for full passthrough
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, Request.Path + Request.QueryString)
            {
                Content = new StringContent(bodyText, System.Text.Encoding.UTF8, "application/json")
            };

            // Copy all headers
            foreach (var header in Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            var response = await _proxyService.ForwardAsync(requestMessage, Request.Path + Request.QueryString, bodyText, "openai", model, isStreaming);

            // Stream response back
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            // Forward response headers (excluding those managed by ASP.NET Core)
            foreach (var header in response.Headers)
            {
                if (!Response.Headers.ContainsKey(header.Key) &&
                    !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            // For non-streaming, read full content and write it
            if (!isStreaming)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                await Response.WriteAsync(responseContent);
                await Response.Body.FlushAsync();
                return;
            }

            // For streaming (SSE), copy the stream directly without buffering
            Response.Headers.Remove("Content-Length");
            var stream = await response.Content.ReadAsStreamAsync();
            try
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await Response.Body.WriteAsync(buffer, 0, read);
                    await Response.Body.FlushAsync();
                }
            }
            finally
            {
                // 必须对 stream 调用 DisposeAsync：
                // HttpContent.DisposeAsync() 的基类实现只同步调用 Dispose(true)，
                // 不会把 DisposeAsync 传播到内层流，MetricsCollectingStream 就拿不到 await 的机会。
                await stream.DisposeAsync();
                // 兜底清理，此时内层流已被 _disposed 标记为已释放，为空操作
                response.Content.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OpenAI proxy");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                Response.ContentType = "application/json";
                await Response.WriteAsync(JsonSerializer.Serialize(new { 
                    error = new { 
                        message = ex.Message, 
                        type = "proxy_error" }}));
            }
        }
    }

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        var config = _configService.GetConfiguration();
        var models = new List<Dictionary<string, object>>();
        var autoCopilotSupportedEndpointTypes = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.AutoCopilot.OpenAICurrentModel) &&
            !string.IsNullOrWhiteSpace(config.AutoCopilot.OpenAICurrentProviderId))
        {
            autoCopilotSupportedEndpointTypes.Add("openai");
        }

        if (!string.IsNullOrWhiteSpace(config.AutoCopilot.AnthropicCurrentModel) &&
            !string.IsNullOrWhiteSpace(config.AutoCopilot.AnthropicCurrentProviderId))
        {
            autoCopilotSupportedEndpointTypes.Add("anthropic");
        }

        models.Add(new Dictionary<string, object>
        {
            ["id"] = "auto-copilot",
            ["object"] = "model",
            ["owned_by"] = "autocopilot",
            ["supported_endpoint_types"] = autoCopilotSupportedEndpointTypes,
            ["openai_current_model"] = config.AutoCopilot.OpenAICurrentModel,
            ["openai_current_provider_id"] = config.AutoCopilot.OpenAICurrentProviderId,
            ["anthropic_current_model"] = config.AutoCopilot.AnthropicCurrentModel,
            ["anthropic_current_provider_id"] = config.AutoCopilot.AnthropicCurrentProviderId
        });

        foreach (var provider in config.Providers)
        {
            var supportedEndpointTypes = string.IsNullOrWhiteSpace(provider.Type)
                ? new List<string>()
                : new List<string> { provider.Type };

            foreach (var modelName in provider.Models)
            {
                models.Add(new Dictionary<string, object>
                {
                    ["id"] = $"{provider.Name},{modelName}",
                    ["object"] = "model",
                    ["owned_by"] = provider.Name,
                    ["supported_endpoint_types"] = supportedEndpointTypes
                });
            }
        }

        return Ok(new { data = models, @object = "list" });
    }
}
