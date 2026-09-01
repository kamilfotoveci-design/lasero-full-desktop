# Lasero Desktop — handoff

Stav k **1. 9. 2026**, koniec session. Tento súbor existuje preto, aby sa dalo pokračovať v novom
chate (alebo iným modelom) bez čítania celej histórie. Popisuje **kde to je, čo sa spravilo, čo je
rozrobené a čo ďalej**.

Predchádzajúca verzia tohto dokumentu (31. 8.) je v histórii commitu `98fc0d5`.

---

## 1. Kde projekt je

**`E:\lasero-desktop`** — nie na `C:`. Vetva: `design-system-tokens`. HEAD: `5b683e1`.
Pracovný strom je čistý. **339 testov, všetky prechádzajú.**

**Pozor: `E:` je druhý disk a počas tejto session sa raz sám odpojil** — `Get-PSDrive` ho prestal
vidieť. Ak zmizne, projekt je neprístupný; nie je to chyba repozitára.

```bash
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug
```

```bash
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln
```

Spustiteľný build: `E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe`

### Vizuálne overovanie

Je to WPF, nie web — browser tooling neplatí. V `.uiqa/`:

- `shot.ps1 -Out x.png -WindowTitle "Lasero Desktop"` — screenshot okna
- `crop.ps1 -In a.png -Out b.png -X .. -Y .. -W .. -H .. -Scale ..` — výrez a zväčšenie
- `click.ps1 -X .. -Y ..` / `drag.ps1` — syntetický vstup, súradnice relatívne k **hlavnému** oknu
- `resize.ps1 -W .. -H .. -X .. -Y ..` — veľkosť okna
- `ui.ps1 -Action click -Name "…"` — klik cez UI Automation podľa prístupného mena
- `toggle.ps1 -Name "…"` *(nové)* — vypíše všetky prvky daného mena aj s typom a rámčekom a skúsi na
  nich SelectionItem / Toggle / Invoke. Použi, keď `ui.ps1` vráti `NO_PATTERN` — typicky preto, že
  meno nesie `TextBlock` vnútri tlačidla a nie tlačidlo samo.
- `setvalue.ps1 -Name "…" -Value "…"` *(nové)* — zápis do TextBoxu cez ValuePattern

**Pasce QA, ktoré ma tu opakovane stáli čas:**

- `shot.ps1` používa `PrintWindow`, takže **nezachytí tooltip, ContextMenu ani Popup** — tie sú
  samostatné top-level okná. Na ne treba `CopyFromScreen`.
- `shot.ps1` bez `-WindowTitle` vezme prvé okno procesu, čo môže byť práve otvorený popup — potom
  dostaneš obrázok 160×28 a myslíš si, že je rozbité UI.
- **Kliky podľa súradníc sú nespoľahlivé**, lebo appka si po pripojení stroja sama prepne obrazovku
  a okno sa vie premaximalizovať. Vždy si po `resize.ps1` over veľkosť z výstupu `shot.ps1`.
- Po nečistom ukončení nabehne dialóg **„Nalezena záloha projektu"**. Pri QA použiť **„Obnovit
  projekt"**; zálohu nezahadzovať.
- Na testovanie netreba hardvér: v *Zařízení* je port **`SIMULÁTOR — Virtuální laser`**. Pozor, počas
  QA sa mi cez zle mierený klik naozaj spustila úloha — na simulátore je to bezpečné, na stroji nie.
- **Appka drží zamknutý `Lasero.App.exe`** — pred buildom:
  `Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force`.
- V Bash tooling nefunguje `python`/`python3` (je to Windows Store stub). Používaj plnú cestu
  `/c/Users/Ruzovka/AppData/Local/Programs/Python/Python312/python.exe`, a pri dlhších skriptoch ich
  radšej zapíš do súboru než cez heredoc.

---

## 2. Vizuálny smer

**Svetlý režim.** Používateľ v tejto session požiadal o tmavý, nasadil som ho celý (`ef64ab6`) a
o pár minút ho chcel späť (`918a263`). Tmavá paleta je teda odskúšaná a funkčná — ak sa k nej niekto
vráti, je to **zmena hodnôt v `LaseroTheme.xaml`, nie prepis markupu**, presne preto, lebo z tej
odbočky zostal token `Brush.AccentText`.

