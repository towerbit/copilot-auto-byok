/*
mimo 在流式响应里“双轨输出”：一边用标准 OpenAI tool_calls 字段正确给出工具调用，
一边又在 delta.content 里用 XML 复述一遍 <tool_call>...</tool_call>，导致 copilot 解析
失败（can't parse JSON）并反复重试。启用 STRIP_TOOLCALL_XML 在代理层把 content 中的
<tool_call>...</tool_call> 片段剔除，只保留自然语言文本。通过 ShouldStripToolCallXml()
控制仅针对 mimo 生效。
*/
#define STRIP_TOOLCALL_XML
/*
临时诊断流开关：DiagnoseStream 在 BC_DIAG=1 时把每次读取的 chunk 实时打到日志，
分 RAW（代理转换前）/ OUT（copilot 实际收到）两路，便于对照排查问题。
默认开启；稳定后如需彻底移除，先 #undef 本开关并删除 DiagnoseStream 类及两处引用。
*/
#undef ENABLE_DIAGNOSE_STREAM

using copilot_auto_byok.Models;
using copilot_auto_byok.Models.Metrics;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace copilot_auto_byok.Services;

public interface IProxyService
{
    Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, string pathAndQuery, string bodyText, string protocol, string requestedModel, bool isStreaming);
}

public class ProxyService : IProxyService
{

#if STRIP_TOOLCALL_XML
    private static bool ShouldStripToolCallXml(string model) =>
        model.StartsWith("mimo", StringComparison.OrdinalIgnoreCase) &&
        !model.Contains("tts", StringComparison.OrdinalIgnoreCase) &&
        !model.Contains("asr", StringComparison.OrdinalIgnoreCase);
#endif
    private readonly IConfigService _configService;
    private readonly IMetricsService _metricsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProxyService> _logger;
    // 实际用的根本就不是下面这些模型，预置价格毫无用处
    internal static readonly Dictionary<string, (double input, double output)> Pricing = new()
    {
        //["gpt-4o"] = (2.50, 10.00),
        //["gpt-4o-mini"] = (0.15, 0.60),
        //["gpt-3.5-turbo"] = (0.50, 1.50),
        //["gpt-4"] = (30.00, 60.00),
        //["gpt-4-turbo"] = (10.00, 30.00),
        //["claude-3-5-sonnet-20241022"] = (3.00, 15.00),
        //["claude-3-opus-20240229"] = (15.00, 75.00),
        //["claude-3-sonnet-20240229"] = (3.00, 15.00),
        //["claude-3-haiku-20240307"] = (0.25, 1.25),
    };

