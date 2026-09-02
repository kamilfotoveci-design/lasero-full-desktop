# Lasero Desktop — handoff

Stav ku **2. 9. 2026, popoludnie**. Píšem to pre pokračovanie **v Codexe** — session v Claude Code
bola veľmi dlhá (stabilizácia → UI recovery → 3-agentový KAMIL rebuild → typografia + ikony
rozbehnuté) a treba odovzdať bez straty kontextu. Predchádzajúca verzia handoffu (ráno 1. 9., HEAD
`81d80e7`) je stále v histórii — commit `853b24f` ju nahradil aktuálnym stavom nižšie.

---

## 0. Najdôležitejšie — over toto ako prvé

```bash
cd /e/lasero-desktop && git status --short   # čo je rozrobené, necommitnuté
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug   # musí byť 0/0
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln             # baseline pred KAMIL Phase 4: 389/389
```

**Zabi appku pred buildom, drží zamknutý `.exe`:**
```bash
powershell -NoProfile -Command "Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force"
```

Vetva `design-system-tokens`, HEAD `2c5dcbf`. **Commitnuté aj rozrobené kroky KAMIL rebuildu**
(§2) — commitol som to takto zámerne, aby handoff začínal z čistého, buildovateľného stromu
(0 warnings, 389/389 testov v momente commitu). Ak `git status` ukazuje iné súbory, práca
pokračovala po tomto zápise; ber skutočný `git log`/`git diff` ako pravdu, nie tento súbor.

---

## 1. Čo je hotové a commitnuté (`853b24f`, `2c5dcbf`)

Dve veci naraz, jeden commit:

1. **Prevzatý veľký rozrobený redesign**, ktorý ležal necommitnutý v strome: kompaktný nástrojový
   rail v Designeri (`DesignerToolRail`), plávajúca kontextová `SelectionPropertiesBar` (namiesto
   celoškej lišty), panel stroja vytiahnutý z inšpektora do vlastného okna
   (`MachineControlWindow`), KAMIL rozdelený do `Views/Kamil/*`, oprava hover/press animácie (dve
   nezávislé vrstvy namiesto jednej, ktorá vedela zostať „zaseknutá" tmavá po kliku, čo otvoril
   okno), a nová funkcia **hold-to-fire positioning laser** (nízkovýkonový laser na polohovanie,
   `JogViewModel`).
2. **Opravená skutočná bezpečnostná diera**, ktorú tento redesign priniesol: positioning laser
   hlási GRBL stav `Idle` rovnako ako vypnutý stroj, takže Jog/Home/SetOriginHere/GoToWorkZero
   zostávali spustiteľné, kým bol lúč fyzicky zapnutý. Pridané `CanManualMotionWithLaserOff()`,
   zapojené do všetkých pohybových príkazov, regresný test
   `MotionCommandsAreBlockedWhileThePositioningLaserIsLit`.

Vedľajšie upratovanie v tom istom commite: 5 osirotených zoom-click handlerov zmazaných z
`MainWindow.xaml.cs` (skutočná implementácia je teraz v `CanvasViewControls.xaml.cs`), nepoužívaný
`IsBelowWidth` converter resource odstránený z `MainWindow.xaml` (samotná trieda
`IsBelowWidthConverter.cs` **zostala** — používateľ zamietol jej zmazanie pri jednom `rm`, nechaj
tak, kým sám nepovie inak), `TextToolWindow.Style` premenované na `TextStyle` (tienilo
`FrameworkElement.Style`, CS0108 warning).

**Baseline v tomto commite: build 0/0, testy 353/353.**

### 1.1 Následná „UI recovery" prechádzka (commitnuté v `2c5dcbf`)

Po `853b24f` prebehla živá vizuálna kontrola appky (viď §3, `.uiqa/` toolkit už funguje, appka sa
spúšťa priamo zo session.dat bez loginu). Nájdené a opravené:

- **Ruler v Designeri orezával posledný label** (`"520"` → `"52"`) — `WorkspaceCanvas.xaml.cs`
  (moja prvá, čiastočná oprava) aj **`SceneCanvas.xaml.cs`** (skutočne používaný live kontrol,
  opravu doplnil live-testing agent — koreň bol, že label sa meral pred pridaním do stromu bez
  nastaveného `FontFamily`, takže `DesiredSize` podhodnotil reálnu šírku).
