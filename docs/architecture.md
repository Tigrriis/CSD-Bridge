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
│  Commands (AIDRAFT, AIPANEL…)      ChatPalette (WinForms panel)      │
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
  and `IDrawingHost`, the seven operations Claude may perform. It also has an in-memory host
  and DXF writer for the CLI harness and tests. Porting to AutoCAD means writing another
  `IDrawingHost`. The BricsCAD host already uses the AutoCAD-compatible API, so that is mostly a
  namespace swap.
- **CsdBridge.BricsCAD** is the add-in: commands, panel, settings, and the drawing host.

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

Seven tools: `get_drawing_info`, `list_entities`, `ensure_layers`, `draw_entities`,
`modify_entities`, `pick_point`, `select_entities`.

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

## Model settings and conversation rules

- **Model:** `claude-opus-5-5`, with `thinking: adaptive` and `effort: high` (configurable).
  Geometry mistakes cost more than a few seconds of waiting. Try `medium` for speed.
- **Server-side refusal fallback** (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`)
  is on by default. If a request is declined on policy grounds, Anthropic re-runs it on its
  recommended fallback model. That is unlikely for drafting, but it costs nothing otherwise.
  Turn it off with `server_side_fallback: false`. It is Claude API only, not Bedrock, Vertex AI
  or Foundry.
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
  harness, so prompt changes can be measured.
