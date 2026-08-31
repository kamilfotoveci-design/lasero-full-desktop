# Lasero Desktop — handoff

Stav k 31. 8. 2026. Tento súbor existuje preto, aby sa dalo v novom chate pokračovať bez
prečítania celej histórie. Popisuje **kde to je, čo sa spravilo, čo je rozrobené a čo ďalej**.

---

## 1. Kde projekt je

**`E:\lasero-desktop`** — nie na `C:`.

Projekt bol 27. 8. 2026 presunutý z `C:\Users\Ruzovka\Videos\lasero-desktop`, pretože `C:` mal
0,5 GB voľných. **Disk `D:` na tomto stroji neexistuje** (sú len `C:`, `E:`, `F:` = CD-ROM).
`E:` má ~329 GB voľných.

Vetva: `design-system-tokens`. Posledný commit: `ce104ca`.

```bash
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug
```

```bash
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln
```

Spustiteľný build: `E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe`

**282 testov, všetky prechádzajú.** Release build čistý.

### Vizuálne overovanie
Je to WPF, nie web — browser tooling neplatí. V `.uiqa/` sú pomocné skripty:

- `shot.ps1 -Out x.png` — screenshot okna
- `crop.ps1 -In a.png -Out b.png -X .. -Y .. -W .. -H .. -Scale ..` — výrez a zväčšenie
- `click.ps1 -X .. -Y ..` / `drag.ps1 -X1 .. -Y1 .. -X2 .. -Y2 ..` — syntetický vstup (súradnice sú relatívne k oknu)
- `resize.ps1 -W .. -H .. -X .. -Y ..` — veľkosť okna
- `ui.ps1 -Action click -Name "…"` — klik cez UI Automation podľa prístupného mena (spoľahlivejšie než súradnice)

Po nečistom ukončení appky nabehne dialóg **„Nalezena záloha projektu"** — treba ho odkliknúť
(`ui.ps1 -Action click -Name "Zahodit zálohu"`), inak screenshot zachytí dialóg.

Na testovanie pripojenia netreba hardvér: v *Zařízení* je port **`SIMULÁTOR — Virtuální laser`**,
ktorý neotvára fyzický port ani nezapína laser.

---

## 2. Vizuálny smer (rozhodnuté používateľom, needitovať bez dohody)

Neutrálny grafit + soft white + kobaltová modrá. **Jedna interakčná farba.** Oranžová ako
druhý akcent je zrušená — modrá `#2563EB` znamená vybrané/aktívne/primárne všade: položka
navigácie, nástroj, prepínač, focus ring, výber na plátne, Spustit.

Rozpočet farieb: ~90 % neutrál, 8 % modrá, 2 % sémantická zelená/oranžová/červená.
Červená je len pre nebezpečné akcie a bodku v logu — nie je to tretia UI farba.

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

**Typografia: Inter**, zabalený v aplikácii (`Lasero.App/Assets/Fonts`, SIL OFL). Nie je to
Windows font — odkaz len menom by ticho spadol na Segoe, preto je súčasťou buildu ako `<Resource>`.
Váhy: 400 text, 500 ovládacie prvky a hodnoty, 600 nadpisy a vybraná navigácia.
Bežný text 13–14 px, **nič pod 12 px**.

**Čísla nie sú monospace.** Používa sa Inter s `Typography.NumeralAlignment="Tabular"` —
zarovnané stĺpce bez terminálového vzhľadu. Skutočný monospace zostal len pre GRBL konzolu
a G-kód, kde poloha v stĺpci nesie význam.

Tokeny sú centralizované v `Lasero.App/Theme/LaseroTheme.xaml` (`Brush.*`, `Radius.*`,
`Size.Control.*`, `Size.Text.*`, `Size.Icon.*`) a `Theme/SharedUiStyles.xaml`.
**Test `ThemeTokenTests.NoMarkupCarriesALiteralFontSize` zakazuje literálne `FontSize` v XAML** —
nová veľkosť musí byť token.

---

## 3. Čo sa v tejto session spravilo

Tri commity nad `69fcf49`.

### `ab776fb` — maximalizácia, raster, brand assety, text

- **Maximalizované okno orezávalo spodnú lištu.** Bezrámové WindowChrome okno sa maximalizuje na
  rozmery monitora nafúknuté o resize okraj; `Rámovat`/`Spustit` končili za taskbarom. Nový
  `MaximizeWorkAreaHook.cs` odpovedá na `WM_GETMINMAXINFO` pracovnou plochou monitora, na ktorom
  okno naozaj je.
- **Bitmapa sa kreslila ako plochý sivý blok** — dve nezávislé príčiny:
  - Dithering je zapnutý a *správne* dáva dvojúrovňový obraz (dióda je jednobitová), ale na plátne
    sa vzor spriemeruje. Plátno teraz volá `ProcessedImagePreviewRenderer.RenderFileForCanvas`,
    ktoré vypne dithering. Dialóg importu a G-kód sa nezmenili.
  - **`BitmapLoader.LoadGrayscale` skladal obrázok na neinicializovaný 32bpp povrch a ignoroval
    alfu**, takže priehľadný pixel čítal ako čierny = plný výkon. Logo s priehľadným pozadím by
    stroj vypálil ako plnú plochu. Teraz sa skladá na bielu. **Toto mení G-kód pre priehľadné PNG.**
- **Text**: `VectorTextStyle` (rodina, bold, italic), výber fontu s náhľadom, zapamätaná posledná
  voľba. Predtým natvrdo Segoe UI Regular.