    public ProxyService(
        IConfigService configService,
        IMetricsService metricsService,
        IHttpClientFactory httpClientFactory,
        ILogger<ProxyService> logger)
    {
        _configService = configService;
        _metricsService = metricsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, string pathAndQuery, string bodyText, string protocol, string requestedModel, bool isStreaming)
    {
        var config = _configService.GetConfiguration();
        var metrics = new RequestMetrics
        {
            Timestamp = DateTime.UtcNow,
            RequestedModel = requestedModel,
            Protocol = protocol,
            IsStreaming = isStreaming
        };

        var stopwatch = Stopwatch.StartNew();
        // 流式并且已把响应流交给调用方时为 true：此时指标改由 MetricsCollectingStream
        // 在 DisposeAsync 时落库，ForwardAsync 不再 Stop 计时、也不再记录。
        var streamHandoff = false;

        try
        {
            // Resolve AutoCopilot or an explicit providerName,modelName binding.
            string targetModel = requestedModel;
            string targetProviderId = "";
            string targetProviderType = protocol;

            if (requestedModel.Equals("auto-copilot", StringComparison.OrdinalIgnoreCase))
            {
                // 要解析 AnthropicCurrentModel/OpenAICurrentModel 获取实际的 modelName
                if ("anthropic".Equals(protocol, StringComparison.OrdinalIgnoreCase))
                {
                    targetModel = config.AutoCopilot.AnthropicCurrentModel.Split(',')[1];
                    targetProviderId = config.AutoCopilot.AnthropicCurrentProviderId;
                }
                else
                {
                    targetModel = config.AutoCopilot.OpenAICurrentModel.Split(',')[1];
                    targetProviderId = config.AutoCopilot.OpenAICurrentProviderId;
                }

                if (string.IsNullOrWhiteSpace(targetModel) || string.IsNullOrWhiteSpace(targetProviderId))
                {
                    throw new InvalidOperationException($"AutoCopilot binding is not configured for protocol '{protocol}'.");
                }

                _logger.LogInformation("AutoCopilot resolved: protocol={Protocol}, model={TargetModel}, providerId={TargetProviderId}", protocol, targetModel, targetProviderId);
            }
            else
            {
                var commaIndex = requestedModel.IndexOf(',');
                if (commaIndex <= 0 || commaIndex == requestedModel.Length - 1)
                {
                    throw new InvalidOperationException("Model name must use 'provider,model' format, or 'auto-copilot'.");
                }

                var requestedProviderName = requestedModel[..commaIndex].Trim();
                targetModel = requestedModel[(commaIndex + 1)..].Trim();

                if (string.IsNullOrEmpty(requestedProviderName) || string.IsNullOrEmpty(targetModel))
                {
                    throw new InvalidOperationException("Model name must use 'providerName,modelName' format, or 'auto-copilot'.");
                }

                var explicitProvider = config.Providers.FirstOrDefault(p =>
                    string.Equals(p.Name, requestedProviderName, StringComparison.OrdinalIgnoreCase));

                if (explicitProvider == null)
                {
                    throw new InvalidOperationException($"Provider '{requestedProviderName}' not found for model '{requestedModel}'.");
                }

                if (!explicitProvider.Models.Contains(targetModel, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Model '{targetModel}' is not configured under provider '{explicitProvider.Name}'.");
                }

                targetProviderId = explicitProvider.Id;
            }

            // Resolve provider
            ProviderConfig? provider = null;
            if (!string.IsNullOrEmpty(targetProviderId))
            {
                provider = config.Providers.FirstOrDefault(p => p.Id == targetProviderId);
                if (provider != null) targetProviderType = provider.Type;
            }

            if (provider == null)
            {
                throw new InvalidOperationException($"No provider resolved for model '{requestedModel}'.");
            }

            metrics.Provider = provider.Name;
            metrics.ProviderId = provider.Id;
            metrics.ActualModel = targetModel;

            // Build forward request with full passthrough
            var forwardRequest = await BuildForwardRequestAsync(request, pathAndQuery, bodyText, provider, targetModel, targetProviderType);

            // Send request
            var client = _httpClientFactory.CreateClient("ProxyClient");
            var response = await client.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);
            metrics.LatencyMs = stopwatch.ElapsedMilliseconds;
            metrics.StatusCode = (int)response.StatusCode;

            // Process response
            if (isStreaming)
            {
                // For streaming, wrap the response stream with MetricsCollectingStream
                // so we can collect usage data while still allowing the caller to read the stream.
                var originalStream = await response.Content.ReadAsStreamAsync();
#if ENABLE_DIAGNOSE_STREAM
                // 诊断：抓取 mimo 原始响应（进 ToolCallXmlStripStream 之前）
                var diagRaw = new DiagnoseStream(originalStream, "RAW", _logger);
                var teeStream = new MetricsCollectingStream(
                    diagRaw, targetProviderType, metrics, stopwatch, _metricsService, _logger, response.IsSuccessStatusCode);
#else
                var teeStream = new MetricsCollectingStream(
                    originalStream, targetProviderType, metrics, stopwatch, _metricsService, _logger, response.IsSuccessStatusCode);
#endif
                {
                    // 针对 mimo：剥离 content 里的 <tool_call> XML 复述，并从中捞回必填工具参数
                    Stream finalStream = teeStream;
#if STRIP_TOOLCALL_XML
                    if (pathAndQuery.Contains("/chat/", StringComparison.OrdinalIgnoreCase) && ShouldStripToolCallXml(targetModel))
                        finalStream = new ToolCallXmlStripStream(teeStream);
#endif

                    // Replace content with the tee stream; preserve original content headers
                    var originalHeaders = response.Content.Headers.ToList();
#if ENABLE_DIAGNOSE_STREAM
                    // 诊断：抓取转换后（copilot 实际收到的）响应
                    var diagOut = new DiagnoseStream(finalStream, "OUT", _logger);
                    response.Content = new StreamContent(diagOut);
#else
                    response.Content = new StreamContent(finalStream);
#endif
                    foreach (var header in originalHeaders)
                    {
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    // 指标改由 MetricsCollectingStream 在 DisposeAsync 时落库
                    streamHandoff = true;
                    return response;
                }
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                if (targetProviderType == "openai")
                    ParseOpenAIUsage(responseContent, metrics);
                else
                    ParseAnthropicUsage(responseContent, metrics);
                metrics.TotalDurationMs = stopwatch.ElapsedMilliseconds;
            }

            metrics.IsSuccess = response.IsSuccessStatusCode;
            metrics.EstimatedCost = CalculateCost(metrics);
            metrics.TokensPerSecond = metrics.TotalDurationMs > 0 && metrics.CompletionTokens > 0
                ? Math.Round(metrics.CompletionTokens / (metrics.TotalDurationMs / 1000.0), 2)
                : 0;

            return response;
        }
        catch (Exception ex)
        {
            metrics.Error = ex.Message;
            metrics.IsSuccess = false;
            metrics.StatusCode = 500;
            metrics.TotalDurationMs = stopwatch.ElapsedMilliseconds;
            metrics.EstimatedCost = CalculateCost(metrics);
            _logger.LogError(ex, "Error forwarding request");
            throw;
        }
        finally
        {
            if (!streamHandoff)
            {
                // 非流式，或流式但尚未把流交给调用方就出错：在此停止计时并记录。
                // 修正了原先「流式请求在建流前抛异常则日志彻底丢失」的问题。
                stopwatch.Stop();
                if (metrics.TotalDurationMs == 0)
                    metrics.TotalDurationMs = stopwatch.ElapsedMilliseconds;
                await _metricsService.RecordAsync(metrics);
            }
            // streamHandoff == true 时故意不 Stop：计时持续到响应体读完，
            // 由 MetricsCollectingStream.ParseAndFillMetrics 停止并落库。
        }
    }

    private async Task<HttpRequestMessage> BuildForwardRequestAsync(HttpRequestMessage original, string pathAndQuery, string bodyText, ProviderConfig provider, string model, string providerType)
    {
        // Build target URL preserving path and query
        // Avoid duplicate path segments if base URL already contains /v1
        var targetBase = provider.BaseUrl.TrimEnd('/');
        var path = pathAndQuery;
        if (targetBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring("/v1".Length);
        }
        var targetUrl = $"{targetBase}{path}";

        _logger.LogDebug("Target URL: {TargetUrl} (base={Base}, path={Path})", targetUrl, targetBase, path);

        var forwardRequest = new HttpRequestMessage(original.Method, targetUrl);

        // Forward all headers (except hop-by-hop and auth)
        foreach (var header in original.Headers)
        {
            if (ShouldForwardHeader(header.Key))
            {
                forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Forward content headers
        if (original.Content != null)
        {
            foreach (var header in original.Content.Headers)
            {
                if (!forwardRequest.Content?.Headers.Contains(header.Key) ?? true)
                {
                    // Will be added when content is set
                }
            }
        }

        if (!string.IsNullOrEmpty(bodyText))
        {
            try
            {
                var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(bodyText);
                if (jsonNode is System.Text.Json.Nodes.JsonObject jsonObj)
                {
                    var oldModel = jsonObj["model"]?.ToString() ?? "(none)";
                    jsonObj["model"] = model;

                    // Anthropic's Messages API does not allow "system" as a message role.
                    // Extract system messages into the top-level "system" field before forwarding.
                    if (providerType == "anthropic")
                    {
                        ConvertSystemRoleForAnthropic(jsonObj);
                    }

                    var newBody = jsonObj.ToJsonString();
                    _logger.LogInformation("Body replaced: oldModel={OldModel}, newModel={NewModel}", oldModel, model);

                    forwardRequest.Content = new StringContent(newBody, System.Text.Encoding.UTF8, "application/json");
                    _logger.LogDebug("newBody={NewBody}",newBody);
                }
                else
                {
                    forwardRequest.Content = new StringContent(bodyText, System.Text.Encoding.UTF8, original.Content?.Headers.ContentType?.MediaType ?? "application/json");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse body as JSON: {Error}. Forwarding as-is.", ex.Message);
                // 这里原样发送 bodyText 显然也是错误的，因为 model 名字对不上，
                // 除非目标服务器上恰好有叫 auto-copilot 的模型。最终仍返回报错
                forwardRequest.Content = new StringContent(bodyText, System.Text.Encoding.UTF8, original.Content?.Headers.ContentType?.MediaType ?? "application/json");
            }
        }

        // Override with provider auth
        if (providerType == "openai")
        {
            forwardRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }
        else
        {
            // 需要先删除 request.headers 的 x-api-key, 否则 TryAddWithoutValidation 添加不成功
            forwardRequest.Headers.Remove("x-api-key"); 
            forwardRequest.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
            forwardRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        return forwardRequest;
    }

    /// <summary>
    /// Anthropic's Messages API has no "system" message role; the system prompt must live in the
    /// top-level "system" field. Claude Code (and other OpenAI-style clients) may emit
    /// {"role":"system",...} entries inside "messages". This converts them so the upstream
    /// Anthropic endpoint does not reject the request with a 500.
    /// </summary>
    private static void ConvertSystemRoleForAnthropic(System.Text.Json.Nodes.JsonObject jsonObj)
    {
        if (jsonObj["messages"] is not System.Text.Json.Nodes.JsonArray messages)
            return;

        var systemTexts = new List<string>();
        var preservedSystemBlocks = new List<System.Text.Json.Nodes.JsonNode>();

        // Preserve any existing top-level "system" (string or content blocks array).
        if (jsonObj["system"] is System.Text.Json.Nodes.JsonNode existingSystem)
        {
            if (existingSystem is System.Text.Json.Nodes.JsonValue)
            {
                var s = existingSystem.GetValue<string>();
                if (!string.IsNullOrEmpty(s))
                    systemTexts.Add(s);
            }
            else if (existingSystem is System.Text.Json.Nodes.JsonArray sysArr)
            {
                foreach (var block in sysArr)
                {
                    if (block is System.Text.Json.Nodes.JsonObject bo && bo["type"]?.GetValue<string>() == "text")
                    {
                        var t = bo["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(t)) systemTexts.Add(t!);
                    }
                    else
                    {
                        preservedSystemBlocks.Add(block!);
                    }
                }
            }
        }

        // Filter messages: pull out "system" role entries, keep the rest.
        var newMessages = new System.Text.Json.Nodes.JsonArray();
        foreach (var m in messages)
        {
            if (m is System.Text.Json.Nodes.JsonObject mo && mo["role"]?.GetValue<string>() == "system")
            {
                var text = ExtractTextFromContent(mo["content"]);
                if (!string.IsNullOrEmpty(text))
                    systemTexts.Add(text!);
            }
            else
            {
                newMessages.Add(m!.DeepClone());
            }
        }

        jsonObj["messages"] = newMessages;

        if (systemTexts.Count == 0)
            return;

        if (preservedSystemBlocks.Count > 0)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var block in preservedSystemBlocks)
                arr.Add(block.DeepClone());
            foreach (var t in systemTexts)
            {
                var block = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "text",
                    ["text"] = t
                };
                arr.Add(block);
            }
            jsonObj["system"] = arr;
        }
        else
        {
            jsonObj["system"] = string.Join("\n\n", systemTexts);
        }
    }

    /// <summary>
    /// Extract plain text from a message "content" node, which may be either a string
    /// or an array of content blocks (e.g. [{"type":"text","text":"..."}]).
    /// </summary>
    private static string? ExtractTextFromContent(System.Text.Json.Nodes.JsonNode? content)
    {
        if (content == null)
            return null;

        if (content is System.Text.Json.Nodes.JsonValue val)
        {
            return val.TryGetValue<string>(out var s) ? s : null;
        }

        if (content is System.Text.Json.Nodes.JsonArray arr)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in arr)
            {
                if (item is System.Text.Json.Nodes.JsonObject o && o["type"]?.GetValue<string>() == "text")
                {
                    var t = o["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                    }
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    private static bool ShouldForwardHeader(string headerName)
    {
        var lower = headerName.ToLowerInvariant();
        return lower is not (
            "host" or "connection" or "keep-alive" or "transfer-encoding" or
            "upgrade" or "proxy-authorization" or "proxy-authenticate" or
            "te" or "trailer" or "content-length" or "authorization"
        );
    }

    private void ParseOpenAIUsage(string responseContent, RequestMetrics metrics)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                metrics.PromptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                metrics.CompletionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;

                if (usage.TryGetProperty("prompt_tokens_details", out var ptd) &&
                    ptd.TryGetProperty("cached_tokens", out var cached))
                {
                    metrics.CachedTokens = cached.GetInt32();
                    metrics.IsCacheHit = metrics.CachedTokens > 0;
                }
            }
        }
        catch { }
    }

    private void ParseAnthropicUsage(string responseContent, RequestMetrics metrics)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                metrics.PromptTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                metrics.CompletionTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;

                if (usage.TryGetProperty("cache_read_input_tokens", out var cacheRead))
                {
                    metrics.CachedTokens = cacheRead.GetInt32();
                    metrics.IsCacheHit = metrics.CachedTokens > 0;
                }
            }
        }
        catch { }
    }

