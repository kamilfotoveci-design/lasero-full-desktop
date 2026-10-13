using System.Reactive;
using Avalonia.Threading;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

/// <summary>Port of Lasero.App/ViewModels/MachineStatusViewModel.cs — a thin, read-only projection of
/// ILaserMachine's live status reports. No logic changed, only the dispatcher and MVVM base class.</summary>
public sealed class MachineStatusViewModel : ReactiveObject
{
    private readonly ILaserMachine connection;

    private GrblMachineMode mode = GrblMachineMode.Unknown;
    public GrblMachineMode Mode { get => mode; private set => this.RaiseAndSetIfChanged(ref mode, value); }

    private double workX;
    public double WorkX { get => workX; private set => this.RaiseAndSetIfChanged(ref workX, value); }

    private double workY;
    public double WorkY { get => workY; private set => this.RaiseAndSetIfChanged(ref workY, value); }

    private double workZ;
    public double WorkZ { get => workZ; private set => this.RaiseAndSetIfChanged(ref workZ, value); }

    private double feedRate;
    public double FeedRate { get => feedRate; private set => this.RaiseAndSetIfChanged(ref feedRate, value); }

    private double spindleSpeed;
    public double SpindleSpeed { get => spindleSpeed; private set => this.RaiseAndSetIfChanged(ref spindleSpeed, value); }

    private MachineAlert? activeAlert;
    public MachineAlert? ActiveAlert
    {
        get => activeAlert;
        private set
        {
            this.RaiseAndSetIfChanged(ref activeAlert, value);
            this.RaisePropertyChanged(nameof(HasActiveAlert));
            DisplayState = connection.DisplayState;
        }
    }

    private LaserMachineDisplayState displayState = LaserMachineDisplayState.Disconnected;
    public LaserMachineDisplayState DisplayState { get => displayState; private set => this.RaiseAndSetIfChanged(ref displayState, value); }

    public bool HasActiveAlert => ActiveAlert is not null;

    public ReactiveCommand<Unit, Unit> DismissAlert { get; }

    public MachineStatusViewModel(ILaserMachine connection)
    {
        this.connection = connection;
        DismissAlert = ReactiveCommand.Create(connection.DismissAlert);

        connection.StatusUpdated += status => RunOnUiThread(() => Apply(status));
        connection.AlertChanged += alert => RunOnUiThread(() => ActiveAlert = alert);
        connection.ConnectionStateChanged += _ => RunOnUiThread(() => DisplayState = connection.DisplayState);
    }

    private void Apply(MachineStatus status)
    {
        Mode = status.Mode;
        WorkX = status.WorkPosition.X;
        WorkY = status.WorkPosition.Y;
        WorkZ = status.WorkPosition.Z;
        FeedRate = status.FeedRate;
        SpindleSpeed = status.SpindleSpeed;
        DisplayState = connection.DisplayState;
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
