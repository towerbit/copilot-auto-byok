using System.Text.Json;

namespace copilot_auto_byok.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred: {Message}", ex.Message);

            if (context.Response.HasStarted)
            {
                return;
            }

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";

            var error = new
            {
                error = new
                {
                    message = "Internal server error",
                    type = "server_error"
                }
            };

            var json = JsonSerializer.Serialize(error);
            await context.Response.WriteAsync(json);
        }
    }
}
