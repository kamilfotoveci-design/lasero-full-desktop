# Lasero Desktop — handoff

Stav k **31. 8. 2026**, koniec session. Tento súbor existuje preto, aby sa dalo pokračovať v novom
chate (alebo iným modelom) bez čítania celej histórie. Popisuje **kde to je, čo sa spravilo, čo je
rozrobené a čo ďalej**.

---

## 1. Kde projekt je

**`E:\lasero-desktop`** — nie na `C:`.

Projekt bol 27. 8. 2026 presunutý z `C:\Users\Ruzovka\Videos\lasero-desktop`, pretože `C:` mal
0,5 GB voľných. **Disk `D:` na tomto stroji neexistuje** (sú len `C:`, `E:`, `F:` = CD-ROM).
`E:` má ~329 GB voľných.

Vetva: `design-system-tokens`. Posledný commit: `7ce52b5`.

```bash
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug
```

```bash
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln
```

Spustiteľný build: `E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe`

**308 testov, všetky prechádzajú.**

### Vizuálne overovanie

Je to WPF, nie web — browser tooling neplatí. V `.uiqa/` sú pomocné skripty:

- `shot.ps1 -Out x.png` — screenshot okna
- `crop.ps1 -In a.png -Out b.png -X .. -Y .. -W .. -H .. -Scale ..` — výrez a zväčšenie
- `click.ps1 -X .. -Y ..` / `drag.ps1 -X1 .. -Y1 .. -X2 .. -Y2 ..` — syntetický vstup (súradnice sú relatívne k oknu)
- `resize.ps1 -W .. -H .. -X .. -Y ..` — veľkosť okna
- `ui.ps1 -Action click -Name "…"` — klik cez UI Automation podľa prístupného mena (spoľahlivejšie než súradnice)

**Pasca:** `shot.ps1` používa `PrintWindow`, takže **nezachytí tooltip, ContextMenu ani dropdown** —
tie sú samostatné top-level okná. Na ne treba záber obrazovky:

```powershell
Add-Type -AssemblyName System.Drawing
$b = New-Object System.Drawing.Bitmap 760, 220
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 170, 0, 0, (New-Object System.Drawing.Size 760, 220))
$g.Dispose(); $b.Save("out.png", [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
```

**Druhá pasca:** tooltip nevyvoláš cez `Cursor.Position` jedným priradením — WPF potrebuje skutočné
`WM_MOUSEMOVE`. Treba kurzorom „zatriasť" (opakované `SetCursorPos` s malými zmenami) a počkať ~1,5 s.

Po nečistom ukončení appky nabehne dialóg **„Nalezena záloha projektu"** — treba ho odkliknúť
(`ui.ps1 -Action click -Name "Zahodit zálohu"`), inak screenshot zachytí dialóg.

Na testovanie pripojenia netreba hardvér: v *Zařízení* je port **`SIMULÁTOR — Virtuální laser`**.

**Appka drží zamknutý `Lasero.App.exe` a `Lasero.Core.dll`** — pred každým buildom ju treba zabiť:
`Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force`.

---

## 2. Vizuálny smer (rozhodnuté používateľom, needitovať bez dohody)

Neutrálny grafit + soft white + kobaltová modrá. **Jedna interakčná farba.** Oranžová ako
druhý akcent je zrušená — modrá `#2563EB` znamená vybrané/aktívne/primárne všade.

Rozpočet farieb: ~90 % neutrál, 8 % modrá, 2 % sémantická zelená/oranžová/červená.
Červená je len pre nebezpečné akcie a bodku v logu.

| rola | hodnota |
|---|---|
| pozadie | `#F7F7F5` |
| panel / sidebar | `#FFFFFF` |
| jemná plocha, field | `#F3F4F2` |
| border | `#E4E5E2` |
| silný border | `#D2D4D0` |
| primárny text | `#171918` |
| sekundárny text | `#666B68` |
| muted text | `#929793` |
| accent / selection / Start | `#2563EB` |
| accent hover | `#1D4ED8` |
| vybraná plocha | `#EFF6FF` |
| vybraný border | `#BFDBFE` |
| success | `#15803D` |
| warning | `#D97706` |
| danger | `#DC2626` |

