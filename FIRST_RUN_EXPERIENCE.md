# Lasero Desktop — first-run experience

Status: implementační UX specifikace  
Cílová verze: Windows desktop, light mode, čeština  
Primární uživatel: člověk s první laserovou gravírkou, bez znalosti GRBL, G-code a laserového softwaru

Navazující specifikace pro zkušené uživatele a migraci z LightBurnu je v [`LIGHTBURN_MIGRATION_EXPERIENCE.md`](LIGHTBURN_MIGRATION_EXPERIENCE.md). Obě persony používají stejné rozhraní s progresivním odhalením funkcí, nikoli dva oddělené režimy.

## Rozhodnutí, která platí pro celý flow

- Cílem onboardingu není vysvětlit aplikaci, ale bezpečně dokončit první výrobek.
- Uživatel vždy vidí jednu hlavní další akci a krátce ví, co bude následovat.
- Lasero nejprve zjišťuje automaticky, potom výsledek potvrdí a technické volby ukáže pouze při problému.
- První fyzická úloha používá vedený režim. Editor zůstává reálným editorem, nejde o oddělenou tutorialovou maketu.
- Přihlášení je produktová podmínka. Musí ale vysvětlit hodnotu účtu: trvalé přihlášení, Vzorník, Lasero Chat, licence a budoucí synchronizace. Po prvním úspěšném přihlášení se už neopakuje, dokud relaci nelze bezpečně obnovit.
- Editor funguje bez připojeného stroje. Jen fyzické akce vysvětlují, proč jsou nedostupné.
- Rámování je v prvním flow povinné. V běžném provozu zůstává bezpečnostní podmínkou podle nastavení stroje.
- Nevratné, rizikové a servisní funkce nejsou součástí základního rozhraní.

## Současný stav a využitelné základy

Lasero již má persistentní přihlášení, virtuální GRBL laser, detekci GRBL nastavení a pracovní plochy, framing, preflight, řízení úlohy, autosave každých 30 sekund a obnovu projektu po pádu. Tyto schopnosti se nemají přepisovat; musí se spojit stavovým first-run koordinátorem.

Aktuální `OnboardingWindow` je pouze statická informační obrazovka. Neověřuje připojení, bezpečnost, pohyb, materiál, rámování ani dokončení úlohy. Připojení stále začíná výběrem portu a baud rate. Preflight vrací užitečné blokující důvody, ale většinou pouze jako text bez konkrétní opravné akce. Chybí také success flow po dokončení a kontextová pomoc podle aktuálního kroku.

---

# A. Kompletní first-run experience

## A1. Instalace

1. Uživatel stáhne podepsaný instalátor z oficiálního webu Lasero.
2. Instalátor před spuštěním jasně ukáže vydavatele „Lasero / Fotověci“ a verzi.
3. Preferovat per-user instalaci bez administrátorského oprávnění. O zvýšení oprávnění požádat pouze při instalaci ovladače.
4. Instalátor ověří Windows 10/11, volné místo a přístup k lokálním datům. Nesrozumitelnou technickou podmínku přeloží do běžného jazyka.
5. USB/serial ovladač neinstalovat preventivně. Po prvním neúspěšném hledání zařízení spustit diagnostiku a nabídnout správný ovladač podle nalezeného USB ID.
6. Firewall řešit pouze tehdy, když uživatel zapne síťové hledání nebo Wi-Fi zařízení. Lokální editor a USB provoz nesmí vyžadovat firewall výjimku.
7. Aktualizace jsou in-app: „Aktualizovat a restartovat“. Uživatel znovu nestahuje EXE.

Pokud se objeví SmartScreen, webová nápověda a instalátor používají stejné copy: „Windows může při prvním spuštění zobrazit kontrolu aplikace. Ověřte vydavatele Lasero a pokračujte pouze u instalátoru staženého z lasero.net.“ Cílem je podepsaná aplikace bez běžného SmartScreen varování, ne návod k jeho obcházení.

## A2. Stavový model prvního spuštění

`Welcome → Account → DeviceSearch → DeviceReady → Safety → MotionCheck → ProjectChoice → Material → Operation → Recommendation → Placement → Framing → Preflight → Running → Result → Completed`

Každý stav ukládá dokončení lokálně. Po pádu nebo restartu se pokračuje od posledního bezpečného bodu. Stav `Running` se nikdy automaticky neobnoví; po návratu se nejprve ověří stav stroje.

## A3. Celá cesta uživatele