    private double CalculateCost(RequestMetrics metrics) => 0.0;
    //{
    //    var key = metrics.ActualModel.ToLower();
    //    if (!Pricing.TryGetValue(key, out var price))
    //    {
    //        foreach (var (k, p) in Pricing)
    //        {
    //            if (key.Contains(k.ToLower()) || k.ToLower().Contains(key))
    //            {
    //                price = p;
    //                break;
    //            }
    //        }
    //    }

    //    if (price == default)
    //        return 0;

    //    // Cache hit tokens are discounted 50% for prompt/input tokens
    //    var uncachedPromptTokens = metrics.PromptTokens - metrics.CachedTokens;
    //    var cachedPromptTokens = metrics.CachedTokens;

    //    var inputCost = (uncachedPromptTokens / 1_000_000.0) * price.input +
    //                    (cachedPromptTokens / 1_000_000.0) * price.input * 0.5;
    //    var outputCost = (metrics.CompletionTokens / 1_000_000.0) * price.output;
    //    return Math.Round(inputCost + outputCost, 6);
    //}
}

/// <summary>
/// A stream that copies all read data to a memory buffer for later analysis.
/// When disposed, it parses the collected data for usage metrics.
/// 项目指标系统的核心组件，除非你确定不需要流式请求的 token 统计和日志功能，否则不应移除
/// </summary>
internal class MetricsCollectingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _copy;
    private readonly string _protocol;
    private readonly RequestMetrics _metrics;
    private readonly Stopwatch _stopwatch;
    private readonly IMetricsService _metricsService;
    private readonly ILogger<ProxyService> _logger;
    private readonly bool _isSuccess;
    private bool _disposed;

    public MetricsCollectingStream(
        Stream inner,
        string protocol,
        RequestMetrics metrics,
        Stopwatch stopwatch,
        IMetricsService metricsService,
        ILogger<ProxyService> logger,
        bool isSuccess)
    {
        _inner = inner;
        _copy = new MemoryStream();
        _protocol = protocol;
        _metrics = metrics;
        _stopwatch = stopwatch;
        _metricsService = metricsService;
        _logger = logger;
        _isSuccess = isSuccess;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            _copy.Write(buffer, offset, read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (read > 0)
            await _copy.WriteAsync(buffer, offset, read, cancellationToken);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            // Copy to the internal stream for metrics collection
            var temp = ArrayPool<byte>.Shared.Rent(read);
            try
            {
                buffer.Span[..read].CopyTo(temp);
                await _copy.WriteAsync(temp, 0, read, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }
        return read;
    }

    /// <summary>
    /// 停止计时、解析用量并填充到 _metrics，返回响应正文（供日志使用）。解析失败降级，不抛出。
    /// </summary>
    private string ParseAndFillMetrics()
    {
        // 停止计时：流式分支把流交给调用方时并未 Stop，这里拿到的才是
        // 响应体读完的真实耗时；否则 TotalDurationMs 会被冻结成 LatencyMs。
        _stopwatch.Stop();

        string contentText;
        try
        {
            _copy.Position = 0;
            contentText = Encoding.UTF8.GetString(_copy.ToArray());

            if (_protocol == "openai")
                ParseOpenAIStreamingUsage(contentText, _metrics);
            else
                ParseAnthropicStreamingUsage(contentText, _metrics);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse streaming usage");
            contentText = string.Empty;
        }

        _metrics.TotalDurationMs = _stopwatch.ElapsedMilliseconds;
        _metrics.IsSuccess = _isSuccess;
        _metrics.EstimatedCost = CalculateCostStatic(_metrics);
        _metrics.TokensPerSecond = _metrics.TotalDurationMs > 0 && _metrics.CompletionTokens > 0
            ? Math.Round(_metrics.CompletionTokens / (_metrics.TotalDurationMs / 1000.0), 2)
            : 0;

        return contentText;
    }

    /// <summary>
    /// 异步释放：解析用量后 await 落库。这是流式请求写入 request_metrics 的正式路径，
    /// 调用方必须 await DisposeAsync，否则无法 await RecordAsync。
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var contentText = ParseAndFillMetrics();

            await _metricsService.RecordAsync(_metrics).ConfigureAwait(false);

            _logger.LogDebug("Streaming metrics recorded: prompt={Prompt}, completion={Completion}, total={Total}ms, contentLength={ContentLength}",
                _metrics.PromptTokens, _metrics.CompletionTokens, _metrics.TotalDurationMs, contentText.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record streaming metrics");
        }

        _copy.Dispose();
        try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// 同步释放（兜底路径，正常应走 DisposeAsync）。
    /// 此处无法 await，只能同步等待投递完成，否则日志会丢失。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try
            {
                var contentText = ParseAndFillMetrics();

                _metricsService.RecordAsync(_metrics).GetAwaiter().GetResult();

                _logger.LogDebug("Streaming metrics recorded (sync dispose): prompt={Prompt}, completion={Completion}, total={Total}ms, contentLength={ContentLength}",
                    _metrics.PromptTokens, _metrics.CompletionTokens, _metrics.TotalDurationMs, contentText.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record streaming metrics");
            }

            _copy.Dispose();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void ParseOpenAIStreamingUsage(string contentText, RequestMetrics metrics)
    {
        try
        {
            var lines = contentText.Split('\n');
            var completionContent = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                if (line.StartsWith("data: ") && !line.Contains("[DONE]"))
                {
                    var json = line["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);

                    // Try to get usage from chunk
                    if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                        usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                            metrics.PromptTokens = pt.GetInt32();
                        if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                            metrics.CompletionTokens = ct.GetInt32();
                        metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;

                        if (usage.TryGetProperty("prompt_tokens_details", out var ptd) &&
                            ptd.ValueKind == JsonValueKind.Object &&
                            ptd.TryGetProperty("cached_tokens", out var cached) &&
                            cached.ValueKind == JsonValueKind.Number)
                        {
                            metrics.CachedTokens = cached.GetInt32();
                            metrics.IsCacheHit = metrics.CachedTokens > 0;
                        }
                    }

                    // Collect content for fallback estimation
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var content) &&
                                content.ValueKind == JsonValueKind.String)
                            {
                                completionContent.Append(content.GetString());
                            }
                            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                                reasoning.ValueKind == JsonValueKind.String)
                            {
                                completionContent.Append(reasoning.GetString());
                            }
                        }
                    }
                }
            }

            // Fallback: estimate tokens from content length if usage not provided
            if (metrics.TotalTokens == 0 && completionContent.Length > 0)
            {
                // Rough estimate: ~4 chars per token for English/Chinese mixed
                var estimatedCompletionTokens = Math.Max(1, completionContent.Length / 4);
                metrics.CompletionTokens = estimatedCompletionTokens;
                metrics.TotalTokens = metrics.PromptTokens + estimatedCompletionTokens;
            }
        }
        catch { }
    }

    private static void ParseAnthropicStreamingUsage(string contentText, RequestMetrics metrics)
    {
        try
        {
            var lines = contentText.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("data: "))
                {
                    var json = line["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("type", out var type) &&
                        type.GetString() == "message_delta" &&
                        doc.RootElement.TryGetProperty("usage", out var usage))
                    {
                        // message_delta 是最终统计，以这里为准
                        if (usage.TryGetProperty("output_tokens", out var ot))
                            metrics.CompletionTokens = ot.GetInt32();
                        if (usage.TryGetProperty("input_tokens", out var it))
                            metrics.PromptTokens = it.GetInt32();
                        if (usage.TryGetProperty("cache_read_input_tokens", out var cr))
                        {
                            metrics.CachedTokens = cr.GetInt32();
                            metrics.IsCacheHit = metrics.CachedTokens > 0;
                        }
                        metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;
                    }
                    if (doc.RootElement.TryGetProperty("type", out var type2) &&
                        type2.GetString() == "message_start")
                    {
                        // message_start 里的 usage 经常是全 0 的占位数据，
                        // 只在 message_delta 没提供 input_tokens 时作为 fallback
                        JsonElement usageSource;
                        if (doc.RootElement.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("usage", out var messageUsage))
                        {
                            usageSource = messageUsage;
                        }
                        else if (doc.RootElement.TryGetProperty("usage", out var rootUsage))
                        {
                            usageSource = rootUsage;
                        }
                        else
                        {
                            continue;
                        }

                        if (metrics.PromptTokens == 0 &&
                            usageSource.TryGetProperty("input_tokens", out var it2) &&
                            it2.GetInt32() > 0)
                        {
                            metrics.PromptTokens = it2.GetInt32();
                            metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;
                        }
                        if (!metrics.IsCacheHit &&
                            usageSource.TryGetProperty("cache_read_input_tokens", out var cr2) &&
                            cr2.GetInt32() > 0)
                        {
                            metrics.CachedTokens = cr2.GetInt32();
                            metrics.IsCacheHit = true;
                        }
                    }
                }
            }
        }
        catch { }
    }
    /// <summary>
    /// 费用以上游供应商账单为准，此处仅记录 Token 使用量，不估算费用
    /// </summary>
    /// <param name="metrics"></param>
    /// <returns></returns>
    private static double CalculateCostStatic(RequestMetrics metrics) => 0.0;
    //{
    //    var key = metrics.ActualModel.ToLower();
    //    if (!ProxyService.Pricing.TryGetValue(key, out var price))
    //    {
    //        foreach (var (k, p) in ProxyService.Pricing)
    //        {
    //            if (key.Contains(k.ToLower()) || k.ToLower().Contains(key))
    //            {
    //                price = p;
    //                break;
    //            }
    //        }
    //    }
    //    if (price == default) return 0;
    //    var uncachedPromptTokens = metrics.PromptTokens - metrics.CachedTokens;
    //    var cachedPromptTokens = metrics.CachedTokens;
    //    var inputCost = (uncachedPromptTokens / 1_000_000.0) * price.input +
    //                    (cachedPromptTokens / 1_000_000.0) * price.input * 0.5;
    //    var outputCost = (metrics.CompletionTokens / 1_000_000.0) * price.output;
    //    return Math.Round(inputCost + outputCost, 6);
    //}
}

