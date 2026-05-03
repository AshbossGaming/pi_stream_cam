using System.Device.I2c;
using Iot.Device.Pwm;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace pi_stream_cam.Services;

public class ServoService : IDisposable
{
    private readonly int _panChannel;
    private readonly int _tiltChannel;
    private int _panAngle;
    private int _tiltAngle;
    private bool _isHardwareAvailable;
    private object _lock = new();
    private readonly string _stateFile;
    private Pca9685? _pca9685;

    private List<PtzPreset> _presets = new();

    public int PanAngle => _panAngle;
    public int TiltAngle => _tiltAngle;
    public IReadOnlyList<PtzPreset> Presets => _presets.AsReadOnly();
    public bool IsFlipped => _tiltAngle > 90;
    public bool IsPanInverted => _tiltAngle > 90;

    public ServoService(int panChannel = 0, int tiltChannel = 1)
    {
        _panChannel = panChannel;
        _tiltChannel = tiltChannel;
        _panAngle = 90;
        _tiltAngle = 90;
        _stateFile = "/var/lib/pi-stream-cam/ptz-state.json";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/dev/i2c-1"))
        {
            try
            {
                var i2cDevice = I2cDevice.Create(new I2cConnectionSettings(1, 0x40));
                _pca9685 = new Pca9685(i2cDevice);
                // Set PWM frequency to 50Hz for servos
                _pca9685.PwmFrequency = 50;
                _pca9685.SetDutyCycle(_panChannel, 0);
                _pca9685.SetDutyCycle(_tiltChannel, 0);
                _isHardwareAvailable = true;
                Console.WriteLine("PCA9685 initialized on I2C address 0x40 (50Hz)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PCA9685 init failed: {ex.Message}");
                _isHardwareAvailable = false;
            }
        }
        else
        {
            _isHardwareAvailable = false;
            Console.WriteLine("PCA9685 not available (not on Linux or /dev/i2c-1 missing)");
        }

        LoadState();
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                var state = JsonSerializer.Deserialize<PtzState>(json);
                if (state != null)
                {
                    _panAngle = Math.Clamp(state.Pan, 0, 180);
                    _tiltAngle = Math.Clamp(state.Tilt, 0, 180);
                    _presets = state.Presets ?? new List<PtzPreset>();
                    // Ensure we have exactly 4 preset slots to match the UI
                    while (_presets.Count < 4) _presets.Add(null!);
                    Console.WriteLine($"Restored position: Pan={_panAngle}°, Tilt={_tiltAngle}°, Presets={_presets.Count(p => p != null)}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load state error: {ex.Message}");
        }
    }

    public void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            var state = new PtzState { Pan = _panAngle, Tilt = _tiltAngle, Presets = _presets };
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(_stateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Save state error: {ex.Message}");
        }
    }

    public void SetPreset(int index, int pan, int tilt)
    {
        lock (_lock)
        {
            if (index >= 0 && index < 4)
            {
                _presets[index] = new PtzPreset { Pan = pan, Tilt = tilt };
                SaveState();
            }
        }
    }

    public void ClearPreset(int index)
    {
        lock (_lock)
        {
            if (index >= 0 && index < 4)
            {
                _presets[index] = null!;
                SaveState();
            }
        }
    }

    public Task SetPanAsync(int angle)
    {
        lock (_lock)
        {
            angle = Math.Clamp(angle, 0, 180);
            _panAngle = angle;
            SetServoAngle(_panChannel, angle);
            SaveState();
        }
        return Task.CompletedTask;
    }

    public Task SetTiltAsync(int angle)
    {
        lock (_lock)
        {
            angle = Math.Clamp(angle, 0, 180);
            _tiltAngle = angle;
            SetServoAngle(_tiltChannel, angle);
            SaveState();
        }
        return Task.CompletedTask;
    }

    public Task MovePanAsync(int delta)
    {
        if (_tiltAngle > 90)
            delta = -delta;
        return SetPanAsync(_panAngle + delta);
    }
    public Task MoveTiltAsync(int delta) => SetTiltAsync(_tiltAngle + delta);
    public Task CenterAsync() => SetTiltAsync(90).ContinueWith(_ => SetPanAsync(90));

    private void SetServoAngle(int channel, int angle)
    {
        if (!_isHardwareAvailable || _pca9685 == null) 
        {
            Console.WriteLine($"Servo CH{channel}: {angle}° (simulation)");
            return;
        }

        try
        {
            // Convert angle to duty cycle for MG90S servo (500-2400 μs pulse at 50Hz)
            // 50Hz = 20ms period, so 500μs = 0.025 duty, 2400μs = 0.12 duty
            double dutyCycle = 0.025 + (angle / 180.0) * 0.095;
            _pca9685.SetDutyCycle(channel, dutyCycle);
            Console.WriteLine($"Servo CH{channel}: {angle}° (duty: {dutyCycle:F3})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Servo error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            // Center servos on shutdown
            if (_isHardwareAvailable && _pca9685 != null)
            {
                SetServoAngle(_panChannel, 90);
                SetServoAngle(_tiltChannel, 90);
                Thread.Sleep(500);
            }
            _pca9685?.Dispose();
        }
        catch { }
    }
}

    public class PtzState
    {
        public int Pan { get; set; } = 90;
        public int Tilt { get; set; } = 90;
        public List<PtzPreset>? Presets { get; set; }
    }

    public class PtzPreset
    {
        public int Pan { get; set; }
        public int Tilt { get; set; }
    }