- **`SelectionPropertiesBar` pretekala aj pri plnej šírke okna**, nielen na 1080px floor ako
  hovoril pôvodný komentár — keď je vybraný text, celý riadok (Poloha+Rozměr+Otočení+Text cluster
  ~370px+Uspořádání+Přetečení) presahuje dostupnú šírku canvas stĺpca. Oprava: **Align/Flip menu sa
  skryje, keď je vybraný text** (`Scene.IsTextSelected` → `InverseBoolToVisibility`), rovnaké
  príkazy sú duplikované do overflow „…" menu. Pri 1480/1920px teraz nepreteká vôbec, pri 1366px
  floor ešte občas ukáže scrollbar pre Bold/Italic/overflow — akceptované ako zvyškový P2 (pôvodný
  komentár v súbore to aj tak volá „safety net, not normal state").

Súbory: `WorkspaceCanvas.xaml.cs`, `SceneCanvas.xaml.cs`, `SelectionPropertiesBar.xaml` (plus nová
`InverseBooleanToVisibilityConverter` resource entry tamže).

---

## 2. KAMIL rebuild — rozrobené, TOTO je hlavná nedokončená vec

Zadanie prišlo v troch vlnách (užívateľ postupne sprísňoval požiadavky), použil sa **3-agentový
tímový postup** cez custom subagentov nainštalovaných v `.claude/agents/` (pozri §5 — **Codex má
tieto persony prečítať a prevziať ich rozdelenie zodpovednosti**, nie ich mechanicky kopírovať ako
konfiguráciu, lebo Codex nemá rovnaký subagent mechanizmus ako Claude Code).

### 2.1 Rozhodnutý cieľový dizajn (nemeň bez dôvodu — je to finálna špecifikácia od ui-designer fázy)

**Minimalizovaný stav KAMILa je LEN avatar/hlava — kruh 48px, žiadny text, žiadna pilulka.**
Predchádzajúci dizajn (176×48 pilulka s "KAMIL"/"AI asistent" textom) je **zamietnutý používateľom**
explicitne. Presné čísla:

| stav | rozmery | anchor |
|---|---|---|
| Minimized | 48×48 kruh, `Size.Icon.Xxl` token, `Radius.Pill` | rovnaký bottom-right bod ako doteraz |
| QuickAsk | 400×132 (bolo 440×156) | rastie z rovnakého bodu (`RenderTransformOrigin="1,1"`) |
| Expanded | 420×(500–640 adaptívne, bolo 340–640) | rastie z rovnakého bodu, hlavne nahor |

**Prechody** (všetky `Ease.Out`, žiadny bounce/overshoot): Minimized→QuickAsk 210ms,
QuickAsk→Minimized 210ms, QuickAsk→Expanded 240ms, Expanded→Minimized 210ms.
**Expanded→QuickAsk zámerne NEEXISTUJE** — Expanded sa vždy zmenšuje rovno na Minimized (cez
Minimize tlačidlo alebo Esc). Minimized→Expanded priamo tiež neexistuje — klik na avatar vždy
otvorí najprv QuickAsk.

**Skutočný root-cause pôvodného hláseného bugu** ("Expanded sa nedá vrátiť späť"): `MinimizeCommand`
**fungoval správne** už predtým (jeden krok, hocikedy). Reálny problém: **Close vyzeral vizuálne
identicky ako Minimize** (obe malé bezpopisné ghost ikonové tlačidlá) a Close vedie do stavu
`Hidden`, z ktorého **`ShowCommand` nebol nikde v UI napojený** — potvrdené grepom, nula miest
volania. Operátor klikol Close namiesto Minimize a KAMIL zmizol natrvalo do reštartu appky.

Oprava (rozdelená medzi backend-architect a frontend-developer fázy):
- **Close dostáva vlastný vizuál** — prevziať `Button.ChromeClose` (rovnaký štýl ako titulková
  lišta okna) namiesto `Kamil.HeaderButton`, 8px medzera od New Chat/Minimize páru.
- **`ShowCommand` sa naviaže** na nav rail tlačidlo „Lasero Chat" (Kamil avatar v ráile) — pri kliku
  má spustiť aj navigáciu na Chat obrazovku aj `ShowCommand` ak je `State == Hidden`.
- **`StepBack()` (Esc) prerobený** aby skákal rovno na Minimized z hocijakého stavu (bolo:
  Expanded→QuickAsk→Minimized, dva stlačenia Esc) — teraz symetrické s tlačidlom Minimize.

**Ďalší potvrdený, nezávislý bug**: `AvailableExpandedHeight()` v `KamilAssistantHost.xaml.cs`
meria `ActualHeight` hostiteľa **predtým, ako sa prekreslí** (číta starú veľkosť z predošlého
stavu), plus má **druhú, nezávislú** hardcoded konštantu `ReservedVerticalChrome = 24` navrch
existujúceho `AssistantClearanceConverter` marginu — dva rôzne zdroje "koľko miesta dole
rezervovať" v dvoch súboroch. Oprava: zmazať `ReservedVerticalChrome`, jediný zdroj clearance je
`AssistantClearanceConverter`.

**Kolízia so `CanvasViewControls`** (zoom/undo klaster, dolný pravý roh canvasu): KAMIL dnes sedí s
takmer nulovou medzerou vedľa/nad tým klastrom (rovnaký pravý okraj, takmer rovnaký spodný okraj) —
vyzerá to ako jeden súvislý pruh. Oprava: `AssistantClearanceConverter` sa mení z
`IValueConverter` na **`IMultiValueConverter`** s druhým vstupom `CanvasViewControls.ActualHeight`,
výstup `Thickness(0, 0, 20+inspectorWidth, 16+canvasControlsHeight+16)`. Binding v `MainWindow.xaml`
treba prerobiť z `Binding`+`Converter` na `MultiBinding` s dvomi `Binding`.

Plný spec (vizuálne stavy hover/pressed/focus/unread indicator, presné hex/token hodnoty,
zdôvodnenia) je v transcript výstupe ui-designer agenta z tejto session — **nie je uložený ako
súbor**, len v histórii chatu. Ak sa stratí, treba ho odvodiť znova z tejto tabuľky + princípov v
§3 nižšie (Apple-like disciplína, žiadny glow/gradient/glassmorphism).

### 2.2 Presný stav implementácie PRÁVE TERAZ

Commitnuté v `2c5dcbf` (build bol 0/0, testy 389/389 **v momente commitu**):
```
Lasero.App/Controls/SceneCanvas.xaml.cs              (ruler fix, pozri §1.1)
Lasero.App/Controls/WorkspaceCanvas.xaml.cs          (ruler fix, pozri §1.1)
Lasero.App/Converters/AssistantClearanceConverter.cs (KAMIL: MultiValueConverter)
Lasero.App/MainWindow.xaml                           (KAMIL: MultiBinding + ShowCommand na nav rail)
Lasero.App/MainWindow.xaml.cs                        (KAMIL: click handler pre dve commandy naraz)
Lasero.App/ViewModels/ChatViewModel.cs               (backend fáza: SelectedLayerIdProvider)
Lasero.App/ViewModels/KamilAssistantViewModel.cs     (backend fáza: StepBack, ShowCommand guard, staleness guard)
Lasero.App/ViewModels/ParameterRecommendation.cs     (backend fáza: OriginLayerId)
Lasero.App/Views/Kamil/KamilAssistantHost.xaml       (frontend fáza: avatar-only, geometria, Close štýl)
Lasero.App/Views/Kamil/KamilAssistantHost.xaml.cs    (frontend fáza: AvailableExpandedHeight fix, animácie)
Lasero.App/Views/SelectionPropertiesBar.xaml         (pozri §1.1, nesúvisí s KAMILom)
Lasero.Tests/KamilAssistantViewModelTests.cs         (nové, backend fáza)
Lasero.Tests/ParameterRecommendationTests.cs         (nové, backend fáza)
```

**Backend-architect fáza (3) je hotová a overená**: 36 nových testov (state transitions,
zachovanie konverzácie cez minimize/close/reopen, staleness guard pre Apply,
`ParameterRecommendation.TryParse` edge cases), všetky prechádzajú.

**Frontend-developer fáza (4) DOKONČENÁ a živo overená** (report prišiel po tom, čo bol tento
súbor prvýkrát napísaný — toto je opravená, finálna verzia). Build 0/0, testy 389/389 nezmenené.
Živo overené cez `.uiqa/` (nie len prečítané v kóde):

- **Minimized = 48px kruh** (`Size.Icon.Xxl`), žiadny text/pilulka/pozadie. Hover → scale 1.04 +
  1px `Brush.PanelBorderStrong` ring, pressed → 0.96, `FocusRing.Pill` znovupoužitý. Overené
  screenshotom s reálnym hoverom myšou.
- **QuickAsk 400×132** — obsah (`ContextBar`+`Composer`+chips) sa pri týchto rozmeroch **musel
  zúžiť** (Grid margin 12→8, ContextBar bottom margin 9→6, chip-row top margin 9→6), inak
  pretekal/orezával chipy o pár pixelov. **Toto je odchýlka od pôvodnej špecifikácie** ("obsah
  nezmenený") — nutná, lebo 400×132 na pôvodné rozostupy nestačilo. Vizuálne funguje (screenshot
  potvrdený), ale stojí za rýchlu kontrolu, či zúžené rozostupy ešte vyzerajú dobre.
- **Expanded 420×(500–640) — `AvailableExpandedHeight()` fix naozaj funguje**, overené naživo
  zmenou veľkosti bežiaceho okna medzi 900px a 768px výškou počas otvoreného Expanded stavu: panel
  reálne zmenil výšku (~635px vs ~585px). Koreň bugu bol dvojitý: (1) `Root.Margin` bolo vždy
  nulové (skutočný margin sa aplikuje o úroveň vyššie, na samotný `UserControl` v
  `MainWindow.xaml`), (2) `ActualHeight` hostiteľa bolo **самореferenčné** — Host je
  `HorizontalAlignment="Right" VerticalAlignment="Bottom"` (size-to-content, nie stretched), takže
  jeho `ActualHeight` bolo len aktuálna veľkosť Surface, nie skutočný dostupný priestor. Oprava:
  číta sa `ActualHeight` **rodičovského Gridu** (editor row `*` medzi 60px title a 48px machine
  strip) cez `Parent as FrameworkElement`, bottom reservation je `this.Margin.Bottom` (Hostov
  vlastný margin). `ReservedVerticalChrome` zmazané.
- **Close vs Minimize vizuálne odlíšené** — Close teraz `Button.ChromeClose` (28×28, 8px margin od
  páru New Chat/Minimize), overené hoverom → červená danger farba ako na titulkovej lište.
- **Reopen funguje end-to-end**: Close → avatar zmizne → klik na nav rail „Lasero Chat" → naviguje
  na Chat obrazovku AJ zavolá `Kamil.ShowCommand` (nová `OnLaseroChatClick` v `MainWindow.xaml.cs`,
  keďže `MainViewModel.cs` bol mimo scope tejto fázy) → návrat na Home → avatar (Minimized) sa
  znova zobrazí. Celý kolobeh overený naživo.
- **Kolízia s `CanvasViewControls` vyriešená** — `MultiBinding` funguje, overené na Home (avatar
  mimo machine strip) aj pri 1366×768 (avatar mimo obsahu, žiadna kolízia).

**Dva nálezy nechané pre ďalšie kolo (neboli opravované v tejto fáze, mimo scope):**

1. **QuickAsk rozostupy zúžené** (viď vyššie) — ui-designer by mal skontrolovať, či to ešte sedí s
   vizuálnym jazykom.
2. **`Image.KamilAvatar` bitmapa má vpečený zaoblený-štvorcový okraj/pozadie v zdrojovom súbore.**
   Pri pôvodných 28–30px veľkostiach to nebolo vidieť; pri 48px, kde je celý Minimized povrch
   tvorený týmto obrázkom orezaným do Ellipse, je nesúlad medzi hranou obrázka a kruhovým orezom
   viditeľný. **Toto je problém so zdrojovým assetom, nie s kódom** — treba čisto kruhovo
   orezaný (alebo full-bleed štvorcový) zdrojový obrázok pre avatar, aby vyzeral čisto pri 48px.

Žiadny nový test nepridaný v tejto fáze — reopen-wiring je v code-behind proti živému `Window`,
mimo existujúcich testovacích vzorov projektu; overené naživo namiesto toho (súhlasí s pôvodným
zadaním, ktoré presne pre tento prípad live-UI overenie povoľovalo namiesto testu).

**Zostávajúce fázy KAMIL workflow (nespustené)**:
- Fáza 5 — ui-designer review implementácie, hlavne tie dva nálezy vyššie (QuickAsk rozostupy,
  avatar bitmapa)
- Fáza 6 — frontend-developer opravuje nálezy z fázy 5 (najmä nahradenie/orezanie avatar bitmapy —
  to je asset práca, nie kód, môže vyžadovať používateľa ak niet zdroja na kruhový orez)
- Fáza 7 — backend-architect verifikuje state/session integritu po UI zmenách (frontend-developer
  už poznamenal, že session persistence naprieč reštartom appky funguje nezmenené, ale formálne
  overenie fázy 7 neprebehlo)
- Fáza 8 — finálny build/test + finálny report vo formáte, ktorý si používateľ vyžiadal (viď jeho
  posledná KAMIL správa — má presnú šablónu "# KAMIL FLOATING ASSISTANT REPAIR REPORT")

---

## 3. Vizuálny smer (nezmenené, stále platí)

**Svetlý režim.** Neutrálny grafit + soft white + kobaltová modrá `#2563EB`, jedna interakčná
farba. Zdroj pravdy: `Lasero.App/Theme/LaseroTheme.xaml`, vysvetlenie `DESIGN.md`.

- Hover = neutrálny wash, nikdy neprevezme kobalt (to je len pre selected/active).
- `Radius.Sm`(6) ikonové tlačidlá, `Radius.Md`(8) tlačidlá/vstupy, `Radius.Lg`(12) panely/popupy,
  `Radius.Pill` len skutočné kruhy.
- Ikony: jedna rodina, 24×24 grid, stroke `Size.Icon.Stroke`(1.75), round cap/join. **Toto sa
  čoskoro mení** — pozri §4.3, prebieha migrácia na Iconoir.
- Nič pod 12px FontSize, žiadny literál v XAML (`ThemeTokenTests.cs` to stráži).

Apple-like disciplína pre KAMIL a novú typografiu: pár konzistentných veľkostí, silná hierarchia,
sebavedomé stredné hmotnosti, kompaktné ovládanie, zdržanlivý bold, žiadna dekorácia, žiadne
obrovské nadpisy.

---

## 4. Ďalšie zadané, ešte nespustené/rozbehnuté úlohy

Používateľ zadal tri ďalšie veľké úlohy počas tejto session, **v tomto poradí, každá čaká na
predchádzajúcu kvôli konfliktu v tých istých zdieľaných súboroch** (`LaseroTheme.xaml`,
`SharedUiStyles.xaml`, `Icons.xaml`, `MainWindow.xaml`, KAMIL views):

### 4.1 Neue Montreal typografia

**Fáza 1 (detekcia fontu) je hotová a overená dvomi API** (GDI+ `InstalledFontCollection` aj WPF
`Fonts.SystemFontFamilies` cez DirectWrite):

- **Presný WPF FontFamily string: `"Neue Montreal"`** — jedna rodina, WPF ju vidí správne
  zjednotenú (na rozdiel od GDI+, ktoré ju vidí ako 4 samostatné pseudo-rodiny).
- **Reálne nainštalované hmotnosti**: Light(300), Normal/Regular(400), Medium(500), Bold(700) —
  každá skutočný samostatný obrys (súbory `neuemontreal-{regular,medium,bold,light}.otf` +
  kurzívy, v `%LOCALAPPDATA%\Microsoft\Windows\Fonts`, per-user inštalácia nie systémová).
- **SemiBold/Demi(600) NEEXISTUJE** — ani ako súbor, ani vo WPF enumerácii. Použitie
  `FontWeight="SemiBold"` by spadlo do nedefinovaného/nepotvrdeného matchovacieho správania WPF.
  **Mapuj koncepčný "SemiBold" tier (nadpisy, navigácia, dôležité hodnoty) na skutočný Bold(700)
  font-weight explicitne** — nie na FontWeight enum hodnotu SemiBold.

Zvyšok úlohy (audit existujúcej typografie, centrálny `LaseroFontFamily` resource, sémantické
štýly ako `PageTitle`/`SectionTitle`/atď., fallback reťazec, responsive/DPI QA, ui-designer review)
**nezačaté**. Presné zadanie s cieľovou škálou (11/12/13/14/16/20, presné mapovanie na obrazovky
Home/Designer/Materials/Settings/KAMIL) je v histórii chatu — veľmi detailné, oplatí sa ho
znovu-prečítať celé, nie parafrázovať.

### 4.2 Iconoir + custom Lasero SVG ikony

**Fáza 1 (audit súčasného stavu) hotová**: mechanizmus je `Icons.xaml` (83 `Geometry` resources,
kľúč `Glyph.<Name>`, 24×24 grid) + `IconGlyph` control (`Path.Data` = geometria, `Path.Stroke`
dedí `Foreground`, `StrokeThickness` = `Size.Icon.Stroke` token) — **tento mechanizmus sa má
znovupoužiť, nie nahradiť**. 16 z 83 ikon je nepoužívaných (menovite v transcript výstupe, grep
potvrdené). Niekoľko nekonzistentných hardcoded veľkostí/strokes mimo `IconGlyph` nájdených
(shípka combobox 1.6 vs globál 1.75, title-bar Minimize 16px vs Maximize/Close 12px, atď.).

**Fáza 2 (overenie oficiálneho zdroja Iconoir) hotová**:
- Repo: `github.com/iconoir-icons/iconoir`, licencia **MIT** (overené priamym fetchom LICENSE
  súboru), aktuálny release `v7.12.1` (12. 8. 2026), aktívne udržiavané.
- SVG žijú v `/icons/regular/` (hlavná, ~1600+ ikon, stroke štýl) a `/icons/solid/` (len 256 ikon,
  filled štýl, čiastočná podmnožina — nie každá regular ikona má solid variant).
- Overená špecifikácia (fetchnuté reálne súbory `home.svg`, `settings.svg`, `link.svg`,
  `undo.svg`): `viewBox="0 0 24 24"`, `stroke-width="1.5"`, `stroke-linecap="round"`,
  `stroke-linejoin="round"`, `fill="none"` na root, `stroke="currentColor"` na každej `<path>`.
  **Toto je veľmi blízke súčasnému Lasero systému** (24×24 grid, round cap/join) až na
  `stroke-width` (Iconoir 1.5 vs Lasero token 1.75) — treba sa rozhodnúť, či prebrať Iconoirovu
  1.5 alebo si nechať 1.75 a mierne poupraviť SVG pri importe (spec to nechá na frontend-developer
  fázu, obe voľby sú obhájiteľné).
- **Nepotvrdené presné názvy súborov** pre save/delete/refresh/lock/group/flip-horizontal/atď. —
  pri budovaní mapovacej tabuľky (§ Phase 4 v zadaní) treba dotazovať GitHub Contents API pre
  `icons/regular/`, nehádať kebab-case názvy naslepo (viacero intuitívnych názvov ako `home-alt`
  neexistuje).

Zvyšok úlohy (fázy 3–10: stiahnutie assetov, mapovacia tabuľka, custom laser ikony ako
Frame/Engrave/Positioning Laser/Work Origin, WPF integrácia, DPI QA, cleanup starých ikon)
**nezačaté**.

---

## 5. Custom subagenti — čítaj a preber, nie kopíruj mechanicky

V tejto session boli nainštalované tri custom subagent persony (stiahnuté zo subagents.cc, overený
bezpečný obsah pred uložením) do `C:\Users\Ruzovka\Videos\.claude\agents\` (**pozor: nie v
`E:\lasero-desktop`**, ale v pracovnom adresári Claude Code session):

- `frontend-developer.md` — WPF/XAML implementačná perspektíva
- `ui-designer.md` — UX/vizuálny dizajn, hierarchia, interakcia
- `backend-architect.md` — stavové/aplikačné архитектúra, MVVM, testovanie

Celý KAMIL rebuild (§2) bol robený **explicitným zadaním používateľa** cez tento trojicový postup:
paralelný read-only audit → ui-designer špecifikácia → backend-architect oprava logiky →
frontend-developer implementácia → review → fix → verifikácia. Používateľ výslovne trval na tom,
že "Do not perform the whole task yourself" — každá fáza musí ísť cez pomenovaného agenta.

**Codex nemá rovnaký `subagent_type` mechanizmus ako Claude Code** (Task tool s pomenovanými
personami). Ak chceš zachovať rovnaké rozdelenie zodpovednosti a kvalitu bez prerušenia (aby si sa
nemusel pýtať používateľa "ako mám nastaviť agentov"):

1. **Prečítaj si tie tri `.md` súbory priamo** (`C:\Users\Ruzovka\Videos\.claude\agents\*.md`) —
   obsahujú detailný popis zodpovednosti, prístupu a princípov pre každú rolu.
2. Keď zadanie/pokračovanie vyžaduje "ui-designer" prácu, **prepni sa do tej perspektívy sám**
   (alebo spusti vlastný subprocess/plán s tým promptom ako system kontextom) — nečakaj, že
   Claude-Code-špecifický `subagent_type: "ui-designer"` bude fungovať v Codexe, nebude.
3. Rovnaké odporúčanie pre `.agents/plugins/lasero-*` (Antigravity formát, `E:\lasero-desktop\.agents\plugins\`) —
   sú tam doménové pravidlá (kto vlastní ktoré súbory, aké sú bezpečnostné limity pre
   `lasero-machine`) written for a different tool's plugin schema, ale obsah pravidiel je
   univerzálne platný a stojí za prečítanie, hlavne `.agents/rules/AGENTS.md` (project-wide rules,
   nezávislé od nástroja) a `.agents/plugins/lasero-lead/rules/AGENTS.md`.

Skrátka: **neinštaluj cudziu subagent konfiguráciu do Codexu naslepo** — prečítaj si obsah tých
súborov ako kontext/inštrukcie a nes rovnaké rozdelenie zodpovednosti a rovnaké princípy (najmä
"stability first", "no fake UI", "one owner per shared file", "build → test → continue") ďalej vo
vlastnom pracovnom štýle.

---

## 6. `.uiqa/` toolkit — funguje, používaj ho

Appka beží živo, prihlásená (`session.dat` existuje), appka sa dá spustiť priamo:
```bash
Start-Process -FilePath "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe" -WorkingDirectory "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows"
```
Ak nabehne dialóg **„Nalezena záloha projektu"** (leftover autosave z predošlej testovacej
session), **zahoď ju** (`Zahodit zálohu`) — nie je to skutočný projekt používateľa.

| skript | na čo |
|---|---|
| `shot.ps1 -Out x.png [-WindowTitle "..."]` | screenshot okna cez `PrintWindow` |
| `crop.ps1 -In a.png -Out b.png -X.. -Y.. -W.. -H.. -Scale 2` | výrez a zväčšenie na kontrolu pixelov |
| `invoke.ps1 -Name "..." [-Window "..."]` | najspoľahlivejší klik — cez UI Automation Invoke/SelectionItem/Toggle podľa presného mena |
| `click.ps1 -X.. -Y.. [-Move hover]` | súradnicový klik, keď `invoke.ps1` zlyhá (napr. flyout menu tlačidlá hlásia NO_INVOKABLE) |
| `drag.ps1 -X1.. -Y1.. -X2.. -Y2..` | ťahanie myšou |
| `resize.ps1 -W.. -H.. [-X.. -Y..]` | zmena veľkosti/pozície okna pre responsive QA |
| `ui.ps1 -Action tree -Depth N [-Window "..."]` | dump UI Automation stromu (mená, offscreen/disabled flagy) |

**Poučenie z tejto session**: klikaj a hneď screenshotni, over, potom ďalší krok. Séria naslepo
zreťazených klikov vytvorila duplicitné testovacie objekty na plátne, ktoré vyzerali ako bug, ale
neboli — strávil som s tým zbytočne čas. Appku po teste **zabi bez uloženia**
(`Stop-Process -Name Lasero.App -Force`), nikdy needit File→Save na testovacej session.

---

## 7. Čo NEROB

- Nemeň `JogViewModel` bezpečnostnú logiku (§1) bez konkrétneho nového nálezu.
- Nemeň GRBL/preflight/Start/Frame/Pause/Resume/Stop sémantiku kvôli UI problému — vyrieš to v
  layoute.
- Nevytváraj druhý konkurenčný design-token systém popri `LaseroTheme.xaml`.
- Nezačínaj typografiu (§4.1) ani ikony (§4.2) implementačne, kým nie je KAMIL (§2) commitnutý a
  zelený — všetky tri sa dotýkajú tých istých zdieľaných súborov.
- Nepridávaj `IsEnabled="False"` natvrdo v XAML — vždy cez CanExecute binding.
- Necommituj `.uiqa/*.png` screenshoty (sú to scratch artefakty, nie sú v `.gitignore`, ale nemajú
  čo robiť v histórii) ani `docs/stitch-*`/`.agents/` bez opýtania sa používateľa — tie boli pridané
  v samostatnej úlohe (setup agentov v Antigravite), nie sú súčasť kódu appky.