#if ENABLE_DIAGNOSE_STREAM
/// <summary>
/// 临时诊断流：环境变量 BC_DIAG=1 时，把每次读取的 chunk 实时通过 ILogger 打到控制台
/// （每个 chunk 单独一行，便于对照 mimo 原始响应 RAW 与转换后响应 OUT）。默认不启用。
/// </summary>
internal class DiagnoseStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _tag;
        private readonly ILogger _logger;
        private static readonly object _logLock = new();
        private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "DiagnoseStream.log");
#if DEBUG
        private static readonly bool _on = true;
#else
        private static readonly bool _on = Environment.GetEnvironmentVariable("BC_DIAG") == "1";
#endif
        public DiagnoseStream(Stream inner, string tag, ILogger logger) { _inner = inner; _tag = tag; _logger = logger; }

        private void Dump(byte[] buffer, int offset, int count)
        {
            if (!_on || count <= 0) return;
            try
            {
                var text = Encoding.UTF8.GetString(buffer, offset, count);
                _logger.LogWarning("BC_DIAG[{Tag}] chunk:\n{Chunk}", _tag, text);
                try
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{_tag}] chunk({count} bytes):\n{text}\n";
                    lock (_logLock)
                    {
                        //Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                        File.AppendAllText(_logPath, line, Encoding.UTF8);
                    }
                }
                catch { }
            }
            catch { }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var r = _inner.Read(buffer, offset, count);
            Dump(buffer, offset, r);
            return r;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var r = await _inner.ReadAsync(buffer, offset, count, ct);
            Dump(buffer, offset, r);
            return r;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var r = await _inner.ReadAsync(buffer, ct);
            if (r > 0) Dump(buffer.Span[..r].ToArray(), 0, r);
            return r;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long o, SeekOrigin or) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
