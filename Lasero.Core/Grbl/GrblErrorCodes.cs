namespace Lasero.Core.Grbl;

/// <summary>
/// Human-readable messages for GRBL 1.1 "error:N" (rejected line) and
/// "ALARM:N" (critical fault) codes, so the UI never has to show a bare
/// number to the operator. Source: GRBL's documented error/alarm code list.
/// </summary>
public static class GrblErrorCodes
{
    public static string DescribeError(int code) => Errors.TryGetValue(code, out var msg)
        ? msg
        : $"Neznámá chyba GRBL (error:{code})";

    public static string DescribeAlarm(int code) => Alarms.TryGetValue(code, out var msg)
        ? msg
        : $"Neznámý alarm GRBL (ALARM:{code})";

    private static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "Neplatný příkaz G-code nebo chybějící parametr.",
        [2] = "Neplatná hodnota parametru.",
        [3] = "Příkaz '$' nelze spustit, když stroj není v klidu.",
        [4] = "Chybějící nebo neplatný argument P u příkazu G10 nebo $.",
        [5] = "V paměti chybí definovaný souřadnicový systém, například G54.",
        [6] = "Příkaz G53 je platný pouze s G0 nebo G1.",
        [7] = "Slovo G-code se na jednom řádku opakuje vícekrát.",
        [8] = "Příkaz není v aktuálním režimu povolen.",
        [9] = "G-code je uzamčený. Zařízení odemkněte příkazem $X.",
        [10] = "Na řádku G-code chybí požadovaná osa.",
        [11] = "Řádek obsahuje příliš mnoho slov G-code.",
        [12] = "Hodnota překračuje podporované rozlišení řídicí jednotky.",
        [13] = "Bezpečnostní kryt je otevřený a stroj nemůže pokračovat.",
        [14] = "Řádek překračuje maximální podporovanou délku.",
        [15] = "Cílová pozice překračuje softwarové limity stroje.",
        [16] = "Neplatná hodnota slova G-code.",
        [17] = "Příkaz vyžaduje aktivní laser nebo vřeteno.",
        [20] = "Nepodporovaný nebo neznámý příkaz G/M-code.",
        [21] = "Řádek obsahuje příliš mnoho příkazů G-code.",
        [22] = "Chybí rychlost posuvu požadovaná pro tento pohyb.",
        [23] = "Neplatné číslo souřadnicového systému.",
        [24] = "Příkaz G53 vyžaduje G0 nebo G1 na stejném řádku.",
        [25] = "Řádek obsahuje více příkazů ze stejné modální skupiny.",
        [26] = "Na řádku chybí osa požadovaná tímto příkazem.",
        [27] = "Číslo řádku překračuje maximální povolenou hodnotu.",
        [28] = "Příkaz vyžaduje alespoň jednu osu X, Y nebo Z.",
        [29] = "Číslo souřadnicového systému je mimo podporovaný rozsah G59.x.",
        [30] = "G53 nelze použít s aktivní kompenzací poloměru nástroje.",
        [31] = "Řádek obsahuje nadbytečné osy bez příkazu pohybu.",
        [32] = "Oblouk G2/G3 vyžaduje alespoň jednu osu v rovině oblouku.",
        [33] = "Cílová pozice oblouku je shodná s počáteční pozicí.",
        [34] = "Definice oblouku obsahuje neplatný poloměr nebo úhel.",
        [35] = "Chybí definice posunu I, J nebo K pro oblouk.",
        [36] = "Řádek obsahuje nadbytečná slova G-code bez účinku.",
        [37] = "Kompenzaci délky nástroje lze nastavit pouze pro podporovanou osu.",
        [38] = "Číslo nástroje překračuje podporovaný rozsah.",
    };

    private static readonly Dictionary<int, string> Alarms = new()
    {
        [1] = "Během pohybu byl aktivován hardwarový limit. Stroj byl bezpečně zastaven.",
        [2] = "Cílová pozice překračuje pracovní prostor stroje.",
        [3] = "Během pohybu došlo k resetu. Znovu najeďte do výchozí polohy.",
        [4] = "Najetí do výchozí polohy selhalo: koncový spínač nebyl nalezen včas.",
        [5] = "Najetí do výchozí polohy selhalo: koncový spínač zůstal aktivní.",
        [6] = "Najetí do výchozí polohy selhalo při druhém dotyku spínače.",
        [7] = "Najetí do výchozí polohy selhalo, protože byl otevřen bezpečnostní kryt.",
        [8] = "Najetí do výchozí polohy není povoleno během ručního pohybu $J.",
        [9] = "Najetí do výchozí polohy není povoleno, dokud je stroj uzamčený alarmem.",
        [10] = "Najetí do výchozí polohy selhalo: současně bylo aktivních více koncových spínačů.",
    };
}
