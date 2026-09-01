# Lasero Desktop — handoff

Stav ku **koncu session 1. 9. 2026**. Tento súbor existuje preto, aby sa dalo pokračovať v novom
chate alebo **iným modelom** (táto verzia je písaná pre Codex) bez čítania celej histórie. Popisuje
kde to je, čo sa spravilo, čo je rozrobené, čo sa nesmie rozbiť a na aké pasce si dať pozor.

Predchádzajúca verzia (rovnaký deň, ráno) je v commite `7506e1f`. Verzia z 31. 8. je v `98fc0d5`.

---

## 1. Kde projekt je

**`E:\lasero-desktop`** — nie na `C:`. Vetva `design-system-tokens`, HEAD `81d80e7`.
Pracovný strom čistý. **344 testov, všetky prechádzajú.**

**Pozor: `E:` je druhý disk a už sa raz sám odpojil** — `Get-PSDrive` ho prestal vidieť. Ak zmizne,
projekt je neprístupný; nie je to chyba repozitára.

```bash
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug
```

```bash
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln
```

**Pred každým buildom zabi appku, drží zamknutý `Lasero.App.exe`:**

```bash
powershell -NoProfile -Command "Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force"
```

Spustiteľný build: `E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe`

### 1.1 Technológia — čítaj, než začneš

Je to **WPF na .NET 8** (`net8.0-windows`), MVVM, `CommunityToolkit.Mvvm`. **Nie WinUI 3, nie
WinAppSDK, nie Win2D.** CLAUDE.md kedysi tvrdil WinUI3/Win2D a bola to fikcia; prepísaný bol
v minulej session. Ak niekde uvidíš WinUI, je to chyba dokumentu, nie stav kódu.

Prechod na WinUI 3 by nebol prepnutie ale prepis: iný XAML dialekt (`Microsoft.UI.Xaml`), iný
templating, iné okenné chrome (`WindowChrome` a `WindowFrameHook` by padli), packaging/identity,
a `System.Drawing`/`System.Windows.Media` cesty v rastri a v canvase by sa museli nahradiť. Bez
zadania to nerob.

### 1.2 Vizuálne overovanie — WPF, nie web

Browser tooling neplatí. Skripty sú v `.uiqa/`:

| skript | na čo |
|---|---|
| `shot.ps1 -Out x.png -WindowTitle "Lasero Desktop"` | screenshot okna cez `PrintWindow` |
| `screen.ps1 -Out x.png [-X -Y -W -H -Scale]` | **`CopyFromScreen`** — jediná cesta k tooltipom, ContextMenu a Popup |
| `crop.ps1 -In a.png -Out b.png -X .. -Y .. -W .. -H .. -Scale ..` | výrez a zväčšenie |
| `click.ps1 -X .. -Y .. [-Move hover]` | klik alebo len presun kurzora, súradnice relatívne k **hlavnému** oknu |
| `rclick.ps1 -X .. -Y ..` | pravý klik |
| `drag.ps1` / `slowdrag.ps1` | ťahanie |
| `resize.ps1 -W .. -H .. -X .. -Y ..` | veľkosť a poloha okna |
| `top.ps1 [-Off]` | dá okno dopredu **a** nastaví topmost |
| `uia.ps1 -Contains "text" \| -List` | UI Automation invoke podľa časti prístupného mena, diakritika sa ignoruje |
| `ui.ps1 -Action click -Name "…"` | starší UIA klik podľa presného mena |
| `toggle.ps1 -Name "…"` | vypíše prvky daného mena a skúsi SelectionItem / Toggle / Invoke |
| `setvalue.ps1 -Name "…" -Value "…"` | zápis do TextBoxu cez ValuePattern |

**Postup, ktorý funguje spoľahlivo:**

```bash
powershell -NoProfile -File .uiqa/uia.ps1 -Contains "obnovit"     # ak nabehol dialóg zálohy
powershell -NoProfile -Command "cd 'E:\lasero-desktop'; & '.uiqa\resize.ps1' -W 1700 -H 1000 -X 60 -Y 20; & '.uiqa\top.ps1'; & '.uiqa\click.ps1' -X 800 -Y 700 -Move hover; Start-Sleep -Milliseconds 800; & '.uiqa\shot.ps1' -Out '.uiqa\x.png' -WindowTitle 'Lasero Desktop'"
```

