# Civil Site Design knowledge for CSD-Bridge

Sources: Civil Site Design's help, knowledgebase and release notes (from search excerpts, Nov 2026); dumps of the CSD V27 dialogs taken in BricsCAD V26. Where a fact was not confirmed it says so. When this disagrees with what a dialog shows, trust the dialog.

## overview: How to work with CSD
- CSD (Civil Site Design, formerly Advanced Road Design, ARD) is a .NET add-in running in BricsCAD. Its dialogs are WinForms windows the csd_ tools can read and operate from inside BricsCAD.
- Usual loop: find the command (csd_commands) → open the dialog (csd_run_command) → csd_list_dialogs → csd_read_dialog → change values (csd_set_values) or press buttons (csd_press) → read back to confirm → read the volumes (csd_read_volumes).
- Most CSD dialogs are modeless: BricsCAD keeps working while they are open. Some (the template editor) are modal and block BricsCAD until closed. While a CSD command or a modal dialog is running, the drafting tools can't run; the csd_ tools still work.
- CSD changes are not in BricsCAD's UNDO. Record the values you change (read them first) so they can be put back, and tell the user what you changed.
- CSD message boxes and file dialogs are ordinary Windows dialogs: csd_list_dialogs shows them as "win32" dialogs with their text and buttons; csd_press presses their buttons and csd_set_values can type into their edit boxes (for example a file name).
- CSD needs an existing surface for most road work.

## commands: Typed commands
- Commands use the legacy prefix ARDUV7 (many are ARDUV7AJEB...). csd_commands lists the commands this installation actually defines (read from CSD's assemblies) and the commands behind its ribbon/menu buttons (from CSD's menu file, C:\ProgramData\CSS\ARD\Menu\*.cui). Always take names from csd_commands; don't guess.
- Confirmed in CSD's documentation:
  - ARDUV7EDITDEFAULTS: Active Drawing Settings (per-drawing defaults including the VGE section: crest/sag K, max/min grade warnings, minimum VC length, segment length).
  - ARDUV7AJEBINTERSECTIONMANAGER: Intersection Manager.
  - ARDUV7AJEBUPDATEPROJECTEDPROFILES: Update Projected Profiles.
  - ARDUV7REMOVEARDRUNFLAG: stop creating a -Data folder for a drawing with no CSD data.
- Seen in forums or stack traces only (check with csd_commands): ARDUV7EDITTEMPLATE (template editor), arduv7PlotProfiles, arduv7SelectDisplay, ARDUV7INDEXROAD.
- Ribbon tabs: General, Surfaces, Roads, Pipes, Data Share. Roads panels: Design, Select, Edit, Templates, Modelling, Reports, Plan Production.
- Useful ribbon commands (find their typed names with csd_commands, filter by the button name): Create/Edit Road (Roads > Design), Vertical Grading Editor (Roads > Select), Open Cross Section (Roads > Select), Update All Road Objects (Roads > Edit), Create/Edit Section (Roads > Templates), Rebuild Models (Roads > Modelling), Volume (Roads > Reports), Create/Edit Surface and BricsCAD Surfaces (Surfaces tab), Thread Editor / Run Thread.
- Many commands prompt at the command line first (for example "select alignment" or "select road"). csd_run_command then reports the command as still running; ask the user to answer the prompt in BricsCAD, then call csd_list_dialogs.

