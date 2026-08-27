# Lasero Desktop — přechod z LightBurnu

Status: implementační produktová a UX specifikace  
Persona: zkušený operátor laseru, který zná LightBurn a nechce ztratit pracovní schopnosti  
Produktový filtr: **Familiarity → Simplicity → Delight**

Navazující specifikace prvního použití pro úplného začátečníka je v [`FIRST_RUN_EXPERIENCE.md`](FIRST_RUN_EXPERIENCE.md). Nejde o samostatný „beginner mode“; obě cesty se po úvodním kontextu sbíhají do stejného pracovního prostředí.

## Executive summary

Lasero nemusí kopírovat LightBurn, ale při 80–90 % běžných zakázek nesmí uživatel narazit na chybějící základní schopnost. Familiarita znamená předvídatelný import, přesný editor, barevné výrobní vrstvy, jasný origin, framing, preview, řízení stroje a znovupoužitelný projekt. Jednoduchost znamená jednu souvislou pracovní plochu, kontextové nástroje a progresivně odhalená pokročilá nastavení. Delight přichází z automatické detekce stroje, Vzorníku, lidských chyb, diagnostiky a kontextového Lasero Chatu.

Lasero dnes už má důležité základy: SVG a rastrový import, G-code, vektorové transformace, multi-selection, group/ungroup, sjednocení uzavřených vektorů, barevné vrstvy Line/Fill, recepty, grayscale/threshold/dithering, tracing, framing, simulaci, dva praktické placement režimy, konzolu, autosave/recovery a přenosný `.lasero` balík s vloženými bitmapami. Parita ale zatím není dostatečná pro spolehlivý přechod profesionála.

---

# 1. Prvních 30 minut po přechodu

| Krok | Co očekávám z LightBurnu | Co musí nabídnout Lasero | Jak to Lasero udělá jednodušeji |
|---|---|---|---|
| 1. Připojit laser | uložený profil, port, pracovní plocha | automatická detekce a uložený profil schopností | „Najít zařízení“; COM/baud až v Pokročilé |
| 2. Otevřít SVG | zachované rozměry, tvary, barvy a skupiny | robustní SVG import s reportem změn | import se otevře rovnou na canvasu; případné rozdíly v nenásilném souhrnu |
| 3. Upravit velikost | W/H, zámek poměru, numerický vstup, handles | přesná transformace v mm a undo | hodnoty přímo nad výběrem; žádný duplicitní objektový panel |
| 4. Rozdělit do vrstev | barva objektu určuje cut layer | jasné „objekt → vrstva → parametry“ | barevná paleta a „Přiřadit výběr do vrstvy“ v jednom kontextu |
| 5. Řezání a gravírování | Line, Fill, Fill+Line, image | výrobní režim pro každou vrstvu | lidské názvy Gravírovat / Řezat / Výplň + čára; technický termín v tooltipech |
| 6. Vybrat materiál | knihovna nebo vlastní profil | offline Vzorník a moje recepty | materiál → tloušťka → operace; 1 klik na aplikaci do vrstvy |
| 7. Speed/power | ruční kontrola bez blokování | vždy dostupný manual override | doporučení je výchozí hodnota, ne automatické uzamčení |
| 8. Umístit grafiku | rulers, grid, snap, align, center | přesný canvas se snappingem | aktivní snap body jsou krátce vizuálně označené; jeden toggle |
| 9. Manuálně posunout laser | Move/Jog panel | XY/Z, krok, rychlost, Home, Stop | kompaktní panel Stroj vedle návrhu; capability-based Z |
| 10. Nastavit nulu | Set Origin / Go to Origin | jasná pracovní nula a current-position job | „Nula zde“ a „Na nulu XY“ s aktuálními souřadnicemi |
| 11. Framing | viditelné Frame | framing bez hledání v menu | dominantní sekundární akce vedle Spustit a v preflightu |
| 12. Zkontrolovat výsledek | Preview s pořadím a časem | simulace drah, vrstev, travelů a času | náhled otevře samostatné plynulé okno a ukáže rizika před výrobou |
| 13. Spustit | jistota o start pointu a pořadí | souhrn + živý preflight | ne modal pokaždé; rozbalitelný dock se stavem Připraveno |
| 14. Pozastavit | Pause/Resume a Stop | spolehlivé řízení stavu | Pause se změní na Pokračovat; Stop je vizuálně i textově destruktivní |
| 15. Dokončit | historie úlohy | výsledek, čas a použité parametry | „Spustit znovu“ nebo „Upravit recept“ přímo z historie |
| 16. Uložit projekt | vše se vrátí stejně | grafika, vrstvy, pořadí, materiál, parametry a placement | autosave + jasné „Uloženo“ + přenosný `.lasero` balík |

