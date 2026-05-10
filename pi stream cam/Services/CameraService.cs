using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace pi_stream_cam.Services;

public class CameraService : IDisposable
{
    private readonly bool _hasCamera;
    private readonly object _lock = new();
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

    private Process? _captureProcess;

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

    private Process? _recordProcess;
    private string? _recordingPath;
    private readonly object _recordLock = new();

    public bool IsRecording => _recordProcess != null && !_recordProcess.HasExited;
    public string? RecordingPath => _recordingPath;

    public CameraService()
    {
        _hasCamera =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            File.Exists("/usr/bin/gst-launch-1.0");

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
                height = _captureProcess != null ? 720 : 0,
                recording = IsRecording,
                recordingPath = RecordingPath
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
    }

    public void SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine($"Focus set: {_focus}");
        SaveState();
    }

    public void EnableAutofocus(string mode = "continuous")
    {
        _autofocus = true;
        _afMode = mode;

        Console.WriteLine($"Autofocus enabled: {_afMode}");
        SaveState();
    }

    public void DisableAutofocus()
    {
        _autofocus = false;
        _afMode = "manual";

        Console.WriteLine("Autofocus disabled");
        SaveState();
    }

    public void SetAfMode(string mode)
    {
        if (mode == "manual" || mode == "auto" || mode == "continuous")
        {
            _autofocus = mode != "manual";
            _afMode = mode;
            Console.WriteLine($"AF mode: {_afMode}");
            SaveState();
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
        RestartCapture();
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
                await Task.Delay(200, token);

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
            Console.WriteLine(
                "Camera capture requires gst-launch-1.0");

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

        StopRecording();

        _captureProcess = null;
        _captureCts = null;
    }

    public byte[]? CaptureSnapshot(int width = 1920, int height = 1080)
    {
        if (!_hasCamera)
            return null;

        try
        {
            var wasCapturing = IsCapturing;
            if (wasCapturing)
                StopCapture();

            Thread.Sleep(300);

            var pipeline = $"""
                libcamerasrc num-buffers=1 !
                video/x-raw,width={width},height={height},framerate=30/1 !
                videoconvert !
                jpegenc quality={_quality} !
                fdsink fd=1 sync=false
                """;

            var psi = new ProcessStartInfo
            {
                FileName = "gst-launch-1.0",
                Arguments = $"-q {pipeline}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.EnvironmentVariables["GST_DEBUG"] = "0";

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            using var ms = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(ms);
            process.WaitForExit(5000);

            var frame = ms.ToArray();

            if (wasCapturing)
                StartCapture();

            return frame.Length > 0 ? frame : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Snapshot error: {ex.Message}");
            return null;
        }
    }

    public bool StartRecording(string outputPath, int width = 1280, int height = 720, int framerate = 30)
    {
        lock (_recordLock)
        {
            if (IsRecording)
                return false;

            if (!_hasCamera)
                return false;

            try
            {
                var wasCapturing = IsCapturing;
                if (wasCapturing)
                    StopCapture();

                Thread.Sleep(300);

                var dir = Path.GetDirectoryName(outputPath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var pipeline = $"""
                    libcamerasrc !
                    video/x-raw,width={width},height={height},framerate={framerate}/1 !
                    videoconvert !
                    v4l2h264enc extra-controls="encode,h264_i_frame_period=30" !
                    h264parse !
                    mp4mux !
                    filesink location={outputPath}
                    """;

                var psi = new ProcessStartInfo
                {
                    FileName = "gst-launch-1.0",
                    Arguments = $"-e {pipeline}",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.EnvironmentVariables["GST_DEBUG"] = "0";

                _recordProcess = Process.Start(psi);
                _recordingPath = outputPath;

                if (_recordProcess == null)
                    return false;

                _recordProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"rec: {e.Data}");
                };
                _recordProcess.BeginErrorReadLine();

                Console.WriteLine($"[Camera] Recording started: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Record start error: {ex.Message}");
                return false;
            }
        }
    }

    public string? StopRecording()
    {
        lock (_recordLock)
        {
            if (_recordProcess == null || _recordProcess.HasExited)
            {
                _recordingPath = null;
                return null;
            }

            try
            {
                Console.WriteLine("[Camera] Stopping recording...");
                _recordProcess.StandardInput.Close();
                _recordProcess.WaitForExit(5000);

                if (!_recordProcess.HasExited)
                    _recordProcess.Kill(true);
            }
            catch
            {
            }
            finally
            {
                _recordProcess.Dispose();
                _recordProcess = null;
            }

            var path = _recordingPath;
            _recordingPath = null;

            if (!IsCapturing)
                StartCapture();

            Console.WriteLine($"[Camera] Recording stopped: {path}");
            return path;
        }
    }

    private string BuildGstPipeline(
        int width,
        int height,
        int framerate)
    {
        var pipeline = new List<string>();

        pipeline.Add("libcamerasrc");

        pipeline.Add(
            $"! video/x-raw,width={width},height={height},framerate={framerate}/1");

        if (_videoFlipped)
        {
            pipeline.Add("! videoflip method=vertical-flip");
        }

        pipeline.Add("! videoconvert");

        pipeline.Add($"! jpegenc quality={_quality}");

        pipeline.Add("! fdsink fd=1 sync=false");

        return string.Join(" ", pipeline);
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
                var pipeline = BuildGstPipeline(
                    width,
                    height,
                    framerate);

                Console.WriteLine($"[GST] {pipeline}");

                var psi = new ProcessStartInfo
                {
                    FileName = "gst-launch-1.0",
                    Arguments = $"-q {pipeline}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.EnvironmentVariables["GST_DEBUG"] = "2";

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
                        Console.WriteLine($"gst: {e.Data}");
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
            byte[] buffer = new byte[1024 * 1024];

            int idx = 0;

            while (true)
            {
                int b = reader.ReadByte();

                if (b == -1)
                    return null;

                if (b == 0xFF)
                {
                    b = reader.ReadByte();

                    if (b == -1)
                        return null;

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

                if (b == -1)
                    return null;

                buffer[idx++] = (byte)b;

                if (b == 0xFF)
                {
                    b = reader.ReadByte();

                    if (b == -1)
                        return null;

                    buffer[idx++] = (byte)b;

                    if (b == 0xD9)
                        foundEoi = true;
                }
            }

            if (!foundEoi)
                return null;

            var frame = new byte[idx];

            Array.Copy(buffer, frame, idx);

            return frame;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _restartCts?.Cancel();
        StopRecording();
        StopCapture();
    }
}