**Typografia: Inter**, zabalený v aplikácii (`Lasero.App/Assets/Fonts`, SIL OFL) ako `<Resource>` —
odkaz len menom by ticho spadol na Segoe. Váhy: 400 text, 500 ovládacie prvky a hodnoty,
600 nadpisy. Bežný text 13–14 px, **nič pod 12 px**.

**Čísla nie sú monospace** — Inter s `Typography.NumeralAlignment="Tabular"`. Skutočný monospace
zostal len pre GRBL konzolu a G-kód.

Tokeny sú v `Lasero.App/Theme/LaseroTheme.xaml` (`Brush.*`, `Radius.*`, `Size.Control.*`,
`Size.Text.*`, `Size.Icon.*`) a `Theme/SharedUiStyles.xaml`.

**Testy, ktoré držia dizajn systém** (v `Lasero.Tests/ThemeTokenTests.cs`):
- `NoMarkupCarriesALiteralFontSize` — zakazuje literálne `FontSize` v XAML
- `NoMarkupCarriesAScalarLiteralCornerRadius` — zakazuje literálne `CornerRadius`
- `RoundControlsUseThePillTokenRatherThanHalfTheirBox` — kruhy musia ísť cez `Radius.Pill` (999)

---

## 3. Čo sa v tejto session spravilo

Šesť commitov nad `5f8d9e7`.

### `a1fc143` — rozmery z toolbaru do inšpektora
Prvý pokus o presun bloku poloha/veľkosť/rotácia. **Neskôr prekonané `7307749`** — používateľ chcel
LightBurn-style pruh hore. Commit ostáva v histórii, ale inšpektor už tie sekcie nemá.

Zároveň opravené: **panel vlastností tiekol mimo okna.** `DesignerInspectorView` má `MinWidth="320"`,
stĺpec dovoľoval `280` a uložená šírka bola presne 280. **Grid dieťa pod jeho `MinWidth` nezmenší** —
takže 40 px panelu viselo za pravým okrajom a hodnoty X/Y/Š/V boli prepolené. Zjednotené na 320 na
troch miestach (`MainWindow.xaml` stĺpec, `DesignerInspectorView` `MinWidth`,
`WorkspacePreferences.MinInspectorWidth`) + test `InspectorColumnCannotBeNarrowerThanThePanelItHolds`.

### `0a2fc14` — výplň zložených ciest + chodiace linky

- **Výplň sa kreslila po jednotlivých obrysoch**, takže vnútro „e" a „o" bolo vyplnené namiesto
  toho, aby bolo dierou. Obrysy sa teraz zoskupujú podľa `GeometrySetId` (dokumentovaná
  `Guid.Empty` = „jedna zložená cesta na objekt", čo stále používa SVG import) a kreslia sa ako
  jedna geometria s `FillRule.Nonzero`. **Nonzero, nie EvenOdd** — dva rovnako vinuté obrysy, ktoré
  sa len prekrývajú, sa majú spojiť, nie si vyrezať dieru. Otvorené obrysy majú `IsFilled=false`.
- **Chodiace linky (marching ants)** na výbere aj na obrysoch objektu. Všetky čiarkované vizuály
  bindujú `StrokeDashOffset` na jednu animovanú property `MarchingAntsPhaseProperty` na plátne —
  **nie na vlastnú animáciu**, pretože overlay sa prekresľuje pri každom pohybe myši počas ťahania
  a per-shape animácie by sa reštartovali na nulu, takže mravce by stáli práve pri ťahaní.
  Animácia sa zastaví, keď nie je nič vybrané, a rešpektuje `SystemParameters.ClientAreaAnimation`.

### `3ff71c2` — editovateľný text

- **`Lasero.Core/Scene/TextSource.cs`** (nový): čo text hovorí a ako je vysadený — `Text`,
  `HeightMm`, `FontFamily`, `Bold`, `Italic`, `Uppercase`, `Weld`, `Distortion`.
  Plus `TextDistortion` (štyri rohy jednotkového boxu, bilineárne mapovanie) a `TextDistortionCorner`.
- `SceneObject.Text` + `IsText` — presne podľa vzoru `RasterFilePath` / `IsRaster`.
- `VectorTextFactory.Rebuild(existing, source)` — prekreslí obrysy a **zachová Id, Transform,
  vrstvu, viditeľnosť a príznaky**, takže sa zmenia písmená a nič iné sa nepohne.
- **Velká písmena** sa aplikujú pri vykreslení, nie na uložený string, aby vypnutie vrátilo to, čo
  používateľ napísal.
- **Svařeno** = `Geometry.Combine(geometry, Geometry.Empty, Union)` — beží **pred** flattenovaním,
  na skutočných krivkách.
- **Tolerancia flattenovania 0,2 → 0,01 mm.** 0,2 mm je ~22 úsečiek na kružnicu, takže krivky boli
  viditeľne hranaté. **Toto mení G-kód** — viac a kratších úsečiek na krivkách.
- Projekt: `ProjectObject.Text` (nepovinné). Starý projekt bez tohto členu sa načíta ako krivky.
- Dialóg: pole „Výška" si zvisle odrezávalo hodnotu (vlastný `TextBox` s `Padding="10,6,42,6"`
  v 36px prvku) → použitá komponenta `UnitField.*`.
