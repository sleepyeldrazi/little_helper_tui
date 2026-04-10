# little_helper_tui -- Implementation Plan

Framework: **Spectre.Console** (no Terminal.Gui)
Pattern: REPL loop with rich console rendering
Core dependency: issues #1-#4 on sleepyeldrazi/little_helper_core

---

## Phase 0: Scaffolding

Set up the project structure so it compiles and runs.

**Files:**
- `little_helper_tui.sln`
- `src/little_helper_tui.csproj` -- Spectre.Console, reference to `core/src/`
- `src/Program.cs` -- entry point, just prints "hello" via Spectre
- `src/Adapters/` -- empty directory, .gitkeep

**Verify:** `dotnet build` succeeds. `dotnet run` prints a Spectre.Console greeting.

**Blocking core issue:** #1 (public types + library output). Without this, the TUI
cannot reference any core types. We can scaffold the project structure but can't
reference core until this lands.

**Deliverable:** Compiling skeleton that references core.

---

## Phase 1: Model Selection + Single Prompt

The user can pick a model from their config and send one prompt. No agent loop yet --
just a raw model call to prove the pipeline works.

**Files:**
- `src/Adapters/ModelSelector.cs`
  - Loads `~/.little_helper/models.json` via `ModelConfig.Load()`
  - Shows a Spectre selection prompt listing all configured models
  - Returns a `ResolvedConfig` for the chosen model
- `src/Adapters/ClientFactory.cs`
  - Takes `ResolvedConfig`, creates `ModelClient` + `ToolExecutor`
  - Registers all tool schemas via `ToolSchemas.RegisterAll()`
  - Returns a ready-to-use `(ModelClient, ToolExecutor)` tuple
- `src/PromptLoop.cs`
  - Reads user input via Spectre's `TextPrompt`
  - Sends to `ModelClient.Complete()`
  - Renders the response using Spectre panels/markdown

**UI flow:**
```
  ╭──────────────────────────────────────────╮
  │  Select a model:                         │
  │  > qwen3:14b (local)                     │
  │    deepseek-r1:14b (local)               │
  │    gpt-4o (openrouter)                   │
  ╰──────────────────────────────────────────╯

  ╭─ You ────────────────────────────────────╮
  │  What files are in the src directory?     │
  ╰──────────────────────────────────────────╯

  ╭─ qwen3:14b ──────────────────────────────╮
  │  I'll check the src directory for you.    │
  │                                           │
  │  🔧 run("ls src/")                        │
  │    → Program.cs                           │
  │      Types.cs                             │
  │      ...                                  │
  ╰──────────────────────────────────────────╯

  >
```

**Core dependency:** Issue #1 only.

**Deliverable:** Working model selection + single prompt/response display.

---

## Phase 2: Agent Loop + Live Progress

Wire up the full `Agent.RunAsync()` with a Spectre `Status` or `Live` display
showing what's happening. This is the MVP -- the agent runs, you watch.

**Files:**
- `src/Adapters/AgentRunner.cs`
  - Wraps `Agent.RunAsync()` with progress tracking
  - Uses core issue #2 (IAgentObserver/events) to get step-level updates
  - Exposes: `StepCount`, `CurrentState`, `LastToolCall`, `LastResponse`
  - Falls back to `CancellationToken`-only if events aren't available yet
- `src/Renderers/ActivityStream.cs`
  - Renders the conversation as a scroll of panels
  - User messages, assistant messages, tool calls with collapsible output
  - Uses Spectre `Panel`, `Table`, `Tree` for structured display
- `src/Renderers/StatusBar.cs`
  - Top-of-screen bar: model name, step count, token usage
  - Renders via Spectre `Rule` or simple formatted line

**UI during agent run:**
```
  ── qwen3:14b │ Step 3/30 │ Tokens: 4.2K/32K │ State: Observing ──

  ╭─ You ────────────────────────────────────╮
  │  Add error handling to FileReader         │
  ╰──────────────────────────────────────────╯

  ╭─ Step 1 ─────────────────────────────────╮
  │  🔧 read("src/FileReader.cs")             │
  │    → 47 lines                             │
  ╰──────────────────────────────────────────╯

  ╭─ Step 2 ─────────────────────────────────╮
  │  🤔 Analyzing the FileReader class...     │
  │                                           │
  │  🔧 write("src/FileReader.cs")            │
  │    +12 / -3 lines                         │
  ╰──────────────────────────────────────────╯

  ⠋ Working... (Step 3)
```

**After agent completes:**
```
  ╭─ Done ───────────────────────────────────╮
  │  ✅ Completed in 4 steps, 6.1K tokens     │
  │  Files changed: src/FileReader.cs         │
  ╰──────────────────────────────────────────╯

  > _
```

