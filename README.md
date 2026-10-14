# Lasero Desktop

Profesionální Windows pracovní editor a ovládací panel pro GRBL laserové gravírky.

## Aktuální stav

- světlé rozhraní jako výchozí, ručně přepínatelný tmavý režim;
- české texty v přihlášení, onboardingu, editoru a nastavení;
- trvalé přihlášení přes DPAPI chráněný refresh token;
- offline editor a lokální připojení ke gravírce po přihlášení;
- pracovní plocha s importem, objekty, vrstvami, undo/redo a náhledem dráhy;
- ukládání a otevírání projektu `.lasero`;
- dirty stav, autosave recovery snapshot každých 30 sekund a potvrzení při ukončení;
- ergonomické rozdělení: nástroje, zařízení/jog, plátno, vlastnosti, vrstvy a úloha;
- vektorový editor: Bézierovy uzly, booleovské operace, offset (i propojený), výplň mezi cestami, skupiny a text;
- bitmapy s úpravou, ditheringem a náhledem; rámování, bezpečnostní preflight a import SVG/PNG/JPG/G-code přetažením;
- zhruba 2 190 automatických testů (`dotnet test` trvá kolem 3 minut).

## Spuštění

```powershell
dotnet run --project Lasero.App/Lasero.App.csproj
```

## Windows build a instalátor

Release balíček je self-contained pro Windows x64, takže cílový počítač nemusí mít předem nainstalované .NET Runtime.

```powershell
.\build-installer.ps1
```

Výstup vznikne v `artifacts\installer`. Instalátor je v češtině, instaluje aplikaci pouze pro aktuálního uživatele bez UAC výzvy, registruje soubory `.lasero` a volitelně vytvoří zástupce na ploše.

Při přechodu z LightBurnu zůstává původní instalace beze změny. Lasero podporuje import SVG, PNG, JPG a G-code; přímý import `.lbrn` a `.lbrn2` zatím není podporovaný.

## Zbývá pro další fázi

- skutečný offline vzorník s katalogem materiálů, mřížkou testů a aplikací doporučených parametrů;
- Lasero Chat s backendem, sdílenou historií a explicitním potvrzením změn projektu;
- ověření na reálném hardwaru: tok výkonu, pauza delší než 8 s, rámování a rastrový výstup (zatím jen simulátor a testy);
- výběr počátku úlohy a směru osy Y, překročení a obousměrný rastr, výstup oblouků G2/G3;
- plný instalační balíček, aktualizace a testování na různých DPI/rozlišeních Windows.

## Poznámka k repozitáři

Experimentální migrace na Avalonia (`Lasero.Avalonia`, `Lasero.Avalonia.Tests`) byla z hlavní větve odstraněna a zůstává ve větvi `archive/avalonia-experiment`. WPF je jediný produkční základ.
