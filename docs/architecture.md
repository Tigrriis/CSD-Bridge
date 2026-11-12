# CSD-Bridge architecture

## Why call the API from inside the add-in (instead of MCP)

| | MCP (Claude client drives CAD) | In-process add-in (this project) |
|---|---|---|
| Where the user works | In the Claude app, switching to CAD to look | In BricsCAD: command line or docked panel |
| Interactive input | Hard: the CAD side can't easily prompt mid-turn | Claude can ask the user to pick a point or select objects (`pick_point`, `select_entities`) |
| Undo | Each tool call is a separate change | Whole request runs inside one command, so it is **one UNDO step** |
| Context | Must be fetched over the bridge | Pre-selected objects are passed in automatically |
| Control over prompts, model, tools | Limited to what the client exposes | Full: system prompt, office standards, effort, tool set, logging |
| Deployment | Claude app + MCP server + CAD plugin per seat | One DLL + an API key, or a central gateway |
| Cost model | Claude subscription | Pay per token (API) |

The trade-off: we own the agent loop, the prompt and the error handling. That code is small
(`DraftingAgent.cs`, ~400 lines) and unit-tested.

## Components

```
┌──────────────────────── BricsCAD process ────────────────────────────┐
│                                                                      │
│  Commands, ribbon tab, ChatPalette, HistoryWindow (WinForms)         │
│          │                                  │ queues prompt, then    │
│          ▼                                  │ SendStringToExecute    │
│     AgentRunner ◄───────────────────────────┘                        │
│      │   one DraftingAgent (conversation) per open drawing           │
│      │                                                               │
│      │ main thread                         background thread         │
│      │ ┌──────────────┐  tool calls   ┌─────────────────────────┐    │
│      ├─┤MainThreadPump│◄──────────────┤ DraftingAgent.RunAsync  │────┼──► Claude API
│      │ └──────┬───────┘  results ────►│  (Core, Anthropic SDK)  │    │
│      │        ▼                       └─────────────────────────┘    │
│      │  ToolDispatcher (validate) → BricsCadDrawingHost → DWG        │
└──────────────────────────────────────────────────────────────────────┘
```

- **CsdBridge.Core** has no BricsCAD dependency: the agent loop, tool schemas, input validation,
  and `IDrawingHost`, the operations Claude may perform. It also has an in-memory host
  and DXF writer for the CLI harness and tests. Porting to AutoCAD means writing another
  `IDrawingHost`. The BricsCAD host already uses the AutoCAD-compatible API, so that is mostly a
  namespace swap.
- **CsdBridge.Core/Vehicles** is the AS/NZS 2890.1 turning path simulator: design vehicles (B99,
  B85, or any added to `turn-paths.json`), a kinematic bicycle-model simulation along a path, the
  swept path as the exact boundary of the union of the body over every pose, the clearance
  template, the `draw_turn_path` tool, a drive planner (turn at the radius, then straight, forwards or in
  reverse, leg by leg, optionally arriving on a set heading), conflict detection against obstruction linework,
  a multi-point turn solver (`ManoeuvreSolver`: a hybrid A* search over position, heading and direction of
  travel, stepping along full-lock arcs and straights, finishing with turn-straight-turn paths to the target or
  to a straight run-in to it, and testing every step by sweeping the body's true outline, enlarged by the
  clearance, against the obstruction linework),
  the saved manoeuvre record that `AITURNEDIT` regenerates from, and the Appendix C ground clearance check (`check_ground_clearance`) on a long
  section or along each side of the car on a surface. It has no CAD dependency, so the same code serves
  the `AITURN`/`AICLEARANCE` commands, Claude's tools and the tests.
- **CsdBridge.BricsCAD** is the add-in: commands, panel, settings, and the drawing host. `AITURN`
  shows a WinForms dialog, prompts for geometry with the Editor, and draws through
  `BricsCadDrawingHost` like the tools do, so the result is one UNDO step.

## Request flow

1. The user runs `AIDRAFT`, or presses Send in the panel. The panel queues the prompt and starts
   the internal command `CSDAI_PALETTE_RUN`, so both paths run **inside a BricsCAD command**.
2. `AgentRunner` adds any pre-selected objects to the prompt as a `<cad_context>` block. It then
   starts `DraftingAgent.RunAsync` on a background thread.
3. Each loop turn: send the conversation to Claude, then echo the assistant reply back into
   history exactly as received. Each `tool_use` block is executed on the main thread.
   All results go back in one user message.
4. The loop ends when Claude replies without tool calls. It also ends on cancel, refusal, an
   API error, or after `max_turns` round trips.
5. The command ends. BricsCAD records everything as one UNDO step.

## Threading model

BricsCAD's API must only be used on the main thread. The HTTP calls must not block it, or
BricsCAD would freeze for the 10-60 s a request can take.

- The agent runs on a thread-pool thread. Every tool call goes through
  `IMainThreadInvoker.InvokeAsync`.
