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

    [HttpGet("snapshot")]
    public IActionResult GetSnapshot()
    {
        var frame = _cameraService.GetCurrentFrame();
        if (frame == null)
            return NotFound(new { error = "No frame available" });

        return File(frame, "image/jpeg");
    }

    [Authorize]
    [HttpGet("snapshot/highres")]
    public IActionResult GetHighResSnapshot(int width = 1920, int height = 1080)
    {
        var frame = _cameraService.CaptureSnapshot(width, height);
        if (frame == null)
            return StatusCode(500, new { error = "Failed to capture snapshot" });

        return File(frame, "image/jpeg");
    }

    [Authorize]
    [HttpPost("record/start")]
    public IActionResult StartRecording([FromQuery] string? filename = null)
    {
        if (_cameraService.IsRecording)
            return BadRequest(new { error = "Already recording", path = _cameraService.RecordingPath });

        var path = filename;
        if (string.IsNullOrEmpty(path))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            path = $"/var/lib/pi-stream-cam/recordings/{timestamp}.mp4";
        }

        var ok = _cameraService.StartRecording(path);
        if (!ok)
            return StatusCode(500, new { error = "Failed to start recording" });

        return Ok(new { recording = true, path });
    }

    [Authorize]
    [HttpPost("record/stop")]
    public IActionResult StopRecording()
    {
        var path = _cameraService.StopRecording();
        return Ok(new { recording = false, path });
    }

    [Authorize]
    [HttpGet("record/status")]
    public IActionResult RecordingStatus()
    {
        return Ok(new
        {
            recording = _cameraService.IsRecording,
            path = _cameraService.RecordingPath,
            uptime = _cameraService.Uptime.TotalSeconds,
            frames = _cameraService.FrameCount
        });
    }
}
