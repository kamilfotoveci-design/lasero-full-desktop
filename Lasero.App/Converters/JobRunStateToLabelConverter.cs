using System.Globalization;
using System.Windows.Data;
using Lasero.Core.Jobs;

namespace Lasero.App.Converters;

public sealed class JobRunStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        // Idle used to render as "Připraveno" too, so a disconnected app with no job loaded announced
        // "Nepřipojeno · Připraveno". Idle means there is nothing to run; Ready means a job has been
        // prepared and cleared preflight. Only Ready may claim readiness.
        JobRunState.Idle => "Bez úlohy",
        JobRunState.Preparing => "Připravuji",
        JobRunState.Ready => "Připraveno",
        JobRunState.Framing => "Rámování",
        JobRunState.Running => "Probíhá",
        JobRunState.Paused => "Pozastaveno",
        JobRunState.Completed => "Dokončeno",
        JobRunState.Cancelled => "Zrušeno",
        JobRunState.Error => "Chyba",
        JobRunState.Aborted => "Zastaveno",
        JobRunState.Faulted => "Chyba",
        _ => "Neznámý stav",
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
