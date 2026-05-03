using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using pi_stream_cam.Services;

namespace pi_stream_cam.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/stream")]
public class StreamController : ControllerBase
{
    private readonly CameraService _cameraService;

    public StreamController(CameraService cameraService)
    {
        _cameraService = cameraService;
    }

    [HttpGet("mjpeg")]
    public async Task MjpegStream()
    {
        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        
        await Response.Body.FlushAsync();
        
        while (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            var frame = _cameraService.GetCurrentFrame();
            if (frame != null && frame.Length > 0)
            {
                var header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
                await Response.WriteAsync(header);
                await Response.Body.WriteAsync(frame);
                await Response.Body.FlushAsync();
            }
            await Task.Delay(100);
        }
    }

    [HttpGet("snapshot")]
    public IActionResult GetSnapshot()
    {
        var frame = _cameraService.GetCurrentFrame();
        if (frame == null || frame.Length == 0)
            return NotFound("No frame available");
        
        return File(frame, "image/jpeg");
    }
}