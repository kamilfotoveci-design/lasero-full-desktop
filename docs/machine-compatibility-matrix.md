# Machine compatibility — 2026-09-10

No priority target is hardware verified. A catalog entry, another application's configuration and offline tests do not establish direct integration. New priority selections fail closed before opening a transport. The existing manually selected generic GRBL/simulator workflow remains available, without a model-specific compatibility claim. Saved profile keys and workspace data were not migrated.

| Exact identity | Module / firmware scope | Interface evidence | LASERO implementation and offline status | GUI / hardware | Limitation / next step |
|---|---|---|---|---|---|
| AlgoLaser MK2 — provisionally Alpha MK2 | Identity must be confirmed; 20 W and 40 W differ. Support page lists 208+130; exact applicability unverified. | Official Alpha tutorial describes USB GRBL and front-left origin. | Catalog and service rejection tested; existing shared GRBL stack hardened. No new executable Alpha profile. | Not performed / not performed | Confirm exact model/module; obtain current configuration, identity transcript, power scaling, origin and firmware scope. |
| AlgoLaser PIXI | 3 W / 5 W / 10 W; exact controller/firmware/module mapping unresolved. | Manufacturer lists LightBurn/LaserGRBL workflows; exact manual/configuration not content-verified. | Catalog and service rejection tested. Matching 100×100 travel no longer assigns PIXI identity. | Not performed / not performed | Obtain model-specific USB configuration, identity/settings transcript and module limits. |
| xTool F2, standard (not Ultra/UV) | 15 W blue / 5 W IR; direct-interface firmware scope unknown. | Current FAQ excludes LightBurn. Studio USB/Wi-Fi/IP and SVG/DXF/bitmap import documented; no public F2 SDK found. | Direct control rejected and tested. Documented original-artwork handoff to Studio; no LASERO design exporter or settings transfer implemented. | Not performed / not performed | Manufacturer request prepared; verify actual import size and artwork in Studio under a separately authorized session. |
| xTool S1 | 20 W / 40 W distinguished; guide says firmware >004. Other modules unresolved. | Official LightBurn config: GRBL serial 115200, DTR false, S scale 1000, no $J, buffered transfer. | Strict offline M1111 parser with machine/module/firmware/height calibration scope: 24 tests. Direct execution unavailable. | Not performed / not performed | Resolve configuration/manual area conflict, startup/end commands, identity, framing, buffering and completion/cancel semantics. |
| AlgoLaser DIY KIT MK2 | 5 W / 10 W; not original, Mini or MK3; exact firmware scope unresolved. | Exact support page and product located. Linked USB profile names Delta and is NOT evidence for MK2. | Separate catalog entry and service rejection tested; no guessed driver/constants. | Not performed / not performed | Obtain exact MK2 USB profile/manual details, settings and identity transcript. |

## Constants are evidence, not execution defaults

- PIXI product: 100×100 mm, 6000 mm/min.
- Alpha MK2 product: 20 W 400×410 mm; 40 W 400×386 mm. Current advertised image/movement speeds differ by module. Existing 20000 default is retained only for source compatibility; not asserted as a device maximum.
- DIY KIT MK2 product: 400×435 mm, 12000 mm/min.
- S1 user guide: 40 W 498×319 mm, 20 W 498×330 mm. Linked configuration says 508×390: unresolved safety conflict, not used as executable bounds. Disabled offset Y=21 is not calibration.
- F2 FAQ: 115×115 mm. This does not authorize motion or supply origin/units/protocol semantics.

All named-target machine capabilities remain unresolved/unverified in the catalog. Unsupported, unresolved and not-applicable are distinct types; no unsupported manufacturer capability is invented. The connect gate is enforced by GrblConnection before transport opening, not merely a disabled button. Existing generic GRBL operations are implemented/offline-tested, but not verified for these exact models.

## Evidence register

Retrieved 2026-09-10. Titles identify exact pages; manufacturer marketing values are not protocol specifications.