---

## 2. Pasce QA — každá z nich stála čas

1. **`shot.ps1` používa `PrintWindow`, takže nezachytí tooltip, ContextMenu ani Popup** — sú to
   samostatné top-level okná. Na ne `screen.ps1`.
2. **`PrintWindow` vráti bielu plochu, keď je okno zakryté iným.** Vyzerá to ako rozbité UI a nie je.
   Preto `top.ps1` pred každým screenshotom.
3. **`top.ps1` musí urobiť dve veci:** `SetWindowPos(HWND_TOPMOST)` **a** ťuknúť ALT pred
   `SetForegroundWindow` — Windows odmieta zmenu foregroundu z procesu, ktorý nie je vpredu.
4. **`shot.ps1 -WindowTitle 'Lasero Desktop'` môže trafiť tooltip**, ktorý má rovnaký titul. Dostaneš
   obrázok 160×28. Pred screenshotom odveď kurzor: `click.ps1 -X 800 -Y 700 -Move hover`.
5. **PowerShell mrzačí diakritiku v argumentoch z Bash toolu.** `-Name "Obnovit projekt"` nikdy
   netrafilo. `uia.ps1 -Contains "obnovit"` porovnáva bez diakritiky a bez ohľadu na veľkosť.
6. **Kliky podľa súradníc sú nespoľahlivé**, appka si po pripojení stroja sama prepne obrazovku a
   okno sa vie premaximalizovať. Po `resize.ps1` si vždy over veľkosť z výstupu `shot.ps1`.
7. Po nečistom ukončení nabehne **„Nalezena záloha projektu"**. Pri QA použi **„Obnovit projekt"**
   (`uia.ps1 -Contains "obnovit"`), zálohu nezahadzuj. Môžu prísť dva dialógy za sebou.
8. Na testovanie netreba hardvér: v *Zařízení* je port **`SIMULÁTOR — Virtuální laser`**. Appka si ho
   pamätá a **pripojí sa sama** — potom si prepne panel na tab *Stroj*, čo vyzerá ako chyba UI.
   Počas QA sa mi cez zle mierený klik naozaj spustila úloha; na simulátore je to bezpečné, na
   stroji nie.
9. V Bash tooling nefunguje `python`/`python3` (Windows Store stub). Používaj
   `/c/Users/Ruzovka/AppData/Local/Programs/Python/Python312/python.exe`, a **dlhšie skripty zapíš
   do súboru** — vnorený heredoc v jednom Bash príkaze padne na `unexpected EOF`.

---

## 3. Vizuálny smer

**Svetlý režim.** Neutrálny grafit + soft white + kobaltová modrá. **Jedna interakčná farba.**
Modrá `#2563EB` znamená vybrané/aktívne/primárne všade. Rozpočet ~90 % neutrál, 8 % modrá,
2 % sémantická. Červená len pre nebezpečné akcie a bodku v logu.

Zdroj pravdy pre tokeny je **`Lasero.App/Theme/LaseroTheme.xaml`**, vysvetlenie je
**`DESIGN.md`**, a celý specimen je **`docs/design/foundation.html`**. Keď sa specimen a téma
nezhodujú, je to chyba na zavretie, nie variácia — a specimen nemá automaticky pravdu.

Tmavá paleta je odskúšaná a funkčná (`ef64ab6`, vrátené v `918a263`); je to **zmena hodnôt
v `LaseroTheme.xaml`, nie prepis markupu**.

Pravidlá, ktoré sa v tejto session opakovane porušili a treba ich držať:

- **Hover je neutrálny wash bez zmeny okraja. Kobalt znamená vybrané.** Prvok, ktorý na hover
  prevezme vybraný odtieň, je chyba — DESIGN.md to hovorí menovite.
- **`Radius.Sm` (6) patrí ikonovým tlačidlám**, `Radius.Md` (8) tlačidlám a vstupom, `Radius.Lg` (12)
  panelom a popupom, `Radius.Pill` len skutočným kruhom.