1. Vítejte v Lasero — příslib první úspěšné výroby.
2. Přihlášení — stejný účet jako lasero.net, relace se bezpečně zapamatuje.
3. Najít gravírku — automatický scan USB, modelu a dostupných vlastností.
4. Potvrzení zařízení — model, výkon a pracovní plocha.
5. Jedna stručná bezpečnostní obrazovka.
6. Ověření komunikace bezpečným pohybem Home.
7. Výběr jednoduchého demo projektu.
8. Výběr materiálu podle lidského názvu a vizuálního vzorku.
9. Výběr cíle: gravírovat, řezat, označit.
10. Automatické doporučení parametrů pro konkrétní stroj.
11. Umístění návrhu a jasný rozměr v mm.
12. Rámování s potvrzením, že návrh sedí na materiálu.
13. Preflight shrnutí s opravnými akcemi.
14. Spuštění, přehledný průběh, pauza a zastavení.
15. Výsledek, rychlá zpětná vazba a případná pomoc Lasero Chat.
16. Ukončení onboardingu a přechod na plnohodnotný Domov.

---

# B. Onboarding obrazovky

## B1. Vítejte

- **Headline:** Vítejte v Lasero
- **Text:** Připravíme vaši gravírku a společně dokončíme první jednoduchý projekt. Zabere to přibližně 5 minut.
- **Primary CTA:** Začít
- **Secondary:** Prozkoumat bez zařízení
- **Tertiary:** Už Lasero znám → přeskočit
- **Vizuál:** jednoduchá lineární cesta `Zařízení → Materiál → Návrh → Kontrola → Hotovo`, bez carouselu.
- **Po kliknutí:** „Začít“ otevře přihlášení; demo režim přeskočí hledání fyzického stroje a připojí virtuální laser; přeskočení otevře Domov se skrytým nenápadným návratem „Dokončit nastavení“.

## B2. Přihlášení

- **Headline:** Přihlaste se do Lasero
- **Text:** Použijte stejný účet jako na lasero.net. Přihlášení bezpečně uložíme v tomto počítači, abyste se nemuseli přihlašovat znovu.
- **Důvod v jedné řádce:** Účet zpřístupní licenci, Vzorník materiálů, Lasero Chat a synchronizaci receptů.
- **Primary CTA:** Přihlásit se
- **Secondary:** Potřebuji pomoci s přihlášením
- **Vizuál:** čistý formulář e-mail + heslo, odkaz „Zapomenuté heslo“, zřetelný stav odesílání.
- **Po kliknutí:** tlačítko přejde do stavu „Přihlašuji…“, úspěch automaticky pokračuje, chyba zůstane u formuláře a nabídne řešení. Žádné tiché kliknutí.

## B3. Najdeme vaši gravírku

- **Headline:** Připojte a zapněte gravírku
- **Text:** Připojte USB kabel. Lasero zkusí zařízení najít a nastavit automaticky.
- **Primary CTA:** Najít zařízení
- **Secondary:** Použít virtuální laser
- **Tertiary:** Používám Wi‑Fi připojení
- **Vizuál:** tři ilustrované kroky: napájení, USB, hledání. Během scanu checklist „USB zařízení → komunikace → model → pracovní plocha“.
- **Po kliknutí:** scan portů probíhá bez zobrazení COM a baud rate. Vhodné kombinace se zkoušejí řízeně, ostatní sériová zařízení se neotevírají agresivně.

## B4. Gravírka nalezena

- **Headline:** Gravírka je připravena
- **Text:** `{Model} · {výkon}`. Pracovní plocha `{šířka} × {výška} mm` byla načtena ze zařízení.
- **Primary CTA:** Pokračovat
- **Secondary:** Toto není moje zařízení
- **Vizuál:** zelený check, název modelu, typ připojení, plocha a podporované funkce. Žádná technická konzole.
- **Po kliknutí:** profil se uloží k účtu/lokálnímu zařízení a otevře se bezpečnost.

## B5. Zařízení nenalezeno

- **Headline:** Gravírku se nepodařilo najít
- **Text:** Zkontrolujte, že je zapnutá, připojená datovým USB kabelem a není otevřená v jiném programu.
- **Primary CTA:** Zkusit znovu
- **Secondary:** Spustit diagnostiku
- **Tertiary:** Vybrat zařízení ručně
- **Vizuál:** checklist s živým výsledkem. Port, baud a driver až po rozbalení „Technické informace“.
- **Po kliknutí:** diagnostika rozliší chybějící driver, obsazený port, nulovou odpověď a nepodporovaný firmware a nabídne konkrétní opravu.

## B6. Než začneme