Neutrálny grafit + soft white + kobaltová modrá. **Jedna interakčná farba.** Modrá `#2563EB` znamená
vybrané/aktívne/primárne všade. Rozpočet: ~90 % neutrál, 8 % modrá, 2 % sémantická. Červená len pre
nebezpečné akcie a bodku v logu.

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
| accent text | `#2563EB` (samostatný token, pozri nižšie) |
| vybraná plocha | `#EFF6FF` |
| success | `#15803D` · warning `#D97706` · danger `#DC2626` |

**`Brush.AccentText`** je nový token. Na svetlom je to tá istá kobaltová ako výplň, ale je oddelený,
lebo tie dve role sa rozídu vo chvíli, keď chrome stmavne: `#2563EB` je správna **výplň** na tmavom
povrchu, ale ako **text** na ňom má sotva 2,5:1. Všetko, čo *píše* akcentom, binduje sem; výplne,
okraje, focus ringy a výber na plátne na `Brush.Accent`.

**Plátno má vlastné tokeny** (`Brush.Canvas.*`) a zámerne nesleduje chrome: lože zostáva biele bez
ohľadu na tému, lebo operátor podľa neho posudzuje kontrast opálení.

Typografia: Inter, zabalený v aplikácii. `Size.Text.Title` 24, `Size.Text.Section` 16, telo 13,
meta 12, **nič pod 12 px**.

Geometria editora: `Size.Toolbar.ButtonMinWidth` 48, `Size.Toolbar.ButtonHeight` 48,
`Size.Toolbar.Height` 64.

**Testy, ktoré držia dizajn systém** (`Lasero.Tests/ThemeTokenTests.cs`): zakazujú literálne
`FontSize` a `CornerRadius` v XAML a vynucujú `Radius.Pill` na kruhových prvkoch.

---

## 3. Čo sa v tejto session spravilo

Štrnásť commitov nad `ed449ee`.

### `c5d76c4` — vzorkovník materiálov + oprava diódových receptov na rez

Mriežka vzorkovníka mala šestnásť buniek, ktoré vyzerali rovnako: intenzitu niesla len priehľadnosť
písmena a najsilnejšia bunka sa od najslabšej líšila o ~13 z 255 úrovní jasu. Farba bunky sa teraz
mieša z povrchu materiálu k jeho stope podľa relatívnej dávky (výkon/rýchlosť, normalizované,
percepčná krivka), dlaždica sa mierne zakalí spolu so stopou a dvojice povrch/stopa sú vybrané na
kontrast v oboch smeroch. Namerané na obrazovke: rozsah 87–114 úrovní.

**Recepty:** používateľ nahlásil, že 20 W dióda reže 3–6 mm preglejku na 350 mm/min, 100 %, jeden
priechod — katalóg mal 280 mm/min a tri priechody, teda ~3,75× dávku. Katalóg je **doslovný mirror
`lasero-app/index.html`**, takže tá istá chyba je aj na webe (tam **neopravená**, používateľ chcel
zatiaľ len desktop). Prepočítané: 10/40 W preglejka a 10/20/40 W MDF podľa publikovaných tabuliek
lasertinkerer.com, 60 W preglejka doextrapolovaná z 40 W a **označená ako odvodená**, 5 W preglejka
si čísla nechala, ale dostala varovanie, že je to hraničné. Nový test drží pravidlo, ktoré sa tu
porušilo: **silnejší laser nikdy nesmie žiadať väčšiu dávku na ten istý rez.**

### `3f7b754` — průvodce zařízením

`Lasero.Core/Machines/DeviceScanner.cs` prejde sériové porty a na každom skúsi `$$` na sadu bežných
baud rates. Posiela **iba ten jeden dotaz** — žiadny pohyb, žiadny zápis nastavení — lebo sken môže
trafiť 3D tlačiareň. Za GRBL považuje až odpoveď so štyrmi a viac rozparsovanými `$n=v`, inak by
šum na zlom baud rate prešiel. Okno `DeviceWizardWindow` vedie od „mám gravírku na USB" po
„appka je pripojená a vie, aká je plocha", vrátane zapnutia `$32` — **to je jediná vec, ktorú
sprievodca do stroja zapíše, a nikdy automaticky.**

