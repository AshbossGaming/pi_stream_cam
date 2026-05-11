using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private readonly bool _hasCamera;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private byte[]? _latestFrame;
    private CancellationTokenSource? _captureCts;
    private CancellationTokenSource? _restartCts;

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
    public SemaphoreSlim FrameSignal => _frameSignal;

    private Process? _captureProcess;
    private byte[]? _frameTail;
    private int _frameTailLen;

    private long _frameCount;
    private long _droppedFrames;
    private DateTime _captureStartTime;
    private int _lastFpsSample;
    private int _fps;
    private DateTime _lastFpsTime;

    public long FrameCount => Interlocked.Read(ref _frameCount);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public int CurrentFps => _fps;
    public TimeSpan Uptime => IsCapturing ? DateTime.UtcNow - _captureStartTime : TimeSpan.Zero;

    public CameraService()
    {
        _hasCamera =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            File.Exists("/usr/bin/rpicam-vid");

        LoadState();
    }

    public object GetStats()
    {
        lock (_lock)
        {
            return new
            {
                capturing = IsCapturing,
                hasCamera = _hasCamera,
                frameCount = FrameCount,
                droppedFrames = DroppedFrames,
                fps = CurrentFps,
                uptimeSeconds = Uptime.TotalSeconds,
                width = _captureProcess != null ? 1280 : 0,
                height = _captureProcess != null ? 720 : 0
            };
        }
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
        Console.WriteLine($"Zoom set: {_zoom}x");
        SaveState();
        RestartCapture();
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine($"Focus set: {_focus}");
        SaveState();
        RestartCapture();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _autofocus = true;
        _afMode = mode;

        Console.WriteLine($"Autofocus enabled: {_afMode}");
        SaveState();
        RestartCapture();
    }

    public void DisableAutofocus()
    {
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine("Autofocus disabled");
        SaveState();
        RestartCapture();
    }

    public void SetAfMode(string mode)
    {
        if (mode == "manual" || mode == "auto" || mode == "continuous")
        {
            _autofocus = mode != "manual";
            _afMode = mode;
            Console.WriteLine($"AF mode: {_afMode}");
            SaveState();
            RestartCapture();
        }
    }

    public void SetFocusRange(string range)
    {
        _focusRange = range;
        Console.WriteLine($"Focus range: {_focusRange}");
        SaveState();
        RestartCapture();
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
        RestartCapture();
    }

    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 10, 100);

        Console.WriteLine($"JPEG Quality: {_quality}");

        SaveState();
    }

    public void SignalStale()
    {
        Console.WriteLine("[Camera] Stale frames detected. Restarting...");
        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        var token = _restartCts.Token;
        Task.Run(async () =>
        {
            await Task.Delay(100, token);
            if (!token.IsCancellationRequested)
            {
                try
                {
                    if (_captureProcess != null && !_captureProcess.HasExited)
                        _captureProcess.Kill(true);
                }
                catch { }
            }
        });
    }

    private void RestartCapture()
    {
        if (!IsCapturing)
            return;

        _restartCts?.Cancel();

        _restartCts = new CancellationTokenSource();

        var token = _restartCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_captureProcess != null &&
                            !_captureProcess.HasExited)
                        {
                            Console.WriteLine("[Camera] Restarting capture...");
                            _captureProcess.Kill(true);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        });
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
        _frameCount = 0;
        _droppedFrames = 0;
        _captureStartTime = DateTime.UtcNow;
        _lastFpsTime = DateTime.UtcNow;
        _lastFpsSample = 0;

        Task.Run(() =>
            CaptureLoop(width, height, framerate, _captureCts.Token));
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();

        try
        {
            if (_captureProcess != null &&
                !_captureProcess.HasExited)
            {
                _captureProcess.Kill(true);
            }
        }
        catch
        {
        }

        _captureProcess = null;
        _captureCts = null;
    }

    private string[] BuildRpicamVidArgs(
        int width,
        int height,
        int framerate)
    {
        var args = new List<string>
        {
            "--codec", "mjpeg",
            "--output", "-",
            "--inline",
            "--nopreview",
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--framerate", framerate.ToString(),
            "--quality", _quality.ToString(),
            "--timeout", "0",
            "--buffer-count", "1"
        };

        if (_zoom > 1)
        {
            double size = 1.0 / _zoom;
            double offset = (1.0 - size) / 2.0;
            args.Add("--roi");
            args.Add($"{offset:F4},{offset:F4},{size:F4},{size:F4}");
        }

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

        if (_videoFlipped)
            args.Add("--vflip");

        return args.ToArray();
    }

    private async Task CaptureLoop(
        int width,
        int height,
        int framerate,
        CancellationToken token)
    {
        var staleWatch = Stopwatch.StartNew();
        var statsLogTimer = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var args = BuildRpicamVidArgs(
                    width,
                    height,
                    framerate);

                Console.WriteLine($"[RPICAM] rpicam-vid {string.Join(" ", args)}");

                var psi = new ProcessStartInfo
                {
                    FileName = "rpicam-vid",
                    Arguments = string.Join(" ", args),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = null,
                    CreateNoWindow = true
                };

                psi.Environment["LIBCAMERA_LOG_LEVELS"] = "ERROR";

                var process = Process.Start(psi);

                if (process == null)
                {
                    staleWatch.Restart();
                    await Task.Delay(2000, token);
                    continue;
                }

                _captureProcess = process;
                staleWatch.Restart();

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Console.WriteLine($"rpicam: {e.Data}");
                    }
                };

                process.BeginErrorReadLine();

                var reader =
                    new BinaryReader(
                        process.StandardOutput.BaseStream);

                try
                {
                    while (!token.IsCancellationRequested &&
                           !process.HasExited)
                    {
                        if (staleWatch.ElapsedMilliseconds > 5000)
                        {
                            Console.WriteLine("[Camera] No frames for 5s, restarting...");
                            break;
                        }

                        if (!process.StandardOutput.BaseStream.CanRead)
                            break;

                        var frame = ReadOneFrame(reader);

                        if (frame != null)
                        {
                            staleWatch.Restart();

                            lock (_lock)
                            {
                                _latestFrame = frame;
                            }

                            try { _frameSignal.Release(); } catch (SemaphoreFullException) { }

                            Interlocked.Increment(ref _frameCount);

                            var now = DateTime.UtcNow;
                            if ((now - _lastFpsTime).TotalSeconds >= 1)
                            {
                                var current = Interlocked.Read(ref _frameCount);
                                _fps = (int)(current - _lastFpsSample);
                                _lastFpsSample = (int)current;
                                _lastFpsTime = now;
                                staleWatch.Restart();
                            }

                            if (statsLogTimer.Elapsed.TotalSeconds >= 60)
                            {
                                Console.WriteLine($"[Stats] FPS={_fps}, Total={FrameCount}, Dropped={DroppedFrames}, Uptime={Uptime:hh\\:mm\\:ss}");
                                statsLogTimer.Restart();
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref _droppedFrames);
                        }
                    }
                }
                finally
                {
                    reader.Dispose();

                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                    }
                    catch
                    {
                    }

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

                staleWatch.Restart();
                await Task.Delay(2000, token);
            }
        }
    }

    private byte[]? ReadOneFrame(BinaryReader reader)
    {
        try
        {
            var stream = reader.BaseStream;
            byte[] buf = new byte[65536];
            int prev = 0;
            int bufLen = 0;

            // Prepend any tail bytes saved from the previous call
            if (_frameTail != null && _frameTailLen > 0)
            {
                Array.Copy(_frameTail, 0, buf, 0, _frameTailLen);
                bufLen = _frameTailLen;
                _frameTail = null;
                _frameTailLen = 0;
            }

            // Fill the rest of the buffer from the pipe
            int read = stream.Read(buf, bufLen, buf.Length - bufLen);
            if (read == 0) return null;
            bufLen += read;

            // Scan for SOI marker (0xFF 0xD8)
            for (int i = 0; i < bufLen; i++)
            {
                if (prev == 0xFF && buf[i] == 0xD8)
                {
                    using var ms = new MemoryStream(262144);
                    ms.WriteByte(0xFF);
                    ms.WriteByte(0xD8);

                    int afterSoi = bufLen - i - 1;
                    if (afterSoi > 0)
                        ms.Write(buf, i + 1, afterSoi);

                    // Continue reading and scanning for EOI (0xFF 0xD9)
                    prev = 0;
                    while (ms.Length < 1048576)
                    {
                        int br = stream.Read(buf, 0, buf.Length);
                        if (br == 0) return null;

                        int eoiPos = -1;
                        for (int j = 0; j < br; j++)
                        {
                            if (prev == 0xFF && buf[j] == 0xD9)
                            {
                                eoiPos = j + 1;
                                break;
                            }
                            prev = buf[j];
                        }

                        if (eoiPos >= 0)
                        {
                            ms.Write(buf, 0, eoiPos);

                            // Save any bytes after EOI for the next frame
                            int tail = br - eoiPos;
                            if (tail > 0)
                            {
                                _frameTail = new byte[buf.Length];
                                Array.Copy(buf, eoiPos, _frameTail, 0, tail);
                                _frameTailLen = tail;
                            }

                            return ms.ToArray();
                        }

                        ms.Write(buf, 0, br);
                    }
                    return null;
                }
                prev = buf[i];
            }

            // SOI not found in this batch — try again
            return ReadOneFrame(reader);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _restartCts?.Cancel();
        StopCapture();
    }
}
