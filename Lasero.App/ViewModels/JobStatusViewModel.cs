using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.App.Components;
using Lasero.Core.Jobs;

namespace Lasero.App.ViewModels;

/// <summary>
/// The job's run state as one status card: preparing, framing, engraving, paused, done, failed.
/// <para>
/// Presentation only. Whether Start and Frame are available is still decided entirely by
/// GCodeViewModel's CanRun/CanFrame and JobPreflight — this class binds the same commands and takes
/// their CanExecute as it finds it, so nothing here can enable a machine action. What it does own is
/// the wording, and which of the two action slots each command belongs in.
/// </para>
/// </summary>
public partial class JobStatusViewModel : ObservableObject
{
    private readonly GCodeViewModel _job;

    public JobStatusViewModel(GCodeViewModel job)
    {
        _job = job;
        _job.PropertyChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(GCodeViewModel.ProgressPercent)
            or nameof(GCodeViewModel.ElapsedTimeLabel)
            or nameof(GCodeViewModel.RemainingTimeLabel))
        {
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(Details));
            return;
        }

        if (args.PropertyName is not (nameof(GCodeViewModel.JobState)
            or nameof(GCodeViewModel.PreflightMessage)
            or nameof(GCodeViewModel.LastMessage)
            or nameof(GCodeViewModel.StartBlockedReason)
            or nameof(GCodeViewModel.EstimatedTimeLabel)
            or nameof(GCodeViewModel.IsCurrentDocumentFramed)))
        {
            return;
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(PrimaryCommand));
        OnPropertyChanged(nameof(PrimaryIsDanger));
        OnPropertyChanged(nameof(SecondaryLabel));
        OnPropertyChanged(nameof(SecondaryCommand));
        OnPropertyChanged(nameof(SecondaryIsDanger));
        OnPropertyChanged(nameof(Details));
    }

    public ProcessStatus Status => _job.JobState switch
    {
        JobRunState.Preparing or JobRunState.Framing or JobRunState.Running => ProcessStatus.Progress,
        JobRunState.Ready => ProcessStatus.Success,
        JobRunState.Completed => ProcessStatus.Success,
        JobRunState.Paused or JobRunState.Cancelled => ProcessStatus.Warning,
        JobRunState.Error or JobRunState.Aborted or JobRunState.Faulted => ProcessStatus.Error,
        _ => ProcessStatus.Waiting,
    };

    public string Title => _job.JobState switch
    {
        JobRunState.Preparing => "Příprava úlohy",
        JobRunState.Framing => "Rámování pracovní oblasti",
        JobRunState.Running => "Gravírování probíhá",
        // Ready only means the job has been prepared; whether the laser can take it is the
        // start gate's call. Claiming readiness while it is blocked would contradict the machine badge.
        JobRunState.Ready => _job.StartBlockedReason is null ? "Připraveno ke gravírování" : "Úloha je připravená",
        JobRunState.Paused => "Úloha je pozastavená",
        JobRunState.Completed => "Hotovo",
        JobRunState.Cancelled => "Úloha byla zrušena",
        JobRunState.Aborted => "Úloha byla zastavena",
        JobRunState.Error or JobRunState.Faulted => "Úlohu nelze spustit",
        _ => "Úloha ještě není připravená",
    };

    public string Description => _job.JobState switch
    {
        JobRunState.Preparing => "Příkazy pro gravírku se generují a připravuje se přenos.",
        JobRunState.Framing => "Gravírka projíždí obrys návrhu. Polohu je potřeba zkontrolovat na materiálu.",
        JobRunState.Running => _job.JobSourceLabel,
        JobRunState.Ready => _job.StartBlockedReason
            ?? "Návrh je uvnitř pracovní plochy a nastavení operací je platné. Spuštění se ještě potvrdí souhrnem.",
        JobRunState.Paused => "Laser je zhasnutý a osy stojí. Pokračovat lze, až bude vše v pořádku.",
        JobRunState.Completed => "Gravírování bylo dokončeno.",
        JobRunState.Cancelled or JobRunState.Aborted =>
            _job.LastMessage ?? "Úloha byla přerušena před dokončením.",
        JobRunState.Error or JobRunState.Faulted =>
            _job.PreflightMessage ?? _job.LastMessage ?? "Podrobnosti jsou v protokolu aplikace.",
        // Idle with a document loaded: the gate itself is the useful sentence, and it is the same
        // text the Start button's tooltip shows, so the two cannot disagree.
        _ => _job.StartBlockedReason ?? "Je potřeba zkontrolovat rámování a parametry vrstev.",
    };

    public bool ShowsProgress => _job.JobState
        is JobRunState.Preparing or JobRunState.Framing or JobRunState.Running or JobRunState.Paused;

    /// <summary>
    /// A percentage only while lines are actually being sent. Preparing and framing have no line
    /// count to measure against, so they report an indeterminate bar rather than a number the app
    /// would be inventing.
    /// </summary>
    public double? Progress => _job.JobState is JobRunState.Running or JobRunState.Paused
        ? _job.ProgressPercent
        : null;

    public string? PrimaryLabel => _job.JobState switch
    {
        JobRunState.Running => "Pozastavit",
        JobRunState.Paused => "Pokračovat",
        JobRunState.Framing => "Zastavit rámování",
        JobRunState.Ready => "Spustit",
        JobRunState.Completed or JobRunState.Cancelled or JobRunState.Aborted => "Spustit znovu",
        JobRunState.Error or JobRunState.Faulted => null,
        _ => null,
    };

    public ICommand? PrimaryCommand => _job.JobState switch
    {
        JobRunState.Running or JobRunState.Paused => _job.PauseResumeCommand,
        JobRunState.Framing => _job.AbortCommand,
        JobRunState.Ready or JobRunState.Completed or JobRunState.Cancelled or JobRunState.Aborted =>
            _job.RunJobCommand,
        _ => null,
    };

    /// <summary>Only framing's own stop is a red primary: it is the dominant action while the head is
    /// moving over the material with nothing burning yet. During a real job the dominant action is
    /// Pause, and Stop moves to the quiet slot.</summary>
    public bool PrimaryIsDanger => _job.JobState == JobRunState.Framing;

    public string? SecondaryLabel => _job.JobState switch
    {
        JobRunState.Running or JobRunState.Paused => "Zastavit",
        JobRunState.Ready => "Rámovat",
        JobRunState.Error or JobRunState.Faulted => "Rámovat",
        _ => null,
    };

    public ICommand? SecondaryCommand => _job.JobState switch
    {
        JobRunState.Running or JobRunState.Paused => _job.AbortCommand,
        JobRunState.Ready or JobRunState.Error or JobRunState.Faulted => _job.RunFramingCommand,
        _ => null,
    };

    public bool SecondaryIsDanger => _job.JobState is JobRunState.Running or JobRunState.Paused;

    /// <summary>
    /// What the operator watches while standing at the machine. Elapsed and remaining only appear
    /// once a job is under way — before that there is nothing to have elapsed — and the estimate is
    /// labelled as one, because acceleration and firmware lookahead move the real number.
    /// </summary>
    public IReadOnlyList<DeviceDetail> Details
    {
        get
        {
            switch (_job.JobState)
            {
                // No line counter. "2391 / 5158" is the app's own bookkeeping, not something the
                // operator standing at the machine can act on — the bar above already says how far
                // along it is, and in less space.
                case JobRunState.Running or JobRunState.Paused:
                    return
                    [
                        new DeviceDetail("Uplynulo", _job.ElapsedTimeLabel),
                        new DeviceDetail("Zbývá přibližně", _job.RemainingTimeLabel),
                    ];
                case JobRunState.Ready:
                    return
                    [
                        new DeviceDetail("Odhad času", _job.EstimatedTimeLabel),
                        new DeviceDetail("Rámování", _job.IsCurrentDocumentFramed ? "Ověřeno" : "Neověřeno"),
                    ];
                case JobRunState.Completed:
                    return [new DeviceDetail("Celkový čas", _job.ElapsedTimeLabel)];
                default:
                    return [];
            }
        }
    }
}