### `1d1dc92` — Domů podľa mockupu

Hero, „Jak začít", karta zařízení, nedávné projekty.

### `9a3d500` → `9ecdd3a` → `1bb84e6` → `5b683e1` — toolbar editora, štyri kolá

Postupne: popisky pod ikonami a skupiny s oddeľovačmi → vzduch a `Button.Ghost` namiesto
`Button.Icon` → nižšie tlačidlá (54 → 48) → **`ToolBar` s prepadovým menu namiesto rolovania.**

To posledné je to podstatné. Rolovanie bolo zlá odpoveď na skutočný problém: pod ~1200 px sa
pätnásť nástrojov plus zoom do riadku nezmestí, a scrollbar to riešil tak, že ukázal polovicu
tlačidla aj jeho podfarbenia. Teraz `ToolBarPanel` presunie, čo sa nezmestí, do popupu za „…".
**Kreslicí nástroje majú `ToolBar.OverflowMode="Never"`** — nesú aktívny stav, a aktívny nástroj
schovaný v popupe znamená, že na obrazovke nič nehovorí, čo kurzor urobí. Do prepadu preto idú
súbory, historie a transformace.

Šablóna `ToolBar`u je vlastná, lebo stock kreslí úchyt na ťahanie, vystúpený okraj a vlastnú grafiku
tlačidiel. Šipka „…" sa skryje, keď nič nepretečie.

### `ef64ab6` + `918a263` — tmavá paleta tam a späť

Zo zrušenej tmavej fázy **zostali tri veci, lebo to neboli farby ale opravy**:

1. **Každý root okna si nastavuje `Background` a `Foreground` sám.** Pozri §7.1.
2. **Mriežka je obmedzená na lože**, nie na celý viewport.
3. **`Brush.AccentText`** ako samostatný token.

### `0061e7f` + `3efdfae` — `ProcessStatusCard`

Jedna plocha pre všetky dlhé operácie stroja: pripájanie, čítanie radiča, meranie plochy, odhad,
rámování, odesílání, gravírování a ich výsledky. Odpovedá na päť otázok, ktoré operátor pri
zaneprázdnenom stroji má, a všade rovnako: čo sa deje, kam sme došli, je to v poriadku, čo dál,
dá sa to zastaviť. **Nedrží žiadny vlastný stav včetně progressu**, takže nemôže tvrdiť, že stroj
došel dál, než skutočne došel. Jej šesť stavov sú tie isté významy ako `StatePillKind` plus
explicitné „nič nezačalo" — pilulka a karta sa nemôžu rozísť v tom, čo znamená zelená.

Zapojená na dvoch miestach:

- **Zařízení** — `DeviceSetupViewModel` číta `IsConnecting` / `IsConnected` / `ConnectionError` /
  `DetectedDevice`. Ručné ovládanie portu a rychlosti zostalo pod ňou, takže karta je vedená cesta,
  nie jediná. `ConnectionViewModel` dostal `ConnectionError`, lebo `StatusText` nesie aj každý bežný
  stav a nedá sa z neho čítať „něco se pokazilo".
- **Stroj → Spuštění** — `JobStatusViewModel` mapuje `JobRunState`. Nahradila voľný progress bar,
  voľný odhad a dva voľné chybové riadky; tie štyri hovorili časti tej istej veci na štyroch
  miestach a dva z nich sa objavovali len keď sa niečo pokazilo, takže panel menil tvar v najhoršej
  chvíli.

**Pruh je neurčitý všade, kde appka nemá čo merať** — GRBL nehlási, jak daleko je ve čtení
nastavení, a vymyslené percento by bolo horšie než priznať, že to appka nevidí.

### `3efdfae` — okno má okraj

Windows 11 zaobľuje každé rámované okno a kreslí okolo neho vlas. Okno s `WindowStyle="None"` sa
z oboch odhlási, takže appka mala hranaté rohy a na svetlej ploche nebolo vidieť, kde končí.
`WindowFrameHook` si oboje vypýta cez DWM (`DWMWA_WINDOW_CORNER_PREFERENCE`, `DWMWA_BORDER_COLOR`),
nie kreslením vnútri okna — takže sa to správne oreže, vrhá systémový tieň a nezasahuje do layoutu
ani hit-testingu. Na Windows 10 volania tíško zlyhajú.