### Return-to-LightBurn verdict

Současný Lasero zvládne podstatnou část kroků 2, 3, 4, 6, 7, 9–12, 14 a základ 16. Přechod je však dnes blokovaný hlavně formátovou kompatibilitou, neúplným layer workflow, chybějícím snapping/distribute, absencí Fill+Line, slabším exportem a nedotaženou profesionální navigací.

---

# 2. Import a export

| Formát | Očekávání | Stav Lasero dnes | Rozhodnutí |
|---|---|---|---|
| SVG | rozměr, viewBox, cesty, barvy, skupiny, transformace | podporováno, vlastní importer; rozsah kompatibility musí mít fixture testy | 🔴 P0 hardening |
| DXF | základní 2D geometrie a jednotky | chybí | 🔴 P0 |
| PDF | import vektorové stránky, volba stránky, fallback raster | chybí | 🟠 P1 |
| PNG/JPG/JPEG/BMP | rozměr, grayscale workflow, úpravy | podporováno | přidat crop/presety v P1 |
| AI/EPS | férové převedení nebo jasná nepodpora | chybí | ⚪ P3 přes bezpečný konvertor/import PDF-kompatibility |
| G-code/NC/TAP | načtení hotové úlohy bez editorových slibů | podporováno | zachovat jako pokročilé |
| `.lasero` | přenosný projekt včetně bitmap | podporováno jako ZIP balík, verze 4 | doplnit materiál metadata, execution order a migrace verzí |
| Export SVG | vlastnictví dat a spolupráce s grafikou | chybí | 🔴 P0 |
| Export G-code | opakování/externí řízení | není dostupný jako jasný běžný workflow | 🟠 P1 |

Import nikdy nesmí potichu změnit měřítko, text nebo geometrii. Po importu zobrazí pouze v případě potřeby souhrn: „2 textové objekty byly převedeny na křivky“, „nepodporovaný efekt byl rasterizován“ nebo „jednotky nebylo možné určit; použito mm“. Originál se nikdy nepřepisuje.

---

# 3. Editor capability classification

## 🔴 MUST HAVE

- Select, move, scale, W/H, lock aspect, rotate, flip X/Y.
- Duplicate, delete, copy/paste, undo/redo.
- Multi-select: Shift-click, box selection, společný move/scale.
- Group/ungroup, lock/hide, include in output.
- Bring forward/backward i úplně dopředu/dozadu.
- Align left/center/right/top/middle/bottom.
- Distribute horizontally/vertically.
- Center selection on workspace.
- Smooth zoom/pan, rulers, zoom-dependent grid.
- Object, edge, center a grid snapping s rychlým vypnutím.
- Přesné umístění a rozměry v mm bez ořezaných polí.

## 🟠 SHOULD HAVE

- základní node editing pro opravu/importované cesty,
- nudge šipkami a konfigurovatelný jemný/hrubý krok,
- transform pivot a opakování poslední transformace,
- offset/outline,
- základní boolean sada,
- zarovnání k poslednímu vybranému/klíčovému objektu,
- opakovatelné pole/array pro běžnou sériovou výrobu.

## 🟡 ADVANCED

- komplexní node operace a Bézier handles,
- nesting,
- parametrické tvary a pattern fill,
- warp textu a pokročilá typografie,
- automatické skládání na více tabulí.

### Současný editor

Přítomné jsou select/pan, obdélník, elipsa, čára, trojúhelník, pěti/šesti/osmiúhelník, hvězdy a text; dále multi-selection, transformace, duplicate, delete, group/ungroup, lock, visibility, front/back, šest směrů align a sjednocení uzavřených tvarů. Chybí distribute, snapping, center-on-workspace, základní node editing, offset a zbývající boolean operace. To je funkční základ, ale ne ještě pohodlná každodenní náhrada LightBurnu.

---

# 4. Vrstvy a pořadí výroby

## Jediný mentální model

`Objekt používá barvu vrstvy → vrstva nese výrobní režim a parametry → pořadí vrstev určuje pořadí výroby.`

