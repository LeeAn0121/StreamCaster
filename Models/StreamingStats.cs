namespace StreamCaster.Models;

public sealed class StreamingStats
{
    public string Status { get; set; } = "Idle";

    public string BytesSent { get; set; } = "—";

    public string Bitrate { get; set; } = "—";

    public string PacketSize { get; set; } = "1316";
}
