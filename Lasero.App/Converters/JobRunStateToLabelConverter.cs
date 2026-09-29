using System.Globalization;
using System.Windows.Data;
using Lasero.Core.Jobs;

namespace Lasero.App.Converters;

public sealed class JobRunStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is JobRunState state ? Label(state) : "Neznámý stav";

    public static string Label(JobRunState state) => state switch
    {
        // Idle used to render as "Připraveno" too, so a disconnected app with no job loaded announced
        // "Nepřipojeno · Připraveno". Idle means there is nothing to run; Ready means a job has been
        // prepared. It is worded as the job being prepared, never as "Připraveno", because that word belongs
        // to the machine badge and only the machine can be ready.
        JobRunState.Idle => "Bez úlohy",
        JobRunState.Preparing => "Příprava",
        JobRunState.Ready => "Úloha připravena",
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
