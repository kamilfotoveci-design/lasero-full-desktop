using System.IO;
using System.Text.Json;

namespace Lasero.App;

/// <summary>
/// Turns exceptions into sentences an operator can act on. The framework's own messages are English
/// and technical ("Access to the port 'COM3' is denied"); showing them raw tells a first-time owner
/// nothing about what to do. Every method here answers two questions: what happened, and what to try.
///
/// The exception itself is still logged by the caller; only the wording shown to the operator changes.
/// A message that already came from LASERO (Czech, so it carries diacritics) is kept, because those
/// were written for the operator; everything else falls back to a safe generic instruction rather than
/// leaking a stack-trace phrase. Texts are Czech in a neutral form, with no question or exclamation marks.
/// </summary>
public static class UserFacingErrors
{
    private const string CheckCable = "Je potřeba zkontrolovat USB kabel a napájení laseru, potom lze zkusit akci znovu.";

    /// <summary>Opening the serial connection failed.</summary>
    public static string ConnectionFailed(Exception? error, string? port)
    {
        var name = string.IsNullOrWhiteSpace(port) ? "Zvolený port" : $"Port {port}";
        return error switch
        {
            UnauthorizedAccessException =>
                $"{name} právě používá jiný program. Jiný laserový software nebo terminál je potřeba zavřít, potom lze zkusit připojení znovu.",
            FileNotFoundException or DirectoryNotFoundException =>
                $"{name} už není dostupný. Je potřeba ověřit, zda je laser zapnutý a připojený datovým USB kabelem, a zvolit port znovu.",
            IOException when LooksLikeMissingPort(error.Message) =>
                $"{name} už není dostupný. Je potřeba ověřit, zda je laser zapnutý a připojený datovým USB kabelem, a zvolit port znovu.",
            TimeoutException =>
                "Laser neodpověděl včas. Je potřeba ověřit, zda je zapnutý a zda je zvolená správná přenosová rychlost, obvykle 115200.",
            ArgumentException or ArgumentOutOfRangeException =>
                "Zvolený port nebo přenosová rychlost nejsou platné. Port je potřeba zvolit znovu, potom lze zkusit připojení znovu.",
            InvalidOperationException when IsOurs(error.Message) => error.Message,
            _ => $"LASERO se nepodařilo připojit ke stroji. {CheckCable}",
        };
    }

    /// <summary>The link dropped after it had been working.</summary>
    public static string ConnectionLost(Exception? error) =>
        error is null
            ? "Spojení s laserem bylo ukončeno."
            : "Spojení s laserem bylo přerušeno. Je potřeba zkontrolovat USB kabel a napájení, potom lze zařízení připojit znovu.";

    /// <summary>The link dropped while a job or framing was under way.</summary>
    public static string ConnectionLostDuringJob() =>
        "Spojení s laserem bylo přerušeno. Úloha se automaticky neobnoví. Je potřeba zkontrolovat materiál i laser a úlohu připravit znovu.";

    /// <summary>Importing or reading a design file failed.</summary>
    public static string FileImportFailed(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            "Soubor už na tomto místě není. Soubor lze vybrat znovu.",
        UnauthorizedAccessException =>
            "K souboru nelze přistoupit. Je potřeba ověřit, zda není otevřený v jiném programu a zda jsou k němu oprávnění.",
        IOException =>
            "Soubor se nepodařilo přečíst. Je potřeba ověřit, zda je dostupný a není otevřený v jiném programu.",
        OutOfMemoryException =>
            "Soubor je příliš velký. Lze jej zmenšit nebo použít jednodušší grafiku.",
        _ when IsOurs(error.Message) => error.Message,
        _ => "Soubor se nepodařilo načíst. Je potřeba ověřit, zda je platný a zda je ve formátu SVG, obrázek nebo G-code.",
    };

    /// <summary>Saving the project failed. The work is not lost: autosave keeps a recovery copy.</summary>
    public static string ProjectSaveFailed(Exception error) => error switch
    {
        UnauthorizedAccessException =>
            "Do zvolené složky nelze zapisovat. Projekt lze uložit jinam, například do složky Dokumenty. Rozpracovaná práce zůstává v automatické záloze.",
        DirectoryNotFoundException =>
            "Zvolená složka už neexistuje. Projekt lze uložit jinam. Rozpracovaná práce zůstává v automatické záloze.",
        IOException =>
            "Projekt se nepodařilo uložit. Je potřeba ověřit volné místo na disku a zda soubor není otevřený jinde. Rozpracovaná práce zůstává v automatické záloze.",
        _ => "Projekt se nepodařilo uložit. Lze jej uložit jinam. Rozpracovaná práce zůstává v automatické záloze.",
    };

    /// <summary>Opening a project failed. Nothing was changed on disk.</summary>
    public static string ProjectOpenFailed(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            "Soubor projektu už na tomto místě není. Projekt lze otevřít znovu a zvolit jeho nové umístění.",
        UnauthorizedAccessException =>
            "K souboru projektu nelze přistoupit. Je potřeba ověřit oprávnění a zda není otevřený v jiném programu.",
        InvalidDataException or JsonException or NotSupportedException =>
            "Soubor projektu je poškozený nebo pochází z nekompatibilní verze. Originál nebyl změněn. Lze zkusit otevřít automatickou zálohu.",
        IOException =>
            "Soubor projektu se nepodařilo přečíst. Je potřeba ověřit, zda je dostupný, potom lze akci zkusit znovu.",
        _ when IsOurs(error.Message) => error.Message,
        _ => "Projekt se nepodařilo otevřít. Originál nebyl změněn. Lze zkusit otevřít automatickou zálohu.",
    };

    /// <summary>Restoring the autosave copy failed.</summary>
    public static string RecoveryFailed(Exception error) =>
        "Automatickou zálohu se nepodařilo obnovit. Záloha zůstává na disku beze změny, projekt lze začít znovu.";

    /// <summary>Our own wording is Czech, so it carries diacritics; framework text is English and does not.</summary>
    public static bool IsOurs(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        foreach (var character in message)
            if ("áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ".Contains(character)) return true;
        return false;
    }

    private static bool LooksLikeMissingPort(string message) =>
        message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("semaphore", StringComparison.OrdinalIgnoreCase)
        || message.Contains("device is not", StringComparison.OrdinalIgnoreCase);
}
