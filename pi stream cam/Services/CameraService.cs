using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private const string V4l2CtlPath = "/usr/bin/v4l2-ctl";
    private static readonly string FfmpegPath = ResolveFfmpegPath();
    private const string FfmpegPathFile = "/etc/pi-stream-cam-ffmpeg-path";

    private readonly string _devicePath;
    private readonly bool _hasCamera;
    private CancellationTokenSource? _captureCts;

    private int _zoom = 1;
    private int _focus = 51;
    private bool _autofocus = true;
    private string _afMode = "continuous";
    private string _focusRange = "full";
    private int _exposureComp = 0;
    private int _whiteBalance = 0;
    private int _sharpness = 22;
    private int _brightness = 128;
    private int _contrast = 32;
    private int _saturation = 32;
    private int _quality = 40;
    private bool _videoFlipped;
    private int _captureWidth = 1920;
    private int _captureHeight = 1080;
    private int _captureFramerate = 30;
    private bool _focusCalibrated;

    private static readonly int[] WbTemperatures = { 0, 2800, 3200, 4000, 5000, 5600, 6500 };
    private static readonly string[] WbNames = { "Auto", "Incandescent", "Warm Fluor.", "Cool Fluor.", "Daylight", "Cloudy", "Shade" };
    private static readonly int[] ExposureAbsoluteValues = { 0, 3, 50, 100, 200, 400, 800, 1200, 2047 };

    private readonly string _stateFile = "/var/lib/pi-stream-cam/camera-state.json";

    public bool HasCamera => _hasCamera;
    public int Zoom => _zoom;
    public int Focus => _focus;
    public bool AutofocusEnabled => _autofocus;
    public string AfMode => _afMode;
    public string FocusRange => _focusRange;
    public int ExposureCompensation => _exposureComp;
    public int WhiteBalance => _whiteBalance;
    public double Sharpness => _sharpness;
    public int Brightness => _brightness;
    public double Contrast => _contrast;
    public double Saturation => _saturation;
    public int Quality => _quality;
    public bool IsCapturing => _captureCts != null && !_captureCts.IsCancellationRequested;
    public bool VideoFlipped => _videoFlipped;

    private Process? _captureProcess;
    private readonly object _captureLock = new();

    public string StreamUrl => $"rtsp://picam1:8554/cam";

    public CameraService(string devicePath = "/dev/video0")
    {
        _devicePath = devicePath;
        _hasCamera =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            File.Exists(_devicePath);

        LoadState();
    }

    private static string ResolveFfmpegPath()
    {
        try
        {
            if (File.Exists(FfmpegPathFile))
            {
                var path = File.ReadAllText(FfmpegPathFile).Trim();
                if (File.Exists(path))
                    return path;
            }
        }
        catch { }
        return "ffmpeg";
    }

    public object GetStats()
    {
        return new
        {
            capturing = IsCapturing,
            hasCamera = _hasCamera,
            width = _captureWidth,
            height = _captureHeight,
            framerate = _captureFramerate,
            streamUrl = StreamUrl,
            focusCalibrated = _focusCalibrated,
            device = _devicePath
        };
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;

            var json = File.ReadAllText(_stateFile);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("zoom", out var z))
                _zoom = Math.Clamp(z.GetInt32(), 1, 5);

            if (doc.RootElement.TryGetProperty("focus", out var f))
                _focus = Math.Clamp(f.GetInt32(), 0, 255);

            if (doc.RootElement.TryGetProperty("autofocus", out var af))
                _autofocus = af.GetBoolean();

            if (doc.RootElement.TryGetProperty("afmode", out var am))
            {
                _afMode = am.GetString() ?? "continuous";
                if (_afMode == "single") _afMode = "auto";
            }

            if (doc.RootElement.TryGetProperty("focusrange", out var fr))
                _focusRange = fr.GetString() ?? "normal";

            if (doc.RootElement.TryGetProperty("exposurecomp", out var ec))
                _exposureComp = Math.Clamp(ec.GetInt32(), 0, 8);

            if (doc.RootElement.TryGetProperty("whitebalance", out var wb))
                _whiteBalance = Math.Clamp(wb.GetInt32(), 0, 6);

            if (doc.RootElement.TryGetProperty("sharpness", out var sh))
                _sharpness = Math.Clamp(sh.GetInt32(), 0, 255);

            if (doc.RootElement.TryGetProperty("brightness", out var br))
                _brightness = Math.Clamp(br.GetInt32(), 0, 255);

            if (doc.RootElement.TryGetProperty("contrast", out var co))
                _contrast = Math.Clamp(co.GetInt32(), 0, 255);

            if (doc.RootElement.TryGetProperty("saturation", out var sa))
                _saturation = Math.Clamp(sa.GetInt32(), 0, 255);

            if (doc.RootElement.TryGetProperty("videoflipped", out var vf))
                _videoFlipped = vf.GetBoolean();

            if (doc.RootElement.TryGetProperty("quality", out var q))
                _quality = Math.Clamp(q.GetInt32(), 10, 100);

            Console.WriteLine("[Camera] State restored");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load state error: {ex.Message}");
        }
    }

    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile)!;

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
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
                quality = _quality,
                videoflipped = _videoFlipped
            });

            File.WriteAllText(_stateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Save state error: {ex.Message}");
        }
    }

    public void SetZoom(int level)
    {
        _zoom = Math.Clamp(level, 1, 5);
        Console.WriteLine($"Zoom set: {_zoom}x");
        SaveState();
        RunV4l2Ctl($"zoom_absolute={_zoom}");
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 255);
        _autofocus = false;
        _afMode = "manual";
        Console.WriteLine($"Focus set: {_focus}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _autofocus = true;
        _afMode = mode;
        Console.WriteLine($"Autofocus enabled: {_afMode}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void DisableAutofocus()
    {
        _autofocus = false;
        _afMode = "manual";
        Console.WriteLine("Autofocus disabled");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetAfMode(string mode)
    {
        if (mode == "single") mode = "auto";
        if (mode == "manual" || mode == "auto" || mode == "continuous")
        {
            _autofocus = mode != "manual";
            _afMode = mode;
            Console.WriteLine($"AF mode: {_afMode}");
            SaveState();
            ApplyV4l2Controls();
        }
    }

    public void SetFocusRange(string range)
    {
        _focusRange = range;
        Console.WriteLine($"Focus range: {_focusRange}");
        SaveState();
    }

    public void SetExposureCompensation(int value)
    {
        _exposureComp = Math.Clamp(value, 0, 8);
        Console.WriteLine($"Exposure: level={_exposureComp}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetWhiteBalance(int value)
    {
        _whiteBalance = Math.Clamp(value, 0, 6);
        Console.WriteLine($"White balance: {_whiteBalance}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetSharpness(int value)
    {
        _sharpness = Math.Clamp(value, 0, 255);
        Console.WriteLine($"Sharpness: {_sharpness}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetBrightness(int value)
    {
        _brightness = Math.Clamp(value, 0, 255);
        Console.WriteLine($"Brightness: {_brightness}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetContrast(int value)
    {
        _contrast = Math.Clamp(value, 0, 255);
        Console.WriteLine($"Contrast: {_contrast}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetSaturation(int value)
    {
        _saturation = Math.Clamp(value, 0, 255);
        Console.WriteLine($"Saturation: {_saturation}");
        SaveState();
        ApplyV4l2Controls();
    }

    public void SetVideoFlip(bool flipped)
    {
        if (_videoFlipped == flipped)
            return;

        _videoFlipped = flipped;
        Console.WriteLine($"Video flipped: {_videoFlipped}");
        SaveState();
        KillFfmpeg();
    }

    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 10, 100);
        Console.WriteLine($"JPEG Quality: {_quality}");
        SaveState();
    }

    private void KillFfmpeg()
    {
        lock (_captureLock)
        {
            if (_captureProcess != null && !_captureProcess.HasExited)
            {
                try { _captureProcess.Kill(true); } catch (Exception ex) { Console.WriteLine($"Failed to kill ffmpeg: {ex.Message}"); }
                _captureProcess.Dispose();
                _captureProcess = null;
            }
        }
    }

    private void ApplyV4l2Controls()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !File.Exists(V4l2CtlPath))
            return;

        if (_autofocus)
            RunV4l2Ctl("focus_automatic_continuous=1");
        else
        {
            RunV4l2Ctl("focus_automatic_continuous=0");
            RunV4l2Ctl($"focus_absolute={Math.Clamp(_focus, 0, 255)}");
        }

        RunV4l2Ctl($"brightness={Math.Clamp(_brightness, 0, 255)}");
        RunV4l2Ctl($"contrast={Math.Clamp(_contrast, 0, 255)}");
        RunV4l2Ctl($"saturation={Math.Clamp(_saturation, 0, 255)}");
        RunV4l2Ctl($"sharpness={Math.Clamp(_sharpness, 0, 255)}");
        RunV4l2Ctl($"zoom_absolute={Math.Clamp(_zoom, 1, 5)}");

        if (_whiteBalance == 0)
            RunV4l2Ctl("white_balance_automatic=1");
        else
        {
            RunV4l2Ctl("white_balance_automatic=0");
            var wbIdx = Math.Clamp(_whiteBalance, 0, 6);
            if (wbIdx > 0 && wbIdx < WbTemperatures.Length)
                RunV4l2Ctl($"white_balance_temperature={WbTemperatures[wbIdx]}");
        }

        if (_exposureComp == 0)
            RunV4l2Ctl("auto_exposure=3");
        else
        {
            RunV4l2Ctl("auto_exposure=1");
            var expIdx = Math.Clamp(_exposureComp, 0, 8);
            if (expIdx > 0 && expIdx < ExposureAbsoluteValues.Length)
                RunV4l2Ctl($"exposure_time_absolute={ExposureAbsoluteValues[expIdx]}");
        }
    }

    private void RunV4l2Ctl(string controlArg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = V4l2CtlPath,
                Arguments = $"-d {_devicePath} --set-ctrl={controlArg}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[V4L2] Failed to set {controlArg}: {ex.Message}");
        }
    }

    private void KillStaleProcesses(string name)
    {
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName(name))
            {
                if (proc.Id != Environment.ProcessId)
                {
                    try { proc.Kill(true); proc.WaitForExit(3000); } catch (Exception ex) { Console.WriteLine($"Failed to kill stale {name} (PID {proc.Id}): {ex.Message}"); }
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate processes '{name}': {ex.Message}");
        }
    }

    private static void TrimLogFile(string path, int maxBytes = 5 * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= maxBytes) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var keep = (int)(fi.Length - maxBytes / 2);
            fs.Seek(keep, SeekOrigin.Begin);
            var buf = new byte[fs.Length - keep];
            fs.Read(buf, 0, buf.Length);
            var firstNewline = Array.IndexOf(buf, (byte)'\n');
            if (firstNewline > 0 && firstNewline < buf.Length - 1)
            {
                fs.SetLength(0);
                fs.Write(buf, firstNewline + 1, buf.Length - firstNewline - 1);
            }
        }
        catch { }
    }

    public void StartCapture(
        int width = 1920,
        int height = 1080,
        int framerate = 30)
    {
        if (!_hasCamera)
        {
            Console.WriteLine($"Camera capture requires V4L2 device at {_devicePath}");
            return;
        }

        if (IsCapturing)
            return;

        _captureWidth = width;
        _captureHeight = height;
        _captureFramerate = framerate;
        _captureCts = new CancellationTokenSource();
        ApplyV4l2Controls();

        Task.Run(() =>
            CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();
        KillFfmpeg();
        _captureCts = null;
    }

    private async Task CaptureLoop(
        int width,
        int height,
        int framerate,
        CancellationToken token)
    {
        TrimLogFile("/var/log/pi-stream-cam/output.log");
        TrimLogFile("/var/log/pi-stream-cam/error.log");

        if (!_focusCalibrated)
        {
            _focusCalibrated = true;

            if (!_autofocus && _focus >= 45 && _focus <= 55)
            {
                Console.WriteLine("[FOCUS] Focus was at default, switching to autofocus auto");
                _autofocus = true;
                _afMode = "auto";
                SaveState();
            }
        }

        var consecutiveFailures = 0;
        const int maxFailures = 20;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (consecutiveFailures == 5)
                    KillStaleProcesses("ffmpeg");

                if (consecutiveFailures >= maxFailures)
                {
                    Console.WriteLine($"[PIPELINE] {maxFailures} consecutive failures, waiting 60s before retry");
                    await Task.Delay(60000, token);
                    consecutiveFailures = 5;
                    continue;
                }

                var args = BuildFfmpegCaptureArgs(width, height, framerate);
                Console.WriteLine($"[PIPELINE] ffmpeg {args}");

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(30000);
                var ffmpeg = Process.Start(ffmpegPsi);
                if (ffmpeg == null)
                {
                    consecutiveFailures++;
                    var delay = Math.Min(consecutiveFailures * 1000, 15000);
                    await Task.Delay(delay, token);
                    continue;
                }

                consecutiveFailures = 0;
                lock (_captureLock) { _captureProcess = ffmpeg; }

                ffmpeg.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"ffmpeg: {e.Data}");
                };
                ffmpeg.BeginErrorReadLine();

                await ffmpeg.WaitForExitAsync(token);

                lock (_captureLock)
                {
                    if (_captureProcess == ffmpeg)
                        _captureProcess = null;
                }
                ffmpeg.Dispose();

                if (!token.IsCancellationRequested)
                    await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pipeline error: {ex.Message}");
                consecutiveFailures++;
                var delay = Math.Min(consecutiveFailures * 1000, 15000);
                await Task.Delay(delay, token);
            }
        }
    }

    private string BuildFfmpegCaptureArgs(int width, int height, int framerate)
    {
        var vf = _videoFlipped ? "vflip,format=yuv420p" : "format=yuv420p";

        var bitrate = height >= 1080 ? 12000 : 2000;
        return $"-f v4l2 -input_format mjpeg -video_size {width}x{height} -framerate {framerate} " +
               $"-i {_devicePath} " +
               $"-vf \"{vf}\" " +
               $"-c:v h264_v4l2m2m -b:v {bitrate}k -flags low_delay -tune zerolatency " +
               $"-f rtsp -rtsp_transport tcp rtsp://localhost:8554/cam";
    }

    public void Dispose()
    {
        StopCapture();
    }
}