- **Headline:** Než se gravírka poprvé pohne
- **Text:** Při práci používejte odpovídající ochranu, odvětrání a nikdy nenechávejte aktivní laser bez dozoru. Ujistěte se, že je pracovní prostor volný a znáte nouzové zastavení svého zařízení.
- **Primary CTA:** Rozumím a prostor je připravený
- **Secondary:** Ukázat bezpečnost mého modelu
- **Vizuál:** maximálně pět stručných bodů a modelově specifická poloha krytu, interlocku a emergency stopu, pokud jsou známé.
- **Po kliknutí:** souhlas se uloží pro tuto verzi bezpečnostních pravidel a otevře ověření pohybu.

## B7. Ověření pohybu

- **Headline:** Ověříme, že zařízení reaguje
- **Text:** Po kliknutí se gravírka přesune do výchozí pozice. Zkontrolujte, že má volnou dráhu.
- **Primary CTA:** Přesunout do výchozí pozice
- **Secondary:** Moje gravírka nemá Home
- **Vizuál:** jednoduchý náhled směru pohybu a trvale viditelné „Zastavit pohyb“.
- **Po kliknutí:** Lasero odešle Home, sleduje reálný stav a po `Idle` ukáže „Pohyb funguje správně“. Alarm nabídne odemknutí s vysvětlením, ne kód.

## B8. První projekt

- **Headline:** Vytvořme váš první projekt
- **Text:** Začněte jednoduchým návrhem, na kterém si bezpečně projdete celý postup.
- **Primary CTA:** Použít zkušební projekt
- **Secondary:** Otevřít vlastní soubor
- **Tertiary:** Prázdný projekt
- **Vizuál:** náhled malého motivu „LASERO“ nebo jednoduché geometrie s časem do dvou minut.
- **Po kliknutí:** otevře se skutečný editor s připraveným objektem a tříkrokovou kontextovou nápovědou.

## B9. Editor poprvé

- **Headline v kontextové vrstvě:** Připravíme návrh ke gravírování
- **Text:** 1. Toto je pracovní plocha stroje. 2. Modrý rámeček je váš návrh. 3. Vpravo vyberete materiál a doporučené nastavení.
- **CTA:** Vybrat materiál
- **Secondary:** Rozumím, chci pokračovat sám
- **Vizuál:** tři krátké callouty postupně, nikdy více než jeden současně; zbytek editoru zůstává interaktivní.
- **Po kliknutí:** otevře výběr materiálu s předvolenou kategorií pro první projekt.

## B10. Co budete gravírovat?

- **Headline:** Vyberte materiál
- **Text:** Vyberte nejbližší typ. Doporučení můžete později upravit podle konkrétního vzorku.
- **Primary CTA:** Pokračovat s `{materiál}`
- **Secondary:** Nevím, jaký materiál mám
- **Vizuál:** fotografie/vzorky kategorií Dřevo, Překližka, Akryl, Kůže, Kov, Další; následně materiál, varianta a tloušťka v mm.
- **Po kliknutí:** výběr se zapíše do projektu. „Nevím“ otevře kontextový Lasero Chat s připravenou otázkou a možností přidat fotografii.

## B11. Co chcete udělat?

- **Headline:** Jaký výsledek chcete?
- **Primary možnosti:** Gravírovat — obrázek nebo text na povrchu; Řezat — vyříznout tvar skrz; Označit — lehké povrchové značení.
- **CTA:** Zobrazit doporučené nastavení
- **Secondary:** Nejsem si jistý
- **Vizuál:** tři srovnatelné výsledky na stejném materiálu, ne technické ikony bez popisu.
- **Po kliknutí:** filtruje Vzorník podle stroje, technologie, výkonu, materiálu, tloušťky a operace.

## B12. Doporučené nastavení

- **Headline:** Doporučené nastavení pro váš materiál
- **Text:** Doporučeno pro `{výkon stroje}` a `{materiál}`. Před ostrou úlohou ověřte výsledek na odřezku.
- **Hodnoty:** Rychlost `{x} mm/min`, výkon `{y} %`, průchody `{n}×`, případně rozestup/DPI.
- **Primary CTA:** Použít doporučení
- **Secondary:** Upravit ručně
- **Tertiary:** Uložit jako můj recept
- **Vizuál:** čtyři čitelné metriky, poznámka a bezpečnostní upozornění. Žádné nečitelně dlouhé typové názvy vrstvy.
- **Po kliknutí:** hodnoty se aplikují na cílovou vrstvu a změna je viditelná v inspektoru i preflight shrnutí.

## B13. Umístěte návrh