#endif // ENABLE_DIAGNOSE_STREAM

/// <summary>
/// 流式剔除 delta.content 里 mimo 多出来的 <tool_call>...</tool_call> XML 复述片段。
///
/// mimo 在流式响应里“双轨输出”：一边用标准 OpenAI tool_calls 字段正确给出工具调用，
/// 一边又在 content 里用 XML 复述一遍，导致 copilot 解析失败。本流把 content 中
/// &lt;tool_call&gt; 到 &lt;/tool_call&gt; 之间的内容整体丢弃，只保留自然语言文本。
///
/// 字节级实现，扫描的是 ASCII 标记 &lt;tool_call / &lt;/tool_call&gt;，不会误伤任何多字节
/// UTF-8 字符（中文参数等），也绝不会在边界切断字符。
/// 跨 chunk 边界的半截标记会缓存在 _holdback，下一次 Read 时拼接再判断。
/// </summary>
/// <summary>
/// 剥离 mimo 在 delta.content 里用 XML 复述的 &lt;tool_call&gt;...&lt;/tool_call&gt; 片段。
/// mimo 对同一工具调用“双轨输出”：一边用标准 OpenAI tool_calls 字段，一边在 content 里
/// 用 XML 复述。content 的 XML 会被拆成多个 SSE 事件（每个事件是一个独立 JSON chunk）流式吐出，
/// 因此不能在字节层面做脆弱的 tag 匹配，而应按 SSE 事件边界切分，对每个完整事件做 JSON 解析与重写。
///
/// 跨事件状态机：一旦某事件的 content 包含 &lt;tool_call，则标记“正在复述”，后续所有 content 事件
/// （直至某事件 content 包含 &lt;/tool_call&gt;）一并丢弃，避免中间 XML 内部文本泄漏为垃圾 content。
/// 合法 tool_calls 不受影响，copilot 仍可正常执行工具调用。
/// </summary>
internal class ToolCallXmlStripStream : Stream
{
    private readonly Stream _inner;
    private bool _disposed;
    private readonly List<byte> _buf = new();   // 累积中的 SSE 事件（未遇 \n\n 前）
    private bool _inXml;                         // 跨事件：是否正处于 XML 复述的 content 流中
    private readonly List<byte> _out = new();
    private int _outPos;

    // —— tool_calls 规范化所需跨事件状态 ——
    // mimo 在流式 tool_calls 里"双轨输出"：标准 tool_calls 字段只流 command 部分，
    // 而 summary / background 两个必填参数的值被分流进 delta.content 的 XML 复述里
    // （<parameter=summary>...</parameter><parameter=background>...</parameter>）。
    // 若只取 command 丢掉 summary/background，Copilot 校验必填参数失败 → "无法运行 xxx"。
    // 因此这里：从 tool_calls 碎片提取 command，从 XML 复述提取 summary/background，
    // 两者到齐后在 finish_reason 收尾帧一次性输出三字段齐全的完整 arguments。
    private string? _tcId;                       // 缓存的工具调用 id
    private string? _tcName;                     // 缓存的工具名
    private readonly StringBuilder _tcArgs = new(); // 累积的 arguments 字符串
    private bool _tcArgsOpen;                    // 当前是否处于一个 tool_calls 的 arguments 累积中
    private bool _tcHasId;                       // 首片（带 id）是否已发出
    private string? _tcCommand;                  // 从 arguments 碎片提取的 command
    // 从 XML 复述提取的全部参数（通用：任意 <parameter=NAME>，含 summary/background 等），
    // 保持出现顺序，避免硬编码字段名导致换工具就失效。
    private readonly List<KeyValuePair<string, string>> _xmlParams = new();
    private readonly StringBuilder _xmlBuf = new(); // 累积 XML 复述内容，用于提取参数

