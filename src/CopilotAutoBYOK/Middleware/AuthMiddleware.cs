using copilot_auto_byok.Services;
using Microsoft.Extensions.Caching.Memory;

namespace copilot_auto_byok.Middleware;

public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthMiddleware> _logger;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "valid_api_keys";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(2);

    public AuthMiddleware(RequestDelegate next, ILogger<AuthMiddleware> logger, IMemoryCache cache)
    {
        _next = next;
        _logger = logger;
        _cache = cache;
    }

    public async Task InvokeAsync(HttpContext context, IConfigService configService)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for static files, admin API, and fallback routes
        if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var apiKeys = GetCachedApiKeys(configService);
        if (apiKeys.Count == 0)
        {
            // No keys configured — setup mode, allow all
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Missing or invalid API key. Use Authorization: Bearer <key>\",\"type\":\"auth_error\"}}");
            return;
        }

        var providedKey = authHeader["Bearer ".Length..].Trim();
        var isValid = apiKeys.Contains(providedKey);

        if (!isValid)
        {
            _logger.LogDebug("Invalid API key attempted from {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Invalid API key\",\"type\":\"auth_error\"}}");
            return;
        }

        await _next(context);
    }

    private HashSet<string> GetCachedApiKeys(IConfigService configService)
    {
        if (_cache.TryGetValue(CacheKey, out HashSet<string>? cached))
            return cached ?? new();

        var keys = configService.GetApiKeys();
        var keySet = new HashSet<string>(keys.Select(k => k.Key));

        _cache.Set(CacheKey, keySet, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheExpiration,
            Size = 1
        });

        return keySet;
    }
}
