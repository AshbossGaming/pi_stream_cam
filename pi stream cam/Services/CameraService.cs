using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private readonly bool _isRaspberryPi;
    private readonly object _lock = new();
    private byte[]? _latestFrame;
    private CancellationTokenSource? _captureCts;
    private readonly string _cameraId;
    private int _zoom = 1;
    private int _focus = 50;
    private bool _autofocus = true;
    private string _afMode = "continuous";
    private string _focusRange = "normal";
    private int _exposureComp = 0;
    private int _whiteBalance = 0;
    private double _sharpness = 1.0;
    private int _brightness = 0;
    private int _contrast = 1;
    private int _saturation = 1;
    private readonly string _stateFile = "/var/lib/pi-stream-cam/camera-state.json";

    public int Zoom => _zoom;
    public int Focus => _focus;
    public bool AutofocusEnabled => _autofocus;
    public string AfMode => _afMode;
    public string FocusRange => _focusRange;
    public int ExposureCompensation => _exposureComp;
    public int WhiteBalance => _whiteBalance;
    public double Sharpness => _sharpness;
    public int Brightness => _brightness;
    public int Contrast => _contrast;
    public int Saturation => _saturation;
    public bool IsCapturing => _captureCts != null && !_captureCts.IsCancellationRequested;

    public CameraService(string cameraId = "imx519")
    {
        _cameraId = cameraId;
        _isRaspberryPi = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                         (Directory.Exists("/dev/video0") || File.Exists("/usr/bin/libcamera-still"));
        
        LoadState();
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("zoom", out var z))
                {
                    _zoom = Math.Clamp(z.GetInt32(), 1, 8);
                    Console.WriteLine($"Restored zoom: {_zoom}x");
                }
                if (doc.RootElement.TryGetProperty("focus", out var f))
                {
                    _focus = Math.Clamp(f.GetInt32(), 0, 100);
                    Console.WriteLine($"Restored focus: {_focus}");
                }
                if (doc.RootElement.TryGetProperty("autofocus", out var af))
                {
                    _autofocus = af.GetBoolean();
                    Console.WriteLine($"Restored autofocus: {_autofocus}");
                }
                if (doc.RootElement.TryGetProperty("afmode", out var am))
                {
                    _afMode = am.GetString() ?? "continuous";
                    Console.WriteLine($"Restored afmode: {_afMode}");
                }
                if (doc.RootElement.TryGetProperty("focusrange", out var fr))
                {
                    _focusRange = fr.GetString() ?? "normal";
                    Console.WriteLine($"Restored focusrange: {_focusRange}");
                }
                if (doc.RootElement.TryGetProperty("exposurecomp", out var ec))
                {
                    _exposureComp = Math.Clamp(ec.GetInt32(), -8, 8);
                    Console.WriteLine($"Restored exposurecomp: {_exposureComp}");
                }
                if (doc.RootElement.TryGetProperty("whitebalance", out var wb))
                {
                    _whiteBalance = Math.Clamp(wb.GetInt32(), 0, 8);
                    Console.WriteLine($"Restored whitebalance: {_whiteBalance}");
                }
                if (doc.RootElement.TryGetProperty("sharpness", out var sh))
                {
                    _sharpness = sh.GetDouble();
                    Console.WriteLine($"Restored sharpness: {_sharpness}");
                }
                if (doc.RootElement.TryGetProperty("brightness", out var br))
                {
                    _brightness = Math.Clamp(br.GetInt32(), -1, 1);
                    Console.WriteLine($"Restored brightness: {_brightness}");
                }
                if (doc.RootElement.TryGetProperty("contrast", out var co))
                {
                    _contrast = Math.Clamp(co.GetInt32(), 0, 2);
                    Console.WriteLine($"Restored contrast: {_contrast}");
                }
                if (doc.RootElement.TryGetProperty("saturation", out var sa))
                {
                    _saturation = Math.Clamp(sa.GetInt32(), 0, 2);
                    Console.WriteLine($"Restored saturation: {_saturation}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load state error: {ex.Message}");
        }
    }

    public byte[]? GetCurrentFrame()
    {
        lock (_lock)
        {
            return _latestFrame;
        }
    }

    public void SetZoom(int level)
    {
        _zoom = Math.Clamp(level, 1, 8);
        SaveState();
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";
        SaveState();
    }

    public void SetAfMode(string mode)
    {
        _afMode = mode.ToLower() switch
        {
            "continuous" => "continuous",
            "single" => "single",
            "manual" => "manual",
            _ => _afMode
        };
        _autofocus = mode == "continuous" || mode == "single";
        SaveState();
    }

    public void SetFocusRange(string range)
    {
        _focusRange = range.ToLower() switch
        {
            "macro" => "macro",
            "normal" => "normal",
            _ => _focusRange
        };
        SaveState();
    }

    public void SetExposureCompensation(int value)
    {
        _exposureComp = Math.Clamp(value, -8, 8);
        SaveState();
    }

    public void SetWhiteBalance(int value)
    {
        _whiteBalance = Math.Clamp(value, 0, 8);
        SaveState();
    }

    public void SetSharpness(double value)
    {
        _sharpness = Math.Clamp(value, 0.0, 16.0);
        SaveState();
    }

    public void SetBrightness(int value)
    {
        _brightness = Math.Clamp(value, -1, 1);
        SaveState();
    }

    public void SetContrast(int value)
    {
        _contrast = Math.Clamp(value, 0, 2);
        SaveState();
    }

    public void SetSaturation(int value)
    {
        _saturation = Math.Clamp(value, 0, 2);
        SaveState();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _afMode = mode;
        _autofocus = true;
        SaveState();
    }

    public void DisableAutofocus()
    {
        _afMode = "manual";
        _autofocus = false;
        SaveState();
    }
    
    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            var json = System.Text.Json.JsonSerializer.Serialize(new { 
                zoom = _zoom, 
                focus = _focus, 
                autofocus = _autofocus,
                afmode = _afMode,
                focusrange = _focusRange,
                exposurecomp = _exposureComp,
                whitebalance = _whiteBalance,
                sharpness = _sharpness,
                brightness = _brightness,
                contrast = _contrast,
                saturation = _saturation
            });
            File.WriteAllText(_stateFile, json);
        }
        catch { }
    }

    public void StartCapture(int width = 1280, int height = 720, int framerate = 15)
    {
        if (!_isRaspberryPi)
        {
            Console.WriteLine("Camera capture only available on Raspberry Pi");
            return;
        }

        StopCapture();
        _captureCts = new CancellationTokenSource();
        
        Task.Run(() => CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();
        _captureCts = null;
    }

    private async Task CaptureLoop(int width, int height, int framerate, CancellationToken token)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "frame.jpg");
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Build libcamera-still arguments
                // Digital zoom via --roi (region of interest): x,y,w,h as normalized values
                string zoomArg = "";
                if (_zoom > 1)
                {
                    double size = 1.0 / _zoom;
                    double offset = (1.0 - size) / 2.0;
                    zoomArg = $"--roi {offset:F3},{offset:F3},{size:F3},{size:F3}";
                }
                
                var rangeArg = $"--autofocus-range {_focusRange}";
                var focusArg = _afMode switch
                {
                    "continuous" => "--autofocus-mode continuous",
                    "single" => "--autofocus-mode auto",
                    _ => $"--autofocus-mode manual --lens-position {_focus}"
                };
                var exposureArg = _exposureComp != 0 ? $"--ev {_exposureComp}" : "";
                var wbArg = _whiteBalance > 0 ? $"--awb {GetWbValue(_whiteBalance)}" : "";
                var sharpArg = Math.Abs(_sharpness - 1.0) > 0.01 ? $"--sharpness {_sharpness}" : "";
                var brightArg = _brightness != 0 ? $"--brightness {_brightness}" : "";
                var contrastArg = _contrast != 1 ? $"--contrast {_contrast}" : "";
                var satuArg = _saturation != 1 ? $"--saturation {_saturation}" : "";
                
                var args = $"-t 500 --width {width} --height {height} -q 95 -o {tempFile} {rangeArg} {focusArg} {exposureArg} {wbArg} {sharpArg} {brightArg} {contrastArg} {satuArg}";
                if (!string.IsNullOrEmpty(zoomArg)) args += " " + zoomArg;
                args = args.Replace("  ", " ").Trim();
                
                var psi = new ProcessStartInfo
                {
                    FileName = "libcamera-still",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(token);
                    
                    if (File.Exists(tempFile))
                    {
                        var frame = await File.ReadAllBytesAsync(tempFile, token);
                        lock (_lock)
                        {
                            _latestFrame = frame;
                        }
                        try { File.Delete(tempFile); } catch { }
                    }
                }
                
                await Task.Delay(Math.Max(1, 1000 / framerate - 500), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Camera error: {ex.Message}");
                await Task.Delay(1000, token);
            }
        }
    }

    private string GetWbValue(int wb)
    {
        return wb switch
        {
            1 => "auto",
            2 => "incandescent",
            3 => "tungsten",
            4 => "fluorescent",
            5 => "indoor",
            6 => "daylight",
            7 => "cloudy",
            8 => "coolwhite",
            _ => "auto"
        };
    }

    public void Dispose()
    {
        StopCapture();
    }
}
