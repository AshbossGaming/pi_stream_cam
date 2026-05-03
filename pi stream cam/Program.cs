using pi_stream_cam.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/login", async context =>
{
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
        context.Response.Redirect("/");
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

app.MapGet("/mobile", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/mobile.html");
}).RequireAuthorization();

app.MapGet("/dock", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/dock.html");
}).RequireAuthorization();

cameraService.StartCapture();

app.MapGet("/", () => Results.Ok(new
{
    stream = "/api/stream/mjpeg",
    snapshot = "/api/stream/snapshot",
    ptzStatus = "/api/ptz/status",
    ptzMove = "POST /api/ptz/move",
    ptzCenter = "POST /api/ptz/center",
    camera = "Arducam 16MP IMX519",
    servos = "MG90S x2 via PCA9685"
})).AllowAnonymous();

app.Run();
