using Microsoft.AspNetCore.Http.Extensions;

namespace pi_stream_cam.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

        if (path == "/" || path == "/dock" || path.StartsWith("/wwwroot"))
        {
            await _next(context);
            return;
        }

        var key = context.Request.Query["key"].FirstOrDefault()
                  ?? context.Request.Headers["X-Api-Key"].FirstOrDefault();

        var validKey = _config["ApiKey"];

        if (string.IsNullOrEmpty(key) || key != validKey)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await _next(context);
    }
}