- While the request runs, the command sits in `MainThreadPump.PumpUntil` on the main thread.
  That loop does four things:
  - runs queued tool calls and progress updates
  - calls `Application.DoEvents()` so BricsCAD repaints and the panel's Stop button works
  - polls `HostApplicationServices.Current.UserBreak()` for Esc
- Because tools run inside the command, `Editor.GetPoint` and `GetSelection` work normally.
- **Risk:** `DoEvents` allows re-entrancy. The panel disables Send while busy. Typing other
  commands mid-request is the thing to test.

## Tool design

Twelve tools:
- **Drawing:** `get_drawing_info`, `list_entities`, `ensure_layers`, `draw_entities`, `modify_entities`
- **User input:** `pick_point`, `select_entities`
- **Exact geometry:** `calculate`, `offset_entities`, `intersect`, `measure`, `points_on_curve`

- **Exact geometry is computed, never estimated.** LLMs make small arithmetic slips that are
  hard to spot in a drawing. So every derived number goes through a tool:
  - `calculate` is a safe expression parser, with named values that later lines can reuse.
  - Offsets, intersections, lengths, areas, closest points and set-out points come from
    BricsCAD's geometry engine (`GetOffsetCurves`, `IntersectWith`, `GetPointAtDist`, ...).
    The system prompt tells Claude to use these and to feed their exact results into
    `draw_entities`.

  Offsets choose their side from a `side_point`, or from inside/outside for closed shapes. The
  side is found by testing which candidate lies towards the point, so it doesn't depend on
  which way the curve runs. The offline host has its own exact implementation (lines, arcs,
  bulged polylines; mitred offsets for straight-sided polylines), so the tools behave the same
  in the CLI harness and in tests.

- **Constrained surface.** No "run command" or "eval LISP" tool. Everything Claude can do is
  listed in `IDrawingHost`, which keeps behaviour predictable and safe to give to all users.
- **Batching.** `draw_entities` takes up to 500 entities and `modify_entities` up to 200
  operations, and the tool descriptions push Claude to batch. Fewer round trips makes requests
  faster and cheaper.
- **Validate first, then touch the drawing.** `ToolDispatcher` checks the whole batch before
  anything is drawn. If anything is wrong, nothing changes, and Claude gets every problem with
  its exact JSON path (`entities[3].radius must be greater than 0`) so it can fix them all in
  one turn.
- **Per-item results.** Host-level failures, such as a missing block or a locked layer, fail
  only that item. Results come back in order with the new entity handles, which Claude uses
  for later edits.
- **Compact output.** Results are snake_case JSON with nulls omitted and coordinates rounded to
  4 decimals. Listing is capped at 500 entities and reports `truncated`.
- **World coordinates, degrees.** Picked points are converted from UCS to WCS. Colour defaults
  to ByLayer, and missing layers are created.

## Routines

A routine is a saved procedure: a name, typed inputs, and Markdown steps for Claude. The design choice
was to build them from a real chat rather than from a typed prompt or by recording the user's clicks: the
chat already holds what the job took (Claude's questions and the user's answers and corrections), and it
says *why* each step happened, which a recording of commands and pick points can't.

- **Files** (`Core/Routines/RoutineFile.cs`): one Markdown file per routine with a small YAML-like header
  (name, description, effort, csd_edit, inputs). The parser handles only that fixed subset, so Core needs
  no YAML package; anything it doesn't know is kept on save. A file without a header is all steps.
- **Library** (`RoutineLibrary`): the user's folder (`%APPDATA%\CsdBridge\routines`) plus an optional
  shared office folder. Names are unique per folder; files are named after the routine. Rename and "Saved
  in" rewrite and move the file; delete moves it to `deleted\`. A save checks the file's write time against
  when it was read, so two people editing an office routine don't silently overwrite each other.
- **Running** (`RoutinePrompt`): the run dialog collects the typed inputs; points and selections are left to
  `pick_point` / `select_entities` during the run. The routine goes in the **user message**, never the
  system prompt, so the cached prefix is untouched. Its steps are sent with their `{{name}}` placeholders and
  the values listed after them, so if the user later asks to update the routine in the same chat, Claude
  still has the original wording. A routine can fix its effort tier; otherwise Auto judges its steps.
- **Writing one** (`RoutineTools`, the `save_routine` tool): when asked to save a chat as a routine, Claude
  calls `save_routine` with the inputs and steps. The tool validates them (bad input names, choices without
  options, `{{name}}` with no matching input go back to Claude to fix) and hands the draft to the host;
  nothing is written. When the request's command ends, the add-in opens the draft in the routine editor
  for the user to check and save. With `replaces`, the draft is an update of a saved routine.
- **UI** (`BricsCAD/Routines/`): the Routines window (list, search, New, Edit, Rename, Duplicate, Delete,
  Run), the editor, and the run dialog. The run dialog remembers each user's last values per routine.

## Model settings and conversation rules