Každý řádek vrstvy ukazuje:

- pořadové číslo a drag handle,
- název a barevný vzorek,
- režim: Čára / Výplň / Výplň + čára / Obrázek,
- stručně `rychlost · výkon · průchody`,
- Zobrazit a Výstup,
- bezpečnostní status nebo warning.

Klik na objekt vybere jeho vrstvu. Klik na vrstvu zvýrazní všechny její objekty. Změna barvy vrstvy se okamžitě projeví na vektoru. Přetažení vrstvy mění skutečné pořadí generování G-code i preview. Výchozí automatika dává gravírování před řezání; uživatel ji může přepsat. Pokud je řez před gravírováním, Lasero ukáže warning, nikoli povinný modal.

Současný model per-color vrstev, Line/Fill, speed/power/passes/interval, viditelnost a výstup je dobrý základ. P0 musí doplnit explicitní execution order, drag & drop, vazbu výběru a režim Fill+Line.

---

# 5. Positioning a origin bez překvapení

Lasero používá v hlavním workflow dva režimy, které už jsou v doméně aplikace:

1. **Aktuální poloha laseru** — návrh se umístí relativně k místu, kam uživatel laser ručně posunul. Uživatel zvolí referenční bod návrhu: levý horní, střed, pravý horní, levý dolní, pravý dolní.
2. **Absolutní poloha na pracovní ploše** — 0,0 odpovídá pracovní nule stroje a canvas je přesná mapa pracovní plochy.

„Uživatelská nula“ je strojní akce **Nula zde**; „Na nulu XY“ vrací laser na pracovní nulu. Machine coordinates jsou v rozbalené diagnostice, ne v běžném job flow. Před framingem a startem se vždy ukáže věta typu: „Levý horní roh návrhu začne na aktuální poloze X 128,4 · Y 72,0 mm.“

Změna placementu nebo anchoru zneplatní framing. Náhled i skutečná úloha používají stejný vypočtený dokument, aby canvas, framing a výroba nemohly mít rozdílný offset.

---

# 6. Raster, tracing a tvorba

## Bitmapa

Výchozí import je grayscale a okamžitě ukáže reálný výrobní preview. Základní presety:

- **Fotografie** — odstíny šedi/dithering, konzervativní kontrast.
- **Logo** — threshold, ostré hrany.
- **Text / vysoký kontrast** — černobílý threshold.

V základním panelu: preset, jas, kontrast, invert, velikost a kvalita. Pokročilé: threshold, dithering algoritmus, DPI/line interval, scan direction a overscan. Současný Lasero již má grayscale, threshold, dithering, brightness, contrast, invert a DPI; P1 doplní crop, srovnatelný před/po náhled a pojmenované presety.

## Trace image

Současný tracing bitmapy je silná schopnost. UX má být `Originál | Vektor` se zoomem, thresholdem, simplification a minimální velikostí prvku. Výsledek se vloží jako jeden logický objekt; uživatel může následně „Rozdělit na samostatné části“.

## Tvorba a booleany

Obdélník, elipsa, čára, polygon, hvězda a text jsou MUST. Rounded corners a text alignment jsou P1. Text se může převést na křivky a zůstává jeden logický objekt. Boolean nabídka se jmenuje **Kombinovat tvary**: Sjednotit, Odečíst, Průnik, Rozdíl. Trvale viditelné je pouze Sjednotit, když dává smysl; zbytek je v kontextovém menu/overflow.

---

# A. LightBurn parity minimum

1. SVG, DXF, raster a přenosný projekt bez změny fyzických rozměrů.
2. Přesný canvas: move/scale/rotate, multi-select, group, align, distribute, snapping, grid a rulers.
3. Text a základní tvary bez nutnosti externího editoru.
4. Barevné výrobní vrstvy s Line, Fill, Fill+Line, speed, power, passes a skutečným pořadím výroby.
5. Offline materiály a vlastní recepty s manual override.
6. Current position a absolute positioning, pracovní nula a anchor.
7. Kompaktní jog panel s XY/Z, Home, Nula zde, Na nulu XY a Stop movement.
8. Framing jako vždy dostupná akce.
9. Preview pořadí, drah a odhadu času.
10. Preflight, Pause/Resume, Stop a lidské chyby.
11. Save, Save As, autosave/recovery a portable project.
12. Desktopové zkratky, context menu a spolehlivé undo/redo.
13. Editor a lokální recepty fungují offline po prvním ověření účtu/licence.

