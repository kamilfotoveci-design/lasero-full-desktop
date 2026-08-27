namespace Lasero.Core.Grbl;

/// <summary>
/// Seam over GRBL response/status parsing, independent of the transport — lets GrblConnection's
/// protocol interpretation be swapped/mocked without touching serial I/O. GrblProtocolParser is
/// pure delegation to the existing static GrblStatusParser/GrblErrorCodes; behavior is unchanged.
/// </summary>
public interface IGrblProtocolParser
{
    bool TryParseStatus(string line, out MachineStatus status);
    string DescribeError(int code);
    string DescribeAlarm(int code);
}

public sealed class GrblProtocolParser : IGrblProtocolParser
{
    public bool TryParseStatus(string line, out MachineStatus status) => GrblStatusParser.TryParse(line, out status);
    public string DescribeError(int code) => GrblErrorCodes.DescribeError(code);
    public string DescribeAlarm(int code) => GrblErrorCodes.DescribeAlarm(code);
}
