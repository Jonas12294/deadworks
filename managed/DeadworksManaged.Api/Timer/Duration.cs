namespace DeadworksManaged.Api;

/// <summary>
/// Strongly-typed time value that can represent either game ticks or real (wall-clock) time.
/// Created via extension methods: <c>3.Seconds()</c>, <c>64.Ticks()</c>, <c>500.Milliseconds()</c>.
/// </summary>
public readonly record struct Duration
{
    internal long Value { get; }
    internal DurationKind Kind { get; }

    internal Duration(long value, DurationKind kind)
    {
        Value = value;
        Kind = kind;
    }

    internal static Duration FromTicks(long ticks) => new(ticks, DurationKind.Ticks);
    internal static Duration FromMilliseconds(long ms) => new(ms, DurationKind.RealTime);
    internal static Duration FromSeconds(double seconds) => new((long)(seconds * 1000), DurationKind.RealTime);

    public static implicit operator Duration(TimeSpan timeSpan) => FromMilliseconds((long)timeSpan.TotalMilliseconds);
}

internal enum DurationKind
{
    Ticks,
    RealTime
}

public static class DurationExtensions
{
    /// <summary>Creates a tick-based duration. Usage: <c>64.Ticks()</c></summary>
    public static Duration Ticks(this int ticks) => Duration.FromTicks(ticks);

    /// <summary>Creates a tick-based duration. Usage: <c>64L.Ticks()</c></summary>
    public static Duration Ticks(this long ticks) => Duration.FromTicks(ticks);

    /// <summary>Creates a real-time duration in seconds. Usage: <c>3.Seconds()</c></summary>
    public static Duration Seconds(this int seconds) => Duration.FromSeconds(seconds);

    /// <summary>Creates a real-time duration in seconds. Usage: <c>1.5.Seconds()</c></summary>
    public static Duration Seconds(this double seconds) => Duration.FromSeconds(seconds);

    /// <summary>Creates a real-time duration in milliseconds. Usage: <c>500.Milliseconds()</c></summary>
    public static Duration Milliseconds(this int ms) => Duration.FromMilliseconds(ms);

    /// <summary>Creates a real-time duration in milliseconds. Usage: <c>500L.Milliseconds()</c></summary>
    public static Duration Milliseconds(this long ms) => Duration.FromMilliseconds(ms);
}