Pokud chybí položky 1–9 nebo 11–13, jde o 🔴 **Return-to-LightBurn risk**.

---

# B. P0 blockers před releasem

1. **DXF import s jednotkami a vrstvami.** Bez něj je řada běžných výrobních podkladů nepoužitelná.
2. **SVG compatibility suite.** Testovací fixture sada pro viewBox, transformace, nested groups, text, strokes/fills a barvy.
3. **Execution-ordered layers.** Drag & drop, gravírovat-before-cut default, preview a G-code ve stejném pořadí.
4. **Fill+Line.** Jeden logický výrobní krok nebo jasně propojená dvojice výstupů.
5. **Snapping a distribute.** Grid/object/center/edge snap + distribute horizontal/vertical.
6. **Center on workspace a úplná z-order sada.** Bring forward/back one step i front/back.
7. **Save As + Export SVG.** Uživatel nesmí mít pocit uzamčených dat.
8. **Material metadata v projektu.** Materiál, tloušťka, operace, recept ID/revision a manual overrides.
9. **Actionable preflight.** Každý blocker má opravu; framing se váže na revision dokumentu a placement.
10. **Offline production entitlement.** Po prvním platném přihlášení lze otevřít, editovat a vyrábět bez internetu; online funkce se frontují.
11. **Performance budget.** Selection/drag do 16 ms na typickém projektu, zoom/pan bez blokování UI a import/preview mimo UI thread.
12. **Stabilita.** Žádný očekávaný user error nesmí ukončit aplikaci; crash recovery zachová projekt a bezpečně zastaví komunikaci.

---

# C. P1 production features

1. PDF import s výběrem stránky a jasným vector/raster výsledkem.
2. Kompletní boolean operace a offset/outline.
3. Raster crop, pojmenované presety a before/after preview.
4. Material Test generator s uložením nejlepšího pole do receptu.
5. Save/export G-code, zopakování úlohy a historie s použitými parametry.
6. Vlastní klávesové zkratky, nudge a zobrazení shortcutů v tooltipech/menu.
7. mm/inch se stejným interním fyzickým rozměrem.
8. Více strojních profilů a rychlé přepnutí aktivního stroje.
9. Bezpečný reconnect/recovery state machine pro USB disconnect, sleep, pause a alarm.
10. Lokalizovaná Help/diagnostika a export logů pro podporu.
11. Základní node editing a rounded corners.
12. Přístupnost a layout QA při 100–200% DPI a 1366×768 až 4K.

---

# D. P2 differentiators

1. **Smart Material Recipes** — stroj + výkon + materiál + tloušťka + operace + uživatelský výsledek.
2. **Guided Material Test** — automatická mřížka, bezpečné limity a uložení vítězného pole.
3. **Contextual Lasero Chat** — rozumí vybrané vrstvě, receptu, stroji a chybě; změnu vždy navrhne, nikdy sám nespustí.
4. **Human diagnostics** — USB/driver/port/firmware/stav v jednom automatickém testu.
5. **Better job preview** — vrstvy, pořadí, travels, rizika, čas a přesné umístění v jednom pohledu.
6. **Outcome feedback loop** — příliš světlý/tmavý → vysvětlená úprava → nový recept.
7. **Project history** — spustit znovu nebo vytvořit variantu bez ztráty původního nastavení.
8. **Capability-based UI** — Z, rotary, air assist, exhaust, autofocus a kamera se objeví pouze u podporovaného stroje.
9. **Safer automatic cut ordering** — vnitřní tvary a gravírování před uvolněním obrysu.
10. **Portable diagnostics/project bundle** — uživatel vlastní data a může je snadno předat podpoře nebo jinému počítači.

---

# E. Advanced roadmap

- hlubší node editing a complex path manipulation,
- kerf compensation,
- cut optimization: inside-first, shortest travel, direction a start point,
- rotary profily a test rotation,
- camera calibration, capture a workspace overlay,
- accessories orchestration: air assist, exhaust, autofocus,
- macros a servisní konzole,
- AI/EPS import,
- nesting a batch production,
- firmware/controller management.

Tyto funkce se zobrazí pouze v kontextu nebo v Pokročilé; jejich absence nesmí deformovat základní editor.

---

# F. Ideální workflow