- **Headline:** Umístěte návrh na materiál
- **Text:** Přesuňte a upravte velikost návrhu. Aktuální rozměr je `{šířka} × {výška} mm`.
- **Primary CTA:** Nastavit aktuální polohu laseru jako začátek
- **Secondary:** Použít absolutní souřadnice
- **Vizuál:** zvýrazněný návrh, viditelná pracovní plocha a materiálový obdélník. V prvním flow není vidět matice job origin ani strojní terminologie.
- **Po kliknutí:** aktuální poloha se uloží jako referenční bod projektu; absolutní režim použije nulu stroje a vysvětlí rozdíl jednou větou.

## B14. Rámování

- **Headline:** Zkontrolujte umístění
- **Text:** Gravírka obkreslí hranice projektu s vypnutým pracovním výkonem. Sledujte, zda se návrh vejde na materiál.
- **Primary CTA:** Spustit rámování
- **Secondary:** Upravit polohu
- **Vizuál:** bezpečný stav stroje, jasné „Rámování probíhá“ a trvale dostupné zastavení.
- **Po skončení:** otázka „Sedí umístění?“ s volbami „Ano, pokračovat“ a „Upravit polohu“. Potvrzení označí aktuální revizi dokumentu jako zarámovanou.

## B15. Připraveno ke gravírování

- **Headline:** Všechno je připravené
- **Souhrn:** zařízení, materiál, rozměr projektu, operace, rychlost/výkon/průchody, rámování a odhad času.
- **Primary CTA:** Spustit gravírování
- **Secondary:** Zpět do návrhu
- **Vizuál:** řádky s checky. Blokující problém nahradí check srozumitelnou akcí, například „Umístit návrh dovnitř“.
- **Po kliknutí:** poslední živý preflight, bezpečné spuštění a přechod do zjednodušeného runtime zobrazení.

## B16. Gravírování

- **Headline:** Gravírování
- **Obsah:** velké procento, zbývající čas, aktuální vrstva a stav stroje.
- **Primary CTA:** Pozastavit
- **Destructive:** Zastavit úlohu
- **Vizuál:** jeden dominantní progress, žádné editovatelné parametry. „Pozastavit“ vysvětluje možnost pokračovat; „Zastavit“ vyžaduje potvrzení důsledku.
- **Po kliknutí:** pauza přejde do „Pokračovat“. Stop odešle bezpečné ukončení, potvrdí stav stroje a označí úlohu jako nedokončenou.

## B17. Hotovo

- **Headline:** Hotovo — vaše první gravírování je dokončeno
- **Text:** Úloha trvala `{čas}`. Jak výsledek vypadá?
- **Volby:** Perfektní, Příliš světlý, Příliš tmavý, Něco se nepovedlo.
- **Primary CTA po hodnocení:** Vytvořit vlastní projekt
- **Secondary:** Prozkoumat Lasero
- **Vizuál:** střídmý success moment, náhled projektu a možnost přidat fotografii výsledku.
- **Po kliknutí:** problémové hodnocení nabídne jednu bezpečnou změnu receptu a Lasero Chat. Completion flag ukončí first-run; příště se otevře běžný Domov.

---

# C. First project flow

## Doporučený demo projekt

- Jedna seskupená vektorová značka nebo text „LASERO“.
- Výchozí rozměr přibližně 60 × 20 mm.
- Jedna barevná vrstva, režim Gravírovat/Výplň.
- Odhad pod dvě minuty na běžném 10–20W diodovém laseru.
- Bez řezání v prvním povinném projektu. Řezání lze zvolit pouze vědomě s dodatečným bezpečnostním upozorněním.

## Přesné kroky a podmínky

1. Demo objekt je na ploše a viditelně vybraný.
2. Uživatel musí vybrat materiál nebo explicitně zvolit „Použiji vlastní nastavení“.
3. Uživatel vybere operaci. Neaktivní operace se vysvětlí podle materiálu/stroje.
4. Doporučení se aplikuje na konkrétní vrstvu, ne globálně bez cíle.
5. Lasero ukáže rozměr návrhu v mm a hlídá pracovní plochu.
6. Uživatel zvolí aktuální polohu nebo absolutní souřadnice. První možnost je doporučená.
7. Rámování musí proběhnout pro aktuální revizi dokumentu. Změna geometrie, umístění nebo originu stav rámování zneplatní.
8. Preflight ověří spojení, živý stav, pracovní plochu, aktivní výstupní vrstvu, parametry, polohu a framing.
9. Spuštění je dostupné pouze bez blokujícího problému.
10. Runtime omezuje UI na stav, progress, pauzu, stop a bezpečnostní upozornění.
11. Po dokončení se úloha uloží do historie, projekt se autosave a zobrazí se hodnocení výsledku.

