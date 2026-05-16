using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private const string ZmqAddress = "tcp://127.0.0.1:5555";
    private const string ZmqsendPath = "/usr/local/bin/zmqsend";
    private static readonly string FfmpegPath = ResolveFfmpegPath();
    private const string FfmpegPathFile = "/etc/pi-stream-cam-ffmpeg-path";

    private readonly bool _hasCamera;
    private readonly bool _hasZmq;
    private byte[]? _h264Headers;
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
    private double _contrast = 1.0;
    private double _saturation = 1.0;
    private int _quality = 40;
    private bool _videoFlipped;
    private int _captureWidth = 1920;
    private int _captureHeight = 1080;
    private int _captureFramerate = 30;
    private bool _initialFocus = true;
    private bool _focusCalibrated;

    private static readonly string[] AwbPresets = { "auto", "incandescent", "tungsten", "fluorescent", "daylight", "cloudy", "shade", "custom" };

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
    private Process? _ffmpegProcess;
    private readonly object _ffmpegLock = new();

    public string StreamUrl => $"rtsp://picam1:8554/cam";

    // Latency tracking
    private readonly Stopwatch _pipelineTimer = Stopwatch.StartNew();
    private long _pipeChunksRead;
    private long _pipeTotalBytes;
    private double _pipeLastReadTimestampMs;
    private double _pipeInterarrivalSumMs;
    private long _pipeInterarrivalSamples;
    private long _pipeStartTimestamp;

    public CameraService()
    {
        _hasCamera =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            File.Exists("/usr/bin/rpicam-vid");

        _hasZmq = CheckZmqSupport();

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

    private static bool CheckZmqSupport()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = "-filters",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Contains("zmq");
        }
        catch
        {
            return false;
        }
    }

    public object GetStats()
    {
        var elapsed = _pipeStartTimestamp > 0
            ? _pipelineTimer.ElapsedMilliseconds - _pipeStartTimestamp : 0L;
        var avgInterarrival = _pipeInterarrivalSamples > 0
            ? _pipeInterarrivalSumMs / _pipeInterarrivalSamples : 0.0;
        var dataRate = elapsed > 0
            ? _pipeTotalBytes / (elapsed / 1000.0) : 0.0;

        return new
        {
            capturing = IsCapturing,
            hasCamera = _hasCamera,
            width = _captureWidth,
            height = _captureHeight,
            framerate = _captureFramerate,
            streamUrl = StreamUrl,
            focusCalibrated = _focusCalibrated,
            latency = new
            {
                pipelineElapsedMs = elapsed,
                chunksRead = _pipeChunksRead,
                totalDataKb = _pipeTotalBytes / 1024.0,
                dataRateKbps = dataRate / 1024.0,
                avgInterarrivalMs = Math.Round(avgInterarrival, 1),
                estimatedFps = avgInterarrival > 0
                    ? Math.Round(1000.0 / avgInterarrival, 1) : 0.0,
                estimatedEndToEndMs = EstimateEndToEndLatency()
            }
        };
    }

    /// Estimates total camera-to-RTSP latency based on pipeline configuration
    private int EstimateEndToEndLatency()
    {
        // rpicam-vid: sensor capture (~1 frame) + H.264 encode (~1-2 frames)
        var captureMs = 1000 / _captureFramerate * 2;

        // Pipe relay: negligible (microseconds), skip

        // ffmpeg: h264 decode + v4l2m2m hw encode + RTSP mux (~3-6 frames)
        var ffmpegMs = 1000 / _captureFramerate * 4;

        // MediaMTX relay: ~1 frame buffer
        var serverMs = 1000 / _captureFramerate;

        return captureMs + ffmpegMs + serverMs;
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
                _zoom = Math.Clamp(z.GetInt32(), 1, 8);

            if (doc.RootElement.TryGetProperty("focus", out var f))
                _focus = Math.Clamp(f.GetInt32(), 0, 100);

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
                _exposureComp = Math.Clamp(ec.GetInt32(), -8, 8);

            if (doc.RootElement.TryGetProperty("whitebalance", out var wb))
                _whiteBalance = Math.Clamp(wb.GetInt32(), 0, 8);

            if (doc.RootElement.TryGetProperty("sharpness", out var sh))
                _sharpness = sh.GetDouble();

            if (doc.RootElement.TryGetProperty("brightness", out var br))
                _brightness = Math.Clamp(br.GetInt32(), -1, 1);

            if (doc.RootElement.TryGetProperty("contrast", out var co))
                _contrast = Math.Clamp(co.GetDouble(), 0.0, 2.0);

            if (doc.RootElement.TryGetProperty("saturation", out var sa))
                _saturation = Math.Clamp(sa.GetDouble(), 0.0, 2.0);

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
        _zoom = Math.Clamp(level, 1, 8);
        Console.WriteLine($"Zoom set: {_zoom}x");
        SaveState();

        if (_hasZmq)
        {
            var size = 1.0 / _zoom;
            var cropW = (int)(_captureWidth * size) & ~1;
            var cropH = (int)(_captureHeight * size) & ~1;
            var cropX = (int)((_captureWidth - cropW) / 2.0) & ~1;
            var cropY = (int)((_captureHeight - cropH) / 2.0) & ~1;

            SendZmqCommand($"crop w {cropW}");
            SendZmqCommand($"crop h {cropH}");
            SendZmqCommand($"crop x {cropX}");
            SendZmqCommand($"crop y {cropY}");
        }
        else
        {
            KillFfmpeg();
        }
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine($"Focus set: {_focus}");
        SaveState();
        RestartPipeline();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _autofocus = true;
        _afMode = mode;

        Console.WriteLine($"Autofocus enabled: {_afMode}");
        SaveState();
        RestartPipeline();
    }

    public void DisableAutofocus()
    {
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine("Autofocus disabled");
        SaveState();
        RestartPipeline();
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
            RestartPipeline();
        }
    }

    public void SetFocusRange(string range)
    {
        _focusRange = range;
        Console.WriteLine($"Focus range: {_focusRange}");
        SaveState();
        RestartPipeline();
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

    public void SetContrast(double value)
    {
        _contrast = Math.Clamp(value, 0.0, 2.0);
        Console.WriteLine($"Contrast: {_contrast:F1}");
        SaveState();
    }

    public void SetSaturation(double value)
    {
        _saturation = Math.Clamp(value, 0.0, 2.0);
        Console.WriteLine($"Saturation: {_saturation:F1}");
        SaveState();
    }

    public void SetVideoFlip(bool flipped)
    {
        if (_videoFlipped == flipped)
            return;

        _videoFlipped = flipped;

        Console.WriteLine($"Video flipped: {_videoFlipped}");
        SaveState();

        if (_hasZmq)
        {
            var val = flipped ? "1" : "0";
            SendZmqCommand($"vflip enable {val}");
        }
        else
        {
            KillFfmpeg();
        }
    }

    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 10, 100);

        Console.WriteLine($"JPEG Quality: {_quality}");

        SaveState();
    }

    private void KillFfmpeg()
    {
        lock (_ffmpegLock)
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(true); } catch (Exception ex) { Console.WriteLine($"Failed to kill ffmpeg: {ex.Message}"); }
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }
        }
    }

    private void RestartPipeline()
    {
        Console.WriteLine("[PIPELINE] Restarting rpicam-vid to apply new settings...");
        lock (_captureLock)
        {
            if (_captureProcess != null && !_captureProcess.HasExited)
            {
                try { _captureProcess.Kill(true); } catch (Exception ex) { Console.WriteLine($"Failed to kill rpicam-vid: {ex.Message}"); }
            }
        }
    }

    private void SendZmqCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ZmqsendPath,
                Arguments = $"\"{command}\" \"{ZmqAddress}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine($"[ZMQ] Failed to start zmqsend for: {command}");
                return;
            }

            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(true); } catch { }
                Console.WriteLine($"[ZMQ] zmqsend timed out for: {command}");
                return;
            }

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                Console.WriteLine($"[ZMQ] zmqsend error ({proc.ExitCode}): {err.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZMQ] Error sending command: {ex.Message}");
        }
    }

    public void StartCapture(
        int width = 1920,
        int height = 1080,
        int framerate = 30)
    {
        if (!_hasCamera)
        {
            Console.WriteLine("Camera capture requires rpicam-vid");
            return;
        }

        if (IsCapturing)
            return;

        _captureWidth = width;
        _captureHeight = height;
        _captureFramerate = framerate;
        _captureCts = new CancellationTokenSource();

        Task.Run(() =>
            CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();

        Process? rpicam;
        lock (_captureLock)
        {
            rpicam = _captureProcess;
            _captureProcess = null;
        }

        Process? ffmpeg;
        lock (_ffmpegLock)
        {
            ffmpeg = _ffmpegProcess;
            _ffmpegProcess = null;
        }

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
            "--profile", "main",
            "--output", "-",
            "--nopreview",
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--framerate", framerate.ToString(),
            "--timeout", "0",
            "--intra", "15",
            "--inline",
            "--bitrate", "20000000"
        };

        if (_initialFocus)
        {
            args.Add("--autofocus-mode");
            args.Add("auto");
        }
        else if (_autofocus)
        {
            args.Add("--autofocus-mode");
            args.Add(_afMode == "single" ? "auto" : _afMode);
        }
        else
        {
            args.Add("--autofocus-mode");
            args.Add("manual");
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
        var cropW = _zoom > 1 ? (int)(_captureWidth / _zoom) & ~1 : _captureWidth;
        var cropH = _zoom > 1 ? (int)(_captureHeight / _zoom) & ~1 : _captureHeight;
        var cropX = (int)((_captureWidth - cropW) / 2.0) & ~1;
        var cropY = (int)((_captureHeight - cropH) / 2.0) & ~1;

        var flipEnabled = _videoFlipped ? "1" : "0";

        string vf;
        if (_hasZmq)
        {
            vf =
                "zmq," +
                $"vflip=enable={flipEnabled}," +
                $"crop={cropW}:{cropH}:{cropX}:{cropY}," +
                $"scale={_captureWidth}:{_captureHeight}";
        }
        else
        {
            vf =
                $"vflip=enable={flipEnabled}," +
                $"crop={cropW}:{cropH}:{cropX}:{cropY}," +
                $"scale={_captureWidth}:{_captureHeight}";
        }

        var bitrate = _captureHeight >= 1080 ? 20000 : 2000;
        return $"-analyzeduration 1M -probesize 1M -fflags +genpts+discardcorrupt -f h264 -i pipe:0 -vf \"{vf}\" -c:v h264_v4l2m2m -b:v {bitrate}k -f rtsp -rtsp_transport tcp rtsp://localhost:8554/cam";
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

    private async Task CalibrateFocusAsync(
        int width,
        int height,
        int framerate,
        CancellationToken token)
    {
        Console.WriteLine("[FOCUS] Starting focus calibration...");

        try
        {
            var args = new List<string>
            {
                "--codec", "h264",
                "--profile", "main",
                "--output", "/dev/null",
                "--nopreview",
                "--width", width.ToString(),
                "--height", height.ToString(),
                "--framerate", framerate.ToString(),
                "--timeout", "3000",
                "--autofocus-mode", "auto",
            };

            if (_focusRange == "macro")
            {
                args.Add("--autofocus-range");
                args.Add("macro");
            }

            Console.WriteLine($"[FOCUS] rpicam-vid {string.Join(" ", args)}");

            var psi = new ProcessStartInfo
            {
                FileName = "rpicam-vid",
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["LIBCAMERA_LOG_LEVELS"] = "ERROR";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(5000);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine("[FOCUS] Failed to start calibration process");
                return;
            }

            // Read and discard stderr (may contain libcamera focus messages)
            _ = Task.Run(() =>
            {
                try
                {
                    while (!proc.StandardError.EndOfStream)
                    {
                        var line = proc.StandardError.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                            Console.WriteLine($"[FOCUS] {line}");
                    }
                }
                catch { }
            }, token);

            // Wait for the calibration to complete (rpicam-vid exits after timeout)
            await proc.WaitForExitAsync(cts.Token);

            if (proc.ExitCode == 0)
                Console.WriteLine("[FOCUS] Calibration completed successfully");
            else
                Console.WriteLine($"[FOCUS] Calibration exited with code {proc.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[FOCUS] Calibration timed out or cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FOCUS] Calibration error: {ex.Message}");
        }
    }

    private async Task CaptureLoop(
        int width,
        int height,
        int framerate,
        CancellationToken token)
    {
        TrimLogFile("/var/log/pi-stream-cam/output.log");
        TrimLogFile("/var/log/pi-stream-cam/error.log");

        // Run focus calibration once per service lifetime, before the main pipeline
        if (!_focusCalibrated)
        {
            await CalibrateFocusAsync(width, height, framerate, token);
            _focusCalibrated = true;
            _initialFocus = false;

            // If focus was at default (never explicitly set), force autofocus auto
            // so calibration result isn't immediately overridden by a stale manual position
            if (!_autofocus && _focus >= 45 && _focus <= 55)
            {
                Console.WriteLine("[FOCUS] Focus was at default position, switching to autofocus auto");
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
                    KillStaleProcesses("rpicam-vid");

                if (consecutiveFailures >= maxFailures)
                {
                    Console.WriteLine($"[PIPELINE] {maxFailures} consecutive failures, waiting 60s before retry");
                    await Task.Delay(60000, token);
                    consecutiveFailures = 5;
                    continue;
                }

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

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(30000);
                var rpicam = Process.Start(rpicamPsi);
                if (rpicam == null)
                {
                    consecutiveFailures++;
                    var delay = Math.Min(consecutiveFailures * 1000, 15000);
                    await Task.Delay(delay, token);
                    continue;
                }

                consecutiveFailures = 0;
                lock (_captureLock) { _captureProcess = rpicam; }

                rpicam.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"rpicam: {e.Data}");
                };
                rpicam.BeginErrorReadLine();

                await RunPipeLoop(rpicam, token);

                rpicam.Dispose();
                lock (_captureLock) { _captureProcess = null; }

                lock (_ffmpegLock)
                {
                    if (_ffmpegProcess != null)
                    {
                        try { _ffmpegProcess.Kill(true); } catch { }
                        _ffmpegProcess.Dispose();
                        _ffmpegProcess = null;
                    }
                }

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

    private async Task RunPipeLoop(Process rpicam, CancellationToken token)
    {
        var buffer = new byte[65536];
        var stream = rpicam.StandardOutput.BaseStream;

        // Start ffmpeg once and keep it running
        var ffmpeg = StartFfmpeg();
        if (ffmpeg == null) return;

        // Prepend saved H.264 headers for mid-stream decoder init
        if (_h264Headers != null)
        {
            await ffmpeg.StandardInput.BaseStream.WriteAsync(_h264Headers, token);
        }

        _pipeStartTimestamp = _pipelineTimer.ElapsedMilliseconds;
        _pipeLastReadTimestampMs = _pipeStartTimestamp;
        var firstRead = true;
        var logInterval = _captureFramerate * 5;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var readTimestamp = _pipelineTimer.ElapsedMilliseconds;
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (bytesRead == 0) break;

                _pipeChunksRead++;
                _pipeTotalBytes += bytesRead;

                var interarrival = readTimestamp - _pipeLastReadTimestampMs;
                _pipeInterarrivalSumMs += interarrival;
                _pipeInterarrivalSamples++;
                _pipeLastReadTimestampMs = readTimestamp;

                // Save first chunk (contains SPS/PPS headers for decoder recovery)
                if (firstRead)
                {
                    firstRead = false;
                    _h264Headers = new byte[bytesRead];
                    Array.Copy(buffer, _h264Headers, bytesRead);
                    Console.WriteLine($"[LATENCY] First data arrived at +{readTimestamp}ms ({bytesRead} bytes)");
                }

                await ffmpeg.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);

                if (_pipeChunksRead % logInterval == 0)
                {
                    var elapsed = _pipelineTimer.ElapsedMilliseconds - _pipeStartTimestamp;
                    var avgInterarrival = _pipeInterarrivalSamples > 0
                        ? _pipeInterarrivalSumMs / _pipeInterarrivalSamples : 0;
                    var avgChunkSize = _pipeTotalBytes / (double)_pipeChunksRead;
                    var dataRate = _pipeTotalBytes / (elapsed / 1000.0);

                    Console.WriteLine(
                        $"[LATENCY] elapsed={elapsed}ms | " +
                        $"chunks={_pipeChunksRead} | " +
                        $"totalBytes={_pipeTotalBytes / 1024.0:F1}KB | " +
                        $"avgChunk={avgChunkSize:F0}B | " +
                        $"avgInterarrival={avgInterarrival:F1}ms | " +
                        $"dataRate={dataRate / 1024.0:F1}KB/s | " +
                        $"estFps={(avgInterarrival > 0 ? 1000.0 / avgInterarrival : 0):F1}");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                Console.WriteLine("[PIPELINE] ffmpeg pipe broken, restarting...");
                ffmpeg!.Dispose();
                lock (_ffmpegLock) { _ffmpegProcess = null; }

                var newFfmpeg = StartFfmpeg();
                if (newFfmpeg == null)
                {
                    await Task.Delay(2000, token);
                    continue;
                }
                ffmpeg = newFfmpeg;

                if (_h264Headers != null)
                {
                    await ffmpeg.StandardInput.BaseStream.WriteAsync(_h264Headers, token);
                }
            }
        }
    }

    private Process? StartFfmpeg()
    {
        var ffArgs = BuildFfmpegArgs();
        Console.WriteLine($"[PIPELINE] ffmpeg {ffArgs}");

        var ffmpegPsi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = ffArgs,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var ffmpeg = Process.Start(ffmpegPsi);
        if (ffmpeg == null)
        {
            Console.WriteLine("[PIPELINE] Failed to start ffmpeg");
            return null;
        }

        ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine($"ffmpeg: {e.Data}");
        };
        ffmpeg.BeginErrorReadLine();
        lock (_ffmpegLock) { _ffmpegProcess = ffmpeg; }
        return ffmpeg;
    }

    public void Dispose()
    {
        StopCapture();
    }

}
