namespace StreamCaster.Models;

public sealed class NetworkInterfaceOption
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Address { get; init; }

    public override string ToString() => $"{Description} ({Address})";
}