- **Ikony: jedna rodina, 24×24 mriežka, stroke `Size.Icon.Stroke` (1.75), round cap a join.**
  Nekresli 2px hranaté pruhy „nastilizované do ikony".
- **Nič pod 12 px.** Žiadny literálny `FontSize` ani `CornerRadius` v XAML — drží to test.
- Layout: title bar 60, nav rail 164 (zbalený 74), canvas min 420, inšpektor 336 (280–560),
  status strip 56.

**Testy, ktoré držia dizajn systém:** `Lasero.Tests/ThemeTokenTests.cs`.

---

## 4. Čo sa v tejto session spravilo

Šesť commitov nad `7506e1f`.

### `f513df3` — vrstva vie, z akého materiálu má čísla

`LayerSettings.MaterialLabel` (string?), persistované v projekte ako aditívne pole, takže v6 súbory
sa načítajú s `null`. Je to **len prezentácia**, nič v toolpath ho nečíta.

Pravidlo, ktoré to robí užitočným: **label expiruje.** Každý setter `Speed`, `Power`, `Passes`,
`FillLineIntervalMm` a `Mode` ho zmaže, takže ručne prepísané číslo zhodí názov materiálu namiesto
toho, aby vrstva tvrdila recept, ktorý už nemá. Preto je aplikácia receptu **jedno volanie**
`LayerSettings.ApplyRecipe(...)` a nie päť priradení. `MaterialDisplayLabel` vracia
„Vlastní nastavení", keď label nie je.

### `b25e94b` — jeden blok „Nastavení práce" v inšpektore

Šesť riadkov label vľavo / ovládací prvok vpravo: **Materiál, Režim, Rychlost, Výkon, Průchody,
Interval.** Predtým to boli tri sekcie plus disclosure: segmentovaný `Zpracování`, mriežka
`Nastavení` s tromi číslami, Expander s intervalom a tlačidlo „Doporučené parametry" pod tým.

- **Materiál** je `Button` v štýle `Field.Select` (chrome selectu + chevron), nie skutočný ComboBox —
  otvára to isté zoskupené menu receptov (katalóg + vlastné presety, s rýchlosťou a výkonom pri
  každej položke), čo ComboBox s reťazcami nedokáže zobraziť.
- **Režim** je `ComboBox` s `ComboBoxItem.IsSelected` bindingom cez `EnumToBool` — presne ako
  predtým radio segmenty. Rastrová vrstva namiesto selectu ukáže „Obrázek · řádkové gravírování“:
  nie je čo voliť.
- **Interval** má celý riadok (label aj pole) nedostupný, keď sa nič nevyplňuje —
  `WorkSettingRow.FillOnly` v lokálnych resources view.

Chovanie nezmenené: tie isté property, tie isté two-way bindingy, to isté menu receptov.

### `354f9da` — akcie vrstvy na riadok, popupy dostali chrome appky

- **„+ Nová vrstva" je preč.** Vrstva vzniká kreslením alebo importom; prázdna nemala čo povedať.
  `SceneViewModel.AddLayer` je odstránený, aby nevyzeral funkčne.
- **Kontextové menu na riadku vrstvy** (pravý klik): zaradiť/vyradiť z úlohy, vyradiť všetky okrem
  tejto, skryť/zobraziť, skryť všetky okrem tejto, vybrať všetky tvary v tejto vrstve, duplikovať,
  odstrániť. Hlavičky sa preklápajú podľa stavu. `DuplicateSelectedLayer` bol dovtedy **mŕtvy kód
  bez volajúceho**.
- `OnLayerRowRightButtonDown` označí riadok pred otvorením menu — WPF neoznačuje `ListBoxItem` pravým
  tlačidlom, takže bez toho by „Odstranit" na treťom riadku zmazal prvý.
- Príslušnosť objektu k vrstve bola napísaná dvakrát (počítanie vs. výber). Je to netriviálne (shape
  odkazuje na vrstvu ID, staršie projekty len farbou, raster patrí podľa druhu) a obe kópie sa museli
  zhodovať. Teraz je jedno `UsesLayer`.