### Čo sa na žiadosť používateľa **odstránilo**

- **Bezpečnostní kontrola** z Domů (Nedávné projekty dostali celú šírku)
- **Pravý blok hero** s náhľadom a **tlačidlo Importovat**
- **Fotka gravírky** — najprv pridaná (výber súboru, kópia do app data, zobrazenie v hero aj v karte
  zařízení), o chvíľu zrušená. Odstránil som s ňou aj view model, nastavenie a slot v karte, lebo
  bez výberu sa nedala nastaviť a nedosiahnuteľná funkcia vyzerá pre toho, kto ju v kóde nájde, ako
  funkčná. Je to **jeden revert commitu `1bb84e6`**, ak ju bude chcieť inde.
- **Počítadlo příkazů** („2391 / 5158") z bežiacej úlohy — je to interné účtovníctvo, nie niečo, na
  čo operátor pri stroji reaguje.

---

## 4. Rozrobené — pokračovať tu

Používateľ si vyžiadal poradie **1, 4, 2, 3**:

- **1 — toolbar editora** ✅ hotové (`5b683e1`)
- **4 — `ProcessStatusCard`** ✅ hotové (`0061e7f`, `3efdfae`)
- **2 — pravý inšpektor** ⬅️ **tu pokračovať**
- **3 — spodný stavový pruh**

### 4.1 Pravý inšpektor (ďalší krok)

Podľa mockupu má obsahovať, v tomto poradí:

- segmentovaný prepínač **Vrstvy | Stroj** *(existuje; taby už majú `AutomationProperties.Name`)*
- zoznam vrstiev s farebným štítkom, názvom, typom operácie a súhrnom „100% / 20 mm/s" *(existuje,
  formát sedí)* a tlačidlo **+ Nová vrstva** *(existuje)*
- **Nastavení práce** — Materiál / Režim / Rychlost / Výkon / Průchody / Interval, labely vľavo,
  ovládacie prvky vpravo, kompaktne. **Toto chýba ako celok.**
- **Odhadovaný čas** *(hodnota existuje ako `GCode.EstimatedTimeLabel`)*
- **Rámování** s prepínačom, **Náhled rámování**, **Rámovat** *(príkazy existujú)*
- **Spustit** — v mockupe **červené**. Dnes je modré. Podľa pravidiel palety je červená vyhradená
  pre nebezpečnú akciu a spustenie lasera ňou je; `ProcessStatusCard` už má `PrimaryIsDanger`
  a `SecondaryIsDanger` na presne tento účel.

### 4.2 Spodný stavový pruh

Mockup: `● Připojeno · Lasero L2 Pro · COM4 · Ovládání stroje | Pracovní plocha 400 × 400 mm |
Materiál Překližka (3 mm) | Náhled práce`. Dnešný pruh má stavové pilulky, názov súboru a hlášku;
chýbajú názov stroja, port, plocha, materiál a „Náhled práce".

### 4.3 Čo je v mockupe a nie je nasadené (úplný zoznam)

Zo **špecifikácie dizajn systému** v pravom paneli mockupu:

- typografická škála **s riadkovaním** — H1 24/32, H2 18/24, H3 14/20, Body 14/20, Small 12/16.
  Stupeň **18 px chýba** a riadkovania nie sú definované vôbec.
- päť stavových pilulek Připraveno / Probíhá / Varování / Chyba / Čeká — `StatePillKind` má presne
  zodpovedajúcich päť hodnôt, ale nie sú overené proti mockupu
- tlačidlo **Danger „Zastavit"**, Input „Zadejte hodnotu", Select „Vyberte možnost" vo výške 32–36 px

Z **editora**:

- popisky skupin nad toolbarom (Projekt / Úpravy / Nástroje / Transformace / Zobrazení)
- svislá lišta nástrojů po ľavej strane plátna
- horní pruh „Návrh – Motýl", stav „Uloženo", tlačítka „Náhled" a „Uložit"
- rozměrový popisek „400 mm" u výběru na plátně

### 4.4 Lasero Chat