- **Model:** `claude-opus-5-5` by default, with `thinking: adaptive`. The user can switch to
  `claude-sonnet-5-5` or `claude-haiku-5-5` (chat panel or `AIMODEL`; list in
  `src/CsdBridge.Core/Agent/ModelCatalog.cs`). A switch keeps the conversation as it is: the
  API drops thinking blocks the new model can't read (unbilled), and the new model's cache
  starts cold. The one exception is a model without per-message effort taking over a
  conversation that has effort-only system messages; that starts a new conversation instead.
- **Effort per request:** Auto picks Quick (`low`), Normal (`high`) or Thorough (`xhigh`)
  from the request wording, or the user fixes one mode.
  - **How it's sent:** the top-level effort stays at the Normal level. A change is an
    effort-only `role: "system"` message placed right after the user's turn (beta
    `mid-conversation-output-config-2026-07-01`), sent only when the level changes. Earlier
    messages stay byte-identical, so the prompt cache and earlier thinking stay valid.
  - **Older models:** models without per-message effort get a top-level change per request
    instead.
  - **Why not switch models per request:** switching drops the previous model's thinking and
    restarts the per-model cache.
- **Server-side refusal fallback** (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`)
  is on by default. If a request is declined on policy grounds, Anthropic re-runs it on its
  recommended fallback model. That is unlikely for drafting, but it costs nothing otherwise.
  Turn it off with `server_side_fallback: false`. It is Claude API only, not Bedrock, Vertex AI
  or Foundry. Haiku 5.5 has no server-side fallback, so `fallbacks` isn't sent for it.
- **Append-only history.** The system prompt, office standards and tool list are fixed for a
  conversation's lifetime. Assistant turns, including thinking blocks and their signatures,
  are echoed back exactly. Anything that varies per request, such as the selection, goes into
  the user message. This keeps the prompt cache warm and keeps Claude's earlier reasoning
  valid. Current Opus models reject requests whose earlier turns were edited.
- **Prompt caching:** top-level `cache_control`, so each call re-reads the growing conversation
  from cache. The 5-minute cache expires during long pauses. If users pause often, consider
  the 1-hour TTL.
- **Non-streaming, `max_tokens` 16,000.** Simpler, and fine for tool-call-sized replies. If a
  reply is cut off, its tool calls are not run. Claude is told to resend in smaller batches.
- **Every `tool_use` gets exactly one `tool_result`**, including on cancel. That keeps the
  conversation valid for the next request.

## Security and deployment

- **API key:** `AIKEY` stores the key DPAPI-encrypted per Windows user. The
  `ANTHROPIC_API_KEY` environment variable also works.
- **Firm-wide:** for wider rollout, prefer a small internal gateway (`base_url`) that holds the
  key. It also gives central logging, spend limits and per-user attribution. Then keys never
  sit on drafting PCs.
- **Data sent to Anthropic:**
  - the user's prompt
  - the drawing summaries Claude asks for (layer names, entity geometry and text)
  - the office standards file
  - **not** the DWG file itself
- **Corporate proxy:** .NET's HttpClient uses the Windows system proxy by default.
- **Dependency loading:**
  - **V24/V25 (.NET Framework 4.8):** all add-ins share one AppDomain and ignore the add-in's
    binding redirects. `DependencyResolver` loads the SDK's dependencies from the add-in folder.
  - **V26 (.NET 8):** the `.deps.json` handles this. Core is built for both netstandard2.0 and
    net8.0, because the SDK's two builds differ in a way that would otherwise fail at runtime.

## First things to test in BricsCAD

1. `NETLOAD` succeeds on V25 and on V26, and `AISTATUS` runs. A failure here is dependency
   loading. On V26, check whether BricsCAD isolates add-ins: the SDK ships
   System.Text.Json 10, and .NET 8 itself has 8.
2. `AIDRAFT` with a simple request draws and **one** `U` undoes it.
3. Esc during "thinking" cancels quickly, and the drawing stays usable.
4. `pick_point` from the panel. The first click may only focus the drawing window. If so, add
   a focus call before `GetPoint`.
5. Text alignment, MText formatting, and dimension style/scale look right in the office template.
6. Blocks with attributes (title blocks) insert with values filled.
7. Behaviour on a large drawing (10k+ entities): `list_entities` speed and the `truncated` flag.

## Roadmap ideas

- **More tools:** hatch, leaders/multileaders, tables, set attribute values on existing
  blocks, offset/trim/fillet, zoom to result, set the current layer.
- **Preview/accept:** draw into a temporary layer or transaction and let the user accept or
  reject.
- **Visual check:** send a rendered PNG of the result back to Claude (vision) to catch layout
  issues.
- **Streaming:** show Claude's text as it is written.
- **Office knowledge:** standard details and block libraries described in the standards file,
  so "insert our standard pad footing detail" works.
- **Evaluation:** a set of reference prompts plus expected geometry, run through the CLI
  harness, so prompt changes can be measured. Routines are a natural source: the CLI can already run
  one with `--routine` and `--input`.
- **Routines from free text:** list the routine names in the system prompt (fixed per chat) so "do the
  usual profile tidy on MC02" can find the right routine without opening the Routines window.
