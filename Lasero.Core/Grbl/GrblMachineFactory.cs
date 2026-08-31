using Lasero.Core.Machines;

namespace Lasero.Core.Grbl;

/// <summary>Builds a throwaway GRBL connection with its own transports. The app's live connection is
/// a singleton holding one open port, so anything that needs to talk to a *different* port at the
/// same time — scanning, in practice — has to bring its own.</summary>
public sealed class GrblMachineFactory : ILaserMachineFactory
{
    private readonly TimeSpan _commandTimeout;

    public GrblMachineFactory(TimeSpan? commandTimeout = null) =>
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(3);

    public ILaserMachine Create() =>
        new GrblConnection(
            new RoutingGrblTransport(new GrblSerialTransport(), new VirtualGrblTransport()),
            new GrblProtocolParser())
        {
            CommandTimeout = _commandTimeout,
        };
}