Používateľ: *„a lasero chat musi fungovat a vizuálne sa podobat rovnako ako lasero app na webe."*
Zatiaľ nezačaté. Poslal screenshoty webového chatu: hlavička s avatarom KAMIL, `⏱ Historie` /
`+ Nový chat`, kontextový chip, bubliny (asistent biela karta, používateľ plná bublina), indikátor
psaní, karta **DOPORUČENÉ PARAMETRY** so štyrmi dlaždicami a tlačidlom `Uložit parametry`,
„Fungovalo nastavení?" s 👍/👎, návrhové chipy.

**Otvorená otázka na používateľa:** web má **červený** akcent, desktop má jednu interakčnú farbu —
kobaltovú. Používateľ povedal „rovnako ako na webe", čo čítam ako rozhodnutie pre červenú v chate,
ale predtým než sa červená zavedie ako tretia UI farba, potvrdil by som to.

### 4.5 Assety

Fotka gravírovania v hero, render gravírky, náhledy projektů — **žiadne reálne assety neexistujú**,
všetko sú kreslené zástupné z vlastnej ikonovej sady. Zámerne: appka nemá ako vedieť, aký stroj
zákazník má, a stock render cudzej gravírky je obrázok nesprávneho stroja.

### 4.6 Staršie, stále platné

- **Offset / Posunout** (nezačaté) — dialóg ako v LightBurne. Odporúčaný postup:
  `Geometry.GetWidenedPathGeometry(new Pen(...))` s `PenLineJoin` podľa štýlu rohu, potom
  union/difference. `PenLineJoin.Round/Bevel/Miter` mapuje presne na Oblý/Kosý/Roh.
- **Rohové úchopy pre Zkreslit textu** — dátový model (`TextDistortion`,
  `SetSelectedTextDistortionCorner`) a testy existujú, chýba ovládanie na plátne v
  `SceneCanvas.DrawSingleObjectHandles`. **Pasca:** `ResizeHandle` pomenúva roh s najmenším Y ako
  `Top`, ale plátno kreslí najmenšie Y dole. `TextDistortionCorner` je v dokumentovom priestore.

---

## 5. Otvorené / neriešené

- **Zvyšok diódového katalógu je stále neoverené webové dáta.** Opravené sú len preglejka a MDF na
  rez. Gravírovacie rýchlosti a ostatné materiály nikto nezmeral.
- **Web `lasero-app` má tú istú chybu v receptoch** a zámerne sa neopravoval. Desktop je teda dočasne
  rozdielny od webu; je to zapísané v komentári v `MaterialCatalog` aj v teste
  `Catalog_MatchesTheAgreedRecipeValues`.
- **SVG import je hranatý.** `SvgPathParser.cs` má `const int CurveSteps = 16` — pevný počet krokov
  na krivku nezávislý od veľkosti. Správne je subdivízia podľa tolerancie tetivy, ale parser pracuje
  v SVG user units pred transformáciou, takže absolútna mm tolerancia tam nie je dostupná.
- **Uložená šírka inšpektora je 560 px.** Pri načítaní ju obmedzujem na tretinu okna, aby pri 1366
  nezostalo plátnu 640 px, ale **hodnota na disku sa neprepisuje** — je to preferencia používateľa.
  Dôsledok: aj pri 1700 px je workspace pod prahom 1060, takže popisky v toolbare sú zbalené.
- `LaseroProjectFile.Version` má default `7`, ale `Deserialize` aj `CreateArchiveSnapshot` ho
  natvrdo nastavia na `6`. Nič `Version` nečíta.
- Staré publish výstupy `artifacts/` (3,4 GB) a `dist/` (1,1 GB) ležia v repozitári. Regenerovateľné.
- `TextToolWindow.Style` tieni `FrameworkElement.Style` — jediný build warning (CS0108).

---

## 6. Bezpečnostná hranica

Prezentácia sa meniť môže, **správanie nie**. Nikdy neupravovať sémantiku Start, Pause, Stop, Frame,
Home, Origin, Jog, Reset, Unlock, súradníc stroja, firmware príkazov ani bezpečnostných kontrol
kvôli vzhľadu.

- Dostupnosť Start/Frame rozhoduje výhradne `CanRun`/`CanFrame` + `JobPreflight`. **`JobStatusViewModel`
  je čistá prezentácia** — binduje tie isté príkazy a berie ich `CanExecute` tak, ako ho nájde;
  nemôže sprístupniť akciu stroja. Vlastní len formulácie a to, do ktorého z dvoch akčných slotov
  ktorý príkaz patrí.