1. Domov: **Nový projekt** nebo přetažení SVG/DXF/bitmapy do okna.
2. Import zachová rozměr a vrstvy; pouze skutečné změny ukáže v krátkém reportu.
3. Objekt je vybraný, kontextový toolbar nabízí rozměr, transformaci, z-order, group a align.
4. Vrstvy vpravo jasně ukazují barvu, režim, parametry a pořadí.
5. Uživatel vybere materiál/recept nebo ponechá ruční parametry.
6. Canvas a stroj používají zvolený placement: aktuální poloha nebo absolutní plocha.
7. Jog je dostupný vedle návrhu bez přepnutí do jiné obrazovky.
8. Uživatel použije **Nula zde** nebo nastaví návrh absolutně.
9. **Náhled** ověří vrstvy, pořadí, travels, čas a hranice.
10. **Rámování** ověří fyzickou polohu.
11. Preflight ukáže Připraveno nebo konkrétní opravné akce.
12. **Spustit** přepne UI na progress, aktuální vrstvu, Pause/Resume a Stop.
13. Po dokončení se uloží historie, parametry a stav „Uloženo“.
14. Projekt lze zopakovat, upravit, uložit jako nový nebo exportovat.

---

# G. Top 10 Lasero moments

1. ✨ Stroj se najde a nastaví bez ručního portu.
2. ✨ Pracovní plocha a možnosti příslušenství se načtou automaticky.
3. ✨ Materiálový recept se aplikuje na vybranou vrstvu jedním kliknutím.
4. ✨ Vrstva ukáže režim, parametry i reálné pořadí bez barevné šifry.
5. ✨ Lasero předem upozorní na řez před gravírováním a nabídne opravu pořadí.
6. ✨ Náhled ukáže přesné umístění, travels a realistický čas.
7. ✨ Chyba zařízení je přeložená a diagnostika navrhne konkrétní řešení.
8. ✨ Test materiálu vygeneruje mřížku a nejlepší pole uloží jako recept.
9. ✨ Po výsledku „příliš tmavý“ Lasero navrhne konzervativní změnu s vysvětlením.
10. ✨ Offline dílna dál otevře projekt, recept i výrobu; synchronizace počká.

---

# H. Top 10 Return-to-LightBurn risks

1. 🔴 DXF nebo důležité SVG se neotevře správně.
2. 🔴 Vrstvy nelze spolehlivě seřadit a pořadí preview neodpovídá výrobě.
3. 🔴 Chybí Fill+Line nebo nelze rychle rozlišit engrave/cut.
4. 🔴 Bez snappingu/distribute trvá přesná zakázka příliš dlouho.
5. 🔴 Origin není předvídatelný a uživatel neví, kam laser pojede.
6. 🔴 Preview/framing neodpovídá finálnímu G-code.
7. 🔴 Projekt po otevření ztratí materiál, pořadí nebo placement.
8. 🔴 Bez internetu nebo po expirované relaci nelze lokálně vyrábět.
9. 🔴 Drag, zoom nebo výběr laguje u reálného projektu.
10. 🔴 Data nelze exportovat nebo bezpečně přenést mimo Lasero.

---

# I. Konkrétní UX změny

1. Zachovat jedno pracovní prostředí: návrh uprostřed, nástroje vlevo, vrstvy/recepty/stroj vpravo, stav a job akce dole.
2. Nevytvářet přepínač Beginner/Professional. Pokročilé sekce jsou kontextové a pamatují poslední stav.
3. V toolbaru výběru ukazovat W/H/X/Y/rotaci a nejčastější operace; odstranit jejich plnou duplicitu z inspectoru.
4. Pravý inspector má nejvýše tři jasné části: Vrstvy, Zpracování, Stroj. Každá má stabilní pozici a vlastní empty state.
5. Řádek vrstvy je primární výrobní objekt, nikoli obecná „karta“. Je kompaktní, skenovatelný a dragovatelný.
6. „Náhled“ a „Rámování“ jsou rozdílné, vždy stejně pojmenované akce. Náhled je softwarová simulace; rámování fyzický pohyb stroje.
7. Placement používá dvě srozumitelné volby; anchor se zobrazí pouze pro aktuální polohu.
8. Warningy jsou inline a neruší modalem. Blocker zastaví start a nabídne opravu. Info pouze informuje.
9. Kontextové menu objektu: Duplikovat, Skupina, Vrstva, Pořadí, Zrcadlit, Zamknout, Odstranit. Méně časté operace v submenu.
10. Tooltips a menu vždy ukazují desktopové zkratky; focus a keyboard flow jsou plnohodnotné.
11. Stav „Uloženo“ je trvale jemně viditelný vedle názvu projektu.
12. Při běhu se editor nezničí ani nepřepíše; je dočasně read-only a hlavní plocha ukazuje průběh.