Metrika úspěchu: alespoň 80 % nových uživatelů dokončí demo flow bez otevření externího webu; medián od prvního startu po preflight je pod 8 minut a po připojení stroje pod 5 minut.

---

# D. Empty states

| Místo | Headline | Vysvětlení | Primary CTA | Secondary |
|---|---|---|---|---|
| Domov — projekty | Zatím nemáte žádný projekt | Vytvořte jednoduchý návrh nebo otevřete existující soubor. | Nový projekt | Otevřít projekt |
| Domov — úlohy | Dnes ještě neproběhla žádná úloha | Dokončené a přerušené úlohy se zobrazí zde. | Připravit projekt | — |
| Editor | Začněte návrhem | Přidejte text nebo tvar, případně importujte SVG či obrázek. | Přidat text | Importovat soubor |
| Vrstvy | Zatím nejsou žádné výrobní vrstvy | Přidejte objekt. Lasero pro něj vytvoří vrstvu, které nastavíte způsob zpracování. | Přidat objekt | Jak vrstvy fungují? |
| Materiály | Vyberte materiál pro bezpečné výchozí nastavení | Vzorník je dostupný offline a po přihlášení se synchronizuje s vaším účtem. | Otevřít Vzorník | Použít vlastní nastavení |
| Zařízení | Gravírka zatím není připojená | Zapněte ji, připojte USB a nechte Lasero zařízení najít. Editor můžete používat i bez ní. | Najít zařízení | Použít virtuální laser |
| Hledání materiálu | Nenašli jsme odpovídající materiál | Zkuste obecnější název nebo vytvořte vlastní recept. | Vymazat filtry | Zeptat se Lasero |
| Chat | S čím vám Lasero pomůže? | Chat zná aktuální projekt, materiál a obrazovku; technické údaje přidá jen s vaším souhlasem. | Doporučit nastavení | Diagnostikovat připojení |

---

# E. Error states

Každý stav obsahuje: co se stalo, co má uživatel udělat, jednu hlavní opravnou akci a sbalené „Technické informace“ s kódem pro podporu.

| Problém | Uživatelské sdělení | Primary CTA | Další možnost |
|---|---|---|---|
| Zařízení nenalezeno | Gravírku se nepodařilo najít. Zkontrolujte napájení a datový USB kabel. | Zkusit znovu | Spustit diagnostiku |
| Chybějící USB driver | Pro připojení této gravírky je potřeba ovladač USB. | Nainstalovat ovladač | Postup ruční instalace |
| Port je obsazený | Gravírku právě používá jiný program. Zavřete jej a zkuste připojení znovu. | Zkusit znovu | Který program ji může používat? |
| Kabel pouze nabíjí | Windows zařízení vidí, ale nelze s ním komunikovat. Zkuste datový USB kabel. | Hledat znovu | Jak poznat datový kabel |
| Nepodporovaný firmware | Zařízení odpovídá, ale Lasero jeho řízení zatím bezpečně nepodporuje. | Zkontrolovat kompatibilitu | Pokročilé připojení |
| Spojení přerušeno | Gravírka přestala odpovídat. Zkontrolujte USB kabel a napájení. | Obnovit spojení | Bezpečně ukončit úlohu |
| GRBL Alarm | Gravírka je z bezpečnostního důvodu uzamčená. Nejdříve zkontrolujte prostor stroje. | Odemknout po kontrole | Co znamená alarm? |
| Otevřený kryt | Bezpečnostní kryt nebo dveře jsou otevřené. Zavřete je a znovu ověřte stav. | Ověřit znovu | — |
| Aktivní koncový spínač | Některá osa je na koncovém spínači. Zkontrolujte, zda nic nebrání pohybu. | Přesunout do výchozí pozice | Otevřít diagnostiku |
| Neaktuální stav | Lasero čeká na aktuální stav gravírky. | Obnovit stav | Znovu připojit |
| Prázdná úloha | Projekt neobsahuje nic, co by gravírka mohla zpracovat. | Přidat objekt | Importovat soubor |
| Žádná výstupní vrstva | Žádný objekt není zahrnutý do úlohy. | Zahrnout vybranou vrstvu | Otevřít vrstvy |
| Objekt mimo plochu | Projekt přesahuje pracovní plochu o `{x} mm`. | Umístit dovnitř | Upravit ručně |
| Neplatný výkon | Výkon vrstvy musí být mezi 1 a 100 %. | Použít bezpečné doporučení | Upravit ručně |
| Neplatná rychlost | Rychlost musí být větší než nula. | Použít doporučení | Upravit ručně |
| Chybí rámování | Před spuštěním zkontrolujte umístění pomocí rámování. | Spustit rámování | Zpět do návrhu |
| Rámování už neplatí | Návrh nebo jeho poloha se od posledního rámování změnily. | Rámovat znovu | Zobrazit změny |
| Projekt nelze uložit | Projekt se nepodařilo uložit do zvoleného místa. Vaše rozpracovaná práce zůstává v automatické záloze. | Uložit jinam | Otevřít složku zálohy |
| Poškozený projekt | Soubor nelze bezpečně otevřít. Originál nebyl změněn. | Zkusit obnovit zálohu | Technické informace |
| Přihlášení vypršelo | Platnost přihlášení skončila. Projekt zůstává uložený; pro online funkce se přihlaste znovu. | Přihlásit se znovu | Pokračovat offline v editoru |
| Synchronizace selhala | Recept je uložený v tomto počítači a synchronizuje se později. | Zkusit znovu | Pokračovat offline |
| Neočekávaná chyba | Lasero bezpečně zastavilo komunikaci se strojem a uložilo obnovovací kopii projektu. | Restartovat Lasero | Zkopírovat kód pro podporu |