- `ProcessStatusCard` nedrží vlastný stav vrátane progressu. Ak nie je čo merať, je pruh neurčitý.
- Nikdy nezobrazovať „Ready", kým to appka naozaj nepotvrdila. `JobRunState.Idle` je zámerne
  „Bez úlohy", nie „Připraveno".
- Stav sa nikdy nesmie oznamovať iba farbou — vždy tvar (ikona) + slovo + farba.
- Sprievodca zariadením posiela pri skene **iba `$$`**. Jediný zápis do stroja je `$32=1` a nikdy
  nie automaticky.
- **Zmeny, ktoré zmenili generovaný G-kód:** tolerancia flattenovania textu 0,2 → 0,01 mm (`3ff71c2`,
  minulá session) a **diódové recepty na rez preglejky a MDF (`c5d76c4`, táto session)**.

---

## 7. Pasce, ktoré nás v tejto session stáli čas

Všetky sú systémové, aj keď každá vyzerala ako preklep.

1. **Implicitný `Style TargetType="Window"` sa nevzťahuje na žiadne okno v tejto appke.** WPF hľadá
   implicitný štýl podľa **presného typu**, a každé okno tu je odvodená trieda (`MainWindow`,
   `MaterialsWindow`, …). Svetlá téma to úplne skryla, lebo predvolený WPF foreground je čierny a
   ten bol na odtieň od `TextPrimary`. Odhalilo sa to až na tmavej palete, kde bol každý dedený
   nadpis a ikona neviditeľný. **Každý root okna si teraz nastavuje `Background` aj `Foreground` sám.**
2. **WPF ignoruje implicitné štýly deklarované vnútri šablóny.** Pre prvok vytvorený v `ControlTemplate`
   sa implicitný štýl hľadá **len v `Application.Resources`**. Túto pascu má appka zapísanú už
   z minulej session (čierne tooltipy) a **chytila ma znova**: implicitný `Style TargetType="ScrollBar"`
   v `ScrollViewer.Resources` je úplne neúčinný, lebo scrollbar vzniká v šablóne ScrollVieweru.
   Musí sa odovzdať **explicitne kľúčom**.
3. **`DockPanel` neklipuje.** Keď je dieťa širšie než jeho slot, nepreteče preč — podlezie pod
   susedné dieťa. Takto zmizlo tlačidlo *Zrcadlit* pod odčítaním zoomu a v markupe nebolo vidieť nič
   zlé; UIA hlásilo prvok na správnom mieste so správnou šírkou.
4. **Poradie v `ResourceDictionary` platí pre `StaticResource`.** Štýl, ktorý odkazuje na iný štýl,
   musí byť **za ním**. Appka spadla pri štarte na `Cannot find resource named 'Toolbar.OverflowToggle'`.
5. **Obrovský `CornerRadius` na tenkom širokom `Border`i nie je zaoblený pruh.** WPF klampuje rádius
   po osiach, takže `Radius.Pill` na prvku 3 px vysokom a stovky širokom vykreslí šošovku.
6. **`ToolBar` rieši pretečenie za teba.** `ToolBarPanel` + `ToolBarOverflowPanel` + `HasOverflowItems`
   + `ToolBar.OverflowMode` na jednotlivých položkách. Nemá zmysel to písať ručne.
7. **Plátno si drží měřítko, kým je skryté.** `SceneCanvas` nemá pri `Visibility="Collapsed"` veľkosť,
   takže `FitToView` treba zavolať až keď je editor naozaj viditeľný, na `DispatcherPriority.Background`.
8. **`Height` v štýle sa dá prebiť.** Scrollbar vychádzal 15 px namiesto 5 a lokálna hodnota na prvku
   bola jediné, čo zabralo. (Nakoniec bezpredmetné — scrollbar je preč.)
9. **Vypnuté tlačidlo si kreslí plochu.** Je to správne pri tlačidle, ktoré plochu má; pri `Button.Ghost`
   to obracia hierarchiu, lebo jediné boxy v pruhu sú tie nedostupné. Na to je
   `ButtonChrome.SuppressDisabledSurface`.
