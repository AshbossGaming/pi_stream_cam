using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PTZTool.Models;

namespace PTZTool;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://192.168.100.203:5000";
    private int _currentCam = 0;
    private System.Timers.Timer? _statusTimer;
    private System.Timers.Timer? _streamTimer;
    private PtzStatus? _currentStatus;
    
    private readonly (string ip, string name)[] _cameras = {
        ("192.168.100.203", "Cam 1"),
        ("192.168.100.204", "Cam 2")
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SelectCam(0);
        _statusTimer = new System.Timers.Timer(2000);
        _statusTimer.Elapsed += async (s, e) => await FetchStatusAsync();
        _statusTimer.Start();
        
        _streamTimer = new System.Timers.Timer(500);
        _streamTimer.Elapsed += async (s, e) => await UpdateStreamAsync();
        _streamTimer.Start();
    }

    private void SelectCam(int index)
    {
        _currentCam = index;
        _baseUrl = $"http://{_cameras[index].ip}:5000";
        CamIp.Text = _cameras[index].ip;
        Cam1Btn.Background = index == 0 ? Brushes.LimeGreen : Brushes.Gray;
        Cam2Btn.Background = index == 1 ? Brushes.LimeGreen : Brushes.Gray;
        _ = FetchStatusAsync();
    }

    private void Cam1_Click(object sender, RoutedEventArgs e) => SelectCam(0);
    private void Cam2_Click(object sender, RoutedEventArgs e) => SelectCam(1);
    
    private void CamIp_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _baseUrl = $"http://{CamIp.Text}:5000";
        _ = FetchStatusAsync();
    }
    
    private void Go_Click(object sender, RoutedEventArgs e) => _ = FetchStatusAsync();

    private async Task FetchStatusAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<PtzStatus>($"{_baseUrl}/api/ptz/status");
            if (status != null)
            {
                _currentStatus = status;
                
                Dispatcher.Invoke(() =>
                {
                    PanSlider.Value = status.pan;
                    TiltSlider.Value = status.tilt;
                    ZoomSlider.Value = status.zoom;
                    FocusSlider.Value = status.focus;
                    
                    PanValue.Text = $"{status.pan}°";
                    TiltValue.Text = $"{status.tilt}°";
                    ZoomValue.Text = $"{status.zoom}x";
                    FocusValue.Text = $"{status.focus}";
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Status fetch error: {ex.Message}");
        }
    }

    private async Task SetPan(int angle)
    {
        try
        {
            angle = Math.Clamp(angle, 0, 180);
            await _http.PostAsync($"{_baseUrl}/api/ptz/pan/{angle}", null);
            PanValue.Text = $"{angle}°";
        }
        catch { }
    }

    private async Task SetTilt(int angle)
    {
        try
        {
            angle = Math.Clamp(angle, 0, 180);
            await _http.PostAsync($"{_baseUrl}/api/ptz/tilt/{angle}", null);
            TiltValue.Text = $"{angle}°";
        }
        catch { }
    }

    private async Task UpdateStreamAsync()
    {
        try
        {
            var url = $"{_baseUrl}/api/stream/mjpeg";
        }
        catch { }
    }

    private async Task SetZoom(int level)
    {
        try
        {
            level = Math.Clamp(level, 1, 8);
            await _http.PostAsync($"{_baseUrl}/api/ptz/zoom/{level}", null);
            ZoomValue.Text = $"{level}x";
        }
        catch { }
    }

    private async Task SetFocus(int value)
    {
        try
        {
            value = Math.Clamp(value, 0, 100);
            await _http.PostAsync($"{_baseUrl}/api/ptz/focus/{value}", null);
            FocusValue.Text = $"{value}";
        }
        catch { }
    }

    private async Task CenterAsync()
    {
        await _http.PostAsync($"{_baseUrl}/api/ptz/center", null);
        await FetchStatusAsync();
    }

    private void PanLeft_Click(object s, RoutedEventArgs e) => _ = MoveRelative(-5, 0);
    private void PanRight_Click(object s, RoutedEventArgs e) => _ = MoveRelative(5, 0);
    private void TiltUp_Click(object s, RoutedEventArgs e) => _ = MoveRelative(0, 5);
    private void TiltDown_Click(object s, RoutedEventArgs e) => _ = MoveRelative(0, -5);
    private void Center_Click(object s, RoutedEventArgs e) => _ = CenterAsync();
    
    private async Task MoveRelative(int deltaPan, int deltaTilt)
    {
        try
        {
            var request = new { deltaPan, deltaTilt };
            await _http.PostAsJsonAsync($"{_baseUrl}/api/ptz/move", request);
        }
        catch { }
    }
    
    private void Preset1_Click(object s, RoutedEventArgs e) => _ = RecallPreset(0);
    private void Preset2_Click(object s, RoutedEventArgs e) => _ = RecallPreset(1);
    private void Preset3_Click(object s, RoutedEventArgs e) => _ = RecallPreset(2);
    private void Preset4_Click(object s, RoutedEventArgs e) => _ = RecallPreset(3);
    
    private async Task RecallPreset(int index)
    {
        try
        {
            await _http.PostAsync($"{_baseUrl}/api/ptz/presets/{index}/recall", null);
            await FetchStatusAsync();
        }
        catch { }
    }

    private void PanSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) { PanValue.Text = $"{(int)e.NewValue}°"; _ = SetPan((int)e.NewValue); }
    }

    private void TiltSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) { TiltValue.Text = $"{(int)e.NewValue}°"; _ = SetTilt((int)e.NewValue); }
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) { ZoomValue.Text = $"{(int)e.NewValue}x"; _ = SetZoom((int)e.NewValue); }
    }

    private void FocusSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) { FocusValue.Text = $"{(int)e.NewValue}"; _ = SetFocus((int)e.NewValue); }
    }
}
