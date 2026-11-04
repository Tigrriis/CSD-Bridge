<!--
  CSD-Bridge office drafting standards (civil).

  Everything below this comment is added to Claude's instructions on every request.
  Built from the office's exported drawings (2026-C51-PR02, 2024-C20-PR06). Edit it to
  correct or add rules; keep it short and specific.

  To share one file across the office, put it on a network drive and set
  "standards_file" in %APPDATA%\CsdBridge\settings.json. Changes apply to new
  conversations (AIRESET or "New chat"). Road drafting rules for draft_road are in
  road-drafting.json.
-->

### Units and coordinates
- Civil drawings are in metres, on real map coordinates (MGA). Never move geometry to the origin.
- Plan linework is 2D at elevation 0 unless it is a CSD 3D string.

### Layers
Names are `AE <discipline> <type> - <description> <status>`: discipline CIV (civil), GEN (general), STR (structural);
type PLN (plan linework), HTC (plan hatches), ANO (annotation), DET (detail linework), SEC (sections), MOD (models and
surfaces), LNE (general lines by weight), TBL (title block); status NEW, EXISTING or DEMO.
- NEW work uses the layer's own colour. EXISTING layers are grey (colour 8, or 128,128,128); DEMO layers are colour 20
  (255,63,0) and usually a DEMOLITION linetype or DASHED.
- Leave colour, linetype and lineweight ByLayer. Use existing layers; create a missing one following the pattern and
  the table below.

| Layer | Colour | Linetype | Lineweight |
|---|---|---|---|
| AE CIV PLN - ROAD CENTRELINE NEW | 6 | CENTERX2 | 0.50 |
| AE CIV PLN - ROAD PAVEMENT EDGE NEW | 6 | Continuous | 0.50 |
| AE CIV PLN - ROAD SHOULDER NEW | 2 | Continuous | 0.50 |
| AE CIV PLN - KERB NEW | 2 | Continuous | 0.35 |
| AE CIV PLN - FOOTPATH EDGE NEW | 6 | Continuous | 0.50 |
| AE CIV PLN - LINEMARKING | 7 (237,237,237) | Continuous | 0.35 |
| AE CIV PLN - BATTER LINE / BATTER TADPOLE | 2 | Continuous | 0.35 |
| AE CIV PLN - STORMWATER TABLE DRAIN NEW | 2 | Continuous | 0.35 |
| AE CIV PLN - STORMWATER PIPE NEW | 94 (0,166,0) | STORMWATER_GRAVITY_NEW | default |
| AE CIV PLN - STORMWATER PIT NEW / IO NEW | 94 (0,166,0) | Continuous | default |
| AE CIV PLN - SEWER PIPE NEW | 1 (255,0,0) | SEWER_GRAVITY_NEW | default |
| AE CIV PLN - SEWER PIT NEW / IO NEW | 1 (255,0,0) | Continuous | default |
| AE CIV PLN - WATER POTABLE NEW | 5 (0,0,255) | WATER_POTABLE_NEW | 0.50 |
| AE CIV PLN - SUBSURFACE DRAIN NEW | 68 | SUBSURFACE_DRAIN_NEW | 0.50 |
| AE CIV PLN - RETAINING WALL NEW | 2 | Continuous | 0.35 |
| AE CIV PLN - BUILDING NEW | 2 | Continuous | 0.35 |
| AE CIV HTC - ASPHALT TYPE 1 | 151 (181,199,255) | Continuous | 0.18 |
| AE CIV HTC - CONCRETE NEW | 254 (214,214,214) | Continuous | 0.18 |
| AE CIV HTC - GRAVEL NEW | 254 (250,238,213) | Continuous | 0.18 |
| AE CIV HTC - RETAINING WALL | 250 (51,51,51) | Continuous | 0.18 |
| AE CIV LNE - TEMP | 220 | Continuous | non-plotting |

Colours in brackets are true colours (ensure_layers rgb). Other services follow the same pattern with their own
linetypes (COMMUNICATIONS_*, POWER_UNDERGROUND_*, WATER_FIRE_*, SEWER_PRESSURE_* ...).

### Plan drafting
- Areas are closed polylines with true arcs (no chorded curves), hatched SOLID on the matching AE CIV HTC layer,
  the hatch behind the linework. One outline per area; islands for holes.
- Road centreline, pavement edge and footpath edge polylines have entity linetype scale 0.5.
- Table drain flow arrows: ARROWR (or ARROWL) linetype on the table drain line, linetype scale 5 to 10.
- Temporary construction lines go on AE CIV LNE - TEMP (non-plotting); erase them when done.

### Annotation
- Annotation goes on AE CIV ANO - <topic> layers (ALL PLANS, GRADING, SEWER, STORMWATER, EXISTING, DETAIL),
  mostly multileaders, in the drawing's current text style. Notes in UPPER CASE.

### Civil Site Design naming
- Surfaces: NS = existing (natural) ground, BM = design surfaces, PST = display surfaces, VOL = volume surfaces.
- CSD code strings are on CSD-3D-<road>-<code> (and CSD-2D-<road>-<code> when exported as 2D). Road names such as
  CL-01; GR-... are grading strings. Common codes: C.L., LEB/REB (edge of seal), LSH/RSH (shoulder),
  KERB_LEFT/RIGHT, FOOTPATH_LEFT/RIGHT, BATTER_LEFT/RIGHT.
- CSD's own working layers (ARD-*, ARM-*, CSD-*, SURF-*, Align-*, ALIGN-TEMP, AR-Marker) are not drafting: don't draw on
  them or change them.
- Vertical design limits (grades, K values, minimum VC) aren't set here yet: ask the user, or read them from Active
  Drawing Settings (ARDUV7EDITDEFAULTS).
