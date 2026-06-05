using System.Text;
using System.Threading.Channels;
using copilot_auto_byok.Data;
using copilot_auto_byok.Models.Metrics;
using Microsoft.EntityFrameworkCore;

namespace copilot_auto_byok.Services;

public class MetricsService : IMetricsService, IHostedService, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<MetricsService> _logger;
    private readonly Channel<RequestMetrics> _metricsChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _shutdownCts;
    private bool _disposed;

    public MetricsService(IDbContextFactory<AppDbContext> contextFactory, ILogger<MetricsService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;

        // Create bounded channel for metrics buffering (max 10000 items)
        _metricsChannel = Channel.CreateBounded<RequestMetrics>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _shutdownCts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessMetricsAsync(_shutdownCts.Token));
    }

    public async Task RecordAsync(RequestMetrics metrics)
    {
        // Non-blocking write to channel
        await _metricsChannel.Writer.WriteAsync(metrics).ConfigureAwait(false);
    }

    private async Task ProcessMetricsAsync(CancellationToken cancellationToken)
    {
        var batch = new List<RequestMetrics>();
        var flushInterval = TimeSpan.FromSeconds(5);
        var lastFlush = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for item or timeout
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(500));

                    if (await _metricsChannel.Reader.WaitToReadAsync(cts.Token))
                    {
                        while (_metricsChannel.Reader.TryRead(out var metrics))
                        {
                            batch.Add(metrics);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Ignore timeout and continue
                }

                // Flush batch
                if (batch.Count > 0 && (batch.Count >= 100 || DateTime.UtcNow - lastFlush >= flushInterval))
                {
                    await FlushBatchAsync(batch, cancellationToken);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }

            // Final flush on shutdown
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metrics processing failed");
        }
    }

    private async Task FlushBatchAsync(List<RequestMetrics> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        try
        {
            using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            foreach (var metrics in batch)
            {
                context.RequestMetrics.Add(new RequestMetricsEntity
                {
                    Timestamp = metrics.Timestamp,
                    RequestedModel = metrics.RequestedModel,
                    ActualModel = metrics.ActualModel,
                    Provider = metrics.Provider,
                    ProviderId = metrics.ProviderId,
                    Protocol = metrics.Protocol,
                    IsStreaming = metrics.IsStreaming,
                    PromptTokens = metrics.PromptTokens,
                    CompletionTokens = metrics.CompletionTokens,
                    TotalTokens = metrics.TotalTokens,
                    CachedTokens = metrics.CachedTokens,
                    LatencyMs = metrics.LatencyMs,
                    TotalDurationMs = metrics.TotalDurationMs,
                    TokensPerSecond = metrics.TokensPerSecond,
                    IsCacheHit = metrics.IsCacheHit,
                    StatusCode = metrics.StatusCode,
                    IsSuccess = metrics.IsSuccess,
                    Error = metrics.Error,
                    EstimatedCost = metrics.EstimatedCost
                });
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Flushed {Count} metrics to database", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush {Count} metrics", batch.Count);
        }
    }

    public async Task<List<RequestMetrics>> GetRequestsAsync(int page, int pageSize, string? model, DateTime? from, DateTime? to)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.RequestMetrics.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(model))
            query = query.Where(r => r.RequestedModel == model || r.ActualModel == model);
        if (from.HasValue)
            query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.Timestamp <= to.Value);

        return await query
            .OrderByDescending(r => r.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => MapToModel(r))
            .ToListAsync();
    }

    public async Task<int> GetTotalCountAsync(string? model, DateTime? from, DateTime? to)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.RequestMetrics.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(model))
            query = query.Where(r => r.RequestedModel == model || r.ActualModel == model);
        if (from.HasValue)
            query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.Timestamp <= to.Value);

        return await query.CountAsync();
    }

    public async Task<MetricsSummary> GetSummaryAsync(string period)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var from = GetPeriodStart(period);

        // Use database-side aggregation for better performance
        var summary = await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.Timestamp >= from)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Success = g.Count(r => r.IsSuccess),
                PromptTokens = g.Sum(r => (long)r.PromptTokens),
                CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                TotalTokens = g.Sum(r => (long)r.TotalTokens),
                CachedTokens = g.Sum(r => (long)r.CachedTokens),
                EstimatedCost = g.Sum(r => r.EstimatedCost),
                AvgLatency = g.Average(r => (double?)r.LatencyMs) ?? 0,
                AvgTps = g.Average(r => (double?)r.TokensPerSecond) ?? 0,
                CacheHits = g.Count(r => r.IsCacheHit)
            })
            .FirstOrDefaultAsync();

        if (summary == null)
        {
            return new MetricsSummary
            {
                Period = period,
                TotalRequests = 0,
                SuccessfulRequests = 0,
                FailedRequests = 0,
                SuccessRate = 0,
                TokenUsage = new TokenUsageSummary(),
                Performance = new PerformanceSummary(),
                ModelBreakdown = new List<ModelBreakdown>()
            };
        }

        var result = new MetricsSummary
        {
            Period = period,
            TotalRequests = summary.Total,
            SuccessfulRequests = summary.Success,
            FailedRequests = summary.Total - summary.Success,
            SuccessRate = summary.Total > 0 ? Math.Round((double)summary.Success / summary.Total * 100, 2) : 0,
            TokenUsage = new TokenUsageSummary
            {
                PromptTokens = summary.PromptTokens,
                CompletionTokens = summary.CompletionTokens,
                TotalTokens = summary.TotalTokens,
                CachedTokens = summary.CachedTokens,
                EstimatedCost = summary.EstimatedCost
            },
            Performance = new PerformanceSummary
            {
                AvgLatencyMs = Math.Round(summary.AvgLatency, 2),
                AvgTokensPerSecond = Math.Round(summary.AvgTps, 2),
                CacheHitRate = summary.Total > 0 ? Math.Round((double)summary.CacheHits / summary.Total * 100, 2) : 0
            }
        };

        // Percentiles - still need to load data but only latencies
        var latencies = await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.Timestamp >= from && r.LatencyMs > 0)
            .Select(r => (double)r.LatencyMs)
            .OrderBy(l => l)
            .ToListAsync();

        result.Performance.P50LatencyMs = GetPercentile(latencies, 0.5);
        result.Performance.P95LatencyMs = GetPercentile(latencies, 0.95);
        result.Performance.P99LatencyMs = GetPercentile(latencies, 0.99);

        // Model breakdown - use database-side grouping
        result.ModelBreakdown = await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.Timestamp >= from)
            .GroupBy(r => r.ActualModel)
            .Select(g => new ModelBreakdown
            {
                Model = g.Key,
                Requests = g.Count(),
                Tokens = g.Sum(r => (long)r.TotalTokens),
                AvgLatencyMs = Math.Round(g.Average(r => (double)r.LatencyMs), 2),
                EstimatedCost = g.Sum(r => r.EstimatedCost)
            })
            .OrderByDescending(m => m.Requests)
            .ToListAsync();

        return result;
    }

    public async Task<Dictionary<string, object>> GetRealtimeStatsAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);

        // Use database-side aggregation
        var stats = await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.Timestamp >= oneHourAgo)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Requests = g.Count(),
                Tokens = g.Sum(r => (long)r.TotalTokens),
                AvgLatency = g.Average(r => (double?)r.LatencyMs) ?? 0
            })
            .FirstOrDefaultAsync();

        return new Dictionary<string, object>
        {
            ["requestsLastHour"] = stats?.Requests ?? 0,
            ["tokensLastHour"] = stats?.Tokens ?? 0,
            ["avgLatencyLastHour"] = Math.Round(stats?.AvgLatency ?? 0, 2),
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task<HourlyMetricsResponse> GetHourlyMetricsAsync(string period)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var from = GetPeriodStart(period);

        var hours = new List<string>();
        var hourData = new Dictionary<string, Dictionary<string, HourlyMetrics>>();

        var now = DateTime.UtcNow;
        var current = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        while (current <= now)
        {
            var hourKey = current.ToString("yyyy-MM-ddTHH");
            hours.Add(hourKey);
            hourData[hourKey] = new Dictionary<string, HourlyMetrics>();
            current = current.AddHours(1);
        }

        // Query all data in period and aggregate in memory (SQLite EF doesn't support
        // string formatting in GroupBy, so raw SQL is avoided here).
        var allData = await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.Timestamp >= from)
            .Select(r => new { r.Timestamp, r.ActualModel, r.Provider, r.TotalTokens, r.LatencyMs, r.TokensPerSecond, r.EstimatedCost })
            .ToListAsync();

        var modelKeys = new HashSet<string>();
        foreach (var r in allData)
        {
            var hourKey = r.Timestamp.ToString("yyyy-MM-ddTHH");
            var model = r.ActualModel;
            var provider = r.Provider;
            var key = $"{provider}/{model}";
            modelKeys.Add(key);

            if (!hourData.ContainsKey(hourKey)) continue;

            if (!hourData[hourKey].ContainsKey(key))
            {
                hourData[hourKey][key] = new HourlyMetrics
                {
                    Hour = hourKey,
                    Requests = 0,
                    Tokens = 0,
                    AvgLatencyMs = 0,
                    AvgTokensPerSecond = 0,
                    EstimatedCost = 0
                };
            }

            var hm = hourData[hourKey][key];
            hm.Requests++;
            hm.Tokens += r.TotalTokens;
            hm.EstimatedCost += r.EstimatedCost;
            // Running average for latency and TPS
            hm.AvgLatencyMs = (hm.AvgLatencyMs * (hm.Requests - 1) + r.LatencyMs) / hm.Requests;
            hm.AvgTokensPerSecond = (hm.AvgTokensPerSecond * (hm.Requests - 1) + r.TokensPerSecond) / hm.Requests;
        }

        var series = new List<HourlyModelSeries>();
        foreach (var key in modelKeys.OrderBy(k => k))
        {
            var parts = key.Split('/', 2);
            var s = new HourlyModelSeries
            {
                Model = parts.Length > 1 ? parts[1] : key,
                Provider = parts.Length > 1 ? parts[0] : ""
            };

            foreach (var h in hours)
            {
                if (hourData[h].TryGetValue(key, out var m))
                {
                    s.Requests.Add(m.Requests);
                    s.Tokens.Add(m.Tokens);
                    s.Latency.Add(Math.Round(m.AvgLatencyMs, 2));
                    s.Tps.Add(Math.Round(m.AvgTokensPerSecond, 2));
                }
                else
                {
                    s.Requests.Add(0);
                    s.Tokens.Add(0);
                    s.Latency.Add(0);
                    s.Tps.Add(0);
                }
            }
            series.Add(s);
        }

        return new HourlyMetricsResponse
        {
            Hours = hours.Select(h => h.Substring(11, 2) + ":00").ToList(),
            Series = series
        };
    }

    public async Task<List<string>> GetDistinctModelsAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.RequestMetrics
            .AsNoTracking()
            .Where(r => r.ActualModel != null)
            .Select(r => r.ActualModel)
            .Distinct()
            .OrderBy(m => m)
            .ToListAsync();
    }

    public async Task<string> ExportCsvAsync(DateTime? from, DateTime? to)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.RequestMetrics.AsNoTracking().AsQueryable();

        if (from.HasValue)
            query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.Timestamp <= to.Value);

        var data = await query
            .OrderByDescending(r => r.Timestamp)
            .Select(r => MapToModel(r))
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("Timestamp,RequestedModel,ActualModel,Provider,ProviderId,Protocol,IsStreaming,PromptTokens,CompletionTokens,TotalTokens,CachedTokens,LatencyMs,TotalDurationMs,TokensPerSecond,IsCacheHit,StatusCode,IsSuccess,Error,EstimatedCost");

        foreach (var m in data)
        {
            csv.AppendLine($"{m.Timestamp:O},{m.RequestedModel},{m.ActualModel},{m.Provider},{m.ProviderId},{m.Protocol},{m.IsStreaming},{m.PromptTokens},{m.CompletionTokens},{m.TotalTokens},{m.CachedTokens},{m.LatencyMs},{m.TotalDurationMs},{m.TokensPerSecond},{m.IsCacheHit},{m.StatusCode},{m.IsSuccess},\"{m.Error?.Replace("\"", "\"\"")}\",{m.EstimatedCost}");
        }

        return csv.ToString();
    }

    private static double GetPercentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
        return Math.Round(sortedValues[index], 2);
    }

    private static DateTime GetPeriodStart(string period)
    {
        return period.ToLower() switch
        {
            "1h" => DateTime.UtcNow.AddHours(-1),
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.UtcNow.AddHours(-24)
        };
    }

    private static RequestMetrics MapToModel(RequestMetricsEntity e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        RequestedModel = e.RequestedModel,
        ActualModel = e.ActualModel,
        Provider = e.Provider,
        ProviderId = e.ProviderId,
        Protocol = e.Protocol,
        IsStreaming = e.IsStreaming,
        PromptTokens = e.PromptTokens,
        CompletionTokens = e.CompletionTokens,
        TotalTokens = e.TotalTokens,
        CachedTokens = e.CachedTokens,
        LatencyMs = e.LatencyMs,
        TotalDurationMs = e.TotalDurationMs,
        TokensPerSecond = e.TokensPerSecond,
        IsCacheHit = e.IsCacheHit,
        StatusCode = e.StatusCode,
        IsSuccess = e.IsSuccess,
        Error = e.Error,
        EstimatedCost = e.EstimatedCost
    };

    // IHostedService implementation
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MetricsService started with async batch processing");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MetricsService stopping...");

        _shutdownCts.Cancel();
        _metricsChannel.Writer.Complete();

        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch
        {
            _logger.LogWarning("MetricsService shutdown timeout, forcing stop");
        }

        _logger.LogInformation("MetricsService stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _shutdownCts.Cancel();
            _metricsChannel.Writer.TryComplete();
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore disposal errors
        }

        _shutdownCts.Dispose();
    }
}
