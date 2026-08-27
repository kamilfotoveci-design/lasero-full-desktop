namespace Lasero.Core.Grbl;

/// <summary>
/// Single-byte GRBL real-time commands. These bypass the normal line queue —
/// GrblConnection writes them directly to the serial stream regardless of
/// what streamed G-code line is currently in flight.
/// </summary>
public static class GrblRealtimeCommand
{
    public const byte StatusReportQuery = (byte)'?';
    public const byte FeedHold = (byte)'!';
    public const byte CycleStartResume = (byte)'~';
    public const byte SoftReset = 0x18; // Ctrl-X

    public const byte FeedOverrideReset100 = 0x90;
    public const byte FeedOverrideIncrease10 = 0x91;
    public const byte FeedOverrideDecrease10 = 0x92;
    public const byte FeedOverrideIncrease1 = 0x93;
    public const byte FeedOverrideDecrease1 = 0x94;

    public const byte RapidOverrideReset100 = 0x95;
    public const byte RapidOverride50 = 0x96;
    public const byte RapidOverride25 = 0x97;

    public const byte SpindleOverrideReset100 = 0x99;
    public const byte SpindleOverrideIncrease10 = 0x9A;
    public const byte SpindleOverrideDecrease10 = 0x9B;
    public const byte SpindleOverrideIncrease1 = 0x9C;
    public const byte SpindleOverrideDecrease1 = 0x9D;

    public const byte ToggleSpindleStop = 0x9E;
    public const byte ToggleFloodCoolant = 0xA0;
    public const byte ToggleMistCoolant = 0xA1;

    /// <summary>Cancels an in-progress $J= jog without affecting a running job.</summary>
    public const byte JogCancel = 0x85;
}