---

# F. Contextual help

| Kontext | Forma | Obsah |
|---|---|---|
| První připojení | automatická diagnostika | USB, driver, port, odpověď, firmware, stav uzamčení |
| Model zařízení neznámý | krátký průvodce + foto | Kde najít štítek a jak poznat řídicí jednotku |
| První Home | tooltip/callout | „Hlava se přesune do výchozí pozice. Uvolněte pracovní prostor.“ |
| Materiál neznámý | Lasero Chat | připravená otázka, možnost fotografie a doplňující dotazy |
| Výkon, rychlost, průchody | tooltip při prvním hover/focus | lidské vysvětlení a dopad změny, ne definice jednotky |
| Tloušťka | tooltip | proč je důležitá hlavně pro řezání a fokus |
| Umístění/origin | 30s video nebo animace | rozdíl „aktuální poloha“ a „nula stroje“ |
| První framing | jednorázový callout | „Rámování kontroluje hranice; před každou úlohou ověřte materiál.“ |
| Preflight problém | inline help | jedna opravná akce přímo u problému |
| Běh úlohy | tooltip na Pause/Stop | rozdíl mezi dočasným pozastavením a ukončením |
| Špatný výsledek | Lasero Chat | navrhne jednu konzervativní změnu a vysvětlí ji |
| Help centrum | vyhledávání | nejdříve akční návody, potom technická dokumentace |

Krátká videa jsou dobrovolná, 15–60 sekund, bez autoplay a vždy řeší jednu věc. Dismissnutý tip se znovu nezobrazuje; lze jej obnovit v Nápovědě.

---

# G. Beginner vs advanced

| Viditelné okamžitě | Pod „Pokročilé“ nebo „Technické informace“ |
|---|---|
| Název a stav zařízení | COM port a baud rate |
| Najít/připojit zařízení | ruční controller/firmware profil |
| Pracovní plocha v mm | raw GRBL `$` nastavení |
| Home, odemknout s vysvětlením, zastavit pohyb | G-code konzole a vlastní příkazy |
| Materiál, operace a doporučený recept | overscan, scan angle, bidirectional tuning |
| Rychlost, výkon, průchody | kerf compensation a pokročilá optimalizace dráhy |
| Aktuální poloha / absolutní souřadnice | detailní pracovní/strojní souřadnicové systémy |
| Rámování a preflight | ruční optimalizace pořadí, firmware update, controller reset |
| Progress, pauza, stop | detailní stream log a buffer telemetry |
| Běžné vrstvy: barva, čára/výplň, výstup, viditelnost | pokročilé rastrování, dithering a line interval tuning |

Pokročilé nastavení není jiný „profesionální režim“ celé aplikace. Je to rozbalitelná sekce u konkrétního problému. Tím zůstává UI stejné pro začátečníka i odborníka.

---

# H. Safety UX

## Safety ladder

1. **Preventivní omezení:** Spustit je disabled, pokud chybí stroj, dráha, platná vrstva nebo živý stav.
2. **Inline oprava:** problém se ukáže u akce s konkrétním řešením.
3. **Preflight:** jedno souhrnné místo před startem, bez série modalů.
4. **Potvrzení pouze pro důsledek:** stop běžící úlohy, reset, firmware a jiné rizikové akce.
5. **Nouzový stav:** trvale dostupné zastavení a čitelný stav stroje.