## dialogs: CSD dialogs and their controls
Names are the internal form names csd_list_dialogs shows.
- frmPopUp, title = road/string name (e.g. "MC01 C.L."): the Vertical Grading Editor (VGE). Long section viewport, its own ribbon (Ribbon1, not readable as buttons), a status strip (IP number, chainage, RL, grades in/out), a hidden legacy panel (pnlLegacyMenu) of buttons that back the ribbon commands: cmdVGEByGrid (grid editor), cmdBalance, cmdVol, cmdInsert, cmdDelete, cmdMove, cmdRaiseLower, cmdUnDo, cmdReDo, cmdSave ("OK"), cmdJustSaveData ("Save"), cmdCancel. Hidden buttons need allow_hidden in csd_press. Hidden label lblVols holds the volumes readout (csd_read_volumes). Status-strip item strRebuildModels = Rebuild Model.
- frmVGEGrid, title "Grading": the grading grid editor, opened from the VGE (cmdVGEByGrid). Grid flexVC: one row per IP. Visible columns: Distance (chainage), Level, Elevation Change, Back Grade %, Forward Grade %, VC Length, Min VC Length (13 columns in all; read them with csd_read_dialog include_hidden to see the rest). The last row is empty (new IP). Check box chkUpdateModels = Auto Update. Buttons (icons, by name): cmdDelete (delete the highlighted IP), cmdAdjustAll / cmdAdjustSelected (raise/lower IPs), cmdAdjustChainages (shift chainages), cmdAddIPFromScreen, cmdDrape (add IPs to match surface levels at selected sections), cmdFixVC (VC), cmdZoomAll, cmdZoomCurrent.
- frmEditor2, title "Edit Design Data for <road>": the Design Data form. Grid dataFlex lists entries (Templates, Variations, Batters, Design Constraints, Stripping, Compaction Factors, Subgrade, Super, Table Drains...; "+"/"-" rows expand/collapse groups). Selecting an entry fills the edit panel, whose fields follow the pattern <type><entry no.><field>_x<n>, e.g. txtD006StartChainage_x2, cbo006Code_x1, cbo001TemplateName_x3. Buttons: cmdAdd (Add/Merge entry), cmdDelete, cmdCopy, cmdMoveUp, cmdMoveDown, cmdCancelEdit. Menu: File > Import Data / Export Data; Tools > Cross Section Viewer, Vertical Grading Editor, Volume Report, Subgrade Variations, Special Drains Editor, Insert Extra Sections, Shift Chainages, Excel Export, Excel Link, ScriptX Editor; Recalculate; Rebuild Models.
- frmTemplates, title "Editing Template: <name>": the section template editor (modal). Grid flexGrid: Leg | left side (Horizontal Distance, Slope %, Vertical Distance, Plot Y/N, Code) | right side (same columns). Buttons: cmdSelect (Select Template), cmdPrevious/cmdNext, cmdInsertLine, cmdDeleteLine, cdmAddsection (Create Section), cmdAddKerb, CommandButton2/3 (batters left/right), cmdAddExtra/cmdEditExtra (subgrade), cmdOK, cmdCancel. Menu File: New/Copy/Delete template, import/export template packs.