## Terminologie pro české UI

| Lasero | První nápověda pro migranty |
|---|---|
| Vrstvy | „Výrobní vrstvy (Cuts/Layers)“ |
| Čára | „Čára / řez (Line)“ podle operace |
| Výplň | „Výplň / gravírování (Fill)“ |
| Rámování | „Rámování (Frame)“ |
| Aktuální poloha laseru | „Current Position“ v tooltipech prvních použití |
| Absolutní poloha | „Absolute Coordinates“ v tooltipech |
| Nula zde | „Set Origin“ v tooltipech |
| Na nulu XY | „Go to Origin“ v tooltipech |
| Náhled úlohy | „Preview / simulace“ v nápovědě |
| Zahrnout do výstupu | „Output“ sekundárně |

---

# J. Development backlog připravený pro implementaci

## P0

1. **Implementovat DXF importer** pro LINE, LWPOLYLINE/POLYLINE, ARC, CIRCLE, ELLIPSE, SPLINE fallback, INSERT a jednotky; vytvořit fixture testy a import report.
2. **Rozšířit SVG compatibility test suite** o nested transformace, viewBox, skupiny, barvy, stroke/fill, text conversion a fyzické rozměry.
3. **Přidat pořadí výrobních vrstev** s drag & drop, undo/redo, persistencí a stejným pořadím v ToolpathBuilder, preview a G-code.
4. **Přidat režim Fill+Line** s jasným pořadím výplň před obrysem a společnými/oddělenými parametry podle rozhodnutí produktu.
5. **Implementovat snapping service** pro grid, střed, hrany a objekty v modelových souřadnicích; přidat Alt dočasné vypnutí a vizuální guides.
6. **Doplnit distribute a center-on-workspace commands** včetně composite undo a multi-selection testů.
7. **Doplnit z-order po jednom kroku** vedle existujícího front/back a přidat konzistentní context menu.
8. **Přidat Save As a SVG export** s round-trip testy, jasnými upozorněními na raster/unsupported vlastnosti.
9. **Rozšířit `.lasero` manifest** o material ID/name/thickness, operation, recipe ID/revision a explicitní layer execution order; přidat migraci v4 → v5.
10. **Vytvořit UX preflight mapper**: `PreflightIssue.Code → title, explanation, severity, repair command`; odstranit holý blokující text jako jediný feedback.
11. **Zavést offline entitlement cache** pro lokální editor/výrobu po prvním přihlášení; síťové funkce jasně označit a frontovat.
12. **Zavést editor performance instrumentation** a optimalizovat invalidaci canvasu, gridu, thumbnails a raster preview podle rozpočtu 60 fps.
13. **Přidat integrační parity test**: SVG import → vrstvy → placement → preview → framing document → generated G-code sdílí stejné bounds a pořadí.
14. **Harden expected error handling** v Materials, importu, tracingu, projektu a připojení tak, aby nevyústilo do globálního crash dialogu.

## P1

1. Implementovat PDF import s výběrem stránky, vector/raster volbou a import reportem.
2. Implementovat Subtract, Intersect a Difference nad uzavřenými cestami s undo a self-intersection validací.
3. Implementovat Offset/Outline s vnějším/vnitřním směrem, join style a preview.
4. Přidat Material Test generator: rozsahy, počet kroků, bezpečné limity, labels a uložení receptu.
5. Doplnit raster crop, presety a synchronizovaný original/processed preview.
6. Přidat Export G-code a job repeat/history detail s uloženým strojním profilem a parametry.
7. Přidat mm/inch prezentační jednotky bez změny interních mm a fyzického rozměru.
8. Přidat basic node editing: move node, delete node, close/open path a převod segmentu.
9. Přidat keyboard nudge, custom shortcuts a úplné shortcut labels v tooltipech/menu.
10. Vytvořit multi-machine profile manager s capabilities a bezpečným přepnutím aktivního stroje.
11. Implementovat reconnect/recovery state machine pro disconnect, sleep, hold, alarm a pause s jasnou bezpečnostní politikou.
12. Přidat export diagnostického balíčku bez obsahu projektu, pokud uživatel explicitně nepovolí jeho přiložení.

