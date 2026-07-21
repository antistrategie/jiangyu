namespace Jiangyu.Shared.Net;

/// <summary>A transport-level peer identity. Over Steam this is the SteamID64; over
/// loopback it is any distinct value the pair was created with.</summary>
public readonly struct PeerId : IEquatable<PeerId>
{
    public PeerId(ulong value) => Value = value;

    public ulong Value { get; }

    public bool Equals(PeerId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is PeerId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString();

    public static bool operator ==(PeerId left, PeerId right) => left.Equals(right);

    public static bool operator !=(PeerId left, PeerId right) => !left.Equals(right);
}