**Core dependency:** Issues #1 + #2 (events). Can work without #2 by wrapping
RunAsync and polling, but events give much better UX.

**Deliverable:** Full agent loop with live progress display.

---

## Phase 3: Thinking Panel + Token Budget

Add observability for what the model is thinking and where tokens are going.

**Files:**
- `src/Renderers/ThinkingPanel.cs`
  - Renders model reasoning content from `ModelResponse.ThinkingContent`
  - Display modes: full (verbatim), condensed (first+last 3 lines), hidden
  - Toggle with keybinding or `:thinking` command
  - Export to markdown via `:export-thinking`
- `src/Renderers/TokenBudget.cs`
  - ASCII bar chart of context window usage
  - Categories: system prompt, user messages, assistant text, tool output, thinking
  - Uses `Compaction.EstimateTokens()` for calculations
  - Renders when agent is idle or on `:tokens` command

**Token budget display:**
```
  ╭─ Token Budget ───────────────────────────╮
  │  System  ████████░░░░░░░░░░░░  2.1K      │
  │  User    ██░░░░░░░░░░░░░░░░░░  0.4K      │
  │  Assist  ████████████░░░░░░░░  2.8K      │
  │  Tools   ██████░░░░░░░░░░░░░░  1.5K      │
  │  Think   ████░░░░░░░░░░░░░░░░  0.9K      │
  │  ──────────────────────────────────       │
  │  Used    7.7K / 32K (24%)       24.3K left│
  ╰──────────────────────────────────────────╯
```

**Thinking display (condensed mode):**
```
  ╭─ Thinking (Step 2) ──────────────────────╮
  │  I need to analyze the FileReader class   │
  │  structure to determine where errors...   │
  │  ...                                     │
  │  ...best approach is to add try-catch     │
  │  blocks around the ReadAllText call...    │
  │                                           │
  │  847 tokens │ [Full] [Export]             │
  ╰──────────────────────────────────────────╯
```

**Core dependency:** Issues #1 + #2. Token breakdown benefits from #4 (message
history access). Thinking content is already in `ModelResponse.ThinkingContent`.

**Deliverable:** Thinking panel + token budget visualization.

---

## Phase 4: Session Persistence + History

Save and reload conversations. Resume where you left off.

**Files:**
- `src/Adapters/SessionManager.cs`
  - Write JSONL session logs to `~/.little_helper/logs/` (same format as core)
  - Log entries: `session_start` (model, config), `step` (messages, response),
    `tool` (call + result), `session_end` (summary)
  - Load/parse JSONL logs for history browsing
  - Resume: reconstruct `List<ChatMessage>` from log, pass to Agent via issue #4
  - Branch: snapshot message list at step N, start new session from there
- `src/Renderers/SessionBrowser.cs`
  - List recent sessions with Spectre selection prompt
  - Show session summary (model, steps, tokens, files changed)
  - Browse individual session transcripts
- `src/Commands/SessionCommands.cs`
  - `:history` -- list recent sessions
  - `:resume <id>` -- continue a past session
  - `:branch <id> <step>` -- fork from a checkpoint
  - `:export <file>` -- save transcript as markdown

**Session list display:**
```
  ╭─ Recent Sessions ────────────────────────╮
  │                                           │
  │  #12  today 14:32  qwen3:14b  8 steps    │
  │       "Add error handling to FileReader"  │
  │                                           │
  │  #11  today 11:15  deepseek-r1  12 steps  │
  │       "Refactor the tool executor"        │
  │                                           │
  │  #10  yesterday   gpt-4o       4 steps    │
  │       "Write unit tests for Compaction"   │
  │                                           │
  ╰──────────────────────────────────────────╯
```

**Core dependency:** Issue #4 (run from existing history, message list access).
Can write the JSONL layer without it but can't resume without being able to pass
a pre-built message list to Agent.

**Deliverable:** Full session save/load/resume/branch.

---

## Phase 5: Intervention System

Pause the agent, edit/skip tool calls, inject messages.

