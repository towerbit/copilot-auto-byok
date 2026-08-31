using Microsoft.AspNetCore.Mvc;
using copilot_auto_byok.Services;
using System.Text.Json;

namespace copilot_auto_byok.Controllers;

[ApiController]
[Route("v1")]
public class AnthropicController : ControllerBase
{
    private readonly IProxyService _proxyService;
    private readonly ILogger<AnthropicController> _logger;

    public AnthropicController(IProxyService proxyService, ILogger<AnthropicController> logger)
    {
        _proxyService = proxyService;
        _logger = logger;
    }

    [HttpPost("messages")]
    [Consumes("application/json")]
    public async Task ProxyMessages(
        [FromHeader(Name = "Authorization")] string? authorization,
        [FromHeader(Name = "x-api-key")] string? apiKey,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonElement? body)
    {
        if (!string.IsNullOrWhiteSpace(authorization))
            Request.Headers["Authorization"] = authorization;
        if (!string.IsNullOrWhiteSpace(apiKey))
            Request.Headers["x-api-key"] = apiKey;

        try
        {
            string bodyText = string.Empty;
            string model = "claude-3-sonnet-20240229";
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

            var response = await _proxyService.ForwardAsync(requestMessage, Request.Path + Request.QueryString, bodyText, "anthropic", model, isStreaming);

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
                // Convert Python-style booleans (True/False) to JSON-style (true/false)
                responseContent = copilot_auto_byok.Services.BooleanConvertStream.ConvertPythonBooleans(responseContent);
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
                try { await stream.DisposeAsync(); }
                catch (Exception ex)
                { _logger.LogWarning(ex, "Stream disposal failed (metrics may be lost)"); }
                // 兜底清理，此时内层流已被 _disposed 标记为已释放，为空操作
                response.Content.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Anthropic proxy");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                Response.ContentType = "application/json";
                await Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = new { message = ex.Message, type = "proxy_error" } }));
            }
        }
    }
}
