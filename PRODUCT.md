# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

Primárne ide o majiteľov a operátorov GRBL laserových gravírok v dielni alebo domácom makerspace. Používateľ potrebuje pripraviť návrh, nastaviť parametre, skontrolovať pracovnú plochu a bezpečne spustiť úlohu.

## Product Purpose

Lasero Desktop je Windows aplikácia na návrh jednoduchých laserových úloh, komunikáciu s GRBL zariadením, prípravu G-code, rámovanie a sledovanie priebehu práce. Úspech znamená, že používateľ vie od importu po spustenie úlohy postupovať bez hľadania ovládacích prvkov a bez nejasnosti o stave stroja.

## Positioning

Pracovný editor a ovládací panel v jednom: návrh, kontrola pracovnej plochy a bezpečné spustenie sú spojené v jednom súvislom workflow.

## Operating Context

Používa sa pri reálnom stroji, často v tmavšej dielni, s potrebou rýchlo skontrolovať port, polohu, rámovanie, výkon, rýchlosť a stav úlohy. Dôležité sú okamžité stavové informácie a bezpečné potvrdenia kritických akcií.

## Capabilities and Constraints

Existujúca funkcionalita zahŕňa GRBL sériové pripojenie, jogging, homing, odomknutie, nastavenie nuly, reset, import SVG/rastrov, vektorový canvas, vrstvy, undo/redo, náhľad dráhy, framing, streamovanie G-code, pauzu, zastavenie a konzolu. Projekt používa .NET 8, WPF, MVVM a CommunityToolkit.Mvvm. Ukladanie projektov, textový nástroj, skupiny a pokročilé geometrické operácie sú zatiaľ otvorené alebo nedokončené.

## Brand Commitments

Názov Lasero Desktop a červený akcent značky zostávajú zachované.

## Product Principles

- Najprv bezpečnosť a stav stroja.
- Najčastejší workflow musí byť viditeľný bez zahltenia.
- Pokročilé možnosti sú dostupné kontextovo.
- Každá kritická akcia má jasnú spätnú väzbu.

## Accessibility & Inclusion

Použiteľnosť pri rôznych veľkostiach okna, dostatočný kontrast, zrozumiteľné názvy akcií, tooltipy pre ikonové ovládanie a klávesové skratky pre bežné editorové operácie.
