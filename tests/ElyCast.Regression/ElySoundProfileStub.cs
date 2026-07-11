namespace Elysium_Cast_IPTV.Models;

public class ElySoundProfile
{
    public int Version { get; set; } = 2;
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "custom";
    public int Preamp { get; set; }
    public int Bass { get; set; }
    public int LowMid { get; set; }
    public int Mid { get; set; }
    public int Presence { get; set; }
    public int Treble { get; set; }
    public int Clarity { get; set; }
    public int Width { get; set; }
    public int Compressor { get; set; }
    public double LimiterCeilingDb { get; set; } = -1;
    public int Limiter { get; set; }
}
