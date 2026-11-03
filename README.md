# CSD-Bridge

A BricsCAD add-in that lets you ask Claude to draft in the open drawing ("draw a 6 m x 4 m slab
outline on S-CONC with a 2.4 m grid, and dimension it"). The add-in calls the Claude API itself.
There is no MCP server and no Claude desktop app in the loop.

```
BricsCAD ── CSD-Bridge add-in ── Claude API
              │  agent loop + 7 drafting tools
              └─ draws, edits, asks you to pick points / select objects
```

## What it does

- **AIDRAFT** (command line) or **AIPANEL** (dockable chat panel): describe what you want.
  Both are also on the **CSD-Bridge ribbon tab**.
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
- It can **ask you to click a point or select objects** mid-request, which an external MCP
  client can't do naturally.
- Objects you select *before* asking are passed in as context ("dimension these").
- Conversations are per drawing, so follow-ups work ("make it 300 wider").
- The whole request is **one UNDO step**. Esc or Stop cancels.
- Claude can only use these tools. It cannot run commands, LISP, scripts or open files.

## Status

This is a working scaffold for exploration, not a finished product.

| Part | State |
|---|---|
| Core (agent loop, tools, validation) | Built and unit-tested: 16 tests, including the full loop run against the real Anthropic SDK with canned API responses |
| BricsCAD add-in, V26 (.NET 8) | Compiles against BricsCAD V26 reference assemblies. **Not yet run inside BricsCAD.** |
| BricsCAD add-in, V24/V25 (.NET Framework 4.8) | Same source. **Not yet compiled** (needs V25's `BrxMgd.dll`/`TD_Mgd.dll` on Windows). |
| Live Claude API | **Not yet called**: the build machine had no API key. Run the CLI harness first (below). |

See [docs/architecture.md](docs/architecture.md) for the design, and "First things to test" there.

## Repository layout

```
src/CsdBridge.Core/        CAD-independent: Claude agent loop, tool schemas, input validation,
                           in-memory drawing + DXF preview (netstandard2.0 + net8.0)
src/CsdBridge.BricsCAD/    The add-in: commands, chat panel, BricsCAD drawing host, settings
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

## Commands

| Command | What it does |
|---|---|
| `AIDRAFT` | Ask Claude on the command line. Uses any pre-selected objects as context. |
| `AIPANEL` | Open the chat panel (Enter sends, Shift+Enter for a new line, Stop cancels) |
| `AIHISTORY` | Open the history window: every request and what Claude did |
| `AIRESET` | New conversation for this drawing; reloads settings |
| `AIKEY` | Set or remove the stored API key |
| `AISTATUS` | Show model, key source, standards file, settings and log locations, usage |
| `AIRIBBON` | Add the CSD-Bridge ribbon tab again, if a workspace change removed it |

The **CSD-Bridge ribbon tab** has large Chat, Draft and History buttons, and small New chat,
Status and API key buttons. It's added when the add-in loads. If your workspace hides the
ribbon, turn it on with `RIBBON`, then run `AIRIBBON`.

## Settings

`%APPDATA%\CsdBridge\settings.json` is created on first use:

| Key | Default | Notes |
|---|---|---|
| `model` | `claude-opus-5-5` | Any Claude model ID |
| `effort` | `high` | `low` / `medium` / `high` / `xhigh` / `max`. Lower is faster and cheaper. |
| `max_turns` | `30` | Safety limit on model round trips per request |
| `server_side_fallback` | `true` | If Claude declines a request on policy grounds, Anthropic retries it on a fallback model. Claude API only. |
| `standards_file` | *(blank)* | Path to your office standards. Blank uses `office-standards.md` next to the DLL. |
| `base_url` | *(blank)* | Optional internal gateway URL that holds the API key centrally |

## Office standards

`office-standards.md` is added to Claude's instructions on every request: layers, colours,
lineweights, text heights, dimension conventions. **The shipped file is an example; replace it
with your real standards.** To share one file across the office, put it on a network drive and
point `standards_file` at it.

## Cost and logs

Each request is several API calls, one per round of tool use. The ~3,400-token system prompt
and tool definitions are prompt-cached, so repeat calls mostly pay the cheap cache-read rate.
The command line and panel show token usage after every request. The history window
(`AIHISTORY`) shows each request's usage and today's total. Every request is saved to
`%LOCALAPPDATA%\CsdBridge\logs\` as one line of JSON in a file per day (`yyyy-MM-dd.jsonl`).
Use those numbers rather than estimates when deciding on effort level or model.

## Troubleshooting

| Error | Fix |
|---|---|
| `MSB1009: Project file does not exist` | Run the command from the repo folder (`cd CSD-Bridge`). |
| `NU1100: Unable to resolve 'Anthropic'` | NuGet can't reach nuget.org. The repo's `nuget.config` adds it as a source. If the error persists, run `dotnet nuget enable source nuget.org`. If it still fails, your network is probably blocking `api.nuget.org`: ask IT to allow it, or for the office package feed or proxy address. |
| `The Anthropic API key was rejected` | Create a key at https://console.anthropic.com (API Keys). Keys start with `sk-ant-api03-`. |
