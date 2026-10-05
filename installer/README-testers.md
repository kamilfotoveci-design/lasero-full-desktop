# LASERO Desktop - testovací verze: průvodce pro testery

Děkujeme, že se účastníte testování. Tato verze je určená pouze pro uzavřený okruh testerů. Může obsahovat chyby a mění se.

## Bezpečnost jako první

Aplikace ovládá laserovou gravírku, která může způsobit zranění nebo požár.

- Před první skutečnou úlohou vyzkoušet postup na **odpadním kusu materiálu** a mít při tom nasazené **ochranné brýle** určené pro vlnovou délku laseru.
- **Kompatibilita s konkrétními modely strojů zatím není ověřená.** Žádný model se nedá považovat za schválený. Připojení ke stroji probíhá na vlastní odpovědnost obsluhy a pod dohledem.
- Výchozí rychlost a výkon nejsou ověřeny pro žádnou kombinaci stroje a materiálu. Vždy je potvrdit zkouškou na vzorku.
- U běžící úlohy zůstat, mít po ruce nouzové zastavení stroje. Tlačítko Stop v aplikaci není nouzové zastavení.

## Požadavky

- Windows 10 verze 1809 nebo novější (64bitový), případně Windows 11
- přibližně 400 MB volného místa na disku
- internet pro první přihlášení; poté fungují návrh, vzorník i ovládání stroje také offline
- samostatná instalace .NET není potřeba

## Ověření souboru

Spolu s instalátorem je soubor `SHA256SUMS`. Kontrolní součet ověříte v PowerShellu ve složce se souborem:

```powershell
Get-FileHash .\Lasero-Desktop-Setup-0.1.0.exe -Algorithm SHA256
```

Výsledek se musí shodovat s hodnotou v `SHA256SUMS`. Pokud se neshoduje, instalátor nespouštět a požádat o nové stažení.

## Instalace

1. Spustit `Lasero-Desktop-Setup-<verze>.exe`.
2. Pokud se objeví varování SmartScreen, viz níže.
3. Zvolit jazyk (čeština nebo angličtina) a způsob instalace:
   - **jen pro mě** - výchozí, bez oprávnění správce
   - **pro všechny uživatele** - vyžádá potvrzení správce
4. Projít průvodce. Na konci lze aplikaci rovnou spustit.

Aktualizace se provádí spuštěním novějšího instalátoru; přeinstaluje se přes stávající verzi a projekty i nastavení zůstanou. Před instalací je potřeba aplikaci ukončit, instalátor na to upozorní.

## Varování Windows SmartScreen

Instalátor zatím není podepsán certifikátem vydavatele, proto Windows zobrazí modré okno **Systém Windows ochránil váš počítač**. Je to očekávané. Pokud kontrolní součet výše souhlasí:

1. V okně klepnout na **Další informace**.
2. Zobrazí se název souboru a vydavatel „Neznámý vydavatel“ a tlačítko **Přesto spustit**. Klepnout na něj.

Pokud soubor po stažení nejde spustit vůbec: pravé tlačítko na souboru, **Vlastnosti**, dole na kartě Obecné zaškrtnout **Odblokovat**, potvrdit tlačítkem OK a spustit znovu. Antivirový program může soubor také dočasně zadržet; v takovém případě dejte vědět, soubor se kontroluje podle součtu výše.

## První spuštění

1. Přihlásit se (potřebný internet).
2. Pro vyzkoušení bez hardwaru použít simulátor stroje.
3. Skutečný stroj připojit až po prostudování bezpečnostních poznámek výše.

### Gravírka se nezobrazí jako port COM

Většina gravírek používá USB převodník. Windows obvykle ovladač doplní sám. Pokud se ve Správci zařízení v části Porty (COM a LPT) nic neobjeví, zkontrolujte kabel (některé jsou jen nabíjecí) a případně nainstalujte ovladač podle čipu ze stránek výrobce:

- CH340 / CH341 (WCH): https://www.wch-ic.com
- CP210x (Silicon Labs): https://www.silabs.com
- FTDI: https://ftdichip.com

Instalátor nic nestahuje ani neinstaluje automaticky.

## Odinstalace

Nastavení, Aplikace, **LASERO Desktop**, Odinstalovat. Průvodce se zeptá, zda odstranit také uživatelská data (nastavení, přihlášení, zálohy projektů, historii a protokoly) ze složky `%LOCALAPPDATA%\Lasero`. Výchozí volba je **Ne**, data zůstanou. Vaše uložené soubory `.lasero` se neodstraňují nikdy.

## Hlášení problémů

Napište kontaktu, od kterého jste instalátor dostali, případně přes https://lasero.net. Pomůže:

- co jste dělali a co se stalo (případně snímek obrazovky)
- verze aplikace (název instalátoru), verze Windows
- model stroje a verze firmwaru, pokud šlo o připojení
- soubory z `%LOCALAPPDATA%\Lasero\logs` - obsahují provozní záznam aplikace, před odesláním je lze prohlédnout

