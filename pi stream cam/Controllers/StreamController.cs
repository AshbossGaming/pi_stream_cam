using Microsoft.AspNetCore.Mvc;
using pi_stream_cam.Services;

namespace pi_stream_cam.Controllers;

[ApiController]
[Route("api/stream")]
public class StreamController : ControllerBase
{
    private readonly CameraService _cameraService;

    public StreamController(CameraService cameraService)
    {
        _cameraService = cameraService;
    }

    [HttpGet("info")]
    public IActionResult GetStreamInfo()
    {
        return Ok(new
        {
            url = _cameraService.StreamUrl,
            type = "RTSP/H.264",
            note = "Add Media Source in OBS, enter rtsp://picam1:8554/cam as URL",
            width = 1280,
            height = 720
        });
    }
}