- Testy: `Lasero.Tests/EditableTextTests.cs` (10 testov, vrátane spätnej kompatibility projektu).

**Nedokončené v tomto commite:** výška v textovej sekcii je veľkosť písma *pred* vlastným
zväčšením objektu; skutočná pálená výška je pole `V`. `Rebuild` zachováva `Transform` vrátane scale
zámerne — prepočítavanie scale by spôsobilo, že text skočí pri prvej oprave preklepu po manuálnom
zväčšení. Je to zdokumentované v XML komentári `Rebuild`.

### `7307749` — pruh vlastností hore + čierne tooltipy

- **`Lasero.App/Views/SelectionPropertiesBar.xaml`** (nový) — poloha, veľkosť, rotácia, textové
  nastavenia a akcie objektu v **jednom pruhu na celú šírku okna**, nad workspace, dva riadky.
  Vložený v `MainWindow.xaml` ako `Grid.Row="1"` (riadky sú teraz `60 / Auto / * / 56`).
  Pruh sa sám zbalí, keď nie je nič vybrané. Pri 1080px okne s vybraným textom **scrolluje do
  strany**, nekrája hodnoty.
- Inšpektor je späť len `Vrstvy | Stroj`.
- **`Check.Box`** → skutočný checkbox namiesto iOS prepínača.
- **Čierne tooltipy.** Takmer každý tooltip v appke bol plný čierny blok bez čitateľného textu.
  **Príčina je WPF pravidlo, nie preklep:** téma nastavovala `Foreground` na implicitnom `TextBlock`
  štýle, a **WPF hľadá implicitné štýly pre prvky vytvorené vnútri šablóny len v
  `Application.Resources`** — štýl deklarovaný v `<Border.Resources>` šablóny sa nikdy nepoužije.
  `TextElement.Foreground` na šablóne prehrá, pretože **dedenie prehráva Setter štýlu.**
  Tooltip je teraz svetlý. Dlhé tooltipy sa zalamujú cez implicitný `DataTemplate` pre `String` —
  ten sa (na rozdiel od implicitného *štýlu*) rieši normálnym resource lookupom.

### `38eb54a` — checkbox všade, biely text na akcente, menej otázok pri importe

- **Implicitný `Style TargetType="TextBlock"` je zrušený** a s ním celá trieda chýb. Nastavoval
  `Foreground` na tmavú, takže `Content="Přidat text"` na modrom tlačidle bolo tmavé.
  Predvolená farba textu ide teraz zo `Style TargetType="Window"` (`Control.Foreground` sa dedí),
  takže prvok, ktorý si nastaví vlastný `Foreground`, správne vyhrá pre svoj obsah.
  **Popupy nededia od okna** — `ComboBox`, `ComboBoxItem`, `MenuItem` a `ToolTip` si `Foreground`
  nastavujú samy, to ich kryje.
- Prepínač (switch) je zrušený, `CheckBox` je implicitne štvorček so zaškrtnutím.
- **Import rastra sa menej pýta:** rýchlost, výkon, průchody a rozestup řádků sú z dialógu von —
  vrstva ich aj tak prepíše pri generovaní (`SceneObject.BuildRasterOutputOptions`), takže
  operátor nastavoval čísla, ktoré úloha ignorovala. Výber tónu (Odstíny šedi / Práh / Stucki) je
  tiež von — **vždy Stucki**. `Lasero.Core` si oba režimy ponecháva.

### `7ce52b5` — port pipeline obrázku z `lasero-app`