- **`ContextMenu` nemal štýl.** `MenuItem` a submenu áno, takže submenu vyzeralo správne a menu, ktoré
  ho drží, bolo stock WPF: hranaté rohy, plochý šedý vlas, žiadny tieň a starý ľavý žliabok na ikony.
  `HasDropShadow` musí zostať `True` — to je to, čo Popupu pod ním zapne transparentnosť, bez ktorej
  zaoblený roh nemá o čo byť zaoblený. Opravilo to naraz zoom presety, menu receptov aj nové menu
  vrstvy.
- Zoom presety sú teraz **25 / 50 / 75 / 100 %** + „Přizpůsobit oknu".

### `5ae615f` + `21ffa12` + `81d80e7` — zbaliteľný nav rail, tri kolá

Rail sa zbalí na **74 px** ikonový pruh a späť na 164. Stav sa pamätá
(`WorkspacePreferences.IsNavCollapsed`) — je to postoj, nie voľba na jednu úlohu.

- Šírka 74 je odvodená od **najširšej veci v pruhu**, nie typickej: 14 + 10 + 26px avatar účtu +
  10 + 14. Pri 66 (odvodených od 18px glyfov) sa avatar **tichšie orezal** o pravú hranu.
- Zbalený rail má padding 18 namiesto 14, inak ikony sedia 8 px vľavo od stredu.
- Labely idú cez `IconLabel.CompactMode`. Chat a účet si obsah skladajú ručne, takže tam je to
  vypísané zvlášť vrátane zhodenia 9px medzery za avatarom.
- Stĺpec je nastavovaný z kódu, **animuje sa `Width` samotného Borderu** (`GridLength` nemá
  animáciu). 180 ms, `CubicEase`, ease-out pri otváraní / ease-in pri zatváraní. Ctí sa
  `SystemParameters.ClientAreaAnimation`, a uložený stav sa nasadzuje **bez animácie**.
- Ovládač prešiel tri polohy: päta railu → titulná lišta → **pravá hrana railu**. Finálne je to
  22px chevron handle na hrane v polovici výšky, chevron sa **otočí o 180°** (nie výmena dvoch
  glyfov), 220 ms `CubicEase EaseInOut`.
- Titulná lišta je **identita** — wordmark, projekt, neuložené zmeny, okenné tlačidlá. Ovládač tam
  nepatrí a MainWindow.xaml to má napísané.
- **Hover-to-expand bol nasadený a na žiadosť používateľa vyhodený.** Na canvas appke je ľavá hrana
  na cestách všade: kurzor ňou prechádza k toolbaru, k plátnu, do menu. Ani s 180 ms otváracieho
  a 260 ms zatváracieho intentu to neprestalo hýbať panelom v koutku oka. **Rail sa mení len klikom
  na handle.**

---

## 5. Rozrobené — pokračovať tu

Používateľove poradie bolo **1, 4, 2, 3**:

- **1 — toolbar editora** ✅ (`5b683e1`)
- **4 — `ProcessStatusCard`** ✅ (`0061e7f`, `3efdfae`)
- **2 — pravý inšpektor** 🔶 **rozrobené, pozri nižšie**
- **3 — spodný stavový pruh** ⬅️ nezačaté

### 5.1 Pravý inšpektor — čo z §4.1 ešte chýba

Hotové: prepínač Vrstvy | Stroj, zoznam vrstiev, kontextové menu, **Nastavení práce**.

**Chýba, v tomto poradí, prilepené na spodok tabu Vrstvy (nie v ScrollVieweri):**

1. **Odhadovaný čas** — hodnota je `GCode.EstimatedTimeLabel`, drží „—“ dokým sa nespustí
   `PreviewSimulationCommand`. **To je zámer, nemeň to na permanentný readout** — dôvod je vypísaný
   v komentári v `MainWindow.xaml` pri tlačidle „Odhad času": číslo v kúte si vyžaduje dôveru, ktorú
   si nezaslúžilo, a operátor nevidí, čo do neho vstúpilo.