| Page | Scope / finding / conflict |
|---|---|
| [AlgoLaser Alpha MK2 machine support](https://algolaser.com/pages/algolaser-alpha-mk2-machine-support) | Exact family; support firmware label and third-party workflow; linked Google manual not content-verified. |
| [Connect LightBurn with AlgoLaser Alpha MK2](https://algolaser.com/blogs/how-to/connect-lightburn-with-algolaser-alpha-mk2-laser-engraver) | USB GRBL, front-left origin; approximate 400×400 conflicts with exact product specs. |
| [Why isn't LightBurn detecting my laser machine?](https://algolaser.com/blogs/how-to/why-isnt-lightburn-detecting-my-laser-machine) | 115200 guidance; not proof for every model/firmware. |
| [Alpha MK2 product](https://algolaser.com/products/algolaser-alpha-mk2-diode-laser-engraver-algolaser) | Separate 20 W/40 W dimensions and advertised speeds. |
| [PIXI machine support](https://algolaser.com/pages/algolaser-pixi-machine-support) | Exact model workflows; manual is a Drive folder, not read as a protocol specification. |
| [PIXI product](https://algolaser.com/products/algolaser-pixi-smart-laser-engraver-with-enclosure) | 3/5/10 W dimensions/speed. |
| [DIY KIT MK2 support](https://algolaser.com/pages/algolaser-diy-kit-mk2-machine-support) | Exact MK2; no extrapolation from MK3/original. |
| [DIY KIT MK2 product](https://algolaser.com/products/diy-kit-mk2-10w-smart-enclosed-diode-laser-engraver) | 5/10 W specs; downloadable USB profile names Delta and is excluded. |
| [DIY KIT MK2 linked PDF](https://cdn.shopify.com/s/files/1/0826/1402/6547/files/0729_DIY_KIT_MK2_160X215.pdf?v=1724910499) | 20.8 MB exceeds web extraction limit; contents not verified. |
| [Operate S1 with LightBurn](https://support.xtool.com/article/1036) | Firmware >004, USB, buffered transfer, height-dependent offset, raster overscan. |
| [Official-linked S1 configuration](https://drive.google.com/file/d/1pIeSPyD6ZZNTe4-oKt9-Asa1Vr8UUgfX/view?usp=sharing) | Downloaded and decoded by research agent; HomeOnStartup=true NOT imported. Start $L/M109 S1/M96 S0/M110 X1Y1Z1/M7 S1 and end M6 NOT executed or copied into a driver. |
| [S1 user guide](https://support.xtool.com/article/1106) | Updated 2026-06-22; usable module work areas and physical button workflow. |
| [S1 LightBurn troubleshooting](https://support.xtool.com/article/1099) | Raster/constant-power guidance; pause/resume indications can lag. |
| [S1 lid detection](https://support.xtool.com/article/1088) | M802 reports detection configuration, not current lid-closed telemetry. |
| [F2 FAQ](https://support.xtool.com/article/2818) | Updated 2026-06-03; standard F2 lacks LightBurn support. No conclusion about undisclosed partner APIs. |
| [System optimization and upgrade notification](https://support.xtool.com/article/3078) | Updated 2026-03-16; named restrictions on P2S/F1 variants do not supply S1/F2 firmware thresholds. |
| [Connect F2 to Studio](https://support.xtool.com/article/2657) | Studio USB/Wi-Fi/IP connectivity; not public protocol documentation. |
| [Set up F2 in Studio](https://support.xtool.com/article/2658) | Studio exports GCode; does NOT establish external GCode import or GRBL streaming. |
| [Studio cannot find F2 USB](https://support.xtool.com/article/2654) | Standard F2 uses Studio, not XCS application. |
| [Import and parse vectors](https://support.xtool.com/article/2520) | SVG/DXF accepted; SVG DPI and DXF units affect dimensions. |
| [Studio import failures](https://support.xtool.com/article/2805) | Plain SVG, text to paths; PNG/JPG/JPEG/BMP accepted. No processing parameter transfer claim. |

## F2 design handoff (not direct control)

Use the original Plain SVG with text outlined, DXF with explicit units, or supported original bitmap in Studio. Do not export LASERO machine G-code as a substitute for artwork. LASERO currently has no full scene-to-SVG exporter; editor changes are not carried by reopening the original artwork. Verify width/height, placement, paths, image appearance, source and all processing parameters in Studio. A 10×10 mm test SVG and bitmap should be imported and compared before relying on this workflow. That live import check was not performed. No laser source, speed, power, pass count, framing or start action transfers through this documented handoff.