## P2

1. Napojit kontextový Lasero Chat na vybranou vrstvu, materiál, stroj, preflight issue a historii výsledku; každou změnu aplikovat až po potvrzení.
2. Implementovat guided outcome feedback a verzované vlastní recepty.
3. Rozšířit preview o travel moves, layer timeline, heat-density warning a scrubber.
4. Přidat inside-first a engrave-before-cut automatickou optimalizaci s preview změn.
5. Implementovat camera overlay abstrakci a kalibrační workflow pro podporovaný hardware.
6. Implementovat capability-based accessory controls pro air assist, exhaust, autofocus a Z.
7. Přidat onboarding variantu „Používal jsem LightBurn“ se čtyřmi mapovacími hinty, ne samostatným UI režimem.

## P3

1. Rotary profily, kalibrace a test rotation.
2. Kerf compensation a pokročilé cut optimization.
3. Rozšířené node/path operace, nesting a arrays.
4. AI/EPS import přes izolovanou konverzní vrstvu.
5. Makra, firmware management a servisní controller tools.
6. Kamera s live capture, distortion correction a material alignment.

---

# Porovnávací tabulka

| Oblast | LightBurn user očekává | Lasero dnes | Lasero musí umět | Priorita |
|---|---|---|---|---|
| Připojení | profily a předvídatelný stroj | port/baud + detekce GRBL plochy + virtuální laser | automatický discovery a diagnostika | 🔴 P0 |
| SVG | věrný import | podporováno | compatibility suite + report | 🔴 P0 |
| DXF | běžný CAD import | chybí | 2D DXF s jednotkami | 🔴 P0 |
| PDF | import výrobních podkladů | chybí | vector/raster page import | 🟠 P1 |
| Raster | fotografie/logo | grayscale, threshold, dither, jas, kontrast, invert, DPI | crop a presety | 🟠 P1 |
| Trace | bitmapa na vektor | podporováno | polish a original/vector compare | 🟠 P1 |
| Transformace | přesný numerický editor | podporováno | performance a konzistence | 🔴 P0 |
| Multi-select/group | běžná práce | podporováno | hardening | 🔴 P0 |
| Align/distribute | přesné layouty | align ano, distribute ne | doplnit distribute | 🔴 P0 |
| Snapping | rychlá přesnost | chybí | grid/object/edge/center | 🔴 P0 |
| Booleany | základ výroby | pouze sjednotit | subtract/intersect/difference | 🟠 P1 |
| Offset | řezací obrys | chybí | inner/outer outline | 🟠 P1 |
| Vrstvy | barva = parametry | per-color Line/Fill | Fill+Line, pořadí, DnD | 🔴 P0 |
| Materiály | vlastní knihovna | offline katalog, presets, sync | projektová metadata + test | 🔴 P0 / 🟠 P1 |
| Origin | absolute/current/user origin | absolute/current + anchors + work zero | sjednotit UX a preview | 🔴 P0 |
| Framing | okamžitý Frame | podporováno | revision-safe workflow | 🔴 P0 |
| Preview | pořadí a čas | simulace a odhad | layer timeline + přesná shoda | 🔴 P0 |
| Projekt | vše znovu použitelné | `.lasero`, layers, placement, raster assets | material/order + Save As/export | 🔴 P0 |
| Autosave | žádná ztráta práce | 30s recovery | viditelné Uloženo a state hardening | 🟠 P1 |
| Console | pokročilá diagnostika | podporováno | copy/export logs | 🟠 P1 |
| Units | mm/inch | mm | mm/inch presentation | 🟠 P1 |
| Offline | lokální výroba | účet je při startu povinný | cached entitlement | 🔴 P0 |
| Rotary/camera | podle workflow | chybí | capability-based roadmap | ⚪ P3 / 🟡 P2 |
| Výkon | plynulý lokální nástroj | vyžaduje měření; dříve hlášené lagy | performance budget + profiling | 🔴 P0 |

Lasero je připravené na release pro tuto personu až tehdy, když uživatel zvládne referenční 30minutový scénář bez návratu do LightBurnu, bez ztráty rozměrů nebo pořadí a bez hledání základní funkce v externím grafickém editoru.