2. **Rámování** — sekcia s **Náhled rámování** a **Rámovat**.
   - `Náhled rámování` **neexistuje** a treba ho napísať: obrys úlohy na plátne bez pohybu stroja.
     V `SceneCanvas` na to nie je overlay; `Document.BoundingBox` + `FramingService.BuildFrameGCode`
     už existujú.
   - `Rámovat` = `GCode.RunFramingCommand`, existuje.
   - **Prepínač režimu rámovania do inšpektora nedávaj.** Používateľ rozhodol: *„celý obrys zachovaj
     len."* Existujúci prepínač Celý obrys / Pouze rohy **zostáva tam, kde je** (Expander „Nastavení
     kontroly oblasti" na tabe Stroj). Je to vratné čítanie krátkej instrukcie; ak sa téma vráti,
     over ju.
3. **Spustit — červené.** Použi `Button.DangerSolid`; `JobStartActionButton` v `MainWindow.xaml`
   dnes dedí z `JobPrimaryButton`/`PrimaryAction` (modré). `Button.Danger` je tichý variant,
   `Button.DangerSolid` je plný.

**Umiestnenie akcií úlohy — rozhodnuté používateľom:** na obrazovke **Návrh** nesú Spustit /
Rámovat / Pozastavit / Zastavit tlačidlá **inšpektor**, prilepené na spodok tabu Vrstvy, aby sa
Zastavit nedalo odscrollovať. Na **Domů / Zařízení / Chat** zostávajú v spodnom pruhu, aby sa žiadna
akcia stroja nestala nedosiahnuteľnou. Štýly `JobIdleActionButton`, `JobStartActionButton`,
`JobRunningActionButton`, `JobPausedActionButton`, `JobActiveStopButton` sú dnes v resources
`MainWindow.xaml` — pre použitie v `DesignerInspectorView` ich treba presunúť do
`SharedUiStyles.xaml` (pozor na poradie `StaticResource`, §7.4) a `MainWindowNavigationTests` má
test na to, že `JobActiveStopButton` dedí z `JobDangerButton` deklarovaného **pred** ním.

Tab *Stroj* má svoju `ProcessStatusCard` s primary/secondary akciami, takže tam sa Zastavit
nestratí — nezdvojuj ho tam.

### 5.2 Spodný stavový pruh (§4.2)

Mockup: `● Připojeno · Lasero L2 Pro · COM4 · Ovládání stroje | Pracovní plocha 400 × 400 mm |
Materiál Překližka (3 mm) | Náhled práce`

Dnešný pruh má stavové pilulky, názov súboru a hlášku. Chýba názov stroja, port, plocha, materiál
a „Náhled práce". **Všetko potrebné existuje:**

| údaj | zdroj |
|---|---|
| názov stroja | `Connection.ActiveMachineName` |
| port | `Connection.SelectedPort` |
| pracovná plocha | `Connection.WorkAreaWidthMm` / `WorkAreaHeightMm` |
| materiál | `Scene.SelectedLayer.MaterialDisplayLabel` (nové z `f513df3`) |
| Náhled práce | `GCode.PreviewSimulationCommand` |
| Ovládání stroje | `ShowDeviceCommand`, alebo `DesignerInspectorView.ShowMachineTab()` |

Na položky pruhu je štýl `StatusItem` v `SharedUiStyles.xaml`.

### 5.3 Lasero Chat (§4.4)

Nezačaté. Používateľ: *„lasero chat musi fungovat a vizuálne sa podobat rovnako ako lasero app na
webe."* Poslal screenshoty webového chatu: hlavička s avatarom KAMIL, `⏱ Historie` / `+ Nový chat`,
kontextový chip, bubliny (asistent biela karta, používateľ plná bublina), indikátor psaní, karta
**DOPORUČENÉ PARAMETRY** so štyrmi dlaždicami a `Uložit parametry`, „Fungovalo nastavení?" s 👍/👎,
návrhové chipy.

**Rozhodnuté:** akcent v chate zostáva **kobaltový `#2563EB`**, nie webová červená. Chat preberá
rozvrh, bubliny a karty; červená zostáva vyhradená nebezpečným akciám.

### 5.4 Diódový katalóg (§5 starého handoffu)

**Rozhodnuté:** zvyšok materiálov sa **doplní z publikovaných tabuliek** (lasertinkerer.com a
podobné, ten istý zdroj, z ktorého už vyšla preglejka a MDF), prepočíta rovnakou metódou a doplní
testom na monotónnosť dávky. **Nie sú to používateľove merania** a treba to tak označiť.