## vge: Vertical Grading Editor workflow
- Open: Create/Edit Road on an alignment that already has a road opens the VGE (on one without a road it opens the Create Road form first). The Vertical Grading Editor command (Roads > Select) also opens it for a road or string.
- IPs can be edited graphically (needs mouse clicks) or in the grading grid (frmVGEGrid), which suits automation: grid and graphics update each other and edits apply to the road straight away.
- VC Length 0.001 means no vertical curve. Min VC Length is the minimum for sight distance. A K value (m per % grade change) gives a suggested VC length: L = K × |A|.
- Design aids live in Active Drawing Settings (ARDUV7EDITDEFAULTS) > VGE: crest K, sag K, max/min grade warnings, minimum VC length, segment length (ideal minimum IP spacing). Use those as the design limits when the user hasn't given any.
- File menu: Export VC / Import VC ("From File"): a plain text file, one line per IP: chainage, elevation, VC length. csd_write_vc_file writes one. The exact format is from the documentation; compare it with a file exported from the VGE before relying on it.
- V26+ has an Optimise tab (CSD's own profile optimisation). The user has said it doesn't balance well yet; use design_vertical_profile unless asked.
- With Auto Update on, every edit recomputes sections and models (slow on big jobs). Fast Mode turns that off; then press Rebuild Model before reading volumes.

## editing_grid: Editing the grading grid safely
1. Read the grid first (csd_read_dialog dialog="frmVGEGrid") and keep the original IP table.
2. Change levels (and chainages, VC lengths) row by row with csd_set_values, one grid cell per change, top to bottom. Use the column names shown by csd_read_dialog.
3. To add an IP, type its Distance in the empty last row; to delete, select the row (select: true) and press cmdDelete. Re-read the grid after adding or deleting: rows renumber.
4. Read the grid back: CSD recomputes the grades and elevation changes. Check every level and chainage took.
5. Read the volumes (csd_read_volumes) after the models update. If Auto Update is off, press Rebuild Model first.
If a change didn't take, try commit "enter" (types Enter like a user) instead of the default.

## design_data: Design Data form
- Templates by chainage: entries with Start Chainage, End Chainage, Template Name. Ranges decide which template applies where.
- Variations change the section code by code, applied in list order. "Set Code Offset to Alignment" (widen to alignment) takes a code (e.g. LEB), chainage range, widen method and target alignment; the help warns not to compound it with other variations.
- Other entries: batters, stripping, compaction factors (used in volumes), subgrade, superelevation, table drains, design constraints (max/min levels drawn in the VGE), ScriptX custom variations.
- Recalculate recomputes every entry. Excel Link gives each entry an INDEX (its position in the form) with its inputs in the columns to the right; saving the workbook updates the design.

## templates: Section templates
- A template is a list of legs, each ending at a code (C.L., LEB, LINV, LTK, LBK, LFPI, LFPO, LBDY...; R... on the right).
- Per leg: Horizontal Distance, then either Slope % or Vertical Distance (leave the other blank), Plot Y/N. Slope sign is side-dependent: on the left + is clockwise, on the right + is counter-clockwise; positive vertical distance is up.
- Subgrade (pavement) layers are set between code pairs. Batters and daylighting are in Intelligent Sections (1:x slope plus end code).

## volumes: Earthworks volumes
- The VGE shows a running volumes readout (csd_read_volumes): Stripping, Total cut, Total fill, Pavement fill, and a net line ("CUT Total Vol" = cut − fill; "FILL Total Vol" when fill exceeds cut). Units are the drawing's (m³).
- The readout can be blank until the models update. With Auto Update off (Fast Mode), press Rebuild Model.
- The full Volume Report (Roads > Reports > Volume) gives section-by-section areas and volumes to CSV and CAD tables.
- Rebuild Models rebuilds models with the Dynamic flag; the Compute Design Models thread rebuilds all of them. Update All Road Objects is needed when Plan Drafting is Manual.

## ground: Existing ground along the centreline
- CSD keeps its own surfaces (Create/Edit Surface); they are not BricsCAD TIN surfaces. sample_ground_profile reads BricsCAD TIN surfaces, so first make a BricsCAD copy with CSD's Surface Exchange (Surfaces tab > BricsCAD Surfaces), or use a BricsCAD TIN the user already has.
- On BricsCAD V26, Surface Exchange needs CSD's hotfix for bricsCADSurfaceStyles.dat if it errors.
- The centreline to sample is the road's alignment polyline in the drawing (ask the user to select it if it isn't obvious). Check that its direction and start chainage match CSD's (the grading grid's first Distance).
- Other routes: the VGE long section shows the surface; the grid tool "add IPs to match surface levels" (cmdDrape) changes the design, so only use it on a copy.

## vertical_method: How this office wants vertical design done
1. Read the current design (grading grid) and the design limits (Active Drawing Settings, or ask). Sample the ground along the centreline.
2. Check the current design with evaluate_vertical_profile.
3. Design with design_vertical_profile, in this priority: even grades within the limits; as few IPs and grade changes as possible (raise min_improvement or lower max_added_ips if it adds too many); only then minimise centreline cut/fill. Keep intersections, tie-ins and any levels the user gives as fixed_ips.
4. Show the user the proposed IP table and the before/after centreline cut/fill. Apply it only when they agree (or asked you to go ahead).
5. Apply through the grading grid, read it back, then read the volumes. Centreline cut/fill is only a guide to the earthworks: report CSD's volumes as the real result.
CSD has no good balancing function yet, so balance is not the first goal; it comes after an even, simple profile.

## data: Data, files and versions
- CSD data lives in a "<drawing>-Data" folder next to the DWG (road data in AdvRoads-10), unless Project Storage/Pack mode puts it in the DWG. The formats are not documented: don't edit them directly.
- Settings and menus: C:\ProgramData\CSS\ARD\ (menu file Menu\CSDBricsV26.cui, shared threads in Common\Common-10).
- Threads (Thread Editor / Run Thread) are CSD's batch macros: Compute Design Models, Rebuild Models, Synchronise Labels, Excel Export/Link and more.
- CSD V26.20+ supports BricsCAD V26 (needs BricsCAD V26.1.07 or later). CSD V27 adds "Project Assist AI", its own in-product assistant.
