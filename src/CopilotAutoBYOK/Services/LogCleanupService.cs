using copilot_auto_byok.Data;
using Microsoft.EntityFrameworkCore;

namespace copilot_auto_byok.Services;

public class LogCleanupService : IHostedService, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<LogCleanupService> _logger;
    private readonly IConfiguration _configuration;
    private Timer? _timer;

    public LogCleanupService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<LogCleanupService> logger,
        IConfiguration configuration)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _configuration = configuration;
    }

    private int RetentionDays =>
        _configuration.GetValue<int>("LogRetentionDays", 30);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Log cleanup service started, retention: {Days} days", RetentionDays);

        // Run first cleanup 1 minute after startup, then every 24 hours
        _timer = new Timer(ExecuteCleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Log cleanup service stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 手动触发清理过期日志，返回删除的记录数。
    /// </summary>
    public async Task<int> CleanupAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        using var context = await _contextFactory.CreateDbContextAsync();
        var deleted = await context.RequestMetrics
            .Where(r => r.Timestamp < cutoff)
            .ExecuteDeleteAsync();

        if (deleted > 0)
        {
            _logger.LogInformation("Cleaned up {Count} log records older than {Cutoff}", deleted, cutoff);
        }

        return deleted;
    }

    /// <summary>
    /// 执行 VACUUM 压缩数据库文件，回收已删除数据占用的磁盘空间。
    /// </summary>
    public async Task<long> VacuumAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var connStr = context.Database.GetConnectionString() ?? "";
        var sourcePrefix = "Data Source=";
        var idx = connStr.IndexOf(sourcePrefix, StringComparison.OrdinalIgnoreCase);
        var filePath = idx >= 0 ? connStr[(idx + sourcePrefix.Length)..].Trim() : null;

        long sizeBefore = filePath != null && System.IO.File.Exists(filePath)
            ? new System.IO.FileInfo(filePath).Length : 0;

        await context.Database.ExecuteSqlRawAsync("VACUUM");

        long sizeAfter = filePath != null && System.IO.File.Exists(filePath)
            ? new System.IO.FileInfo(filePath).Length : 0;

        var freed = sizeBefore - sizeAfter;
        _logger.LogInformation("Database VACUUM completed, size: {Before} -> {After} bytes, freed: {Freed} bytes",
            sizeBefore, sizeAfter, freed);
        return freed;
    }

    private async void ExecuteCleanup(object? state)
    {
        try
        {
            await CleanupAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up old log records");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