Overené je dnes len **preglejka a MDF na rez**. Web `lasero-app` má tú istú starú chybu a zámerne sa
neopravoval — desktop je teda dočasne rozdielny od webu; je to v komentári v `MaterialCatalog` aj
v teste `Catalog_MatchesTheAgreedRecipeValues`.

### 5.5 Nová požiadavka od zákazníka — QR kódy

Zákazník chce **šablónu, v ktorej sa mení len QR kód** (variabilné dáta). Overené k dnešnému dňu:
**LightBurn to umie** — `Tools > Create QR Code` (od 0.9.15) a QR/čiarové kódy podporujú
CSV-merge variable text (`%0`, `%1`, … plus Variable Offset na sériové čísla a riadky CSV).
Variable Text je v porovnávacej tabuľke odškrtnutý pre **Core aj Pro**, QR nástroj je v produkte
dávno pred rozdelením licencií — Core by teda mal stačiť, ale pred nákupom to nech potvrdí
LightBurn support.

Do Lasera sa to dá postaviť a nie je to malé. Návrh rozsahu, keby sa šlo do toho:

1. **Encoder** — QR (model 2, ECC L–H) v čistom C# v `Lasero.Core`, bez natívnej závislosti.
2. **Nový typ objektu scény** — `QrCodeObject`, ktorý si drží **payload ako dáta** a geometriu
   generuje. To je celý fígeľ: „obměnit QR kód" znamená prepísať text a nechať geometriu prepočítať,
   nie importovať nový SVG.
3. **Serializácia** — payload, veľkosť modulu, ECC, quiet zone do `ProjectFile` (aditívne polia,
   ako `MaterialLabel`).
4. **Vykreslenie** — moduly ako výplňové obdĺžniky vo `LayerMode.Fill`; **nie obrys každého modulu**,
   inak sa reže mriežka namiesto gravírovania plochy.
5. **Variabilné dáta** — zdroj (ručný zoznam, CSV, alebo číselná séria) + dávkový beh: jedna šablóna,
   N úloh. Tu treba rozhodnúť, či sa to rieši ako N úloh za sebou alebo array na plátne.
6. **Overenie** — test, že vygenerovaný QR sa dá prečítať dekóderom, nie len že „vyzerá ako QR".

---

## 6. Bezpečnostná hranica — nemeniť

Prezentácia sa meniť môže, **správanie nie**. Nikdy neupravovať sémantiku Start, Pause, Stop, Frame,
Home, Origin, Jog, Reset, Unlock, súradníc stroja, firmware príkazov ani bezpečnostných kontrol
kvôli vzhľadu.

- Dostupnosť Start/Frame rozhoduje **výhradne `CanRun` / `CanFrame` + `JobPreflight`**.
  `JobStatusViewModel` je čistá prezentácia — binduje tie isté príkazy a berie ich `CanExecute` tak,
  ako ho nájde; nemôže sprístupniť akciu stroja. Vlastní len formulácie a to, do ktorého z dvoch
  akčných slotov ktorý príkaz patrí.
- `ProcessStatusCard` nedrží vlastný stav vrátane progressu. Ak nie je čo merať, je pruh neurčitý.
- Nikdy nezobrazovať „Ready", kým to appka nepotvrdila. `JobRunState.Idle` je zámerne „Bez úlohy".
- Stav sa nikdy nesmie oznamovať iba farbou — vždy tvar (ikona) + slovo + farba.
- **`Safety.RequireFramingBeforeStart` nedávaj vedľa Spustit.** Je to poistka, dnes dostupná len
  v Nastavení; jedným klikom vedľa červeného Spustit ju nikto nechce vypnúť omylom.
- Sprievodca zariadením posiela pri skene **iba `$$`**. Jediný zápis do stroja je `$32=1` a nikdy
  nie automaticky.
- **Zmeny, ktoré zmenili generovaný G-kód:** tolerancia flattenovania textu 0,2 → 0,01 mm (`3ff71c2`)
  a **diódové recepty na rez preglejky a MDF** (`c5d76c4`).

