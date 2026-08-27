using System.Globalization;
using System.Windows.Data;
using Lasero.Core.Jobs;

namespace Lasero.App.Converters;

public sealed class JobRunStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        JobRunState.Idle => "Připraveno",
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
