using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Machines;

namespace Lasero.App.ViewModels;

/// <summary>
/// The pre-flight checklist on Home. Only the first item is something the app can actually know —
/// the rest are things nobody but the operator can see, so they are ticked by hand and start
/// untouched every session. A green tick that the software invented would be worse than no tick.
/// </summary>
public partial class HomeSafetyViewModel : ObservableObject
{
    private readonly ConnectionViewModel _connection;
    private readonly MachineStatusViewModel _machineStatus;

    public ObservableCollection<SafetyCheckItem> Items { get; } = new();
    public SafetyCheckItem MachineCheck { get; }

    public HomeSafetyViewModel(ConnectionViewModel connection, MachineStatusViewModel machineStatus)
    {
        _connection = connection;
        _machineStatus = machineStatus;

        MachineCheck = new SafetyCheckItem(
            "Stroj je připojen a připraven",
            "Zjišťuje aplikace sama.",
            isManual: false);
        Items.Add(MachineCheck);
        Items.Add(new SafetyCheckItem(
            "Pracovní plocha je čistá",
            "Odstraňte odřezky a hořlavé zbytky.",
            isManual: true));
        Items.Add(new SafetyCheckItem(
            "Materiál je správně zaaretován",
            "Ujistěte se, že se materiál během práce nepohne.",
            isManual: true));
        Items.Add(new SafetyCheckItem(
            "Odsávání a ventilace",
            "Zapněte odtah dřív, než laser začne hořet do materiálu.",
            isManual: true));

        foreach (var item in Items)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SafetyCheckItem.IsSatisfied)) NotifyTotals();
            };
        }

        _connection.PropertyChanged += (_, _) => RefreshMachineCheck();
        _machineStatus.PropertyChanged += (_, _) => RefreshMachineCheck();
        RefreshMachineCheck();
    }

    public int SatisfiedCount => Items.Count(item => item.IsSatisfied);
    public bool AllSatisfied => Items.All(item => item.IsSatisfied);
    public string Summary => $"{SatisfiedCount} ze {Items.Count} zkontrolováno";

    private void RefreshMachineCheck()
    {
        MachineCheck.IsSatisfied = _connection.IsConnected &&
            _machineStatus.DisplayState is LaserMachineDisplayState.Idle;
        MachineCheck.Detail = _connection.IsConnected
            ? MachineCheck.IsSatisfied ? "Stroj hlásí, že je připraven." : "Stroj je připojen, ale nehlásí klid."
            : "Zařízení zatím není připojeno.";
        NotifyTotals();
    }

    internal void NotifyTotals()
    {
        OnPropertyChanged(nameof(SatisfiedCount));
        OnPropertyChanged(nameof(AllSatisfied));
        OnPropertyChanged(nameof(Summary));
    }
}

public partial class SafetyCheckItem : ObservableObject
{
    public string Title { get; }
    public bool IsManual { get; }

    [ObservableProperty] private bool _isSatisfied;
    [ObservableProperty] private string _detail;

    public SafetyCheckItem(string title, string detail, bool isManual)
    {
        Title = title;
        _detail = detail;
        IsManual = isManual;
    }
}
