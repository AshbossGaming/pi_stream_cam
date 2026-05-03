using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PTZTool;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://192.168.100.203:5000";
    private int _currentCam = 0;
    private int _pan = 90, _tilt = 45, _zoom = 1, _focus = 50;
    private System.Timers.Timer? _statusTimer;
    private System.Timers.Timer? _streamTimer;
    
    private readonly (string ip, string name)[] _cameras = {
        ("192.168.100.203", "Cam 1"),
        ("192.168.100.204", "Cam 2")
    };
    
    private readonly (int pan, int tilt, int zoom)[] _presets = {
        (90, 45, 1),
        (45, 30, 1),
        (135, 30, 1),
        (90, 60, 2)
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
            var json = await _http.GetStringAsync($"{_baseUrl}/api/ptz/status");
            if (json.Contains("pan"))
            {
                var start = json.IndexOf("pan") + 5;
                var end = json.IndexOf(",", start);
                if (int.TryParse(json[start..end], out int p)) _pan = p;
                
start = json.IndexOf("tilt") + 6;
                end = json.IndexOf("}", start);
                if (int.TryParse(json[start..end], out int t)) _tilt = t;
                
                int zPos = json.IndexOf("zoom");
                if (zPos > 0) {
                    start = zPos + 6;
                    end = json.IndexOf(",", start);
                    if (end < 0) end = json.IndexOf("}", start);
                    if (int.TryParse(json[start..end], out int z)) _zoom = z;
                }
                
                int fPos = json.IndexOf("focus");
                if (fPos > 0) {
                    start = fPos + 6;
                    end = json.IndexOf(",", start);
                    if (end < 0) end = json.IndexOf("}", start);
                    if (int.TryParse(json[start..end], out int f)) _focus = f;
                }
                
                Dispatcher.Invoke(() =>
                {
                    PanSlider.Value = _pan;
                    TiltSlider.Value = _tilt;
                    ZoomSlider.Value = _zoom;
                    FocusSlider.Value = _focus;
                    PanValue.Text = $"{_pan}°";
                    TiltValue.Text = $"{_tilt}°";
                    ZoomValue.Text = $"{_zoom}x";
                    FocusValue.Text = $"{_focus}";
                });
            }
        }
        catch { }
    }

    private async Task SetPan(int angle)
    {
        _pan = Math.Clamp(angle, 0, 180);
        await _http.PostAsync($"{_baseUrl}/api/ptz/pan/{_pan}", null);
        PanValue.Text = $"{_pan}°";
    }

    private async Task SetTilt(int angle)
    {
        _tilt = Math.Clamp(angle, 0, 180);
        await _http.PostAsync($"{_baseUrl}/api/ptz/tilt/{_tilt}", null);
        TiltValue.Text = $"{_tilt}°";
    }

    private async Task UpdateStreamAsync()
    {
        try
        {
            var url = $"{_baseUrl}/api/stream/mjpeg";
            // In a real app we'd use a better way to refresh the image, but for a simple tool:
            // StreamPreview.Source = new BitmapImage(new Uri($"{url}?t={DateTime.Now.Ticks}"));
        }
        catch { }
    }

    private async Task SetZoom(int level)
    {
        _zoom = Math.Clamp(level, 1, 8);
        await _http.PostAsync($"{_baseUrl}/api/ptz/zoom/{_zoom}", null);
        ZoomValue.Text = $"{_zoom}x";
    }

    private async Task SetFocus(int value)
    {
        _focus = Math.Clamp(value, 0, 100);
        await _http.PostAsync($"{_baseUrl}/api/ptz/focus/{_focus}", null);
        FocusValue.Text = $"{_focus}";
    }

    private async Task CenterAsync()
    {
        await SetPan(90);
        await SetTilt(45);
    }

    private void PanLeft_Click(object s, RoutedEventArgs e) => _ = SetPan(_pan - 5);
    private void PanRight_Click(object s, RoutedEventArgs e) => _ = SetPan(_pan + 5);
    private void TiltUp_Click(object s, RoutedEventArgs e) => _ = SetTilt(_tilt + 5);
    private void TiltDown_Click(object s, RoutedEventArgs e) => _ = SetTilt(_tilt - 5);
    private void Center_Click(object s, RoutedEventArgs e) => _ = CenterAsync();
    
    private void Preset1_Click(object s, RoutedEventArgs e) => _ = RecallPreset(0);
    private void Preset2_Click(object s, RoutedEventArgs e) => _ = RecallPreset(1);
    private void Preset3_Click(object s, RoutedEventArgs e) => _ = RecallPreset(2);
    private void Preset4_Click(object s, RoutedEventArgs e) => _ = RecallPreset(3);
    
    private async Task RecallPreset(int index)
    {
        var p = _presets[index];
        await SetPan(p.pan);
        await SetTilt(p.tilt);
        await SetZoom(p.zoom);
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

public static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, string requestUri, HttpContent? content)
        => client.PostAsync(requestUri, content);
}