`Lasero.Core/Raster/ImageProcessor.cs` prepísaný. Robí **tie isté operácie, v tom istom poradí, s
tými istými vzorcami** ako `applyFilters` v `C:\Users\Ruzovka\Videos\lasero-app\index.html`:

```
per-pixel:  gamma → expozícia → jas → kontrast → svetlá → tiene → levels → clamp → inverzia
kernely:    redukcia šumu (3x3 box blur) → doostrenie (unsharp) → hrany (Laplacian)
nakoniec:   dither
```

**Poradie je to, na čom záleží** — kontrast po jase, nie pred ním. Jednopixelový okraj sa cez
kernely kopíruje nezmenený (ako vo webe), inak by mala každá gravírovaná fotka viditeľný rám.

`DitheringAlgorithm`: pridané `Jarvis`, `Atkinson`, `Sierra`, `Ordered` k `Stucki` a
`FloydSteinberg`. Váhy sú vypísané, nie derivované, aby sa dali čítať proti publikovanej forme.
Atkinson **zámerne** rozptyľuje len 6/8 chyby.

**Jedna zámerná odchýlka**, zakomentovaná na mieste volania: dither má stále prednosť pred prahom,
kým vo webe prah binarizuje skôr. Ditherovanie už binárneho obrázka ho reprodukuje presne, takže
oba postupy sa zhodujú vždy, keď operátor použije jedno alebo druhé — a inak sa ani nepoužívajú,
prah je v oboch vypnutý.

**Každá nová úprava má predvolene „bez zmeny"**, takže sa nič na existujúcom výstupe nepohlo.
15 nových testov v `ImageProcessorTests.cs`.

---

## 4. Rozrobené — pokračovať tu

### 4.1 Port pipeline obrázku — dokončiť UI (rozrobené, jadro hotové)

Jadro (`Lasero.Core.Raster`) je hotové a otestované. **Chýba zapojenie hore:**

1. `Lasero.Core/Import/RasterImportOptions.cs` — pridať `Gamma`, `Exposure`, `Highlights`,
   `Shadows`, `BlackPoint`, `WhitePoint`, `NoiseReduction`, `Sharpen`, `EdgeEnhance`
   (rovnaké predvolby ako v `ImageProcessingOptions`: Gamma 1, WhitePoint 255, ostatné 0).
2. `Lasero.Core/Import/RasterImporter.cs:51` — `ToProcessingOptions(RasterImportOptions)` je
   **jediné** miesto, kde sa jedno prekladá na druhé. Preniesť tam nové polia.
   (Náhľad na plátne ide cez `ProcessedImagePreviewRenderer`, ktorý používa tú istú cestu, takže
   náhľad a G-kód zostanú zhodné automaticky.)
3. `Lasero.App/ViewModels/RasterImportViewModel.cs` — `[ObservableProperty]` pre každú novú hodnotu,
   `partial void On…Changed` → `ScheduleRecompute()`, a v `BuildOptions()` ich poslať ďalej.
4. **Predvolby po nahraní obrázka** (web `loadFile`): `Contrast = 20`, `Brightness = 5`,
   `Sharpen = 30`, dither Stucki, grayscale zap. Web ich nastavuje pri každom nahraní, nie ako
   default property — treba to spraviť rovnako, v momente načítania súboru.
5. `Lasero.App/RasterImportWindow.xaml` — sekcia „Doladění obrazu" už existuje (Jas, Kontrast).
   Pridať tam ostatné posuvníky. **Neverzuj to späť do „Parametry gravírování"** — používateľ
   výslovne chcel, aby parametre gravírovania boli len vo vrstvách.