---

## 7. WPF pasce — všetky sú systémové

Prvých deväť je z minulých sessions a stále platia. Zvyšok je nový.

1. **Implicitný `Style TargetType="Window"` sa nevzťahuje na žiadne okno v tejto appke.** WPF hľadá
   implicitný štýl podľa **presného typu**, a každé okno tu je odvodená trieda. **Každý root okna si
   nastavuje `Background` aj `Foreground` sám.**
2. **WPF ignoruje implicitné štýly deklarované vnútri šablóny.** Pre prvok vytvorený
   v `ControlTemplate` sa implicitný štýl hľadá **len v `Application.Resources`**. Odovzdaj ho
   **explicitne kľúčom**.
3. **`DockPanel` neklipuje.** Keď je dieťa širšie než jeho slot, podlezie pod susedné dieťa a
   v markupe nie je vidieť nič zlé; UIA hlási prvok na správnom mieste so správnou šírkou.
4. **Poradie v `ResourceDictionary` platí pre `StaticResource`.** Štýl, ktorý odkazuje na iný štýl,
   musí byť **za ním**, inak appka padne pri štarte na `Cannot find resource named …`.
5. **Obrovský `CornerRadius` na tenkom širokom `Border`i nie je zaoblený pruh** — WPF klampuje rádius
   po osiach, takže `Radius.Pill` na prvku 3 px vysokom vykreslí šošovku.
6. **`ToolBar` rieši pretečenie za teba** — `ToolBarPanel` + `ToolBarOverflowPanel` +
   `HasOverflowItems` + `ToolBar.OverflowMode`. Nepíš to ručne.
7. **Plátno si drží měřítko, kým je skryté.** `SceneCanvas` nemá pri `Visibility="Collapsed"` veľkosť,
   takže `FitToView` treba zavolať až keď je editor viditeľný, na `DispatcherPriority.Background`.
8. **Lokálna hodnota na prvku prebije Setter zo štýlu.** Stálo to raz `Height` scrollbaru a v tejto
   session `Margin` — `InlineTitleInput` má `Margin="-6,0,0,0"`, ale prvok mal lokálne
   `Margin="0,0,0,16"`, takže editovateľný nadpis sedel odsadený proti všetkým ostatným v paneli.
   To isté platí pre `IconLabel.Text`: preto existuje `CompactMode`.
9. **Vypnuté tlačidlo si kreslí plochu.** Pri `Button.Ghost` to obracia hierarchiu — na to je
   `ButtonChrome.SuppressDisabledSurface`.
10. **Dieťa `Grid`u väčšie než slot, do ktorého bolo arrangované, dostane layout clip.** Nie Border,
    nie `ClipToBounds` — vlastný WPF mechanizmus. 164 px široký rail v 74 px stĺpci nakreslil labely
    a odsekol ich na 74. Riešenie: `Grid.ColumnSpan` cez susedný stĺpec + `HorizontalAlignment="Left"`
    + `Panel.ZIndex`. Dieťa, ktoré preteká **z rodiča**, sa neklipuje — klipuje sa až prvok, ktorý
    je väčší ako **svoj slot**.
11. **`Path` v `Canvas`e sa layoutuje podľa svojich bounds, nie podľa súradníc vo `Data`.**
    `M4,12 L20,12` v `Canvas` bez `Canvas.Top` sa nakreslí na hornej hrane canvasu, nie na y=12.
    Tri takéto „linky" sa naskladali na seba a posun ±7 vytlačil hornú mimo tlačidla. Na pruhy použi
    `Rectangle` v `Grid`e s riadkami.
12. **`IsKeyboardFocused` nie je `:focus-visible`.** Nerozlíši Tab od kliknutia myšou, takže prvok
    po kliku zostane orámovaný akcentom a vyzerá zapnutý. Použi **`FocusVisualStyle`** — WPF ho
    aplikuje len pri fokuse z klávesnice. V téme je `FocusRing.Pill`.
13. **`ContextMenu` potrebuje vlastný štýl aj keď `MenuItem` už štýl má.** A `HasDropShadow` musí
    zostať `True`, inak Popup pod ním nemá `AllowsTransparency` a zaoblené rohy nefungujú.
