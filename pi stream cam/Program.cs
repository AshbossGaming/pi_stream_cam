using System.Diagnostics;
using pi_stream_cam.Services;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue("PORT", 5000);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port);
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

var cameraDevice = builder.Configuration.GetValue<string>("CameraDevicePath") ?? "/dev/video0";
var cameraService = new CameraService(cameraDevice);
var servoService = new ServoService();

builder.Services.AddSingleton(cameraService);
builder.Services.AddSingleton(servoService);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/login", async context =>
{
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

app.MapPost("/api/option", async context =>
{
    var camera = cameraService;
    var servo = servoService;

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);

    int set = 0, failed = 0;

    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        try
        {
            switch (prop.Name)
            {
                case "zoom": camera.SetZoom(prop.Value.GetInt32()); set++; break;
                case "focus": camera.SetFocus(prop.Value.GetInt32()); set++; break;
                case "autofocus": camera.SetAfMode(prop.Value.GetString()!); set++; break;
                case "afmode": camera.SetAfMode(prop.Value.GetString()!); set++; break;
                case "focusrange": camera.SetFocusRange(prop.Value.GetString()!); set++; break;
                case "exposurecomp": camera.SetExposureCompensation(prop.Value.GetInt32()); set++; break;
                case "whitebalance": camera.SetWhiteBalance(prop.Value.GetInt32()); set++; break;
                case "sharpness": camera.SetSharpness(prop.Value.GetDouble()); set++; break;
                case "brightness": camera.SetBrightness(prop.Value.GetInt32()); set++; break;
                case "contrast": camera.SetContrast(prop.Value.GetDouble()); set++; break;
                case "saturation": camera.SetSaturation(prop.Value.GetDouble()); set++; break;
                case "quality": camera.SetQuality(prop.Value.GetInt32()); set++; break;
                case "videoflipped": camera.SetVideoFlip(prop.Value.GetBoolean()); set++; break;
                case "pan": await servo.SetPanAsync(prop.Value.GetInt32()); set++; break;
                case "tilt": await servo.SetTiltAsync(prop.Value.GetInt32()); set++; break;
                default: failed++; break;
            }
        }
        catch { failed++; }
    }

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { set, failed });
}).AllowAnonymous();

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

app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("Shutting down...");
    servoService.Dispose();
    cameraService.StopCapture();
    cameraService.Dispose();
});

app.MapGet("/api/status", () =>
{
    var camera = cameraService;
    var servo = servoService;

    return Results.Ok(new
    {
        version = VersionInfo.Version,
        revision = VersionInfo.Revision,
        buildDate = VersionInfo.BuildDate,

        camera = new
        {
            capturing = camera.IsCapturing,
            hasCamera = camera.HasCamera,
            zoom = camera.Zoom,
            focus = camera.Focus,
            autofocus = camera.AutofocusEnabled,
            afMode = camera.AfMode,
            focusRange = camera.FocusRange,
            exposureCompensation = camera.ExposureCompensation,
            whiteBalance = camera.WhiteBalance,
            sharpness = camera.Sharpness,
            brightness = camera.Brightness,
            contrast = camera.Contrast,
            saturation = camera.Saturation,
            quality = camera.Quality,
            videoFlipped = camera.VideoFlipped
        },

        ptz = new
        {
            pan = servo.PanAngle,
            tilt = servo.TiltAngle,
            flipped = servo.IsFlipped,
            panInverted = servo.IsPanInverted,
            presets = servo.Presets
        },

        endpoints = new
        {
            stream = new { url = camera.StreamUrl, type = "RTSP/H.264" },
            status = new { url = $"/api/status", type = "application/json" },
            ptzStatus = new { url = $"/api/ptz/status", type = "application/json" },
            ptzMove = new { url = $"/api/ptz/move", method = "POST" },
            ptzCenter = new { url = $"/api/ptz/center", method = "POST" },
        },

        system = new
        {
            port,
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processId = Environment.ProcessId,
            startTime = DateTime.UtcNow.ToString("o")
        }
    });
}).AllowAnonymous();



app.MapGet("/", () => Results.Ok(new
{
    web = $"http://picam1:{port}",
            stream = $"rtsp://picam1:8554/cam",
    status = $"/api/status",
    ptzStatus = "/api/ptz/status",
    ptzMove = "POST /api/ptz/move",
    ptzCenter = "POST /api/ptz/center",
    camera = "Arducam 16MP IMX519",
    servos = "MG90S x2 via PCA9685",
    version = VersionInfo.Version
})).AllowAnonymous();

app.MapPost("/api/system/shutdown", async context =>
{
    var password = await GetPasswordFromBody(context);
    var validPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Password"] ?? "admin";
    if (password != validPassword)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid password" });
        return;
    }

    _ = SystemShutdown(context.RequestServices);
    await context.Response.WriteAsJsonAsync(new { message = "Shutdown initiated" });
}).RequireAuthorization();

app.MapPost("/api/system/reboot", async context =>
{
    var password = await GetPasswordFromBody(context);
    var validPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Password"] ?? "admin";
    if (password != validPassword)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid password" });
        return;
    }

    _ = SystemReboot(context.RequestServices);
    await context.Response.WriteAsJsonAsync(new { message = "Reboot initiated" });
}).RequireAuthorization();

app.Run();

static async Task<string> GetPasswordFromBody(HttpContext context)
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("password", out var pwd) ? pwd.GetString() ?? "" : "";
}

static async Task SystemShutdown(IServiceProvider services)
{
    try
    {
        var helper = "/usr/local/bin/pi-cam-power";
        if (File.Exists(helper))
        {
            await Process.Start(helper, "shutdown").WaitForExitAsync();
        }
        else if (OperatingSystem.IsLinux())
        {
            await Process.Start("systemctl", "poweroff").WaitForExitAsync();
        }
        else if (OperatingSystem.IsWindows())
        {
            await Process.Start("shutdown", "/s /t 3").WaitForExitAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Shutdown failed: {ex.Message}");
    }
}

static async Task SystemReboot(IServiceProvider services)
{
    try
    {
        var helper = "/usr/local/bin/pi-cam-power";
        if (File.Exists(helper))
        {
            await Process.Start(helper, "reboot").WaitForExitAsync();
        }
        else if (OperatingSystem.IsLinux())
        {
            await Process.Start("systemctl", "reboot").WaitForExitAsync();
        }
        else if (OperatingSystem.IsWindows())
        {
            await Process.Start("shutdown", "/r /t 3").WaitForExitAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Reboot failed: {ex.Message}");
    }
}
