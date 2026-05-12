using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private readonly bool _hasCamera;
    private CancellationTokenSource? _captureCts;

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
    private int _quality = 40;
    private bool _videoFlipped;

    private static readonly string[] AwbPresets = { "auto", "incandescent", "tungsten", "fluorescent", "daylight", "cloudy", "shade", "custom" };

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

    private Process? _captureProcess;
    private Process? _ffmpegProcess;

    public string StreamUrl => $"rtsp://picam1:8554/cam";

    public CameraService()
    {
        _hasCamera =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            File.Exists("/usr/bin/rpicam-vid");

        LoadState();
    }

    public object GetStats() => new
    {
        capturing = IsCapturing,
        hasCamera = _hasCamera,
        width = 1280,
        height = 720,
        streamUrl = StreamUrl
    };

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;

            var json = File.ReadAllText(_stateFile);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("zoom", out var z))
                _zoom = Math.Clamp(z.GetInt32(), 1, 8);

            if (doc.RootElement.TryGetProperty("focus", out var f))
                _focus = Math.Clamp(f.GetInt32(), 0, 100);

            if (doc.RootElement.TryGetProperty("autofocus", out var af))
                _autofocus = af.GetBoolean();

            if (doc.RootElement.TryGetProperty("afmode", out var am))
                _afMode = am.GetString() ?? "continuous";

            if (doc.RootElement.TryGetProperty("focusrange", out var fr))
                _focusRange = fr.GetString() ?? "normal";

            if (doc.RootElement.TryGetProperty("exposurecomp", out var ec))
                _exposureComp = Math.Clamp(ec.GetInt32(), -8, 8);

            if (doc.RootElement.TryGetProperty("whitebalance", out var wb))
                _whiteBalance = Math.Clamp(wb.GetInt32(), 0, 8);

            if (doc.RootElement.TryGetProperty("sharpness", out var sh))
                _sharpness = sh.GetDouble();

            if (doc.RootElement.TryGetProperty("brightness", out var br))
                _brightness = Math.Clamp(br.GetInt32(), -1, 1);

            if (doc.RootElement.TryGetProperty("contrast", out var co))
                _contrast = Math.Clamp(co.GetInt32(), 0, 2);

            if (doc.RootElement.TryGetProperty("saturation", out var sa))
                _saturation = Math.Clamp(sa.GetInt32(), 0, 2);

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
        catch
        {
        }
    }

    public void SetZoom(int level)
    {
        _zoom = Math.Clamp(level, 1, 8);
        Console.WriteLine($"Zoom set: {_zoom}x");
        SaveState();
        KillFfmpeg();
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine($"Focus set: {_focus} (applies on next restart)");
        SaveState();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _autofocus = true;
        _afMode = mode;

        Console.WriteLine($"Autofocus enabled: {_afMode} (applies on next restart)");
        SaveState();
    }

    public void DisableAutofocus()
    {
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine("Autofocus disabled (applies on next restart)");
        SaveState();
    }

    public void SetAfMode(string mode)
    {
        if (mode == "manual" || mode == "auto" || mode == "continuous")
        {
            _autofocus = mode != "manual";
            _afMode = mode;
            Console.WriteLine($"AF mode: {_afMode} (applies on next restart)");
            SaveState();
        }
    }

    public void SetFocusRange(string range)
    {
        _focusRange = range;
        Console.WriteLine($"Focus range: {_focusRange} (applies on next restart)");
        SaveState();
    }

    public void SetExposureCompensation(int value)
    {
        _exposureComp = Math.Clamp(value, -8, 8);
        Console.WriteLine($"Exposure compensation: {_exposureComp}");
        SaveState();
    }

    public void SetWhiteBalance(int value)
    {
        _whiteBalance = Math.Clamp(value, 0, 8);
        Console.WriteLine($"White balance: {_whiteBalance}");
        SaveState();
    }

    public void SetSharpness(double value)
    {
        _sharpness = value;
        Console.WriteLine($"Sharpness: {_sharpness}");
        SaveState();
    }

    public void SetBrightness(int value)
    {
        _brightness = Math.Clamp(value, -1, 1);
        Console.WriteLine($"Brightness: {_brightness}");
        SaveState();
    }

    public void SetContrast(int value)
    {
        _contrast = Math.Clamp(value, 0, 2);
        Console.WriteLine($"Contrast: {_contrast}");
        SaveState();
    }

    public void SetSaturation(int value)
    {
        _saturation = Math.Clamp(value, 0, 2);
        Console.WriteLine($"Saturation: {_saturation}");
        SaveState();
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
        var old = Interlocked.Exchange(ref _ffmpegProcess, null);
        if (old != null && !old.HasExited)
        {
            try { old.Kill(true); } catch { }
        }
        old?.Dispose();
    }

    public void StartCapture(
        int width = 1280,
        int height = 720,
        int framerate = 30)
    {
        if (!_hasCamera)
        {
            Console.WriteLine("Camera capture requires rpicam-vid");
            return;
        }

        if (IsCapturing)
            return;

        _captureCts = new CancellationTokenSource();

        Task.Run(() =>
            CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();

        var rpicam = Interlocked.Exchange(ref _captureProcess, null);
        var ffmpeg = Interlocked.Exchange(ref _ffmpegProcess, null);

        foreach (var proc in new[] { rpicam, ffmpeg })
        {
            try
            {
                if (proc != null && !proc.HasExited)
                    proc.Kill(true);
            }
            catch { }
            proc?.Dispose();
        }

        _captureCts = null;
    }

    private string[] BuildRpicamVidArgs(
        int width,
        int height,
        int framerate)
    {
        var args = new List<string>
        {
            "--codec", "h264",
            "--output", "-",
            "--nopreview",
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--framerate", framerate.ToString(),
            "--timeout", "0",
            "--intra", "30",
            "--bitrate", "2000000"
        };

        args.Add("--autofocus-mode");
        args.Add(_afMode);

        if (_afMode == "manual")
        {
            args.Add("--lens-position");
            args.Add((_focus / 100.0).ToString("F2", CultureInfo.InvariantCulture));
        }

        if (_focusRange == "macro")
        {
            args.Add("--autofocus-range");
            args.Add("macro");
        }

        if (_exposureComp != 0)
        {
            args.Add("--ev");
            args.Add(_exposureComp.ToString(CultureInfo.InvariantCulture));
        }

        if (_whiteBalance >= 0 && _whiteBalance < AwbPresets.Length)
        {
            args.Add("--awb");
            args.Add(AwbPresets[_whiteBalance]);
        }

        if (Math.Abs(_sharpness - 1.0) > 0.01)
        {
            args.Add("--sharpness");
            args.Add(_sharpness.ToString("F2", CultureInfo.InvariantCulture));
        }

        if (_brightness != 0)
        {
            args.Add("--brightness");
            args.Add(_brightness.ToString(CultureInfo.InvariantCulture));
        }

        if (_contrast != 1)
        {
            args.Add("--contrast");
            args.Add(_contrast.ToString(CultureInfo.InvariantCulture));
        }

        if (_saturation != 1)
        {
            args.Add("--saturation");
            args.Add(_saturation.ToString(CultureInfo.InvariantCulture));
        }

        return args.ToArray();
    }

    private string BuildFfmpegArgs()
    {
        var filters = new List<string>();

        if (_zoom > 1)
        {
            var size = 1.0 / _zoom;
            var cropW = (int)(1280 * size) & ~1;
            var cropH = (int)(720 * size) & ~1;
            var cropX = (int)((1280 - cropW) / 2.0) & ~1;
            var cropY = (int)((720 - cropH) / 2.0) & ~1;
            filters.Add($"crop={cropW}:{cropH}:{cropX}:{cropY},scale=1280:720");
        }

        if (_videoFlipped)
            filters.Add("hflip,vflip");

        if (filters.Count > 0)
        {
            var vf = string.Join(",", filters);
            return $"-i pipe: -vf {vf} -c:v h264_v4l2m2m -b:v 2000k -f rtsp -rtsp_transport tcp rtsp://localhost:8554/cam";
        }

        return "-i pipe: -c copy -f rtsp -rtsp_transport tcp rtsp://localhost:8554/cam";
    }

    private async Task CaptureLoop(
        int width,
        int height,
        int framerate,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var args = BuildRpicamVidArgs(width, height, framerate);
                Console.WriteLine($"[PIPELINE] rpicam-vid {string.Join(" ", args)}");

                var rpicamPsi = new ProcessStartInfo
                {
                    FileName = "rpicam-vid",
                    Arguments = string.Join(" ", args),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                rpicamPsi.Environment["LIBCAMERA_LOG_LEVELS"] = "ERROR";

                var rpicam = Process.Start(rpicamPsi);
                if (rpicam == null)
                {
                    await Task.Delay(2000, token);
                    continue;
                }

                _captureProcess = rpicam;

                rpicam.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"rpicam: {e.Data}");
                };
                rpicam.BeginErrorReadLine();

                await RunPipeLoop(rpicam, token);

                rpicam.Dispose();
                _captureProcess = null;

                var ffmpeg = Interlocked.Exchange(ref _ffmpegProcess, null);
                if (ffmpeg != null && !ffmpeg.HasExited) { try { ffmpeg.Kill(true); } catch { } }
                ffmpeg?.Dispose();

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
                await Task.Delay(2000, token);
            }
        }
    }

    private async Task RunPipeLoop(Process rpicam, CancellationToken token)
    {
        var buffer = new byte[65536];
        var stream = rpicam.StandardOutput.BaseStream;

        while (!token.IsCancellationRequested)
        {
            var ffmpeg = _ffmpegProcess;
            if (ffmpeg == null || ffmpeg.HasExited)
            {
                if (ffmpeg != null) { try { ffmpeg.Kill(true); } catch { } ffmpeg.Dispose(); }

                var ffArgs = BuildFfmpegArgs();
                Console.WriteLine($"[PIPELINE] ffmpeg {ffArgs}");

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                ffmpeg = Process.Start(ffmpegPsi);
                if (ffmpeg == null) { await Task.Delay(1000, token); continue; }

                ffmpeg.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"ffmpeg: {e.Data}");
                };
                ffmpeg.BeginErrorReadLine();
                _ffmpegProcess = ffmpeg;
            }

            try
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (bytesRead == 0) break;

                await ffmpeg.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                _ffmpegProcess = null;
                ffmpeg?.Dispose();
                continue;
            }
        }
    }

    public void Dispose()
    {
        StopCapture();
    }

}