    public ToolCallXmlStripStream(Stream inner) { _inner = inner; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var served = Drain(buffer, offset, count);
        if (served > 0) return served;
        while (true)
        {
            var tmp = new byte[Math.Max(count, 1)];
            var r = _inner.Read(tmp, 0, tmp.Length);
            if (r <= 0) { FlushTail(); return Drain(buffer, offset, count); }
            Feed(tmp, r);
            served = Drain(buffer, offset, count);
            if (served > 0) return served;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var served = Drain(buffer.Span);
        if (served > 0) return served;
        while (true)
        {
            var tmp = new byte[Math.Max(buffer.Length, 1)];
            var r = await _inner.ReadAsync(tmp, cancellationToken);
            if (r <= 0) { FlushTail(); return Drain(buffer.Span); }
            Feed(tmp, r);
            served = Drain(buffer.Span);
            if (served > 0) return served;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer, offset, count));

    private int Drain(byte[] buffer, int offset, int count)
    {
        var avail = _out.Count - _outPos;
        if (avail <= 0) return 0;
        var n = Math.Min(avail, count);
        _out.CopyTo(_outPos, buffer, offset, n);
        _outPos += n;
        if (_outPos >= _out.Count) { _out.Clear(); _outPos = 0; }
        return n;
    }

    private int Drain(Span<byte> span)
    {
        var avail = _out.Count - _outPos;
        if (avail <= 0) return 0;
        var n = Math.Min(avail, span.Length);
        for (int k = 0; k < n; k++) span[k] = _out[_outPos + k];
        _outPos += n;
        if (_outPos >= _out.Count) { _out.Clear(); _outPos = 0; }
        return n;
    }

    /// <summary>把内层数据喂入事件缓冲，切出完整 SSE 事件（以 \n\n 分隔）逐个处理。</summary>
    private void Feed(byte[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _buf.Add(data[i]);
            // SSE 事件以 \n\n 结束（最后一个 \n 可能是 \r\n，这里统一以 \n\n 判定）
            if (data[i] == (byte)'\n' && _buf.Count >= 2 && _buf[_buf.Count - 2] == (byte)'\n')
            {
                // 取出事件（不含末尾的 \n\n）
                var evt = _buf.GetRange(0, _buf.Count - 2).ToArray();
                _buf.Clear();
                ProcessEvent(evt);
            }
        }
    }

    private void ProcessEvent(byte[] evt)
    {
        // 空事件直接忽略
        if (evt.Length == 0) return;

        // 去掉可能的 \r
        var span = evt.AsSpan();
        if (span.Length > 0 && span[span.Length - 1] == (byte)'\r')
            span = span.Slice(0, span.Length - 1);

        // 非 data: 行原样透传
        const string prefix = "data: ";
        if (!span.StartsWith(Encoding.ASCII.GetBytes(prefix)))
        {
            Emit(evt);
            EmitNewline();
            return;
        }

        var json = Encoding.UTF8.GetString(span.Slice(prefix.Length));

        // data: [DONE] 之类非 JSON 原样透传
        if (!IsJsonLike(json))
        {
            Emit(evt);
            EmitNewline();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                Emit(evt);
                EmitNewline();
                return;
            }
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                Emit(evt);
                EmitNewline();
                return;
            }

            // —— tool_calls 规范化 ——
            // 对齐正常模型（stepfun）的结构：首片带 id/type/name/arguments:""，
            // 后续增量片只带 index + arguments。mimo 的 arguments 碎片残缺（summary/background
            // 的值被分流到 XML 复述里），中间片 arguments 一律置空，避免 Copilot 拼出非法 JSON；
            // 等 XML 复述收完拿到 summary/background 后，在 finish_reason 收尾帧
            // 一次性输出 command + summary + background 三字段齐全的完整 arguments。
            string? tcResult = null;
            if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
            {
                tcResult = NormalizeToolCalls(toolCallsEl);
            }
            else if (_tcArgsOpen && choice.TryGetProperty("finish_reason", out var fr) &&
                        fr.ValueKind == JsonValueKind.String && fr.GetString() == "tool_calls")
            {
                // 收尾帧：汇总 command（tool_calls 碎片）+ summary/background（XML 复述）
                var finalArgs = BuildFinalArgs();
                if (finalArgs != null)
                    tcResult = "[{\"index\":0,\"function\":{\"arguments\":" + JsonEncoded(finalArgs) + "}}]";
                ResetToolCallState();
            }

            string? content = null;
            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                content = contentEl.GetString();

            bool drop = false;
            if (content != null)
            {
                if (_inXml)
                {
                    drop = true;
                    _xmlBuf.Append(content);
                    if (content.Contains("</tool_call>", StringComparison.Ordinal))
                    {
                        _inXml = false;
                        ParseXmlParameters(_xmlBuf.ToString());
                        _xmlBuf.Clear();
                    }
                }
                else if (content.Contains("<tool_call", StringComparison.Ordinal))
                {
                    drop = true;
                    _inXml = true;
                    _xmlBuf.Clear();
                    _xmlBuf.Append(content);
                    if (content.Contains("</tool_call>", StringComparison.Ordinal))
                    {
                        _inXml = false;
                        ParseXmlParameters(_xmlBuf.ToString());
                        _xmlBuf.Clear();
                    }
                }
            }

            if (tcResult == null && !drop)
            {
                Emit(evt);
                EmitNewline();
                return;
            }

            // 有 tool_calls 需要重写，或 content 需清空：整体重写 delta
            var rewritten = RewriteDelta(root, tcResult, drop ? "" : null);
            Emit(Encoding.UTF8.GetBytes(prefix + rewritten));
            EmitNewline();
        }
        catch
        {
            // 解析失败则原样透传，避免误伤
            Emit(evt);
            EmitNewline();
        }
    }

    private static bool IsJsonLike(string s)
    {
        var t = s.Trim();
        return t.StartsWith("{") || t.StartsWith("[");
    }

    /// <summary>
    /// 规范化单个事件的 tool_calls 数组，返回重写后的 JSON 文本（不含外层包裹）。
    /// 维护跨事件状态：累积 arguments 碎片并提取 command；
    /// 首片输出 id/type/name/arguments:""，后续增量片只输出 index + arguments:""
    /// （对齐正常模型 stepfun 的结构——给增量片回填 id 会让 Copilot 误判为新 tool_call）。
    /// 完整参数统一推迟到 finish_reason 收尾帧输出，因为 summary/background 的值
    /// 要从稍后才到达的 XML 复述中提取。
    /// </summary>
    private string NormalizeToolCalls(JsonElement toolCalls)
    {
        if (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
            return toolCalls.GetRawText();

        var sb = new StringBuilder();
        sb.Append('[');
        bool first = true;
        foreach (var tc in toolCalls.EnumerateArray())
        {
            if (!first) sb.Append(',');
            first = false;

            int index = 0;
            if (tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                idxEl.TryGetInt32(out index);

            string? id = null, name = null;
            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString();
            if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
            {
                if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString();
                if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                {
                    var frag = argsEl.GetString() ?? "";
                    if (frag.Length > 0)
                    {
                        _tcArgs.Append(frag);
                        _tcArgsOpen = true;
                    }
                }
            }

            if (!string.IsNullOrEmpty(id)) _tcId = id;
            if (!string.IsNullOrEmpty(name)) _tcName = name;

            // 碎片拼到 command 值闭合即可确定，无需等整个 JSON 完整
            if (_tcCommand == null && _tcArgsOpen && TryExtractCommand(_tcArgs.ToString(), out var cmd))
                _tcCommand = cmd;

            sb.Append('{');
            sb.Append("\"index\":").Append(index);
            if (!_tcHasId && !string.IsNullOrEmpty(_tcId))
            {
                // 首片：带 id/type/name，与正常模型一致
                sb.Append(",\"id\":").Append(JsonEncoded(_tcId));
                sb.Append(",\"type\":\"function\"");
                sb.Append(",\"function\":{\"name\":").Append(JsonEncoded(_tcName ?? ""));
                sb.Append(",\"arguments\":\"\"}");
                _tcHasId = true;
            }
            else
            {
                // 后续增量片：只有 index + 空 arguments，不回填 id/name
                sb.Append(",\"function\":{\"arguments\":\"\"}");
            }
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 汇总 tool_calls 通道真实参数与 XML 复述参数，构造完整 arguments。
    /// mimo 对工具调用采用「双轨输出」：主参数（command / query / ...）多数走标准 tool_calls
    /// 通道，但部分必填参数（summary/background/includePattern/maxResults ...）会被分流到
    /// delta.content 的 &lt;parameter=NAME&gt; XML 复述里。两种来源都可能残缺：
    ///   - tool_calls 碎片里某字段值可能被截断（如 "*.cs 缺闭合引号）；
    ///   - XML 复述则包含这些被截断字段的完整值。
    /// 因此以 tool_calls 通道里「已完整闭合」的字段为主，再用 XML 复述补齐缺失/残缺的字段，
    /// 合并后输出完整 JSON。这样无论工具主参叫 command 还是 query 都不必特判。
    /// 若两者都无有效字段才返回 null（收尾帧不输出 arguments）。
    /// </summary>
    private string? BuildFinalArgs()
    {
        // 1. 从 tool_calls 碎片里提取已完整闭合的 "key":value 对
        //    fromArgs 的值已是合法 JSON 字面量（含转义），写入时必须「原样」输出，不可再 JsonEncoded。
        var fromArgs = ExtractCompleteArgs(_tcArgs.ToString());

        // 2. 用 XML 复述补齐：凡是 tool_calls 里缺失的 key 才用 XML 值。
        //    xmlParams 的值是未转义纯文本，写入时需经 ToJsonValue 转义。
        var merged = new List<(string key, string jsonValue, bool fromXml)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in fromArgs)
        {
            merged.Add((kv.Key, kv.Value, false));
            seen.Add(kv.Key);
        }
        foreach (var kv in _xmlParams)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (seen.Add(kv.Key))       // 仅当 tool_calls 中未出现才补充
                merged.Add((kv.Key, kv.Value, true));
        }

        if (merged.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var (key, jsonValue, fromXml) in merged)
        {
            if (!first) sb.Append(',');
            first = false;
            // 来自 tool_calls 通道的值已是 JSON 字面量，原样输出；
            // 来自 XML 复述的值需经 ToJsonValue（含 JsonEncoded 转义）转换。
            sb.Append(JsonEncoded(key)).Append(':')
                .Append(fromXml ? ToJsonValue(jsonValue) : jsonValue);
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 从 tool_calls arguments 碎片文本中提取「已完整闭合」的顶层 "key":value 字段。
    /// 只接受边界完整的值（完整字符串 / 布尔 / 数字 / null），截断或残缺的值直接跳过，
    /// 留给 XML 复述补全。返回 (key, rawJsonValue) 列表，rawJsonValue 已是合法 JSON 字面量。
    /// </summary>
    private static List<KeyValuePair<string, string>> ExtractCompleteArgs(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            // 跳过空白与逗号
            while (i < n && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= n) break;

            // 期望一个 "key"
            if (text[i] != '"') { i++; continue; }
            int keyStart = i + 1;
            int keyEnd = text.IndexOf('"', keyStart);
            if (keyEnd < 0) break; // 截断
            var key = text.Substring(keyStart, keyEnd - keyStart);
            i = keyEnd + 1;

            // 跳过冒号前空白
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != ':') continue;
            i++; // 跳过 ':'
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n) break;

            // 读取值，按类型扫描一个完整 JSON 字面量
            var (valJson, next) = ReadJsonLiteral(text, i);
            if (valJson == null) { i++; continue; } // 残缺，跳过该 key
            result.Add(new KeyValuePair<string, string>(key, valJson));
            i = next;
        }
        return result;
    }

    /// <summary>
    /// 从 pos 开始读一个完整 JSON 字面量（字符串/布尔/数字/null）。
    /// 返回 (json文本, 结束后位置)；若到文本末尾仍未闭合则返回 (null, pos)。
    /// </summary>
    private static (string? json, int next) ReadJsonLiteral(string text, int pos)
    {
        int n = text.Length;
        char c = text[pos];
        if (c == '"')
        {
            // 扫描字符串直到未转义的闭合引号
            int j = pos + 1;
            while (j < n)
            {
                if (text[j] == '\\') { j += 2; continue; }
                if (text[j] == '"') return (text.Substring(pos, j - pos + 1), j + 1);
                j++;
            }
            return (null, pos); // 截断
        }
        if (c == 't' || c == 'f' || c == 'n')
        {
            // true / false / null
            foreach (var lit in new[] { "true", "false", "null" })
            {
                if (n - pos >= lit.Length && text.Substring(pos, lit.Length) == lit)
                    return (lit, pos + lit.Length);
            }
            return (null, pos);
        }
        if (c == '-' || c == '.' || char.IsDigit(c))
        {
            int j = pos;
            while (j < n && (char.IsDigit(text[j]) || text[j] == '.' || text[j] == '-'
                || text[j] == '+' || text[j] == 'e' || text[j] == 'E')) j++;
            if (j == pos) return (null, pos);
            return (text.Substring(pos, j - pos), j);
        }
        // 其它（对象/数组/残缺）——本场景不需要，跳过
        return (null, pos);
    }

    /// <summary>在已解析的 XML 参数中按名字查找（大小写不敏感）。</summary>
    private string? FindXmlParam(string name)
    {
        foreach (var kv in _xmlParams)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>通用解析 mimo XML 复述里的所有参数，形如
    /// &lt;parameter=summary&gt;检查 Git 工作区状态&lt;/parameter&gt;。
    /// 这些值不会出现在标准 tool_calls 字段里，但可能是工具的必填参数。</summary>
    private void ParseXmlParameters(string xml)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            xml, "<parameter=([^>\\s]+)>(.*?)</parameter>",
            System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var name = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (name.Length == 0) continue;

            var idx = _xmlParams.FindIndex(p =>
                string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            var entry = new KeyValuePair<string, string>(name, value);
            if (idx >= 0) _xmlParams[idx] = entry;   // 同名覆盖，保持原位置
            else _xmlParams.Add(entry);
        }
    }

    /// <summary>把 XML 复述里的参数文本转换为对应的 JSON 值字面量。
    /// 说明：mimo 的 XML 复述（如 &lt;parameter=background&gt;False&lt;/parameter&gt;）里是纯文本，
    /// 这里的 False 只是模型照抄了工具 schema 的示例值，属于 XML 脏数据；
    /// 它既不在 JSON 里，也不是什么"Python 风格布尔"——mimo 的 arguments JSON 部分从未出现过
    /// True/False（正常模型给的也是合法 JSON 小写 false）。按内容推断类型即可：
    /// true/false → 布尔字面量，数字 → 数字字面量，其余 → JSON 字符串。</summary>
    private static string ToJsonValue(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0) return "\"\"";
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";
        if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) &&
            !double.IsNaN(d) && !double.IsInfinity(d))
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return JsonEncoded(raw);
    }

    /// <summary>收尾：清空一次工具调用的全部跨事件状态。</summary>
    private void ResetToolCallState()
    {
        _tcArgsOpen = false;
        _tcArgs.Clear();
        _tcId = null;
        _tcName = null;
        _tcHasId = false;
        _tcCommand = null;
        _xmlParams.Clear();
        _xmlBuf.Clear();
    }

    /// <summary>从累积的 arguments 碎片中提取 command 字段的完整字符串值。
    /// mimo 把 summary 的值分流到 XML 复述的 content 里，导致整体 JSON 永远拼不完整，
    /// 因此不依赖整体解析，只用正则提取 "command":"..." 的闭合字符串。</summary>
    private static bool TryExtractCommand(string raw, out string command)
    {
        command = "";
        var m = System.Text.RegularExpressions.Regex.Match(
            raw, "\"command\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return false;
        command = m.Groups[1].Value;
        return true;
    }

    private static string JsonEncoded(string s) => JsonSerializer.Serialize(s);

    /// <summary>
    /// 重写 choices[0].delta：可选替换 tool_calls（传入已规范化的 JSON 文本）与 content。
    /// toolCallsOverride 为 null 表示保留原 tool_calls；contentOverride 为 null 表示保留原 content，
    /// 传 "" 表示把 content 清空（剥离 XML 复述）。其余字段原样保留。
    /// </summary>
    private static string RewriteDelta(JsonElement root, string? toolCallsOverride, string? contentOverride)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteNodeWithDelta(writer, root, toolCallsOverride, contentOverride);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>把 choices[0].delta.content 重写为 ""，其余字段保持不变（旧实现，保留兼容）。</summary>
    private static string RewriteContentEmpty(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteNode(writer, root);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter w, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name == "delta" && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        w.WritePropertyName("delta");
                        w.WriteStartObject();
                        foreach (var dp in p.Value.EnumerateObject())
                        {
                            if (dp.Name == "content")
                                w.WriteString("content", "");
                            else
                                WriteProperty(w, dp.Name, dp.Value);
                        }
                        w.WriteEndObject();
                    }
                    else
                    {
                        WriteProperty(w, p.Name, p.Value);
                    }
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteNode(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.String: w.WriteStringValue(el.GetString() ?? ""); break;
            case JsonValueKind.Number: el.TryGetInt64(out var l); w.WriteNumberValue(l); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            case JsonValueKind.Null: w.WriteNullValue(); break;
            default: w.WriteNullValue(); break;
        }
    }

    private static void WriteProperty(Utf8JsonWriter w, string name, JsonElement value)
    {
        w.WritePropertyName(name);
        WriteNode(w, value);
    }

    /// <summary>
    /// 类似 WriteNode，但在遇到 choices[].delta 时支持覆盖 tool_calls 与 content。
    /// toolCallsOverride 为已序列化的 JSON 文本（含方括号），contentOverride 为 null 表示保留、"" 表示清空。
    /// </summary>
    private static void WriteNodeWithDelta(Utf8JsonWriter w, JsonElement el,
        string? toolCallsOverride, string? contentOverride)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name == "choices" && p.Value.ValueKind == JsonValueKind.Array)
                    {
                        w.WritePropertyName("choices");
                        w.WriteStartArray();
                        foreach (var c in p.Value.EnumerateArray())
                        {
                            if (c.ValueKind == JsonValueKind.Object)
                            {
                                w.WriteStartObject();
                                foreach (var cp in c.EnumerateObject())
                                {
                                    if (cp.Name == "delta" && cp.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        w.WritePropertyName("delta");
                                        w.WriteStartObject();
                                        foreach (var dp in cp.Value.EnumerateObject())
                                        {
                                            if (dp.Name == "tool_calls" && toolCallsOverride != null)
                                            {
                                                w.WritePropertyName("tool_calls");
                                                using var td = JsonDocument.Parse(toolCallsOverride);
                                                WriteNode(w, td.RootElement);
                                            }
                                            else if (dp.Name == "content" && contentOverride != null)
                                                w.WriteString("content", contentOverride);
                                            else
                                                WriteProperty(w, dp.Name, dp.Value);
                                        }
                                        w.WriteEndObject();
                                    }
                                    else
                                    {
                                        WriteProperty(w, cp.Name, cp.Value);
                                    }
                                }
                                w.WriteEndObject();
                            }
                            else
                            {
                                WriteNode(w, c);
                            }
                        }
                        w.WriteEndArray();
                    }
                    else
                    {
                        WriteProperty(w, p.Name, p.Value);
                    }
                }
                w.WriteEndObject();
                break;
            default:
                WriteNode(w, el);
                break;
        }
    }

    private void Emit(byte[] data)
    {
        foreach (var b in data) _out.Add(b);
    }

    private void EmitNewline() => Emit(Encoding.ASCII.GetBytes("\n\n"));

    // 流结束：缓冲区里若残留未闭合事件，仍尝试处理（丢掉残缺尾部也优于输出非法 JSON）
    private void FlushTail()
    {
        if (_buf.Count > 0)
        {
            var evt = _buf.ToArray();
            _buf.Clear();
            if (evt.Length > 0) ProcessEvent(evt);
        }
        _inXml = false;
        ResetToolCallState();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        catch { }
        base.Dispose(true);
    }
}

