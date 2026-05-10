using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using pi_stream_cam.Services;
using System.IO;

namespace pi_stream_cam.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/stream")]
public class StreamController : ControllerBase
{
    private readonly CameraService _cameraService;
    private readonly ILogger<StreamController> _logger;

    public StreamController(CameraService cameraService, ILogger<StreamController> logger)
    {
        _cameraService = cameraService;
        _logger = logger;
    }

    [HttpGet("mjpeg")]
    public async Task MjpegStream()
    {
        try
        {
            Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            byte[]? lastFrame = null;

            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                var frame = _cameraService.GetCurrentFrame();
                if (frame != null && frame != lastFrame)
                {
                    var header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
                    await Response.WriteAsync(header, HttpContext.RequestAborted);
                    await Response.Body.WriteAsync(frame, HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                    lastFrame = frame;
                }
                await Task.Delay(30, HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
        }
        catch (IOException ex) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "MJPEG stream disconnected");
        }
    }
}
