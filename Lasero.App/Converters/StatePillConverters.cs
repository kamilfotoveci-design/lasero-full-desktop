using System.Globalization;
using System.Windows.Data;
using Lasero.App.Components;
using Lasero.Core.Jobs;

namespace Lasero.App.Converters;

/// <summary>
/// Job run state expressed as a state pill kind. This is presentation only: it never decides
/// whether an action is available, and adding a state here cannot enable a machine command.
/// Unknown states fall back to Neutral rather than to a colour that would claim something.
/// </summary>
public sealed class JobRunStateToStatePillKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JobRunState state
            ? state switch
            {
                JobRunState.Ready => StatePillKind.Ready,
                JobRunState.Completed => StatePillKind.Ready,
                JobRunState.Preparing => StatePillKind.Busy,
                JobRunState.Framing => StatePillKind.Busy,
                JobRunState.Running => StatePillKind.Busy,
                JobRunState.Paused => StatePillKind.Warning,
                JobRunState.Cancelled => StatePillKind.Warning,
                JobRunState.Error => StatePillKind.Error,
                JobRunState.Aborted => StatePillKind.Error,
                JobRunState.Faulted => StatePillKind.Error,
                _ => StatePillKind.Neutral,
            }
            : StatePillKind.Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