## Povinné kontroly před startem

- zařízení je připojené a odpovídá,
- stav není starší než dvě sekundy a stroj je `Idle`,
- kryt/interlock a limitní vstupy jsou v bezpečném stavu,
- dokument obsahuje vykreslitelnou dráhu,
- všechny výstupní vrstvy mají platné parametry,
- dráha je uvnitř pracovní plochy,
- framing odpovídá aktuální revizi dokumentu a placementu,
- materiál a operace nejsou zjevně nekompatibilní,
- uživatel vidí odhad času a použitá nastavení.

Reset, firmware, raw příkazy a změny controlleru musí mít vizuálně oddělenou sekci „Servis a pokročilé“. Nouzové zastavení nesmí být schované v menu ani nahrazené softwarovým tlačítkem, pokud má stroj fyzický emergency stop; UI musí uživatele odkázat i na něj.

---

# I. 10 Lasero WOW moments

1. Lasero samo obnoví trvalé přihlášení a otevře poslední pracovní kontext.
2. Najde gravírku bez dotazu na COM a baud rate.
3. Rozpozná firmware, dostupné osy a pracovní plochu.
4. Při problému samo určí, zda chybí driver, je port obsazený nebo zařízení neodpovídá.
5. Podle stroje, materiálu a operace nabídne použitelný recept jedním kliknutím.
6. Lidsky vysvětlí výkon a rychlost přesně ve chvíli, kdy je uživatel mění.
7. Upozorní na přesah a nabídne „Umístit dovnitř“ místo pouhého erroru.
8. Rámování vede jako přirozenou kontrolu, ne technickou specialitu.
9. Před startem vytvoří čitelné shrnutí a realistický odhad času.
10. Po výsledku „příliš světlý/tmavý“ navrhne bezpečnou úpravu a uloží vylepšený recept.

---

# J. 10 frustration risks

1. Povinný login bez vysvětlení, proč je účet nutný.
2. Kliknutí na přihlášení nebo připojení bez okamžitého busy stavu.
3. Ruční výběr COM/baud jako první krok.
4. Statický onboarding, který nic neověří a po zavření uživatele nechá v editoru.
5. Zařízení „připojeno“, ale bez jasného potvrzení modelu a plochy.
6. Výběr speed/power před výběrem materiálu a cíle.
7. Několik míst, kde se nastavuje tentýž origin, vrstva nebo recept.
8. Blokující preflight text bez tlačítka, které problém opraví.
9. Pauza a stop vypadají stejně nebo není jasný jejich důsledek.
10. Po dokončení se zobrazí pouze „Job complete“ a uživatel neví, jak výsledek zlepšit.

---

# K. Development backlog

## P0 — bez toho první zkušenost nefunguje

1. **First-run coordinator** — nový persistentní stavový model a navigace přes kroky B1–B17. Musí umět pokračovat po restartu a po dokončení se už neukázat.
2. **Welcome před loginem** — zachovat povinný účet, ale nejprve vysvětlit hodnotu; přidat busy, inline validation, reset hesla a jednoznačný error state.
3. **Automatické hledání zařízení** — bezpečný scanner portů/baudů, detekce GRBL a přechod na existující `IdentifyDeviceAsync`; porty skrýt z beginner flow.
4. **Connection diagnostics** — rozlišit driver, port busy, timeout, unsupported firmware a disconnect. Každý výsledek má opravnou akci.
5. **First-run safety + motion check** — uložený souhlas, modelově přizpůsobený text a ověření Home s trvalým stopem.
6. **Demo project** — jeden vestavěný projekt a automaticky vytvořená cílová vrstva.
7. **Material-first wizard** — materiál → operace → doporučený recept → aplikace na konkrétní vrstvu.
8. **Placement simplification** — doporučená aktuální poloha a sekundární absolutní souřadnice, bez matice originů v beginner flow.
9. **Actionable preflight** — mapovat kódy z `JobPreflight` na uživatelský text a command, ne pouze `PreflightMessage`.
10. **Runtime screen** — velký progress, zbývající čas, vrstva, Pause/Resume a potvrzený Stop.
11. **Completion flow** — výsledek, hodnocení a ukončení onboardingu.
12. **Crash-safe first run** — využít existující recovery; startup musí nabídnout návrat ke kroku a nikdy automaticky nepokračovat v běžící fyzické úloze.

### P0 acceptance criteria

