# Draft — standard xTool F2 integration request

Prepared 2026-09-10. NOT SENT. Sending requires explicit approval.

Subject: LASERO integration request — standard xTool F2 (15 W blue / 5 W IR)

We develop LASERO desktop software and request the supported integration path for the standard F2, distinct from F2 Ultra and F2 Ultra UV. Your F2 FAQ states LightBurn is unsupported and previous open-protocol connections were replaced. Your setup guide describes Studio GCode export but does not document external GCode input or a control API.

Please confirm availability of a public SDK/API, licensed partner interface, authorized developer program, or documented Studio file/CLI/deep-link handoff. Please provide:

1. Supported firmware, Studio/plugin versions, exact model identification, USB/network protocol and discovery rules.
2. Authentication/pairing, permissions, access provisioning and key-management responsibilities.
3. Job format, validation, transfer/acknowledgement, buffer limits and accepted-versus-physically-completed semantics.
4. Status, progress, errors, timeouts, pause/resume/cancel, disconnect and reconnect recovery without automatic replay.
5. Blue/IR source selection, units, origin, coordinate transformation, offsets, work area and accessory distinctions.
6. Focus, visible-pointer/motion/processing-laser framing distinctions and physical confirmation requirements.
7. Interlocks and enclosure/emergency-stop state exposed by the interface; whether a stop acknowledgement confirms laser shutdown.
8. Whether Studio-exported GCode is intended for interoperability and whether any supported path accepts external GCode.
9. If file handoff only: SVG/DXF/bitmap specification, DPI/units, text/path/layer treatment, embedded images and a documented format for processing parameters, if any.
10. Licensing, redistribution, NDA, certification and test-device requirements.

Please route this to engineering/developer partnerships. We can provide representative artwork and a staged non-emitting validation plan. Public evidence reviewed and exact limitations are recorded in machine-compatibility-matrix.md.

Contact route: https://support.xtool.com/submit-ticket or published support@xtool.com. No credentials extracted, security mechanisms bypassed or firmware changed.