/*
使用中发现 vs2026 中 copilot 调用 MIMO API 存在大量工具调用失败导致反复尝试的问题
怀疑 toolcall 缺少 reasoning_content 的问题，尝试在代理层缓存和补填，后来在解决了
True/False json 解析失败的问题后，感觉不应该人为干预 reasoning_content，故通过停
用条件编译参数 CACHE_REASONING_CONTENT 的方式，不再缓存和发送 reasoning_content
*/
#undef CACHE_REASONING_CONTENT
/*
copilot 使用 mimo 工具调用还是出现失败反复尝试的问题，比如 grep_search 和 run_cmd_in_terminal
启用 STRIP_REASONING_CONTENT 条件编译参数，从 request/response 中同步移除 reasoning_content
通过 ShouldStripReasoningContent() 控制仅针对 mimo api 进行处理，不影响其他 LLM 模型的工具调用
*/
#define STRIP_REASONING_CONTENT
using copilot_auto_byok.Models;
using copilot_auto_byok.Models.Metrics;
using System.Buffers;
#if CACHE_REASONING_CONTENT
using System.Collections.Concurrent;
#endif
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace copilot_auto_byok.Services;

public interface IProxyService
{
    Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, string pathAndQuery, string bodyText, string protocol, string requestedModel, bool isStreaming);
}

/// <summary>
/// A stream wrapper that converts Python-style booleans (True/False) to JSON-style (true/false).
/// This is needed because some APIs (like MiMo) return Python-style booleans in JSON responses,
/// which causes parsing failures in clients expecting standard JSON.
/// Handles buffer boundary issues where "True"/"False" may be split across reads.
/// </summary>
internal class BooleanConvertStream : Stream
{
    private readonly Stream _inner;
    private readonly StringBuilder _buffer = new();
    private bool _disposed;

    // Potential partial matches at buffer end that need to be held back
    private static readonly string[] _partialTrue = ["T", "Tr", "Tru"];
    private static readonly string[] _partialFalse = ["F", "Fa", "Fal", "Fals"];

