using System.Reactive;
using Lasero.Avalonia.Contracts;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

public sealed class ProjectBrowserViewModel : ReactiveObject
{
    private readonly IProjectPicker picker;
    private readonly IProjectInspector inspector;
    private ProjectSummary? project;
    private string status = "Vyberte existující projekt .lasero.";

    public ProjectBrowserViewModel(IProjectPicker picker, IProjectInspector inspector)
    {
        this.picker = picker;
        this.inspector = inspector;
        OpenProject = ReactiveCommand.CreateFromTask(OpenProjectFile);
    }

    public ProjectSummary? Project
    {
        get => project;
        private set
        {
            this.RaiseAndSetIfChanged(ref project, value);
            this.RaisePropertyChanged(nameof(HasProject));
        }
    }

    public string Status
    {
        get => status;
        private set => this.RaiseAndSetIfChanged(ref status, value);
    }

    public bool HasProject => Project is not null;
    public ReactiveCommand<Unit, Unit> OpenProject { get; }

    private async Task OpenProjectFile()
    {
        var picked = await picker.PickOpen();
        if (picked.IsFailure)
        {
            Status = picked.Error;
            return;
        }

        if (string.IsNullOrWhiteSpace(picked.Value))
        {
            Status = "Výběr projektu byl zrušen.";
            return;
        }

        var inspected = inspector.Inspect(picked.Value);
        if (inspected.IsFailure)
        {
            Project = null;
            Status = inspected.Error;
            return;
        }

        Project = inspected.Value;
        Status = "Projekt je kompatibilní a připravený pro port editoru.";
    }
}