- **„Scéna"** bol interný názov dokumentu pretekajúci do UI ako názov projektu → `SceneJobLabel`
  = „Návrh na plátně", a `HasNamedJobFile` skryje chip so súborom, keď žiadny súbor nie je.
- **Assety**: wordmark bol malý ostrovček v prázdnom 3840×2160 plátne (odtiaľ „miniatúrne a
  rozmazané") → orezaný na obsah, 660×205. Avatar bol portrét 1387×1914, ktorý `UniformToFill`
  rezal cez hlavu → zoštvorcovaný okolo hlavy, 256×256, `BitmapScalingMode="HighQuality"`.
- Stavové „pilulky" stratili plne guľatú geometriu (vyzerali ako stlačiteľné tlačidlá).

### `ce104ca` — kurzory, orezané vety, duplicity

- **Kurzory na úchytoch boli zrkadlovo prehodené.** `ResizeHandle` pomenúva roh s **najmenším Y**
  ako `Top`, ale plátno kreslí najmenšie Y **dole** (`ToCanvasY` prevracia os). Úchyt vykreslený
  vľavo dole tak dostal kurzor „vľavo hore". Týka sa to len diagonál. **Toto je pasca — pri
  akejkoľvek práci s úchytmi si over, či meníš dokumentový alebo obrazovkový smer.**
- **Rotačný úchyt visel pod objektom** z tej istej príčiny → kotví na opačnom úchyte. Rotácia
  samotná sa nemenila (uhol je delta pohybu myši voči pivotu).
- **Päť orezaných viet.** Vždy ikona + zalamovaný `TextBlock` vo vodorovnom `StackPaneli`.
  **`StackPanel` meria deti na nekonečnú šírku, takže `TextWrapping` sa nikdy nespustí.**
  Všetky prepísané na `DockPanel`. Ak sa objaví ďalšia oseknutá veta, príčina bude tá istá.
- Blok „Připravený návrh" v strojovom paneli opakoval to, čo má spodná lišta → odstránený.
- Dvojitý názov vrstvy v inšpektore → z druhého výskytu je nadpis sekcie (`InlineTitleInput`).

---

## 4. Rozrobené — pokračovať tu

### Rozmery (X / Y / Š / V) sú v toolbare natlačené

**Toto je aktívna úloha.** Používateľ postupne žiadal: (1) dostať rozmery z vlastného pruhu hore
medzi nástroje, (2) potom hlásil, že sú orezané, (3) potom že je toolbar natlačený a či to
nevieme zmenšiť alebo navrhnúť niečo iné.

Zmenšovanie polí sa raz vyskúšalo a **zlyhalo** — pri šírke 82 px sa dvojdesatinné hodnoty
orezávali. Teraz sú na 96 px a nič sa neoreže, ale riadok sa zalamuje.

Prečo sa to nezmestí: obsah riadka má ~1 500 px, stĺpec plátna má pri okne 1600 px len 1 082 px
a pri 1920 px stále len 1 402 px. **Na jeden riadok sa to nevojde ani na 4K s otvoreným
inšpektorom.**

**Navrhnuté a odsúhlasené riešenie, ktoré sa nestihlo dokončiť:** presunúť blok
poloha/veľkosť/rotácia + akcie objektu do **pravého inšpektora**, ako to má Illustrator, Figma
aj LightBurn. Toolbar zostane čistý rad nástrojov.

Dôležitý detail návrhu: sekcia musí byť **nad** prepínačom `Vrstvy | Stroj`, aby bola viditeľná
bez ohľadu na to, ktorá záložka je aktívna. V 336 px inšpektore sa pohodlne vojdú dva stĺpce
(riadok `X | Y`, riadok `Š | V` + zámok pomeru, riadok rotácia).

Čo treba spraviť:
1. Z `Lasero.App/MainWindow.xaml` vybrať z toolbarového `WrapPanel`u: oddeľovač + `SelectionSummary`
   + `WrapPanel` s poľami + `StackPanel` s akčnými ikonami.
2. Vložiť ich ako novú `RowDefinition` na vrch `Lasero.App/Views/DesignerInspectorView.xaml`
   (aktuálne má riadky `Auto` = tab strip, `*` = obsah).
3. Handler `OnTransformFieldKeyDown` presunúť do code-behind inšpektora (je krátky a samostatný —
   commituje binding na Enter a označí text).
4. Toolbarový `WrapPanel` môže ísť späť na `StackPanel` a `Border` späť na `Height="46"`.

### Nedopovedaná výhrada

Používateľova posledná správa bola useknutá v polovici: *„Taktiež tu mi vadi že"* — priložený bol
screenshot spodnej lišty. **Treba sa doptať, čo tým myslel**, než sa tam bude čokoľvek meniť.

---

## 5. Otvorené / neriešené

- **Port webovej aplikácie** z `C:\Users\Ruzovka\Videos\lasero-app` do desktopu — používateľ o to
  požiadal, potom to prekryl dvomi UI zadaniami. **Zaparkované, nezrušené, nezačaté.**
- Text sa po vložení sploští na krivky, takže **font sa už nedá dodatočne zmeniť**. Skutočný
  editovateľný textový objekt je väčšia zmena a nie je v pláne.
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
  „Bez úlohy", nie „Připraveno" — inak odpojená appka hlásila „Nepřipojeno · Připraveno".
- Stav sa nikdy nesmie oznamovať iba farbou — vždy tvar (ikona) + slovo + farba.