6. Dither dropdown patrí sem (7 možností vrátane „Žádný"), s popiskom podľa `DITHER_HINTS_CS`
   v `index.html` okolo riadku 16362.

Referencie vo webe (`C:\Users\Ruzovka\Videos\lasero-app\index.html`):
- `loadFile` ~16305, `rstSliders` ~16337, `applyFilters` ~16387
- `applySharpen` ~16472, `applyNoiseReduction` ~16493, `applyEdgeEnhance` ~16509
- `dither` ~16528 (všetkých 6 algoritmov na jednom riadku)
- `DITHER_HINTS_CS` ~16362

### 4.2 Chat — vizuálne a funkčne podľa `lasero-app`

Používateľ poslal dva screenshoty a povedal *„takto by mal fungovat aj ten chat"*. Chce:

- hlavička: avatar KAMIL + meno + zelená bodka + stavová veta („Počítám optimální parametry…")
- riadok `⏱ Historie` (vľavo) a `+ Nový chat` (vpravo, červené primárne)
- chip s kontextom nad konverzáciou (napr. `Kůže + text`, červený, vpravo)
- bubliny: asistent vľavo — biela karta s menom `KAMIL` malým červeným nadpisom;
  používateľ vpravo — plná červená bublina, biely text
- indikátor písania: kurzíva + tri pulzujúce bodky vnútri bubliny
- karta **`DOPORUČENÉ PARAMETRY`**: štyri dlaždice (RYCHLOST mm/min, VÝKON %, PRŮCHODY ×, DPI),
  hodnoty veľké a červené, plus široké červené tlačidlo `Uložit parametry`
- pod ňou „Fungovalo nastavení?" + 👍 / 👎
- návrhy ďalších otázok ako chipy („Jak rychlost ovlivní výsledek?" …)

**Pozor na farbu:** screenshoty sú z webu, ktorý má červený akcent. Desktop má **jednu interakčnú
farbu — kobaltovú modrú** (§2). Buď sa treba používateľa doptať, alebo použiť modrú a červenú
nechať len na nebezpečné akcie. **Nezavádzať červenú ako tretiu UI farbu bez dohody.**

Desktop chat je `Lasero.App/Views/ChatView.xaml`.

### 4.3 Vzorkovník — vizuálne podľa `lasero-app`

Mriežka: os Y rýchlost (pomalšie = tmavšie), os X výkon (%), klik na buňku nastaví parametre.
Vo webe: `renderGallery` ~16159, `selGridCell` ~15573, `showCellRevealAnim` ~15587,
`drawBurn` ~15822 (simulácia vypálenia do dreva).

### 4.4 Offset / Posunout (nezačaté)

Používateľ poslal screenshot LightBurn dialógu „Posunout" a chce ho:

- `Posunout vzdálenost` (mm)
- `Směr`: Ven / Dovnitř / Oba
- `Styl rohu`: Oblý / Kosý / Roh
- `Možnosti`: Pouze vnější tvary / Vybrat výsledné objekty / Odstranit původní objekty /
  Optimalizovat / zjednodušit výsledky

**Ako na to:** WPF nemá offset krivky. `SceneViewModel.UniteSelection` (~716) už používa
`Geometry.Combine(..., GeometryCombineMode.Union, ...)` a `ToImportedShapes` — ten istý pattern sa
dá použiť, ale samotný offset treba spraviť buď cez `Geometry.GetWidenedPathGeometry(new Pen(...))`
s `PenLineJoin` podľa štýlu rohu (dá vonkajší aj vnútorný obrys naraz, potom union/difference), alebo
doťahať Clipper. `GetWidenedPathGeometry` je bez novej závislosti a `PenLineJoin.Round/Bevel/Miter`
mapuje presne na Oblý/Kosý/Roh — **odporúčam to skúsiť prvé.**

### 4.5 Rohové úchopy pre Zkreslit textu (dátový model hotový, ovládanie chýba)

`TextDistortion` + `SceneViewModel.SetSelectedTextDistortionCorner(corner, u, v)` +
`ResetSelectedTextDistortionCommand` **existujú a sú otestované**. Chýba len ovládanie na plátne:
štyri úchopy v `SceneCanvas.DrawSingleObjectHandles`, ktoré pri ťahaní prepočítajú pozíciu myši na
jednotkové súradnice objektu a zavolajú `SetSelectedTextDistortionCorner`.

**Pasca (už nás raz stála čas):** `ResizeHandle` pomenúva roh s **najmenším Y** ako `Top`, ale
plátno kreslí najmenšie Y **dole** (`ToCanvasY` prevracia os). `TextDistortionCorner` je pomenovaný
v **dokumentovom** priestore. Pri akejkoľvek práci s úchopmi si over, či meníš dokumentový alebo
obrazovkový smer.

---

## 5. Otvorené / neriešené

- **SVG import má tú istú hranatosť, akú sme opravili pri texte.**
  `Lasero.Core/Import/Svg/SvgPathParser.cs` má `const int CurveSteps = 16` — pevný počet krokov na
  krivku, nezávislý od veľkosti, takže veľké oblúky sú hranaté.
  `SvgShapeFlattener.cs` má `CircleSteps = 64`. Správne riešenie je subdivízia podľa tolerancie
  tetivy, ale parser pracuje v SVG user units pred transformáciou, takže absolútna mm tolerancia
  tam nie je priamo dostupná — nie je to trojriadková zmena.
- **LightBurn má v pruhu veci, ktoré Lasero nemá** a preto tam nie sú (nevymýšľal som prázdne
  ovládacie prvky): `%` scale polia, 3×3 mriežka ukotvenia pri zmene veľkosti, `H prostor` /
  `Svislá mezera` (prostrkanie a riadkovanie textu), `Přesunout jako skupinu`,
  `Uzamknout vnitřní objekty`.
- `LaseroProjectFile.Version` má default `7`, ale `Deserialize` aj `CreateArchiveSnapshot` ho
  natvrdo nastavia na `6`. Nič `Version` nečíta, takže to nič nelomí — ale je to nekonzistentné.
- Staré publish výstupy `artifacts/` (3,4 GB) a `dist/` (1,1 GB) ležia v repozitári.
  Sú regenerovateľné, `artifacts/` je v `.gitignore`. Dajú sa zmazať.

---

## 6. Bezpečnostná hranica

Prezentácia sa meniť môže, **správanie nie**. Nikdy neupravovať sémantiku Start, Pause, Stop,
Frame, Home, Origin, Jog, Reset, Unlock, súradníc stroja, firmware príkazov ani bezpečnostných
kontrol kvôli vzhľadu.

- Dostupnosť Start/Frame rozhoduje výhradne `CanRun`/`CanFrame` + `JobPreflight`. Dôvod
  nedostupnosti sa **zobrazuje** cez `StartBlockedReason`/`FrameBlockedReason` v tooltipe
  (`ToolTipService.ShowOnDisabled="True"`) — gating sa tým nemení.
- Nikdy nezobrazovať „Ready", kým to appka naozaj nepotvrdila. `JobRunState.Idle` je zámerne
  „Bez úlohy", nie „Připraveno".
- Stav sa nikdy nesmie oznamovať iba farbou — vždy tvar (ikona) + slovo + farba.
- **Zmeny, ktoré v tejto session zmenili generovaný G-kód** (zámerne, obe sú zlepšenia, ale treba
  o nich vedieť): tolerancia flattenovania textu 0,2 → 0,01 mm (`3ff71c2`), a skladanie
  priehľadných PNG na bielu namiesto neinicializovaného povrchu (staršie, `ab776fb`).

---

## 7. Pasce, ktoré nás v tejto session stáli čas

Zapisujem ich, pretože každá vyzerala ako preklep a bola to systémová vec.

1. **WPF ignoruje implicitné štýly deklarované vnútri šablóny.** Pre prvok vytvorený v šablóne sa
   implicitný štýl hľadá len v `Application.Resources`. Preto sa čierne tooltipy nedali opraviť
   pridaním `<Style TargetType="TextBlock">` do `<Border.Resources>` šablóny.
   **A dedenie prehráva Setter štýlu** — `TextElement.Foreground` nikdy neprebije implicitný štýl.
   Implicitný `DataTemplate` sa ale rieši normálnym lookupom a funguje.
2. **`Grid` nezmenší dieťa pod jeho `MinWidth`.** Užší stĺpec panel nestlačí — nechá ho pretiecť
   za okraj okna. Ak niečo „visí mimo obrazovky", hľadaj nesúlad `MinWidth`.
3. **`StackPanel` meria deti na nekonečnú šírku**, takže `TextWrapping` sa v ňom nikdy nespustí a
   dieťa si vezme prirodzenú šírku namiesto stĺpca. Používaj `DockPanel`.
4. **`CornerRadius` 6 na 18px prvku vyzerá ako kruh.** Na malé prvky `Radius.Xs` (4).
5. **Per-shape animácie na overlayi, ktorý sa prekresľuje**, sa reštartujú na nulu — animuj jednu
   property na rodičovi a binduj.
6. **Atkinson dither nie je „vždy svetlejší"** — zahodená chyba je pri svetlých tónoch pozitívna
   (výsledok svetlejší) a pri tmavých negatívna (výsledok tmavší). Tvrdenie platí pre svetlá.
