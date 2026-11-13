# CSD-Bridge

A BricsCAD add-in that lets you ask Claude to draft in the open drawing ("draw a 6 m x 4 m slab
outline on S-CONC with a 2.4 m grid, and dimension it"). The add-in calls the Claude API itself.
There is no MCP server and no Claude desktop app in the loop.

```
BricsCAD ── CSD-Bridge add-in ── Claude API
              │  agent loop + drafting, geometry, road and vehicle tools
              └─ draws, edits, asks you to pick points / select objects
```

## What it does

- **AIDRAFT** (command line) or **AIPANEL** (dockable chat panel): describe what you want.
  Both are also on the **CSD-Bridge ribbon tab**.
- **Routines** (ribbon, or `AIROUTINES`): saved, reusable procedures with inputs. Do a job once with
  Claude in the chat, then save the chat as a routine: Claude writes it up with its inputs and steps, and
  you check it before it's saved. Run it later on any drawing by filling in a few values. Routines can be
  shared with the office. See [Routines](#routines).
- **AIHISTORY** opens a history window:
  - every request, across all drawings and days, searchable
  - each one's full detail: what you asked, every step Claude took, its reply, the result and
    token use
  - updates live while a request runs
  - "Run again", "Edit in chat" and "Copy request" buttons
- Claude reads the drawing setup (units, layers, blocks, styles), plans the geometry, then draws it
  in batches: lines, polylines, circles, arcs, text, mtext, linear/aligned dimensions, and block
  inserts with attribute values.
- It can edit by handle (move, copy/array, rotate, scale, erase, change layer/colour).
- **Exact geometry, not mental arithmetic.** Claude decides *what* to do; BricsCAD's own geometry
  engine works out *where*:
  - `calculate` for sums and trigonometry
  - `offset_entities` for cover lines, wall thicknesses and setbacks
  - `intersect` for where things meet, including when extended
  - `measure` for lengths, areas, closest points and distances
  - `points_on_curve` for set-out by divisions, spacing or distances
  - `trim_extend`, `fillet` (e.g. kerb returns) and `join_curves` (closed outlines) for clean finished linework
  - `hatch` by closed outline, by picking a point inside an area, or by explicit outline
  - `offset_by_chainage` for road lines built exactly from an alignment (constant offsets keep true arcs,
    tapers are straight), and `analyse_offsets` to read the offsets and widenings of existing lines
    such as CSD's exported code strings, ignoring the chords between CSD's sample sections
- It can **ask you to click a point or select objects** mid-request, which an external MCP
  client can't do naturally.
- Objects you select *before* asking are passed in as context ("dimension these").
- Conversations are per drawing, so follow-ups work ("make it 300 wider").
- The whole request is **one UNDO step**. Esc or Stop cancels.
- **Vehicle turning paths (AS/NZS 2890.1 and Austroads).** **AITURN**, or the **Turn path** ribbon button,
  simulates a B99 or B85 car, or an Austroads truck (8.8 m service vehicle, 12.5 m truck, 19 m
  semi-trailer, 25 m and 26 m B-doubles), and draws its swept path and the standard's clearance
  template: a standard turn from a picked start point, a vehicle driven along a line or polyline
  you select (forwards or in reverse), or through points you click. **Solve turn** works a
  multi-point turn out for you: pick where the car starts and where it ends up, and it finds the
  forward and reverse movements between the kerbs, walls and columns. **In and out** does the same
  through a stop on a site, so the vehicle enters forwards and leaves forwards. It works in drawings in
  metres or millimetres. Layers, clearances, what is drawn and the vehicles themselves are
  configurable (`turn-paths.json`). Claude has the same
  simulator as the `draw_turn_path` tool. See [Vehicle turning paths](#vehicle-turning-paths-asnzs-28901) below.
- **Civil Site Design.** Claude can drive CSD's dialogs from inside BricsCAD (see
  [Civil Site Design](#civil-site-design) below), and design vertical profiles with exact
  optimisation rather than guessing levels.
- Claude can only use these tools. It cannot run arbitrary commands, LISP, scripts or open files.

## Status

This is a working scaffold for exploration, not a finished product.

| Part | State |
|---|---|
| Core (agent loop, tools, validation) | Built and unit-tested: 260 tests, including the full loop run against the real Anthropic SDK with canned API responses |
| BricsCAD add-in, V26 (.NET 8) | Compiles against BricsCAD V26 reference assemblies. **Not yet run inside BricsCAD.** |
| BricsCAD add-in, V24/V25 (.NET Framework 4.8) | Same source. **Not yet compiled** (needs V25's `BrxMgd.dll`/`TD_Mgd.dll` on Windows). |
| Live Claude API | **Not yet called**: the build machine had no API key. Run the CLI harness first (below). |

See [docs/architecture.md](docs/architecture.md) for the design, and "First things to test" there.

## Repository layout

```
src/CsdBridge.Core/        CAD-independent: Claude agent loop, tool schemas, input validation,
                           in-memory drawing + DXF preview (netstandard2.0 + net8.0)
src/CsdBridge.BricsCAD/    The add-in: commands, chat panel, BricsCAD drawing host, settings
  Csd/                     Civil Site Design tools: commands, dialogs, ground sampling
  Routines/                Routines window, editor and run dialog
src/CsdBridge.Core/Civil/  Vertical profile designer (exact optimisation) and checks
src/CsdBridge.Core/Routines/ Routine files, the routines library, run prompts, save_routine
src/CsdBridge.Core/Vehicles/ AS/NZS 2890.1 design vehicles, swept path simulator and the draw_turn_path tool
tools/CsdBridge.Cli/       Try prompts without BricsCAD; writes a DXF preview
tools/icons/               Generates the ribbon icons (Python + Pillow)
tests/CsdBridge.Core.Tests Unit tests (no API key or BricsCAD needed)
install/                   Update script (pull, build, install) and autoload registration
docs/architecture.md       Design notes, threading, caching, risks, roadmap
```

## Quick start

### 1. Try it without BricsCAD (any OS)

Needs the .NET 8 SDK and an Anthropic API key from https://console.anthropic.com. Run these
from the repo folder (`cd CSD-Bridge` after cloning). In Windows PowerShell:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-api03-..."
```

(On macOS/Linux: `export ANTHROPIC_API_KEY=sk-ant-api03-...`.)

```bash
dotnet run --project tools/CsdBridge.Cli -- --standards src/CsdBridge.BricsCAD/office-standards.md "Draw a 6000 x 4000 slab outline with grid lines at 2400 centres both ways, labelled, and dimension the overall size"
```

This draws into an in-memory drawing and writes `preview.dxf`; open it in BricsCAD to look.
Run with no prompt for an interactive session. `pick_point` and `select_entities` prompt in
the console. It is the quickest way to tune prompts, tool descriptions and office standards.

To try a routine: `--routine "path\to\Routine.md" --input road=MC01 --input max_grade=7` (defaults fill
the other inputs). With `--routines <folder>`, `--routine` also takes a routine's name, and when you ask
Claude to save a routine it is written straight to that folder (there's no review step in the CLI).

### 2. Build the add-in (Windows, BricsCAD installed)

From the repo folder:

```bash
dotnet build src/CsdBridge.BricsCAD -c Release
```

The build finds the newest BricsCAD under `C:\Program Files\Bricsys` and prints which one it
used. V24/V25 build for .NET Framework 4.8 and V26 onwards for .NET 8. To pick a version, add
`-p:BricsCADVersion=25`. If BricsCAD is installed elsewhere, add
`-p:BricsCADDir="D:\Apps\BricsCAD V25 en_US"`. If it can't find BricsCAD, the build stops
with one message listing the BricsCAD folders it did find.

The output goes to `src/CsdBridge.BricsCAD/bin/Release/V<version>/`. V26 and later need the
x64 .NET 8 Desktop Runtime on the user's machine.

### 3. Load it

1. In BricsCAD: `NETLOAD`, then pick `CsdBridge.BricsCAD.dll` from the output folder.
2. `AIKEY`, then paste your Anthropic API key. It is stored encrypted for your Windows account.
3. `AIPANEL` opens the chat panel, or use `AIDRAFT` on the command line.

To load automatically at startup:
`install\Register-Autoload.ps1 -DllPath <full path to dll> -BricsCADVersion 25`.

### 4. Updating after changes

From the repo folder:

```bash
powershell -ExecutionPolicy Bypass -File install\Update.ps1
```

The script does four things:
- pulls the latest code
- builds for the newest BricsCAD installed (BricsCAD can stay open while it builds)
- if the add-in is in use, asks you to close BricsCAD
- copies the build to `C:\CsdBridge`

Then start BricsCAD again, and `NETLOAD` unless autoload is registered. Useful options:
- `-BricsCADVersion 25` builds for a specific version.
- `-RegisterAutoload` also sets up loading at startup.
- `-InstallDir D:\Tools\CsdBridge` installs somewhere else.
- `-SkipPull` builds what you already have, without pulling.

The update doesn't touch your API key or `settings.json`. If `C:\CsdBridge\office-standards.md`
was edited, the old copy is saved as `office-standards.md.bak`. To keep edits across updates,
make them in `src\CsdBridge.BricsCAD\office-standards.md`, or point `standards_file` at a
shared copy. Changes to `settings.json` or the standards file need no rebuild: just run
`AIRESET`.

### 5. Automatic updates (optional)

```bash
powershell -ExecutionPolicy Bypass -File install\Register-AutoUpdate.ps1
```

This creates a Windows scheduled task, "CSD-Bridge auto-update", that runs `Update.ps1 -Unattended`
when you log in and daily at 07:30 (`-DailyAt 12:30` to change). Each run does one of four things:
- If nothing has changed, it does nothing.
- If BricsCAD has the add-in loaded, it skips and tries again next time.
- Otherwise it builds and installs the update, and BricsCAD uses it from its next start.
- If the build fails, it leaves the installed add-in untouched.

Each run is logged to `%LOCALAPPDATA%\CsdBridge\logs\update.log`. To run it now, use
`Start-ScheduledTask -TaskName 'CSD-Bridge auto-update'`. To turn it off, run the same script
with `-Remove`.

## Commands

| Command | What it does |
|---|---|
| `AIDRAFT` | Ask Claude on the command line. Uses any pre-selected objects as context. |
| `AIPANEL` | Open the chat panel (Enter sends, Shift+Enter for a new line, Stop cancels) |
| `AIROUTINES` | Open the Routines window: run, create, edit, rename, duplicate or delete routines |
| `AIHISTORY` | Open the history window: every request and what Claude did |
| `AITURN` | Draw an AS/NZS 2890.1 B85 or B99 vehicle swept path: a turning template, a vehicle driven along a selected line/polyline, or driven click by click (forward and reverse legs). Opens a dialog for the vehicle, radius, clearances, what to draw and the layers. |
| `AITURNEDIT` | Change a turning path drawn by `AITURN`: pick any part of it, adjust the vehicle, radius, clearances, layers or conflict layers in the dialog, then regenerate it as it was, pick its geometry again, or (drive) continue from where it ended. |
| `AITURNSOLVE` | Work out a multi-point turn: pick where the vehicle starts and where it ends up (position and direction at each), and the forward and reverse movements between the linework on the solve boundary layers are found and drawn. The same as `AITURN` with **Solve (2 points)** chosen. See [Solving a multi-point turn](#solving-a-multi-point-turn). |
| `AITURNINOUT` | Show forward entry and exit from a site: pick where the vehicle arrives, where it stops on the site and where it leaves, and the movements are found so that it drives in forwards and out forwards, with any reversing done on the site. The same as `AITURN` with **Solve in and out (3 points)** chosen. See [In and out of a site](#in-and-out-of-a-site-the-3-point-solve). |
| `AICLEARANCE` | Check a B85 or B99 car's ground clearance (AS/NZS 2890.1 Appendix C): slide the template along a plotted long section, or drive along a path and check each side of the car against a BricsCAD TIN surface, with wheel path long sections plotted. |
| `AIEFFORT` | Choose how hard Claude works: Auto, Quick, Normal or Thorough (also the Effort box in the chat panel) |
| `AIMODEL` | Choose the model: Opus, Sonnet or Haiku (also the Model box in the chat panel) |
| `AIRESET` | New conversation for this drawing; reloads settings |
| `AIKEY` | Set or remove the stored API key |
| `AISTATUS` | Show model, key source, standards file, settings and log locations, usage |
| `AIUIDUMP` | Diagnostic: after a countdown (default 10 s), saves the controls of BricsCAD's open dialogs (labels, values, buttons, keyboard shortcuts) to `%LOCALAPPDATA%\CsdBridge\uidumps\`. Read-only. Used to check whether other add-ins' dialogs, such as Civil Site Design's, can be automated. |
| `AIFORMDUMP` | Diagnostic: like `AIUIDUMP`, but reads open dialogs from inside BricsCAD as WinForms controls, so it also gets internal control names, grid cell values and the grid component's type and version. Read-only; saves to the same folder. |
| `AICSD` | Civil Site Design tools: **Read** (default: Claude can open and read CSD dialogs but not change them), **Edit** (Claude can also change values and press buttons), **Off**, or **List** (save the CSD commands found to a text file) |
| `AIPIPEHATCH` | Hatch the sewer and stormwater pipes in every long section layout. No API key needed; see [Hatching long section pipes](#hatching-long-section-pipes). |
| `AIVPLAYERS` | Grid of layers × layouts: show or hide layers in every viewport of many layouts at once. No API key needed; see [Viewport layers across layouts](#viewport-layers-across-layouts). |
| `AIRIBBON` | Add the CSD-Bridge ribbon tab again, if a workspace change removed it |
| `AISCALEBARS` | Add a scale bar block to every layout for each viewport scale on it. Runs in the add-in, so it doesn't call Claude or cost anything. See [Scale bars](#scale-bars). |

The **CSD-Bridge ribbon tab** has large Chat, Draft, Routines and History buttons, small New chat,
Status and API key buttons, Scale Bars and VP layers buttons in the Sheets panel, and **Turn path** (`AITURN`), **Solve turn** (`AITURNSOLVE`), **In and out** (`AITURNINOUT`),
**Edit path** (`AITURNEDIT`) and **Clearance** (`AICLEARANCE`) buttons in the Vehicles panel. It's added when the add-in loads. If your workspace hides the
ribbon, turn it on with `RIBBON`, then run `AIRIBBON`.

## Settings

`%APPDATA%\CsdBridge\settings.json` is created on first use:

| Key | Default | Notes |
|---|---|---|
| `model` | `claude-opus-5-5` | `claude-opus-5-5`, `claude-sonnet-5-5` or `claude-haiku-5-5`; also set from the chat panel's Model box or `AIMODEL`. Any other Claude model ID works too and is added to the panel's list. |
| `effort_mode` | `auto` | `auto` / `quick` / `normal` / `thorough`. Also set from the chat panel's Effort box or `AIEFFORT`. |
| `effort_quick` | `low` | Effort level for Quick requests (`low` / `medium` / `high` / `xhigh` / `max`). |
| `effort` | `high` | Effort level for Normal requests. |
| `effort_thorough` | `xhigh` | Effort level for Thorough requests. |
| `max_turns` | `30` | Safety limit on model round trips per request |
| `server_side_fallback` | `true` | If Claude declines a request on policy grounds, Anthropic retries it on a fallback model. Claude API only. |
| `standards_file` | *(blank)* | Path to your office standards. Blank uses `office-standards.md` next to the DLL. |
| `office_routines_folder` | *(blank)* | A shared folder of office routines (e.g. on a network drive), listed next to each user's own. Blank = none. |
| `base_url` | *(blank)* | Optional internal gateway URL that holds the API key centrally |
| `csd_mode` | `read` | Civil Site Design tools: `read`, `edit` or `off`. Also set with `AICSD`. |
| `road_drafting_file` | *(blank)* | Road drafting conventions for `draft_road`. Blank uses `road-drafting.json` next to the DLL. |
| `turn_paths_file` | *(blank)* | Vehicle turning path conventions (vehicles, clearances, layers) for `AITURN` and `draw_turn_path`. Blank uses `%APPDATA%\CsdBridge\turn-paths.json` if the AITURN dialog has saved one, else `turn-paths.json` next to the DLL. |
| `long_section_hatch_file` | *(blank)* | Settings for `AIPIPEHATCH`. Blank uses `long-section-hatch.json` next to the DLL. |
| `cache_ttl` | `1h` | How long the prompt cache lives: `1h` suits sessions with pauses; `5m` is cheaper to write. |
| `scale_bars` | *(not set)* | Optional: overrides where `AISCALEBARS` puts scale bars. Add only the keys you want to change: `block_name` (`Scale Bar {0}`), `x`, `y` (paper mm), `layer`, `scale`, `rotation` (degrees) and `stack_offset` (mm between bars; negative stacks downward). |
| `clear_old_tool_results_after_tokens` | `60000` | Once a chat's input passes this size, the oldest tool results are dropped (the latest 6 are kept). `0` = never. |

## Civil Site Design

With CSD loaded, Claude can work in CSD's dialogs the way you would, but from inside BricsCAD:

| Tool | What it does |
|---|---|
| `csd_commands` | Lists the commands your CSD installation defines (read from its .NET assemblies) and the ribbon buttons behind them (from CSD's menu file in `C:\ProgramData\CSS\ARD\Menu`). Claude uses real names, not guesses. |
| `csd_run_command` | Runs a CSD command to open its dialog, and returns once the dialog is up. If the command asks you to pick something first, Claude tells you. |
| `csd_list_dialogs`, `csd_read_dialog` | Lists the open CSD dialogs and reads their fields, grids (every cell), buttons and menus, plus CSD message boxes and file dialogs. |
| `csd_set_values` | Types values into fields and grid cells the way you would (focus, type, commit), so CSD recalculates. It reads each one back. **Edit mode only.** |
| `csd_press` | Presses buttons and menu items, including the Vertical Grading Editor's commands, and the buttons of CSD's message boxes. In read mode it refuses buttons that change the design (OK, Save, Delete, Adjust, Rebuild...). |
| `csd_read_volumes` | Reads the VGE's cut/fill readout. |
| `csd_help` | Built-in notes on CSD: dialogs and their controls, VGE and grading-grid workflow, design data, templates, volumes, ground levels. |
| `csd_open_vge` | One step to the vertical design: opens the VGE (you pick the road), opens its grading grid, and returns the IP table and volumes. |
| `sample_csd_ground` | Gets existing ground levels along a centreline from a CSD surface. CSD surfaces can't be read directly, so it makes a temporary BricsCAD copy through CSD's Surface Exchange, samples it, then removes the copy and its exchange item. Nothing is left in the drawing (or, in edit mode, it can hide the copy on a frozen layer for reuse). |
| `sample_ground_profile` | Samples a BricsCAD TIN surface that is already in the drawing. |
| `design_vertical_profile`, `evaluate_vertical_profile` | Exact vertical design. Priorities, in order: grades and IP spacing within limits, then as few IPs as possible, then least centreline cut/fill. VC lengths come from a K value. It also checks a design for grade, K, spacing and VC-overlap problems. |
| `csd_write_vc_file` | Writes an IP table to a text file for the VGE's File > Import VC. |

**Safety.** CSD changes are **not** in BricsCAD's UNDO. Claude starts in read mode, so it can look
but not change anything; `AICSD` > Edit turns changes on. In edit mode Claude still reads values
before changing them, reads them back afterwards, and asks before changes you didn't spell out.
Work on a copy of the drawing until you trust it.

A typical request: *"Open the VGE for MC01, sample the ground along the centreline on layer
C-ALIGN, and propose a smoother vertical with fewer IPs: max 8 %, min 0.5 %, K 10, IPs at
least 40 m apart, keep the level at the intersection at ch 52.581."*

## Drafting roads

`draft_road` drafts a CSD road in plan to the office conventions in one step:
- **Centreline:** the exact alignment, with true arcs. The alignment comes from CSD's Align-Lines/Arcs layers if they
  cover the whole road. Otherwise it is rebuilt from the C.L. string: its vertices lie on the alignment, so the
  straights, arcs and tangent points can be worked out exactly. On the C1 sample this matched the drafted centreline to
  the millimetre.
- **Road lines:** kerbs, batters and so on.
- **Areas:** closed outlines between two codes (pavement LEB–REB, footpaths, shoulders), hatched SOLID on the AE CIV HTC
  layers. Each outline is an exact offset of the alignment; CSD's strings only supply the offsets and widenings, so the
  chords between CSD's sample sections never reach the drawing.

The rules live in `road-drafting.json` next to the add-in: which codes become which lines and areas, layers, true
colours and linetype scales. The pavement, centreline and layer styles come from the office's sample drawings. The
kerb, shoulder, footpath and batter rules depend on each job's template codes, so check and edit them. Junctions
between roads aren't trimmed: Claude tidies them with `trim_extend` and `fillet`.

## Scale bars

`AISCALEBARS` (ribbon: **Sheets > Scale Bars**) goes through every layout and reads the scale of each viewport. For
each scale it inserts the matching block, such as `Scale Bar 750` for 1:750, at the office's standard spot: (821, 68.5) on layer `AE GEN TBL - SCALE BAR`, scale 1, no rotation.
When a layout has more than one scale, the bars are stacked upward 40 mm apart, the most detailed scale at the standard spot.

- **Section sheets** (layouts with no viewports, just xref'd sections) take their scales from the scale text in their
  xrefs, such as `SCALES: HORIZONTAL 1:50 VERTICAL 1:20`, which gives bars for 1:20 and 1:50. Only text that mentions a
  scale counts, so batter slopes like 1:4 are ignored, and so is anything marked NOT TO SCALE. Only text that lands on the
  sheet counts, in case one xref holds several sheets. Text on frozen or off layers is skipped.
- The blocks must already be loaded in the drawing. A missing one is listed on the command line and skipped.
- Model units are taken as metres (the office standard). If `INSUNITS` is millimetres, the millimetre reading is tried
  first, then metres.
- Run it again after changing viewport scales. Layouts whose bars already match are left alone, so bars you moved by
  hand stay put; other layouts have their old bars replaced. One UNDO reverts the whole run.
- It doesn't call Claude, so it's instant and free.

## Routines

A routine is a saved procedure you can run again on any drawing: a name, the **inputs** that change from
job to job (road name, grades, a point to pick), and the **steps** for Claude. Open the list with the
**Routines** button on the ribbon, `AIROUTINES`, or the chat panel's **Routines** menu.

**Making one**
- **From a chat (recommended):** do the job once with Claude in the chat panel, answering its questions and
  correcting it as you go. Then choose **Routines > Save this chat as a routine** (or **New > From the chat in
  this drawing** in the Routines window, or just ask "save this as a routine"). Claude writes it up: values
  that will differ next time become inputs, your corrections become rules, and dead ends are left out. The
  editor opens with the draft; nothing is saved until you press Save.
- **By hand:** **New > Blank routine** in the Routines window.

**Changing one:** select it in the Routines window and use **Edit** (name, description, effort, inputs and
steps, and where it's saved), **Rename** (F2), **Duplicate** (makes your own copy, e.g. of an office routine
you want to change) or **Delete**. Deleted routines go to a `deleted` folder next to them, so they can be
recovered. If a run needed correcting, choose **Save this chat as a routine** afterwards: Claude offers an
update to that routine with the corrections folded in, for you to check and save.

**Running one:** select it and press **Run** (or double-click, or Enter). The run dialog asks for the
inputs, starting from the values you used last time (**Reset to defaults** goes back). Points and objects are
picked in the drawing: select objects before pressing Run, or Claude asks when it needs them. Tick
**Start a new chat** when the drawing's chat is long or about something else. The run then works like any
other request: progress in the chat panel, one UNDO step, a line in History (marked with the routine's
name), and follow-ups in the chat afterwards.

**Inputs** have a name used in the steps as `{{name}}`, and a type:

| Type | Asked for |
|---|---|
| Text, Number, Choice, Yes/No | In the run dialog. Numbers are checked; choices are picked from a list. |
| Point (pick) | In the drawing: Claude asks you to pick it when it needs it. |
| Objects (select) | In the drawing: the objects selected before the run, or Claude asks. |

Each input can have a label, a default, a unit (`%`, `m`), a hint, and be required.

**Where they're kept**
- **My routines:** `%APPDATA%\CsdBridge\routines`, yours only. The first time you open Routines it gets an
  example, "Smooth vertical profile", to look at and copy.
- **Office routines:** set `office_routines_folder` in `settings.json` to a shared folder (e.g. a network
  drive; in JSON, double the backslashes: `"office_routines_folder": "S:\\CAD\\CSD-Bridge routines"`), then
  run `AIRESET`. Everyone with the same setting sees the same routines. In the editor, **Saved in**
  moves a routine between the two. If someone else saved a routine after you opened it, you're asked before
  your save replaces theirs.

Each routine is a Markdown file named after it, readable in any text editor. A plain text file without the
header also works: it is all steps, named after the file.

```markdown
---
name: Smooth vertical profile
description: Propose a smoother vertical for a CSD road with fewer IPs
effort: thorough
inputs:
  - name: road
    label: Road
    type: text
    required: true
  - name: max_grade
    label: Max grade
    type: number
    default: 8
    unit: "%"
  - name: side
    type: choice
    options: [Left, Right, Both]
---

1. Open the Vertical Grading Editor for {{road}} with csd_open_vge ...
```

Types in the file are `text`, `number`, `choice`, `yesno`, `point` and `selection`; `effort` is `quick`,
`normal` or `thorough` (leave it out to use the chat panel's Effort setting); `csd_edit: true` marks a
routine that changes Civil Site Design values, and the run dialog warns if CSD edits are off.

Routines don't give Claude any new powers: it has the same tools as in the chat, and CSD changes still need
`AICSD` Edit.

## Vehicle turning paths (AS/NZS 2890.1)

CSD has no vehicle turning analysis, so CSD-Bridge includes one for the car park standard's design
vehicles. `AITURN` (ribbon: **Vehicles > Turn path**) opens a dialog, then prompts in the drawing:

| Manoeuvre | What you pick | What it does |
|---|---|---|
| **Standard turn template** | Start point (centre of the rear axle of the parked car) and the direction of travel | A single turn at the nominated radius, measured at the outer front wheel as in Paragraph B3.1 (default the vehicle's minimum: B99 6.3 m, B85 5.8 m), through the turn angle(s) you enter (`90`, or `30,60,90,120,150,180` for a fan like Figures B3 to B6), left or right, with straight travel before and after. |
| **Follow a line, arc or polyline** | The curve, near the end the vehicle starts from | Drives the vehicle along it, forwards (the front axle centre follows the line, the rear axle tracks in behind) or in reverse (the rear axle follows the line). Use it to check a driveway, ramp or aisle you have already drawn. |
| **Drive** | The start point and direction, then where the vehicle goes next, click by click | Each click is a destination. The car drives there on one arc when that arc is no tighter than the turn radius; otherwise it turns at the turn radius until it points at the destination, then drives straight (the **Drive turns** option makes it always do the latter). A click ahead goes forward, a click behind reverses. With **arrival direction** on (a dialog tick box, or `D` while driving), a second pick sets which way the car faces at the destination, and the leg becomes turn, straight, turn so it arrives exactly on that heading: square into a bay, parallel to an aisle. A leg never turns more than a half turn, so a point inside the turning circle can't be reached in one leg: drive past and reverse. Every leg keeps its own swept path, so a three-point turn is three clicks. `U` takes back the last leg, Enter finishes. |

| **Solve (2 points)** | The start position and direction, then the target position and direction | Finds the movements for you: forward and reverse legs from the start to the target that keep the clearance to everything on the solve boundary layers. See [Solving a multi-point turn](#solving-a-multi-point-turn). |
| **Solve in and out (3 points)** | The entry, the stop on the site and the exit (position and direction at each), then optionally the site boundary across the driveway | Finds the way in to the stop and the way out again, so the vehicle enters forwards and leaves forwards. See [In and out of a site](#in-and-out-of-a-site-the-3-point-solve). |

While you pick the direction, or the next destination, the swept path, clearances, driven path and vehicle
are previewed live under the cursor (at a coarser step than the final result), with the earlier legs kept on screen.

What is drawn, each part on its own configurable layer:

- **Swept path**: the envelope of the body, two solid lines from the parked car's rear corners to the
  front corners at the end (or one closed outline). It is the exact boundary of everything the body
  covered, including the rear swinging out at the start of a turn.
- **Clearances**: the swept path offset by the standard's clearances, dashed: 300 mm manoeuvring on
  both sides, plus 300 mm circulation clearance on the outside for the B99 (Paragraph B3.2). The
  values are editable per run.
- **Vehicle** outline with wheels and axles at the start and end, and optionally every *n* metres along the path.
- The **driven path**, optional **wheel paths**, a **label** (e.g. `B99 R6.3 m`) and an optional **hatch** of the swept area.
- Optionally the whole result is made into a **block** (`B99 turning path`, numbered if the name is taken),
  inserted at the vehicle's start point on the Block layer, so it moves, copies and deletes as one. The
  entities keep their own layers inside the block.

The command reports the tightest turn the manoeuvre needed (as an outer-front-wheel radius) against
the vehicle's minimum, and warns when a path is too tight for the vehicle or a template radius is below
the standard's minimum. Reminders from the standard: B99 is required for access driveways, ramps and
circulation roadways; B85 may only be used for parking spaces and aisles (B2.2, B2.3); turns on
circulating roadways should be at least 8 m radius (A4). Aisle widths for parking manoeuvres come
from Tables B2/B3 and Figure 2.2, not from swept paths.

**How it works.** The vehicles are the base dimensions of Figures B1 and B2 (B99: 5.2 × 1.94 m,
wheelbase 3.05 m; B85: 4.91 × 1.87 m, wheelbase 2.8 m), with the effective front and rear overhangs
and effective width that the standard uses for swept paths (rounded corners). The rear axle cannot
slip sideways, so when a point of the vehicle follows the path the heading changes by that point's
sideways movement divided by the wheelbase: the classic tractrix. That is why a template starts with
the car straight and the inner line only gradually tightens, as in the standard's figures. The swept
path is the boundary of the union of the body over every position: every corner's and side's trace,
minus whatever another position of the body covers.

**Drawing units.** The tools work in drawings in metres, millimetres or any other unit. The dialogs
have a **Drawing units** box: *Automatic* reads the drawing's `INSUNITS` (and takes metres when that
isn't set), or choose *Metres* or *Millimetres* yourself; **Save as defaults** keeps the choice.
Everything you type stays in metres whatever the drawing: vehicle sizes, radii, clearances, outline
intervals and the label text height (0.3 is 300 in a millimetre drawing). Points you pick, the kerbs
and walls on the obstruction layers, a line the vehicle follows and surface levels are read in the
drawing's units, and the result is drawn at the drawing's scale. If the units look wrong for the
size of the drawing (a "millimetre" drawing only 300 units across, say) the dialog says so in red
and asks before carrying on.
A path remembers the units it was drawn with; changing them in `AITURNEDIT` keeps it where it is and
redraws the vehicle at the new scale.

**Configuration.** `turn-paths.json` next to the add-in holds the vehicles (add your own with the
same fields), the drawing units, the default clearances, lead-in/lead-out, text height, what to draw and the layer for
each part (`{vehicle}` in a layer name is replaced by the vehicle's name). Layer styles apply only
when a layer doesn't exist yet. The dialog can change all of it per run; **Save as defaults** writes
`%APPDATA%\CsdBridge\turn-paths.json`, which is then used instead of the shipped file (or point
`turn_paths_file` in `settings.json` at a shared copy). Vehicles that a saved or shared copy doesn't
have are still taken from the shipped file, so new vehicles appear after an update.

**In chat.** Claude has the same simulator as `draw_turn_path`: *"check a B99 can get into the
driveway on the kerb return I just drew"* or *"add B85 turning templates at 5.8 m for each aisle
end"*. It can also run it as a dry run and report the tightest radius without drawing, and solve a
manoeuvre between obstruction layers: *"can a B99 turn round at the end of this aisle? The kerbs
are on C-KERB"*, or in and out of a site through a stop on it: *"show the 8.8 m service vehicle
entering forwards, reversing onto the dock and leaving forwards"*.

### Trucks and trailers

The Austroads design vehicles for roads and commercial sites are included, from the dimension drawings of
the *Design Vehicles and Turning Path Templates Guide* (AP-G34-23, Edition 4.0):

| Vehicle | Guide | Dimensions (m) | Smallest template radius |
|---|---|---|---|
| **Service 8.8 m** | Figure 2.3, service vehicle | 2.3 rear overhang, 5.0 wheelbase, 1.5 front overhang | 9 m |
| **Truck 12.5 m** | Figure 2.4, single unit truck/bus | 3.45, 6.85, 2.2; two rear axles | 12.5 m |
| **Semi 19 m** | Figure 2.7, prime mover and semi-trailer | prime mover 5.3 wheelbase, 1.6 front overhang, kingpin 0.2 ahead of the drive axles; trailer 1.4 ahead of the kingpin, 9.5 to its axles, 2.8 behind them | 12.5 m |
| **B-double 25 m** | Figure 2.9 | prime mover 4.0, 1.0, kingpin 0.4 ahead; A-trailer 0.9 and 8.1; B-trailer 0.2, 9.4 and 2.9 | 12.5 m |
| **B-double 26 m** | Figure 2.10 | prime mover 4.5, 1.4, kingpin over the drive axles; A-trailer 1.4 and 8.05; B-trailer 0.45, 9.15 and 2.9 | 12.5 m |

All are 2.5 m wide. As for the cars, the radius is measured at the outer front wheel, which is how the guide
measures its templates. They default to 0.5 m clearance on both sides (Section 3.4.1: 0.5 m from the swept
path to kerbs, edge lines and objects) and a 70° limit on the angle between coupled units (Section 3.5). The
guide's usual choices (Table 2.3): the 19 m semi-trailer at 15 m radius for arterial and industrial
intersections, the single unit truck at 12.5 m for collector streets, the service vehicle at 9 m for local
residential streets, with B-doubles as checking vehicles. A few things the guide's figures don't dimension
are scaled from them and don't move the swept path: the track widths, and how far a prime mover and an
A-trailer reach behind their axles under the trailer that sits on them.

Any other vehicle can be added to `turn-paths.json`:

- **Rigid trucks.** Give the real body; the `effective_*` fields can be left out, because a square body
  sweeps with its real corners. With a group of rear axles, measure the wheelbase and overhangs from the
  centre of the group.
- **Trailers.** Give the towing vehicle a `hitch` (its fifth wheel, in metres ahead of the centre of its
  rear axles) and a list of `trailers`, front to back. Each trailer has its `width`, its `front_overhang`
  (the kingpin's setback from the front of the trailer), its `wheelbase` (kingpin to the centre of its axle
  group), its `rear_overhang` and, when another trailer follows, its own `hitch`. The comments at the top
  of `turn-paths.json` show the layout.

A trailer is towed the way a real one is: its axles can't slip sideways, so it is dragged round behind its
coupling and cuts inside the path of the vehicle ahead. The swept path covers the whole combination: the
trailer's inner side on the inside of the turn, the prime mover's front corner on the outside. Every unit is
drawn at the start and end (and along the way, with an outline interval), and the report gives the sharpest
angle the combination folds to, with a warning if it goes past the vehicle's limit.

Limits for a vehicle with trailers:

- It drives **forwards only**. Reversing a trailer isn't modelled, so a reversed follow, a drive destination
  behind the vehicle and **Solve** are refused for it.
- The ground clearance check is for rigid vehicles.
- Trailer axles are fixed: steerable and self-tracking axles aren't modelled.
- Turns are tangential, as the guide's templates are: the vehicle arrives straight and the front wheel
  follows the arc. There is no limit on how fast the steering is wound on.

The other vehicles in the guide (the buses, the long semi-trailer and the road trains) and the AS 2890.2
vehicles aren't included; they can be added the same way from their dimension drawings.

### Conflicts with kerbs, walls and columns

Tick **Check against kerbs, walls and columns** in the dialog and name the layers they are on (commas between,
`*` as a wildcard; `A-WALL*` also finds xref layers such as `arch|A-WALL-EXT`). Leave the box blank to pick
objects on those layers after OK. Lines, arcs, circles and polylines on those layers are checked, in the drawing
and inside blocks and xrefs; frozen and off layers are ignored. After drawing, the command reports:

- a **hit** where linework is inside the swept path, with how far inside;
- an **encroachment** where linework is between the swept path and the clearance line, with the gap left and the
  gap required on that side;
- otherwise, how close the nearest obstruction comes to the swept path.

Each conflict is marked on the Conflicts layer: the offending stretch of the obstruction, a ring at the worst
point and a note (`HIT 120 mm`, or `215 mm (needs 300)`).

### Solving a multi-point turn

`AITURNSOLVE` (ribbon: **Vehicles > Solve turn**, or **Solve (2 points)** in the `AITURN` dialog) answers "can it
turn round here, and in how many movements?" without you driving it click by click.

1. In the dialog, choose the vehicle, the turn radius (default its minimum) and the clearance, and name the
   **solve boundary layers**: the kerbs, walls, columns and bay lines that bound the space. Leave the layers
   blank to pick objects on them after OK. See [Solve boundary layers](#solve-boundary-layers) for setting the
   office's standard list once.
2. Pick the **start**: the centre of the rear axle, then a point ahead of the car for the way it faces.
3. Pick the **target** the same way: where the rear axle ends up, and the way the car faces there.

The solver then searches for the forward and reverse movements between the two, turning no tighter than the
radius, and draws each movement as its own swept path, with the car shown where it stops between them. It
reports what it found, for example `Solved in 3 movements (forward 7.4 m, reverse 3.3 m, forward 8.6 m)`. It
looks for the fewest movements first, then the shortest driving.

**Clearance.** The clearance (the *inside* box in the dialog; the same is kept on both sides, 300 mm by default,
Paragraph B3.2) is kept between the body and everything on the obstruction layers, for the whole manoeuvre. Two
things can reduce it, and the report always says so:

- Linework that is already closer than the clearance where the car starts or ends, such as the sides of a 2.4 m
  bay, can't have it. That linework keeps the distance it has there, or half the clearance where passing that
  close saves movements. Everything else still keeps the full clearance: `...keeping 300 mm clearance except to
  what is already closer than that at the target, which keeps 150 mm`.
- If there is no way through at all, the clearance is halved, then dropped: `...but only with the clearance
  reduced from 300 to 150 mm`.

The conflict check runs on the result as usual, so wherever the clearance couldn't be kept is marked on the
drawing with the gap that is left. A tight space can take a lot of shuffling at full clearance (reversing a
B85 into a 2.6 m bay off a 6.2 m aisle takes six movements keeping 300 mm to the bay lines, two keeping 150 mm),
so if the answer looks excessive, try a smaller clearance.

If there is no solution the command says why (the car overlaps an obstruction at the start or target, or no
route was found) and draws nothing. Things to know:

- Only lines, arcs, circles and polylines on the solve boundary layers count (in blocks and xrefs too). Anything
  not on those layers is ignored, so an open side of the space needs a line across it, or the car will use it.
- The car may go up to `solver_search_margin` (15 m) beyond the start and target, and the search stops after
  `solver_time_limit` (10 s); both are in `turn-paths.json`. Most manoeuvres solve in under a second.
- The movements are made of full-lock turns and straights, as a careful driver would make them. It finds a
  good manoeuvre, not always the one with the very fewest movements.

### In and out of a site (the 3-point solve)

`AITURNINOUT` (ribbon: **Vehicles > In and out**, or **Solve in and out (3 points)** in the `AITURN` dialog)
shows that a vehicle can enter a site forwards and leave it forwards, which is what most councils ask of a
service or waste vehicle. Left to itself, the 2-point solve will happily drive past a driveway and reverse in
off the street. This one will not.

1. Pick the **entry**: where the vehicle arrives on the street, and the way it is travelling.
2. Pick the **stop on the site**: the loading dock, bin store or bay, and the way the vehicle faces there.
3. Pick the **exit**: where the vehicle ends up back on the street, and the way it is travelling.
4. Optionally pick the **site boundary** across the driveway, as two points. Enter skips it.

It then solves two manoeuvres, the way in to the stop and the way out from it, and draws them as one path.
The rules it keeps:

- the first movement on the way in is forwards, and the last movement on the way out is forwards;
- with a boundary line picked, no part of the body is over that line while the vehicle is reversing, so all
  the shunting happens on the site.

Without the boundary line the first and last movements are still forwards, but a long reverse in the middle
could reach back over the boundary, so pick the line whenever the driveway is short. The report gives both
parts, for example `Way in: 2 movements (forward 21.3 m, reverse 7.5 m). Way out: 1 movement (forward 23.3 m).
It drives in forwards and out forwards.` Each movement in the command line report is marked `in` or `out`
(`Leg 2 (in, reverse 7.5 m)`). If either
part can't be done, nothing is drawn and the message says which (`on the way in, ...`).

The clearance, the search limits and the things to know above apply to both parts. Like the 2-point solve it
is for cars and rigid trucks; a vehicle with a trailer is refused.

### Solve boundary layers

Both solves stay between the linework on the **solve boundary layers**, which are set apart from the conflict
check's layers so the office's standard list can be set once:

- In the dialog, **Solve boundary layers** (under Conflicts) takes the list, separated by commas, with `*` and
  `?` as wildcards: `C-KERB*, A-WALL*, A-COLS, C-BDY*`. **Save as defaults** keeps it.
- Or set `solve_layers` in `turn-paths.json`, which is the place for a shared office copy.

Leave the list empty and a solve uses the conflict check's **Layers** instead, as it did before. Leave both
empty and you are asked to pick objects on the layers after OK. A solved manoeuvre remembers the layers it
was solved between.

### Editing a turning path

Every path remembers how it was made. `AITURNEDIT` (ribbon: **Vehicles > Edit path**): pick any part of a path,
or its block. The dialog opens with that path's settings; change the vehicle, radius, clearances, what is drawn,
the layers or the conflict layers, then choose:

- **Regenerate** (Enter): redraw it with the new settings and the same geometry;
- **Pick**: pick the start, direction, line or destinations again;
- **Continue** (drive): add more legs from where the drive ended;
- **Solve** (solved manoeuvres): solve it again, for when the kerbs or walls around it have changed.

A solved manoeuvre is also solved again by itself when you change its vehicle, radius, clearance or solve
boundary layers, pick new positions, or regenerate a block that has been moved.

The old version is only erased once the new one has been worked out, so a change that can't be driven (a bigger
vehicle that can't reach a destination, say) leaves the path as it was and says which destination failed. A path
drawn as a block that has since been moved or rotated is regenerated where the block now is. Paths drawn before
this version carry no settings and can't be edited: draw them again.

### Ground clearance (Appendix C)

`AICLEARANCE` (ribbon: **Vehicles > Clearance**) checks that the car's underside clears a grade change: a
ramp, hump, dip or gutter. The template is Figure C1: 120 mm clearance under the body between the axles,
and an underside rising at the 10° approach and departure angles over the overhangs (Paragraph B5). It is
placed with both wheels on the ground and slid along in small steps; it must never fall below the ground.

- **2D, on a plotted long section**: select the plotted profile polyline and give its vertical exaggeration
  (and its horizontal scale, if it isn't plotted at natural scale: 1 m of chainage drawn 1 m long in the
  drawing's units). The template is run both ways along the
  section, since the two overhangs differ. One line shows the lowest the underside ever gets along the whole
  section (it must stay above the ground everywhere); the template is drawn at its worst position in each
  direction, the ground it touches is marked, and a label gives the smallest gap (or how far it grounds).
- **3D, on a surface**: select the line or polyline the vehicle drives (forwards or in reverse). At every
  position the surface is sampled along each side of the car, from the rear extremity through both wheels to
  the front extremity, and the template is placed on that side's wheels. Grounding spots are ringed on the plan,
  and each wheel path can be plotted as a long section with the underside line and the template at its worst
  position. It reads
  BricsCAD TIN surfaces; for a CSD surface make a BricsCAD copy with Surface Exchange first.
- The clearance and angles are editable per run (Appendix C, Note: lower the underside for a lesser clearance),
  and saved with the other defaults. Layers: templates and plots, where it grounds, labels.

In chat, `check_ground_clearance` does the same from a plotted section, a `ground_id` from
`sample_ground_profile`/`sample_csd_ground` (so CSD surfaces work along any line, wheel paths included), or a
path on a BricsCAD surface.

## Hatching long section pipes

`AIPIPEHATCH` (ribbon: **Hatch pipes**) hatches the pipes of Civil Site Design long sections. It doesn't use Claude,
so it is instant and free.

- It works on every layout with "long section" in its name (**All**, the default), or on the layout you are in
  (**Current**). **Remove** takes its hatches out again.
- In each layout it reads the long section xref in paper space and finds the pipe runs by shape: two parallel lines on
  one layer, one above the other, between the same two chainages. Pit walls, ground lines and the data band don't
  match. Frozen and switched-off layers are ignored.
- Only the section's own pipe is hatched: runs whose two ends sit on a structure's vertical line. Crossing and nearby
  pipes that CSD plots on the section start or stop between structures, so they are left out (and counted in the
  command's report). `"main_pipe_only": false` in `long-section-hatch.json` hatches every pipe found.
- All the runs of a long section get one SOLID hatch in the layout, behind the xref, on
  `AE CIV DET - STORMWATER PIPE NEW` or `AE CIV DET - SEWER PIPE NEW` (created if missing).
- Sewer or stormwater comes from the layout name, then the xref's name, the pipe layer names, then the labels in the
  section (e.g. `900Ø CORRUTECH SW SN8`). If none of those says, the command asks.
- The hatch can't follow the xref. After CSD updates a long section, run `AIPIPEHATCH` again: it replaces the hatches
  it made earlier and leaves hatches you drew yourself alone. The whole run is one UNDO step.

The command lists, per layout, how many runs it hatched and which xref layers the pipe lines were on. If it ever picks
up something that isn't a pipe, put those layer names in `pipe_layers` in `long-section-hatch.json` (next to the DLL),
which also holds the layout name test, the hatch layers and the keywords for each service.

## Viewport layers across layouts

`AIVPLAYERS` (ribbon: **VP layers**) opens a window with the drawing's layers down the side and its layouts across the
top. It doesn't use Claude. Each cell shows how that layer shows in that layout's viewports:

| Cell | Meaning |
|---|---|
| ● green | shown in every viewport on the layout |
| ○ grey | frozen in every viewport on the layout (VP freeze) |
| ◐ orange | frozen in some of the layout's viewports |
| – | the layout has no viewports |

- **Select** like a spreadsheet: drag across cells, Ctrl-click to add, Shift-click for a range. Click a layout's name to
  select its whole column, or a layer's name for its whole row (Ctrl/Shift work there too). Ctrl+A selects everything.
- **Show in viewports** thaws the selected layers in every viewport of the selected layouts; **Hide in viewports**
  freezes them. Space, a double-click or the right-click menu toggles (from the cell you started on: shown → hide,
  otherwise → show). Viewports already right are left alone, and viewports on locked layers are changed too.
- **Filters**: type in the Layers or Layouts box. Plain text matches anywhere in the name, `*` and `?` are wildcards,
  commas separate several, and `~` in front leaves names out, e.g. `sewer, storm, ~*TEXT*`. Untick **Xref layers** to
  hide `xref|layer` names.
- Layers that are off or frozen in the whole drawing are greyed and marked: no viewport shows them until they are turned
  on in the layer manager.
- The window stays open beside the drawing, follows the drawing you switch to, and re-reads it when you come back to it
  (or press Refresh). Each Show/Hide should be one UNDO step.

**Not yet run inside BricsCAD**: the grid rules are unit-tested and the window compiles, but it hasn't been opened in
BricsCAD yet. Check on a copy of a drawing first: that the paper space viewport is never counted as a layout's viewport,
and that UNDO takes back a Show/Hide in one step.

## Saving work between chats

- **Drawing notes:** Claude saves short facts about each drawing (its roads, alignment handles, start chainages, surface
  names, what's drafted) with `save_drawing_note`. They are kept in `%LOCALAPPDATA%\CsdBridge\notes` and given to Claude
  at the start of each new chat on that drawing, so it doesn't work them out again.
- **Office standards:** `office-standards.md` now holds the office's civil layer standard, hatch and drafting rules, and
  CSD naming (NS = existing ground, BM = design). The common CSD commands are built into Claude's instructions, so it
  doesn't have to look them up.
- **Smaller replies from the drawing:** the drawing summary leaves out xref layers and folds CSD's many string layers into
  one list of roads and their codes. Coordinates are rounded to the millimetre.

## Effort

Effort sets how hard Claude thinks before acting. Lower effort is faster and cheaper; higher
effort is more careful. It stays the same model throughout, so the conversation and its cache
carry on.

| Mode | Used for | Default level |
|---|---|---|
| **Quick** | Simple edits ("move these 200 right", "delete the old grid") and questions about the drawing | `low` |
| **Normal** | Ordinary drafting ("draw a 6000 x 4000 slab with a 2400 grid and dimension it") | `high` |
| **Thorough** | Big or multi-step jobs: layouts, plans, sections, schedules, or three or more steps | `xhigh` |
| **Auto** (default) | Picks one of the above from the wording of each request | |

The chosen effort shows on the command line when a request starts, and in the History window.
Auto uses simple word rules (`src/CsdBridge.Core/Agent/EffortPolicy.cs`), so it costs nothing
and is easy to tune. To try the rules without BricsCAD, use `--effort auto` in the CLI harness.

## Model

The chat panel's **Model** box (or `AIMODEL`) picks which Claude model answers:

| Model | Notes |
|---|---|
| **Opus 5.5** (default) | Most capable; slowest and most expensive |
| **Sonnet 5.5** | Faster and cheaper |
| **Haiku 5.5** | Fastest and cheapest. Has no server-side refusal fallback, so a declined request stays declined. |

A change applies from your next message and is remembered in `settings.json`. The drawing's
conversation carries on with the new model. Two things to know when comparing models:

- The new model doesn't see the previous model's thinking (the API drops it, at no charge),
  only the visible conversation. For a fair comparison, press **New chat** first.
- The prompt cache is per model, so the first request after a switch pays to cache the
  conversation again. Compare token use from the second request on.

The model shows when each request starts, in the summary line after it, and in the History window.

## Office standards

`office-standards.md` is added to Claude's instructions on every request: layers, colours,
lineweights, text heights, dimension conventions. **The shipped file is an example; replace it
with your real standards.** To share one file across the office, put it on a network drive and
point `standards_file` at it.

## Cost and logs

Each request is several API calls, one per round of tool use. The ~4,800-token system prompt
and tool definitions are prompt-cached, so repeat calls mostly pay the cheap cache-read rate.
The command line and panel show token usage after every request, with an estimated cost in
US dollars ("about US$0.12"). The history window (`AIHISTORY`) shows each request's usage and
cost, and today's totals.

The estimate uses the Claude API's list prices for each model, kept in
`src/CsdBridge.Core/Agent/ModelPricing.cs` (checked November 2026; compare them with
claude.com/pricing now and then). Cache reads and writes are priced at their own rates, and
Haiku 5.5's higher price for prompts over 100,000 tokens is applied per request. It doesn't
know about gateway pricing (`base_url`), US-only inference or account discounts, so treat it
as a guide and your Anthropic Console bill as the real figure. Models not in the table show
tokens only. Every request is saved to
`%LOCALAPPDATA%\CsdBridge\logs\` as one line of JSON in a file per day (`yyyy-MM-dd.jsonl`).
Use those numbers rather than estimates when deciding on effort level or model.

## Troubleshooting

| Error | Fix |
|---|---|
| `MSB1009: Project file does not exist` | Run the command from the repo folder (`cd CSD-Bridge`). |
| `NU1100: Unable to resolve 'Anthropic'` | NuGet can't reach nuget.org. The repo's `nuget.config` adds it as a source. If the error persists, run `dotnet nuget enable source nuget.org`. If it still fails, your network is probably blocking `api.nuget.org`: ask IT to allow it, or for the office package feed or proxy address. |
| `The Anthropic API key was rejected` | Create a key at https://console.anthropic.com (API Keys). Keys start with `sk-ant-api03-`. |
