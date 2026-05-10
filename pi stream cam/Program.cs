using pi_stream_cam.Services;

var builder = WebApplication.CreateBuilder(args);

var controlPort = builder.Configuration.GetValue("CONTROL_PORT", 5000);
var streamPort = builder.Configuration.GetValue("STREAM_PORT", 5001);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(controlPort);
    if (streamPort != controlPort)
        options.ListenAnyIP(streamPort);
});

builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddAuthentication("Cookies")
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

var cameraService = new CameraService();
var servoService = new ServoService();

builder.Services.AddSingleton(cameraService);
builder.Services.AddSingleton(servoService);

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (streamPort == controlPort)
    {
        await next();
        return;
    }

    var requestPort = context.Request.Host.Port;
    var isStreamRequest = context.Request.Path.StartsWithSegments("/api/stream");

    if (requestPort == streamPort && !isStreamRequest)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (requestPort == controlPort && isStreamRequest)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/login", async context =>
{
    // Check for mobile app key - auto authenticate
    if (context.Request.Headers.TryGetValue("X-App-Key", out var appKey) && appKey == "pi-stream-cam-mobile-v1")
    {
        var claims = new System.Security.Claims.Claim[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "mobile-app") };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Cookies");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(context, "Cookies", principal);
        context.Response.Redirect("/dock.html");
        return;
    }
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <title>Login - PTZ Camera</title>
    <style>
        * { box-sizing: border-box; margin:0; padding:0; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #1a1a1a; color: #eee; display: flex; align-items: center; justify-content: center; height: 100vh; }
        .login-box { background: #2a2a2a; padding: 32px; border-radius: 8px; width: 320px; border: 1px solid #444; }
        h2 { text-align: center; margin-bottom: 24px; color: #fff; }
        .field { margin-bottom: 16px; }
        label { display: block; font-size: 12px; color: #aaa; margin-bottom: 6px; }
        input { width: 100%; padding: 10px; background: #1a1a1a; border: 1px solid #444; color: #fff; border-radius: 4px; font-size: 14px; }
        input:focus { outline: none; border-color: #666; }
        button { width: 100%; padding: 10px; background: #4a4; border: none; color: #fff; border-radius: 4px; font-size: 14px; cursor: pointer; }
        button:hover { background: #5b5; }
        .error { color: #f88; font-size: 12px; margin-top: 8px; text-align: center; }
    </style>
</head>
<body>
    <div class='login-box'>
        <h2>PTZ Camera Login</h2>
        <form method='post' action='/login'>
            <div class='field'>
                <label>Password</label>
                <input type='password' name='password' placeholder='Enter password' autofocus>
            </div>
            <button type='submit'>Login</button>
            " + (context.Request.Query["error"].FirstOrDefault() != null ? "<div class='error'>Invalid password</div>" : "") + @"
        </form>
    </div>
</body>
</html>");
}).AllowAnonymous();

app.MapPost("/login", async context =>
{
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var validPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Password"] ?? "admin";

    if (password == validPassword)
    {
        var claims = new System.Security.Claims.Claim[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "user") };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Cookies");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(context, "Cookies", principal);
        context.Response.Redirect("/dock");
    }
    else
    {
        context.Response.Redirect("/login?error=1");
    }
}).AllowAnonymous();

app.MapGet("/logout", async context =>
{
    await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(context, "Cookies");
    context.Response.Redirect("/login");
});

app.UseStaticFiles();
app.UseCors();
app.MapControllers();

app.MapGet("/mobile", context =>
{
    context.Response.Redirect("/dock");
    return Task.CompletedTask;
}).RequireAuthorization();

app.MapGet("/dock", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync("wwwroot/dock.html");
}).RequireAuthorization();

cameraService.StartCapture();

app.MapGet("/", () => Results.Ok(new
{
    control = $"http://picam1:{controlPort}",
    stream = $"http://picam1:{streamPort}/api/stream/mjpeg",
    ptzStatus = "/api/ptz/status",
    ptzMove = "POST /api/ptz/move",
    ptzCenter = "POST /api/ptz/center",
    camera = "Arducam 16MP IMX519",
    servos = "MG90S x2 via PCA9685"
})).AllowAnonymous();

app.Run();