14. **`GridLength` sa nedá `DoubleAnimation`ovať.** Animuj `Width` prvku a daj stĺpcu `Auto`, alebo
    stĺpec nastav skokom a animuj len prvok.

---

## 8. Otvorené / neriešené

- **SVG import je hranatý.** `SvgPathParser.cs` má `const int CurveSteps = 16` — pevný počet krokov
  na krivku nezávislý od veľkosti. Správne je subdivízia podľa tolerancie tetivy, ale parser pracuje
  v SVG user units pred transformáciou, takže absolútna mm tolerancia tam nie je dostupná.
- **Uložená šírka inšpektora je 546 px.** Pri načítaní sa obmedzuje na tretinu okna, ale **hodnota na
  disku sa neprepisuje** — je to preferencia používateľa. Dôsledok: aj pri 1700 px je workspace pod
  prahom 1060, takže popisky v toolbare sa zbaľujú. Zbalený rail teraz vracia 90 px, čo to zmierňuje.
- `LaseroProjectFile.Version` má default `7`, ale `Deserialize` aj `CreateArchiveSnapshot` ho natvrdo
  nastavia na `6`. Nič `Version` nečíta.
- **Avatar KAMILa v raile je rastrová fotka medzi 1.75px outline glyfmi** a v zbalenom pruhu to je
  vidieť. Je to zámerná voľba (je to tvár asistenta), ale ako rodina to nesedí.
- Staré publish výstupy `artifacts/` (3,4 GB) a `dist/` (1,1 GB) ležia v repozitári. Regenerovateľné.
- `TextToolWindow.Style` tieni `FrameworkElement.Style` — jediný build warning (CS0108).

### 8.1 Staršie, stále platné

- **Offset / Posunout** (nezačaté) — dialóg ako v LightBurne. Odporúčaný postup:
  `Geometry.GetWidenedPathGeometry(new Pen(...))` s `PenLineJoin` podľa štýlu rohu, potom
  union/difference. `PenLineJoin.Round/Bevel/Miter` mapuje presne na Oblý/Kosý/Roh.
- **Rohové úchopy pre Zkreslit textu** — dátový model (`TextDistortion`,
  `SetSelectedTextDistortionCorner`) a testy existujú, chýba ovládanie na plátne
  v `SceneCanvas.DrawSingleObjectHandles`. **Pasca:** `ResizeHandle` pomenúva roh s najmenším Y ako
  `Top`, ale plátno kreslí najmenšie Y dole. `TextDistortionCorner` je v dokumentovom priestore.
- **Assety:** fotka gravírovania v hero, render gravírky, náhledy projektů — **žiadne reálne assety
  neexistujú**, všetko sú kreslené zástupné z vlastnej ikonovej sady. Zámerne: appka nemá ako vedieť,
  aký stroj zákazník má, a stock render cudzej gravírky je obrázok nesprávneho stroja.
- Z mockupu nie je nasadené: typografická škála **s riadkovaním** (stupeň 18 px chýba, riadkovania
  nie sú definované), overenie piatich stavových pilulek proti mockupu, popisky skupin nad toolbarom,
  svislá lišta nástrojů po ľavej strane plátna, horní pruh „Návrh – Motýl" so stavom „Uloženo",
  rozměrový popisek u výběru na plátně.

---

## 9. Ako pracovať

- **Funkčnosť sa nesmie rozbiť.** Gating Start/Frame zostáva výhradne na
  `CanRun`/`CanFrame`/`JobPreflight` — §6.
- Všetky farby, rozmery a rádiusy z tokenov v `LaseroTheme.xaml`. Testy zakazujú literálne
  `FontSize` a `CornerRadius` v XAML.
- Po každej zmene: **build + testy + reálne spusti appku a pozri sa na výsledok** cez `.uiqa/`.
  Nestačí, že to skompiluje — v tejto session sa štyri chyby ukázali až na screenshote (orezaný
  avatar, deformovaný X, odseknuté labely, modrý handle po kliku).
- Commituj po logických celkoch, **anglicky, s vysvetlením PREČO**.
- **UI texty sú česky.** Handoff a komentáre v kóde nie.
