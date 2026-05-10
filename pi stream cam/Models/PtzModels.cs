namespace pi_stream_cam.Models;

public class PtzPreset
{
    public int Pan { get; set; }
    public int Tilt { get; set; }
}

public class PtzState
{
    public int Pan { get; set; } = 90;
    public int Tilt { get; set; } = 90;
    public List<PtzPreset>? Presets { get; set; }
}
