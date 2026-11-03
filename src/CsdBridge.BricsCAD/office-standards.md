<!--
  CSD-Bridge office drafting standards.

  Everything below this comment is added to Claude's instructions on every request.
  Replace the EXAMPLE values with your office's real standards. Keep it short and
  specific: rules Claude can apply directly beat general guidance.

  To share one file across the office, put it on a network drive and set
  "standards_file" in %APPDATA%\CsdBridge\settings.json. Changes apply to new
  conversations (AIRESET or "New chat").
-->

EXAMPLE STANDARDS - replace before use.

### Units and scale
- Drawings are in millimetres, modelled at 1:1 in model space.
- Unless told otherwise, assume plotting at 1:100 for plans and 1:20 for details.

### Layers
Use these layers (create them with ensure_layers if missing). Colour is ACI, lineweight in mm.

| Layer | Use | Colour | Linetype | Lineweight |
|---|---|---|---|---|
| S-GRID | Grid lines | 8 | CENTER | 0.18 |
| S-GRID-TEXT | Grid bubbles and labels | 7 | CONTINUOUS | 0.25 |
| S-CONC | Concrete outlines (slabs, beams, columns, walls) | 7 | CONTINUOUS | 0.35 |
| S-CONC-HIDDEN | Concrete below / hidden edges | 8 | HIDDEN | 0.25 |
| S-STEEL | Structural steel members | 4 | CONTINUOUS | 0.35 |
| S-REO | Reinforcement | 1 | CONTINUOUS | 0.25 |
| S-DIMS | Dimensions | 3 | CONTINUOUS | 0.18 |
| S-TEXT | Notes and labels | 7 | CONTINUOUS | 0.25 |

### Text and dimensions
- Plotted text heights: 2.5 mm general notes, 3.5 mm titles, 5 mm drawing titles.
  Multiply by the plot scale for model space (2.5 mm at 1:100 = 250).
- Notes in UPPER CASE.
- Dimension in millimetres with no unit suffix, using the drawing's current dimension style.
- Place dimension lines 10 mm (plotted) clear of the object.

### Conventions
- Grid lines are labelled with numbers in one direction and letters in the other (skip I and O).
- Draw member outlines as closed polylines.
