namespace Lasero.Core.Grbl;

public sealed record GrblCommandResult
{
    public required bool IsOk { get; init; }
    public int? ErrorCode { get; init; }
    public int? AlarmCode { get; init; }
    public string? FailureMessage { get; init; }

    public string? Message => FailureMessage
        ?? (ErrorCode is { } error ? GrblErrorCodes.DescribeError(error)
        : AlarmCode is { } alarm ? GrblErrorCodes.DescribeAlarm(alarm)
        : null);

    public static readonly GrblCommandResult Ok = new() { IsOk = true };
    public static GrblCommandResult Error(int code) => new() { IsOk = false, ErrorCode = code };
    public static GrblCommandResult Alarm(int code) => new() { IsOk = false, AlarmCode = code };
    public static GrblCommandResult Failure(string message) => new() { IsOk = false, FailureMessage = message };
}