    public BooleanConvertStream(Stream inner)
    {
        _inner = inner;
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
        if (read <= 0) return read;

        var text = Encoding.UTF8.GetString(buffer, offset, read);
        _buffer.Append(text);

        var (output, remaining) = ExtractCompleteData(_buffer.ToString());
        _buffer.Clear();
        _buffer.Append(remaining);

        var bytes = Encoding.UTF8.GetBytes(output);
        var copyCount = Math.Min(bytes.Length, count);
        Array.Copy(bytes, 0, buffer, offset, copyCount);
        return copyCount;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Rent a temporary array for the inner read
        var temp = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = await _inner.ReadAsync(temp.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0) return read;

            var text = Encoding.UTF8.GetString(temp, 0, read);
            _buffer.Append(text);

            var (output, remaining) = ExtractCompleteData(_buffer.ToString());
            _buffer.Clear();
            _buffer.Append(remaining);

            var bytes = Encoding.UTF8.GetBytes(output);
            var copyCount = Math.Min(bytes.Length, buffer.Length);
            bytes.AsSpan(0, copyCount).CopyTo(buffer.Span);
            return copyCount;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    // Keep the byte[] override for compatibility
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Extracts complete data from the buffer, holding back partial "True"/"False" at the end.
    /// Returns (dataToOutput, remainingPartial).
    /// Uses streaming-optimized conversion (no JSON parsing) since SSE chunks aren't pure JSON.
    /// </summary>
    private static (string output, string remaining) ExtractCompleteData(string buffer)
    {
        if (string.IsNullOrEmpty(buffer))
            return ("", "");

        // Check if buffer ends with a partial "True" or "False"
        var holdback = GetHoldbackLength(buffer);

        if (holdback > 0)
        {
            var safePart = buffer[..^holdback];
            var partialPart = buffer[^holdback..];
            var converted = ConvertPythonBooleansStreaming(safePart);
            return (converted, partialPart);
        }

        // No partial match, convert and return everything
        return (ConvertPythonBooleansStreaming(buffer), "");
    }

    /// <summary>
    /// Returns how many characters at the end of the buffer might be the start
    /// of an incomplete "True" or "False" that should be held back.
    /// </summary>
    private static int GetHoldbackLength(string buffer)
    {
        foreach (var partial in _partialFalse)
        {
            if (buffer.EndsWith(partial, StringComparison.Ordinal))
                return partial.Length;
        }
        foreach (var partial in _partialTrue)
        {
            if (buffer.EndsWith(partial, StringComparison.Ordinal))
                return partial.Length;
        }
        return 0;
    }

    /// <summary>
    /// Converts Python-style booleans (True/False) to JSON-style (true/false) in a JSON string.
    /// Strategy: replace first → parse to validate → re-serialize for clean output.
    /// Used for non-streaming responses where full JSON validation is desired.
    /// </summary>
    internal static string ConvertPythonBooleans(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Step 1: Replace Python-style booleans with JSON-style
        var replaced = ReplacePythonBooleans(text);

        // Step 2: Parse to validate and re-serialize (cleans up formatting)
        try
        {
            using var doc = JsonDocument.Parse(replaced);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch
        {
            // Not valid JSON even after replacement, return replaced string
            return replaced;
        }
    }

    /// <summary>
    /// Converts Python-style booleans for streaming SSE responses.
    /// Only does string replacement without JSON parsing validation,
    /// since SSE format (data: {...}\n) is not pure JSON.
    /// </summary>
    internal static string ConvertPythonBooleansStreaming(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return ReplacePythonBooleans(text);
    }

    /// <summary>
    /// Replaces Python-style booleans (True/False) with JSON-style (true/false).
    /// Only replaces when they appear as JSON values (not inside string content).
    /// </summary>
    private static string ReplacePythonBooleans(string text)
    {
        var result = new StringBuilder(text.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Handle escape sequences inside strings
            if (escaped)
            {
                result.Append(c);
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    result.Append(c);
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = false;
                    result.Append(c);
                    continue;
                }
                // Inside string, keep as-is
                result.Append(c);
                continue;
            }

            // Outside string
            if (c == '"')
            {
                inString = true;
                result.Append(c);
                continue;
            }

            // Check for "True" (4 chars)
            if (i + 4 <= text.Length && text.AsSpan(i, 4).SequenceEqual("True"))
            {
                result.Append("true");
                i += 3; // Skip 'r', 'u', 'e'
                continue;
            }

            // Check for "False" (5 chars)
            if (i + 5 <= text.Length && text.AsSpan(i, 5).SequenceEqual("False"))
            {
                result.Append("false");
                i += 4; // Skip 'a', 'l', 's', 'e'
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}

#if STRIP_REASONING_CONTENT
/// <summary>
/// A stream wrapper that strips "reasoning_content" from JSON objects in SSE/streaming responses.
/// </summary>
internal class ReasoningContentStripStream : Stream
{
    private readonly Stream _inner;
    private readonly StringBuilder _buffer = new();

    public ReasoningContentStripStream(Stream inner) { _inner = inner; }

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
        if (read <= 0) return read;
        return ProcessAndCopy(buffer, offset, count, read);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (read <= 0) return read;
        return ProcessAndCopy(buffer, offset, count, read);
    }

    private int ProcessAndCopy(byte[] outputBuffer, int outputOffset, int maxCopy, int read)
    {
        var text = Encoding.UTF8.GetString(outputBuffer, outputOffset, read);
        outputBuffer.AsSpan(outputOffset, read).Clear();
        _buffer.Append(text);

        var (processed, remaining) = ProcessSseBuffer(_buffer.ToString());
        _buffer.Clear();
        _buffer.Append(remaining);

        var bytes = Encoding.UTF8.GetBytes(processed);
        var copyCount = Math.Min(bytes.Length, maxCopy);
        bytes.AsSpan(0, copyCount).CopyTo(outputBuffer.AsSpan(outputOffset, maxCopy));
        return copyCount;
    }

    private static (string output, string remaining) ProcessSseBuffer(string buffer)
    {
        if (string.IsNullOrEmpty(buffer))
            return ("", "");

        var output = new StringBuilder();
        var lastNewline = buffer.LastIndexOf('\n');

        if (lastNewline < 0)
        {
            output.Append(StripReasoningFromLine(buffer));
            return (output.ToString(), "");
        }

        var completePart = buffer[..(lastNewline + 1)];
        var remaining = buffer[(lastNewline + 1)..];

        foreach (var line in completePart.Split('\n'))
        {
            if (line.Length == 0)
            {
                output.AppendLine();
                continue;
            }
            var stripped = StripReasoningFromLine(line);
            output.AppendLine(stripped);
        }

        return (output.ToString(), remaining);
    }

    private static string StripReasoningFromLine(string line)
    {
        if (line.StartsWith("data: ", StringComparison.Ordinal))
        {
            var jsonPart = line["data: ".Length..];
            if (jsonPart.Equals("[DONE]", StringComparison.Ordinal))
                return line;

            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                var newJson = StripReasoningFromJson(doc.RootElement);
                return $"data: {newJson}";
            }
            catch
            {
                return line;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            return StripReasoningFromJson(doc.RootElement);
        }
        catch
        {
            return line;
        }
    }

    private static string StripReasoningFromJson(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            WriteWithoutReasoning(writer, element);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteWithoutReasoning(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "reasoning_content", StringComparison.Ordinal))
                        continue;

                    writer.WritePropertyName(prop.Name);
                    WriteWithoutReasoning(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteWithoutReasoning(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
#endif

/// <summary>
/// A stream that copies all read data to a memory buffer for later analysis.
/// When disposed, it parses the collected data for usage metrics.
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

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try
            {
                _copy.Position = 0;
                var contentText = Encoding.UTF8.GetString(_copy.ToArray());

                if (_protocol == "openai")
                    ParseOpenAIStreamingUsage(contentText, _metrics);
                else
                    ParseAnthropicStreamingUsage(contentText, _metrics);

                _metrics.TotalDurationMs = _stopwatch.ElapsedMilliseconds;
                _metrics.IsSuccess = _isSuccess;
                _metrics.EstimatedCost = CalculateCostStatic(_metrics);
                _metrics.TokensPerSecond = _metrics.TotalDurationMs > 0 && _metrics.CompletionTokens > 0
                    ? Math.Round(_metrics.CompletionTokens / (_metrics.TotalDurationMs / 1000.0), 2)
                    : 0;

                _metricsService.RecordAsync(_metrics).ConfigureAwait(false);
                _logger.LogInformation("Streaming metrics recorded: prompt={Prompt}, completion={Completion}, total={Total}ms, contentLength={ContentLength}",
                    _metrics.PromptTokens, _metrics.CompletionTokens, _metrics.TotalDurationMs, contentText.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse streaming usage");
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
                        if (usage.TryGetProperty("output_tokens", out var ot))
                            metrics.CompletionTokens = ot.GetInt32();
                        metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;
                    }
                    if (doc.RootElement.TryGetProperty("type", out var type2) &&
                        type2.GetString() == "message_start" &&
                        doc.RootElement.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("usage", out var usage2))
                    {
                        if (usage2.TryGetProperty("input_tokens", out var it))
                            metrics.PromptTokens = it.GetInt32();
                        metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;
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

public class ProxyService : IProxyService
{
#if CACHE_REASONING_CONTENT
    private static readonly ConcurrentDictionary<string, string> ReasoningContentCache = new();
    private static bool ShouldCacheReasoningContent(string model) =>
        model.StartsWith("mimo", StringComparison.OrdinalIgnoreCase) &&
        !model.Contains("tts", StringComparison.OrdinalIgnoreCase) &&
        !model.Contains("asr", StringComparison.OrdinalIgnoreCase);
#endif
#if STRIP_REASONING_CONTENT
    private static bool ShouldStripReasoningContent(string model) =>
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
                    throw new InvalidOperationException("Model name must use 'providerName,modelName' format, or 'auto-copilot'.");
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
                var teeStream = new MetricsCollectingStream(
                    originalStream, targetProviderType, metrics, stopwatch, _metricsService, _logger, response.IsSuccessStatusCode);

#if STRIP_REASONING_CONTENT
                if (ShouldStripReasoningContent(targetModel))
                {
                    var stripStream = new ReasoningContentStripStream(teeStream);
                    var booleanConvertStream = new BooleanConvertStream(stripStream);
                    var originalHeaders = response.Content.Headers.ToList();
                    response.Content = new StreamContent(booleanConvertStream);
                    foreach (var header in originalHeaders)
                    {
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    return response;
                }
                else
#endif
                {
                    // Wrap with BooleanConvertStream to convert Python-style booleans to JSON-style
                    var booleanConvertStream = new BooleanConvertStream(teeStream);

                    // Replace content with the tee stream; preserve original content headers
                    var originalHeaders = response.Content.Headers.ToList();
                    response.Content = new StreamContent(booleanConvertStream);
                    foreach (var header in originalHeaders)
                    {
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    return response;
                }
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
#if STRIP_REASONING_CONTENT
                if (ShouldStripReasoningContent(targetModel))
                {
                    responseContent = StripReasoningContentFromJsonString(responseContent);
                }
#endif
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
            stopwatch.Stop();
            if (metrics.TotalDurationMs == 0)
                metrics.TotalDurationMs = stopwatch.ElapsedMilliseconds;
            // For streaming, metrics are recorded when MetricsCollectingStream is disposed
            if (!isStreaming)
                await _metricsService.RecordAsync(metrics);
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
#if CACHE_REASONING_CONTENT
                    else if (providerType == "openai" && ShouldCacheReasoningContent(model))
                    {
                        NormalizeOpenAICompatibleRequest(jsonObj, provider.Id, model);
                    }
#endif
                    var newBody = jsonObj.ToJsonString();
                    _logger.LogInformation("Body replaced: oldModel={OldModel}, newModel={NewModel}, body={Body}", oldModel, model, newBody);
                    forwardRequest.Content = new StringContent(newBody, System.Text.Encoding.UTF8, "application/json");
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
#if CACHE_REASONING_CONTENT
    private static void NormalizeOpenAICompatibleRequest(System.Text.Json.Nodes.JsonObject jsonObj, string providerId, string model)
    {
        if (jsonObj["messages"] is not System.Text.Json.Nodes.JsonArray messages)
            return;

        var cacheKey = $"{providerId}:{model}";
        var lastReasoning = ReasoningContentCache.TryGetValue(cacheKey, out var cached) ? cached : "";

        foreach (var message in messages)
        {
            if (message is not System.Text.Json.Nodes.JsonObject messageObj)
                continue;

            var role = messageObj["role"]?.GetValue<string>();
            if (role != "assistant")
                continue;

            var reasoningContent = messageObj["reasoning_content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(reasoningContent))
            {
                lastReasoning = reasoningContent;
                ReasoningContentCache[cacheKey] = reasoningContent;
            }
            else if (!string.IsNullOrEmpty(lastReasoning) && messageObj.ContainsKey("tool_calls"))
            {
                messageObj["reasoning_content"] = lastReasoning;
            }
        }
    }
#endif

#if STRIP_REASONING_CONTENT
    private static string StripReasoningContentFromJsonString(string jsonText)
    {
        if (string.IsNullOrEmpty(jsonText))
            return jsonText;

        var output = new StringBuilder();
        var lines = jsonText.Split(['\n'], StringSplitOptions.None);

        foreach (var line in lines)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var jsonPart = line["data: ".Length..];
                if (jsonPart.Equals("[DONE]", StringComparison.Ordinal))
                {
                    output.AppendLine(line);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(jsonPart);
                    var newJson = StripReasoningFromJson(doc.RootElement);
                    output.AppendLine($"data: {newJson}");
                }
                catch
                {
                    output.AppendLine(line);
                }
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var newJson = StripReasoningFromJson(doc.RootElement);
                    output.AppendLine(newJson);
                }
                catch
                {
                    output.AppendLine(line);
                }
            }
        }

        return output.ToString();
    }

    private static string StripReasoningFromJson(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            WriteWithoutReasoning(writer, element);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteWithoutReasoning(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "reasoning_content", StringComparison.Ordinal))
                        continue;

                    writer.WritePropertyName(prop.Name);
                    WriteWithoutReasoning(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteWithoutReasoning(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
#endif

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
