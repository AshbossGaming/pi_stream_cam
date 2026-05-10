using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using pi_stream_cam.Services;
using pi_stream_cam.Models;

namespace pi_stream_cam.Controllers;

[ApiController]
[Route("api/ptz")]
public class PtzController : ControllerBase
{
    private readonly ServoService _servoService;
    private readonly CameraService _cameraService;

    public PtzController(ServoService servoService, CameraService cameraService)
    {
        _servoService = servoService;
        _cameraService = cameraService;
    }

    private const string APP_KEY = "pi-stream-cam-mobile-v1";

    private bool IsAuthenticated()
    {
        if (Request.Headers.TryGetValue("X-App-Key", out var appKey) && appKey == APP_KEY)
            return true;
        return User.Identity?.IsAuthenticated ?? false;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!IsAuthenticated()) return Unauthorized();
        return Ok(new { 
            pan = _servoService.PanAngle, 
            tilt = _servoService.TiltAngle,
            flipped = _servoService.IsFlipped,
            paninverted = _servoService.IsPanInverted,
            zoom = _cameraService.Zoom,
            focus = _cameraService.Focus,
            autofocus = _cameraService.AutofocusEnabled,
            afmode = _cameraService.AfMode,
            focusrange = _cameraService.FocusRange,
            exposurecomp = _cameraService.ExposureCompensation,
            whitebalance = _cameraService.WhiteBalance,
            sharpness = _cameraService.Sharpness,
            brightness = _cameraService.Brightness,
            contrast = _cameraService.Contrast,
            saturation = _cameraService.Saturation,
            videoflipped = _cameraService.VideoFlipped,
            presets = _servoService.Presets
        });
    }

    [HttpPost("presets/{index}")]
    public IActionResult SetPreset(int index, [FromBody] PtzPreset? preset)
    {
        if (!IsAuthenticated()) return Unauthorized();
        if (preset == null) return BadRequest(new { error = "Preset body is required" });
        _servoService.SetPreset(index, preset.Pan, preset.Tilt);
        return Ok(new { presets = _servoService.Presets });
    }

    [HttpDelete("presets/{index}")]
    public IActionResult ClearPreset(int index)
    {
        _servoService.ClearPreset(index);
        return Ok(new { presets = _servoService.Presets });
    }

    [HttpPost("zoom/{level}")]
    public IActionResult SetZoom(int level)
    {
        _cameraService.SetZoom(level);
        return Ok(new { zoom = _cameraService.Zoom });
    }

    [HttpPost("focus/{value}")]
    public IActionResult SetFocus(int value)
    {
        _cameraService.SetFocus(value);
        return Ok(new { focus = _cameraService.Focus, autofocus = _cameraService.AutofocusEnabled });
    }

    [HttpPost("autofocus/{mode}")]
    public IActionResult SetAutofocus(string mode)
    {
        _cameraService.SetAfMode(mode);
        return Ok(new { autofocus = _cameraService.AutofocusEnabled, afmode = _cameraService.AfMode });
    }

    [HttpPost("focus-range/{range}")]
    public IActionResult SetFocusRange(string range)
    {
        _cameraService.SetFocusRange(range);
        return Ok(new { focusrange = _cameraService.FocusRange });
    }

    [HttpPost("exposure/{value}")]
    public IActionResult SetExposureCompensation(int value)
    {
        _cameraService.SetExposureCompensation(value);
        return Ok(new { exposurecomp = _cameraService.ExposureCompensation });
    }

    [HttpPost("whitebalance/{value}")]
    public IActionResult SetWhiteBalance(int value)
    {
        _cameraService.SetWhiteBalance(value);
        return Ok(new { whitebalance = _cameraService.WhiteBalance });
    }

    [HttpPost("sharpness/{value}")]
    public IActionResult SetSharpness(double value)
    {
        _cameraService.SetSharpness(value);
        return Ok(new { sharpness = _cameraService.Sharpness });
    }

    [HttpPost("brightness/{value}")]
    public IActionResult SetBrightness(int value)
    {
        _cameraService.SetBrightness(value);
        return Ok(new { brightness = _cameraService.Brightness });
    }

    [HttpPost("contrast/{value}")]
    public IActionResult SetContrast(int value)
    {
        _cameraService.SetContrast(value);
        return Ok(new { contrast = _cameraService.Contrast });
    }

    [HttpPost("saturation/{value}")]
    public IActionResult SetSaturation(int value)
    {
        _cameraService.SetSaturation(value);
        return Ok(new { saturation = _cameraService.Saturation });
    }

    [HttpPost("pan/{angle}")]
    public async Task<IActionResult> SetPan(int angle)
    {
        await _servoService.SetPanAsync(angle);
        return Ok(new { pan = _servoService.PanAngle });
    }

    [HttpPost("tilt/{angle}")]
    public async Task<IActionResult> SetTilt(int angle)
    {
        await _servoService.SetTiltAsync(angle);
        return Ok(new { tilt = _servoService.TiltAngle });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] PtzMoveRequest? request)
    {
        if (request == null) return BadRequest(new { error = "Move body is required" });

        if (request.DeltaPan != 0)
            await _servoService.MovePanAsync(request.DeltaPan);
        if (request.DeltaTilt != 0)
            await _servoService.MoveTiltAsync(request.DeltaTilt);
        
        return Ok(new { pan = _servoService.PanAngle, tilt = _servoService.TiltAngle });
    }

    [HttpPost("center")]
    public async Task<IActionResult> Center()
    {
        await _servoService.CenterAsync();
        return Ok(new { pan = _servoService.PanAngle, tilt = _servoService.TiltAngle });
    }

    [HttpPost("flip")]
    public async Task<IActionResult> Flip()
    {
        await _servoService.FlipAsync();
        return Ok(new { pan = _servoService.PanAngle, tilt = _servoService.TiltAngle, flipped = _servoService.IsFlipped });
    }

    [HttpPost("videoflip")]
    public IActionResult SyncVideoFlip()
    {
        _cameraService.SetVideoFlip(_servoService.TiltAngle > 90);
        return Ok(new { videoflipped = _cameraService.VideoFlipped });
    }
}

public class PtzMoveRequest
{
    public int DeltaPan { get; set; }
    public int DeltaTilt { get; set; }
}






