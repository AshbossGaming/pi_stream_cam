using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private readonly bool _isRaspberryPi;
    private readonly object _lock = new();
    private byte[]? _latestFrame;
    private CancellationTokenSource? _captureCts;
    private CancellationTokenSource? _restartCts = null;
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
    private int _quality = 85;
    private bool _videoFlipped;

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
    public int Quality => _quality;
    public bool IsCapturing => _captureCts != null && !_captureCts.IsCancellationRequested;
    public bool VideoFlipped => _videoFlipped;

    public CameraService(string cameraId = "imx519")
    {
        _cameraId = cameraId;
        _isRaspberryPi = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                         (Directory.Exists("/dev/video0") || 
                          File.Exists("/usr/bin/libcamera-vid") || 
                          File.Exists("/usr/bin/rpicam-vid") ||
                          File.Exists("/usr/bin/libcamera-still"));
        
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
                if (doc.RootElement.TryGetProperty("videoflipped", out var vf))
                {
                    _videoFlipped = vf.GetBoolean();
                    Console.WriteLine($"Restored videoflipped: {_videoFlipped}");
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
        Console.WriteLine($"[DEBUG_LOG] Camera Zoom set to: {_zoom}x");
        SaveState();
    }

    private void RestartCapture()
    {
        if (!IsCapturing) return;
        
        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        var token = _restartCts.Token;
        
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token);
                if (!token.IsCancellationRequested && IsCapturing)
                {
                    Console.WriteLine("[DEBUG_LOG] Killing capture process to apply settings...");
                    try
                    {
                        if (_captureProcess != null && !_captureProcess.HasExited)
                            _captureProcess.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";
        Console.WriteLine($"[DEBUG_LOG] Camera Focus set to: {_focus} (Manual)");
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
        _autofocus = _afMode == "continuous" || _afMode == "single";
        Console.WriteLine($"[DEBUG_LOG] Camera AF Mode set to: {_afMode}");
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
        Console.WriteLine($"[DEBUG_LOG] Camera Focus Range set to: {_focusRange}");
        SaveState();
    }

    public void SetExposureCompensation(int value)
    {
        _exposureComp = Math.Clamp(value, -8, 8);
        Console.WriteLine($"[DEBUG_LOG] Camera Exposure set to: {_exposureComp}");
        SaveState();
        RestartCapture();
    }

    public void SetWhiteBalance(int value)
    {
        _whiteBalance = Math.Clamp(value, 0, 8);
        Console.WriteLine($"[DEBUG_LOG] Camera WB set to: {_whiteBalance}");
        SaveState();
        RestartCapture();
    }

    public void SetSharpness(double value)
    {
        _sharpness = Math.Clamp(value, 0.0, 16.0);
        Console.WriteLine($"[DEBUG_LOG] Camera Sharpness set to: {_sharpness}");
        SaveState();
        RestartCapture();
    }

    public void SetBrightness(int value)
    {
        _brightness = Math.Clamp(value, -1, 1);
        Console.WriteLine($"[DEBUG_LOG] Camera Brightness set to: {_brightness}");
        SaveState();
        RestartCapture();
    }

    public void SetContrast(int value)
    {
        _contrast = Math.Clamp(value, 0, 2);
        Console.WriteLine($"[DEBUG_LOG] Camera Contrast set to: {_contrast}");
        SaveState();
        RestartCapture();
    }

    public void SetQuality(int value)
    {
        _quality = Math.Clamp(value, 10, 100);
        Console.WriteLine($"[DEBUG_LOG] Camera Quality set to: {_quality}");
        SaveState();
        RestartCapture();
    }
    public void SetSaturation(int value)
    {
        _saturation = Math.Clamp(value, 0, 2);
        Console.WriteLine($"[DEBUG_LOG] Camera Saturation set to: {_saturation}");
        SaveState();
        RestartCapture();
    }

    public void SetVideoFlip(bool flipped)
    {
        _videoFlipped = flipped;
        Console.WriteLine($"[DEBUG_LOG] Camera video flip set to: {_videoFlipped}");
        SaveState();
        RestartCapture();
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
                saturation = _saturation,
                videoflipped = _videoFlipped
            });
            WriteAllTextAtomic(_stateFile, json);
        }
        catch { }
    }

    private static void WriteAllTextAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    private Process? _captureProcess;

    public void StartCapture(int width = 1280, int height = 720, int framerate = 30)
    {
        if (!_isRaspberryPi)
        {
            Console.WriteLine("Camera capture only available on Raspberry Pi");
            return;
        }

        if (IsCapturing)
            return;

        _captureCts = new CancellationTokenSource();
        Task.Run(() => CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();
        try
        {
            if (_captureProcess != null && !_captureProcess.HasExited)
            {
                _captureProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _captureProcess = null;
        _captureCts = null;
    }

    private string BuildRpicamArgs(int width, int height, int framerate)
    {
        var args = $"-t 0 --inline --width {width} --height {height} --framerate {framerate} --codec mjpeg -o -";

        if (_quality < 100)
            args += $" --quality {_quality}";

        if (Math.Abs(_sharpness - 1.0) > 0.01)
            args += $" --sharpness {_sharpness:F1}";

        if (_contrast != 1)
            args += $" --contrast {_contrast}";

        if (_brightness != 0)
            args += $" --brightness {_brightness}";

        if (_saturation != 1)
            args += $" --saturation {_saturation}";

        if (_exposureComp != 0)
            args += $" --ev {_exposureComp}";

        var wb = GetWbValue(_whiteBalance);
        if (wb != "auto")
            args += $" --awb {wb}";

        if (_videoFlipped)
            args += " --vflip";

        return args;
    }

    private async Task CaptureLoop(int width, int height, int framerate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "rpicam-vid",
                    Arguments = BuildRpicamArgs(width, height, framerate),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    await Task.Delay(2000, token);
                    continue;
                }

                _captureProcess = process;
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"rpicam-vid: {e.Data}");
                };
                process.BeginErrorReadLine();

                var reader = new BinaryReader(process.StandardOutput.BaseStream);

                try
                {
                    while (!token.IsCancellationRequested && !process.HasExited)
                    {
                        var frame = ReadOneFrame(reader);
                        if (frame != null)
                        {
                            lock (_lock)
                            {
                                _latestFrame = frame;
                            }
                        }
                    }
                }
                finally
                {
                    reader.Dispose();
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Camera error: {ex.Message}");
                await Task.Delay(2000, token);
            }
        }
    }

    private static byte[]? ReadOneFrame(BinaryReader reader)
    {
        try
        {
            byte[] buffer = new byte[1024 * 1024];
            int idx = 0;

            while (true)
            {
                int b = reader.ReadByte();
                if (b == -1) return null;
                if (b == 0xFF)
                {
                    b = reader.ReadByte();
                    if (b == -1) return null;
                    if (b == 0xD8)
                    {
                        buffer[idx++] = 0xFF;
                        buffer[idx++] = 0xD8;
                        break;
                    }
                }
            }

            bool foundEoi = false;
            while (!foundEoi && idx < buffer.Length)
            {
                int b = reader.ReadByte();
                if (b == -1) return null;
                buffer[idx++] = (byte)b;
                if (b == 0xFF)
                {
                    b = reader.ReadByte();
                    if (b == -1) return null;
                    buffer[idx++] = (byte)b;
                    if (b == 0xD9)
                        foundEoi = true;
                }
            }

            if (foundEoi)
            {
                var frame = new byte[idx];
                Array.Copy(buffer, frame, idx);
                return frame;
            }

            return null;
        }
        catch
        {
            return null;
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
        _restartCts?.Cancel();
        StopCapture();
    }
}

