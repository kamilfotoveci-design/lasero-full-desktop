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
- build bez varování a 54 automatických testů.

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
- automatická GRBL identifikace, bezpečnostní preflight a blokace běhu při kritických chybách;
- plný instalační balíček, aktualizace a testování na různých DPI/rozlišeních Windows.