- Nový uživatel nevidí COM, baud, G-code ani matice originu v základní cestě.
- Každá obrazovka má právě jednu vizuálně dominantní akci.
- Demo lze dokončit s virtuálním laserem bez fyzického zařízení.
- Spustit nelze bez aktuálního preflightu a platného rámování.
- Po restartu se neopakuje dokončený onboarding ani přihlášení s platnou relací.

## P1 — musí být ve verzi 1.0

1. Offline Vzorník s plnou základní databází a synchronizací vlastních receptů po přihlášení.
2. Přehledné empty states D a návrat k přerušenému nastavení z Domova.
3. Automatické in-app aktualizace a recovery po update.
4. Kontextová nápověda, vyhledávání a diagnostika přímo v aplikaci.
5. Modelová databáze: název, výkon, plocha, osy, homing, kryt, interlock, emergency stop a ovladač.
6. Telemetrie first-run funnelu bez ukládání obsahu projektu: step viewed/completed, error category, retry, skip, time-to-preflight, completion.
7. Lokalizovatelné české texty v resources; žádné hardcoded technické copy v viewmodelech.
8. Přístupnost: keyboard flow, focus order, screen-reader názvy, 200% scaling, high contrast a reduce motion.
9. Jednotné moderní dialogy pro všechny očekávané chyby; systémový MessageBox pouze jako poslední nouzový fallback.
10. Uživatelské testy s minimálně pěti úplnými začátečníky a Grandma test bez externí pomoci.

## P2 — výrazně lepší onboarding

1. Kontextový Lasero Chat s bezpečně předaným strojem, materiálem, krokem a chybou.
2. Analýza fotografie výsledku a konzervativní návrh úpravy receptu.
3. Krátká modelově specifická videa pro připojení, Home, materiál a framing.
4. Wi‑Fi discovery a průvodce párováním podporovaných zařízení.
5. Detekce neznámého materiálu pomocí řízených otázek/fotografie.
6. Jednoklikový testovací vzorník před ostrou úlohou.
7. Proaktivní varování při neobvyklé kombinaci výkonu, rychlosti, materiálu a operace.
8. Režim „Už jsem používal LightBurn“ s kratším flow a sekundární známou terminologií.

## P3 — advanced

1. Pokročilé GRBL profily, ruční serial konfigurace a export/import profilů.
2. Firmware management s kontrolou kompatibility a recovery plánem.
3. Rozšířené rastrování, dithering, overscan, scan angle a optimalizace dráhy.
4. Kerf test, fokus test, vícevrstvé kalibrační testy a knihovna maker nástrojů.
5. Servisní konzole s exportem diagnostického balíčku pro podporu.
6. Více zařízení, síťová fronta a řízené střídání strojů.

---

## Implementační členění v současné architektuře

- `FirstRunCoordinator` drží stav, persistence a přechody; nepatří do `OnboardingWindow.xaml.cs`.
- Jednotlivé kroky mají vlastní malé viewmodely nebo jeden koordinovaný viewmodel se striktně typovanými stavy.
- `ConnectionViewModel` zůstává zdrojem připojení, ale dostane službu pro discovery/diagnostics místo UI logiky portů.
- `JobPreflight` zůstane čistá doménová validace; aplikační vrstva mapuje `Code` na UX copy a opravné commands.
- `MaterialsViewModel`/Vzorník poskytne doporučení přes query objekt `machine + technology + power + material + thickness + operation`.
- `GCodeViewModel` publikuje runtime snapshot pro onboarding i běžný spodní panel.
- `ProjectRecoveryStore` doplní first-run checkpoint, nikoli stav fyzicky běžící úlohy.
- Veškeré copy se přesune do lokalizačních resources a přístupné názvy se testují spolu s XAML.

## Testovací matice před označením P0 za hotové

- Čistý Windows profil, první instalace a první login.
- Platná uložená relace, expirovaná relace a offline start.
- Žádný port, jeden podporovaný stroj, více portů, obsazený port, chybějící driver.
- Stroj bez Home, se Z osou, bez Z osy, otevřený interlock a alarm.
- Virtuální laser jako plnohodnotná deterministická cesta.
- 100%, 125%, 150% a 200% DPI; 1366×768 až 4K; maximalizované okno bez ořezu.
- Klávesnice bez myši, čtečka obrazovky a reduced motion.
- Pád během materiálu, před framingem, po framingu a během běžící úlohy.
- Projekt mimo plochu, neplatná vrstva, změna po framingu a odpojení během běhu.

První spuštění je hotové pouze tehdy, když úplný začátečník bezpečně dokončí skutečný nebo virtuální demo projekt a při žádném kroku nemusí hledat odpověď mimo Lasero.