**Files:**
- `src/Adapters/InterventionManager.cs`
  - Coordinates pause/resume signals (wraps core's pause mechanism from issue #4)
  - Tool call queue: before execution, the TUI can approve/skip/edit
  - Message injection queue: user messages inserted between steps
  - Maps to core issue #4's interception callback + message injection queue
- `src/Renderers/ToolApproval.cs`
  - When agent is paused on a tool call, show details and prompt for action
  - Actions: Approve, Skip, Edit (opens $EDITOR with JSON args), Redirect (inject message)
- `src/Commands/InterventionCommands.cs`
  - `Space` or `:pause` -- pause agent at next step boundary
  - `:resume` -- resume
  - `:skip` -- skip current tool call
  - `:edit` -- edit current tool call arguments
  - `:say <message>` -- inject a redirect message

**Tool approval prompt:**
```
  ⏸ PAUSED at Step 3

  ╭─ Tool Call Pending ──────────────────────╮
  │  🔧 write("src/FileReader.cs")            │
  │                                           │
  │  +12 / -3 lines                           │
  │                                           │
  │  Arguments:                               │
  │  {                                        │
  │    "path": "src/FileReader.cs",            │
  │    "content": "...(47 lines)...            │
  │  }                                        │
  ╰──────────────────────────────────────────╯

  > [A]pprove  [S]kip  [E]dit  [R]edirect  [V]iew Diff
```

**Core dependency:** Issue #4 (pause/resume, tool interception, message injection).
This entire phase is blocked without it.

**Deliverable:** Full intervention system -- pause, skip, edit, redirect.

---

## Phase 6: Skill Browser + Diff Viewer

Quality-of-life features for daily use.

**Files:**
- `src/Renderers/SkillBrowser.cs`
  - Uses `SkillDiscovery.Discover()` to list available skills
  - Spectre `Tree` or selection prompt for browsing
  - Preview: show skill description + first 20 lines of content
  - Inject: append skill content to next user prompt
- `src/Renderers/DiffViewer.cs`
  - When agent writes a file, compute unified diff (before vs after)
  - Read file content before write (from tool call interception in Phase 5)
  - Render with Spectre syntax: red for removed, green for added
  - Actions: Accept, Reject, Open in $EDITOR
- `src/Commands/SkillCommands.cs`
  - `:skills` -- list and browse skills
  - `:skill <name>` -- inject skill into next prompt
- `src/Commands/DiffCommands.cs`
  - `Ctrl+D` or `:diff` -- show last file diff
  - `:diff <file>` -- show diff for specific file

**Skill browser display:**
```
  ╭─ Skills ─────────────────────────────────╮
  │                                           │
  │  📂 bundled                               │
  │  ├── verify    Verify code changes...     │
  │  └── refactor  Refactor with tests...     │
  │                                           │
  │  📂 project                               │
  │  └── deploy    Deploy to staging...       │
  │                                           │
  │  [Enter] Preview  [i] Inject              │
  ╰──────────────────────────────────────────╯
```

**Diff viewer display:**
```
  ╭─ Diff: src/FileReader.cs ────────────────╮
  │                                           │
  │  -  public string ReadAll(string path) {  │
  │  -      return File.ReadAllText(path);    │
  │  +  public string ReadAll(string path) {  │
  │  +      try {                             │
  │  +          return File.ReadAllText(...); │
  │  +      } catch (FileNotFoundException) { │
  │  +          _logger.Warn($"Not found...);│
  │  +          return string.Empty;          │
  │  +      }                                 │
  │                                           │
  │  +12 -3  │  [A]ccept  [R]eject  [E]dit   │
  ╰──────────────────────────────────────────╯
```

**Core dependency:** Issue #1 + #2. Skill browsing needs public SkillDiscovery.
Diff viewer works with tool interception from Phase 5 but can fall back to reading
files from disk before the agent writes them.

**Deliverable:** Skill browser + diff viewer.

---

## Phase 7: Command System + Keybindings

Unify all commands and add the keyboard shortcuts from the README.

**Files:**
- `src/InputHandler.cs`
  - Central input loop: reads keystrokes and text input
  - Modes: Normal (scrolling, shortcuts), Input (typing prompt), Command (`:` prefix)
  - Keybinding map loaded from `~/.little_helper/tui.json`
- `src/Commands/CommandRegistry.cs`
  - Registry of all `:commands` with descriptions and handlers
  - Tab completion for command names and model/skill names
  - Help via `:help`
- `src/Config/TuiConfig.cs`
  - Load/save `~/.little_helper/tui.json`
  - Theme, thinking panel default, auto-show diffs, editor, keybindings

**Keybinding table (default, vim mode):**
```
  Tab / Shift+Tab   -- Cycle prompt history
  j / k             -- Scroll output down/up
  Space             -- Pause/resume agent
  Ctrl+T            -- Toggle thinking panel mode
  Ctrl+D            -- Show last diff
  Ctrl+S            -- Save checkpoint
  :                 -- Enter command mode
  /                 -- Search mode
  Enter             -- Submit prompt (in input mode)
  Escape            -- Cancel / back to normal mode
```

**Command registry:**
```
  :model <name>      -- Switch model
  :skill <name>      -- Inject skill
  :thinking [mode]   -- Toggle thinking (full/condensed/hidden)
  :tokens            -- Show token budget
  :skills            -- Browse skills
  :diff [file]       -- Show diff
  :history           -- Browse sessions
  :resume <id>       -- Resume session
  :branch <id> <n>   -- Branch from checkpoint
  :checkpoint [note] -- Save checkpoint
  :export <file>     -- Export transcript
  :reset             -- Fresh conversation
  :config            -- Edit TUI config
  :help              -- Show commands
  :quit              -- Exit
```

**Core dependency:** None (pure TUI code).

**Deliverable:** Full command system with keybindings and config.

---

## Phase 8: Model Pool + Arena Mode

Run two models side-by-side on the same prompt and compare.

**Files:**
- `src/Adapters/ModelPool.cs`
  - Manages multiple `ModelClient` instances
  - Hot-swap: switch active model mid-conversation (creates new Agent)
  - Token budget tracking per model
  - Uses `ModelConfig.GetAllModels()` to list available models
- `src/Renderers/ArenaView.cs`
  - Side-by-side comparison of two model runs
  - Spectre `Columns` with two `Panel`s
  - Shows: speed (time to first token, total time), token count, quality (user rates)
  - Summary table after both complete

**Arena display:**
```
  ╭─ Arena: qwen3:14b vs deepseek-r1:14b ───╮
  │                                           │
  │  Prompt: "Refactor the tool executor"     │
  │                                           │
  │  ╭─ qwen3:14b ──╮  ╭─ deepseek-r1 ──╮    │
  │  │ Step 3/30     │  │ Step 5/30      │    │
  │  │ 2.1K tokens   │  │ 3.4K tokens    │    │
  │  │ 🔧 write(...) │  │ 🤔 Thinking... │    │
  │  │ 12s elapsed   │  │ 18s elapsed    │    │
  │  ╰───────────────╯  ╰────────────────╯    │
  │                                           │
  │  [W]ait  [1] Pick left  [2] Pick right    │
  ╰──────────────────────────────────────────╯
```

**Core dependency:** Issues #1 + #2. The TUI runs two Agent instances independently.
No core changes needed for arena beyond what's already requested.

**Deliverable:** Model switching + arena mode.

---

## Phase 9: Polish + Publish

Hardening, edge cases, documentation.

**Tasks:**
- Error handling: network failures, malformed responses, missing config
- Graceful shutdown: save session on Ctrl+C, clean up temp files
- Performance: lazy rendering (only render visible output), trim large tool outputs
- Terminal compatibility: test across xterm, screen, tmux, Windows Terminal
- Publishing: `dotnet publish` as single binary, update README with install instructions
- Add `--headless` flag that runs without TUI (pipe-friendly, for scripting)

**Core dependency:** None.

**Deliverable:** Production-ready TUI binary.

---

## Dependency Graph

```
Phase 0 ── needs core issue #1
Phase 1 ── needs Phase 0
Phase 2 ── needs Phase 1 + core issue #2
Phase 3 ── needs Phase 2
Phase 4 ── needs Phase 2 + core issue #4
Phase 5 ── needs Phase 4 (core issue #4 fully)
Phase 6 ── needs Phase 2 (can overlap with Phase 5)
Phase 7 ── needs Phase 2 (can start anytime after)
Phase 8 ── needs Phase 2
Phase 9 ── needs all phases
```

**Critical path:** #1 -> Phase 0 -> Phase 1 -> Phase 2 -> Phase 4 -> Phase 5

**Parallel tracks after Phase 2:**
- Track A: Phase 3 -> Phase 6 (observability)
- Track B: Phase 4 -> Phase 5 (state management)
- Track C: Phase 7 (commands -- independent)
- Track D: Phase 8 (arena -- independent)

---

## Estimated LOC

| Phase | New Files | Est. LOC | Actual LOC |
|-------|-----------|----------|------------|
| 0     | 3         | 50       | 21         |
| 1     | 3         | 200      | 139        |
| 2     | 3         | 350      | 297        |
| 3     | 2         | 250      | 74         |
| 4     | 3         | 400      | 150        |
| 5     | 3         | 300      | --         |
| 6     | 4         | 350      | 232        |
| 7     | 3         | 300      | --         |
| 8     | 2         | 250      | --         |
| 9     | 0         | 100      | --         |
| Total | 26        | ~2,550   | 913        |

---

## Core Feature Requests (Filed)

| Issue | Title | Blocks |
|-------|-------|--------|
| #1 | Make types public + library output | Phase 0 |
| #2 | Step-level events for observability | Phase 2 |
| #3 | SSE streaming support | Phase 3 (nice to have) |
| #4 | Conversation history + pause/resume | Phase 4-5 |
