# little_helper_tui

Terminal UI frontend for [little_helper_core](./core).

## Architecture

```
little_helper_tui/           <- this repo
  core/                      <- git submodule -> little_helper_core (read-only)
  src/
    Adapters/                <- TUI-specific wrappers around core types
      AgentRunner.cs         <- wraps Agent with events, pause/resume, streaming
      SessionManager.cs      <- loads JSONL logs, resume/branch conversations
      ModelPool.cs           <- multi-model switching, arena mode
    TUI.cs                   <- Terminal.Gui / Spectre.Console frontend
    ...
  little_helper_tui.sln
```

The TUI references `core/src/` as a linked project (ProjectReference to `core/src/little_helper_core.csproj`).
This gives it access to all the core types: `Agent`, `ModelClient`, `AgentResult`, `ThinkingLog`, etc.

### Adapters

The `Adapters/` directory contains TUI-owned code that wraps core types. Core is a read-only submodule — the TUI never modifies it directly. When an adapter needs something core doesn't provide, that's a core feature request:

1. TUI adapter hits a wall (e.g. needs `Agent.ToolExecuted` event)
2. Add the extension point to core, push to core repo
3. Bump the submodule pointer in TUI
4. Adapter now works

**Adapter responsibilities:**

| Adapter | Wraps | Purpose |
|---------|-------|---------|
| `AgentRunner` | `Agent` | Events on step/tool/thinking, pause/resume, cancellation |
| `SessionManager` | JSONL logs | Load history, resume conversations, branch at checkpoints |
| `ModelPool` | `ModelClient`, `ModelConfig` | Hot-swap models, arena mode, token budget tracking |

---

## Design Philosophy

The TUI is an **observability and control layer**, not a replacement for the agent's autonomy. It surfaces what the agent is doing, lets you steer when needed, and gets out of the way when the agent is working well.

Key principles:
- **Progressive disclosure** — deep detail is one keypress away, not in your face
- **Non-blocking** — the agent runs; you browse, inspect, intervene
- **Keyboard-first** — vim-style navigation, modal when appropriate
- **Context-preserving** — restart, branch, or rewind without losing work

---

## Core UI Layout

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  little_helper  │  Model: qwen3:14b  │  Tokens: 12.4K / 32K  │  [⚙]  [?]   │
├──────────┬────────────────────────────────────────────┬─────────────────────┤
│          │                                            │                     │
│  SKILL   │                                            │    🤔 THINKING      │
│  BROWSER │      💬 CHAT / ACTIVITY STREAM             │    ─────────────    │
│          │                                            │                     │
│  [verify]│  ┌────────────────────────────────────┐   │  The model is       │
│  [refac] │  │ User: Add error handling to        │   │  analyzing the      │
│  [test]  │  │       the file reader              │   │  FileReader class   │
│          │  └────────────────────────────────────┘   │  structure to       │
│  ─────── │                                            │  determine where    │
│  SESSION │  ┌────────────────────────────────────┐   │  NullReference      │
│  HISTORY │  │ 🔧 read("src/FileReader.cs")       │   │  exceptions might   │
│          │  │   → 47 lines                       │   │  occur...           │
│  [today] │  └────────────────────────────────────┘   │                     │
│  [yest.] │                                            │  Step 3 of 8        │
│          │  ┌────────────────────────────────────┐   │                     │
│          │  │ ✏️  write("src/FileReader.cs")     │   │                     │
│          │  │   +12 / -3 lines                   │   │                     │
│          │  │   [View Diff] [Revert]             │   │                     │
│          │  └────────────────────────────────────┘   │                     │
├──────────┴────────────────────────────────────────────┴─────────────────────┤
│  > _                                                                        │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Features

### Chat / Activity Stream (Center)
- Collapsible tool calls — expand to see full output, collapse to summary
- Syntax highlighting in code blocks
- Inline diffs on file writes with `[View Full]` and `[Revert]`
- Search/filter — `/pattern` to search history, `@tool` to filter by tool type

### Thinking Panel (Right, Toggleable)
- Live streaming of model reasoning from `ThinkingLog`
- Step correlation — which thoughts belong to which step
- Modes: Full / Condensed / Hidden
- Export reasoning to markdown

### Skill Browser (Left)
- Lists skills from `~/.little_helper/skills/` and `./.little_helper/skills/`
- Preview on hover, inject with Enter

### Real-Time Diff Viewer
- Unified diff when agent writes a file
- Accept / Reject / Edit (opens `$EDITOR`)

### Token Budget Visualizer
- ASCII bar chart showing context window breakdown:
  system prompt / user prompts / assistant text / tool outputs / thinking / available

### Checkpoint & Time Travel
- `:checkpoint "note"` — save state
- `:rewind N` — go back N steps
- `:branch "try this"` — fork conversation

### Intervention System
- Pause/resume with Space
- Skip tool calls, edit arguments before execution
- Inject redirect messages mid-loop

### Model Arena
- A/B test two models side-by-side on the same prompt
- Compare speed, token usage, quality

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Tab` / `Shift+Tab` | Cycle focus between panels |
| `j` / `k` | Scroll down / up |
| `Enter` | Expand/collapse tool call |
| `Space` | Pause / resume agent |
| `Ctrl+T` | Toggle thinking panel |
| `Ctrl+D` | Open diff for last write |
| `Ctrl+S` | Save checkpoint |
| `:` | Command mode |
| `/` | Search mode |

### Command Mode

| Command | Description |
|---------|-------------|
| `:model [name]` | Switch model |
| `:skill [name]` | Inject skill |
| `:checkpoint [note]` | Save state |
| `:rewind [n]` | Go back N steps |
| `:export [file.md]` | Save transcript |
| `:reset` | Start fresh conversation |

---

## Configuration

TUI settings in `~/.little_helper/tui.json`:

```json
{
  "theme": "dark",
  "thinking_panel_default": "condensed",
  "auto_show_diffs": true,
  "confirm_destructive": true,
  "editor": "${EDITOR:-nano}",
  "keybindings": "vim"
}
```

---

## Setup

```bash
git clone --recurse-submodules https://github.com/sleepyeldrazi/little_helper_tui.git
cd little_helper_tui
dotnet build
```

## Status

Pre-alpha. Core engine is complete (62 tests passing). TUI not yet implemented.

---

*The TUI should feel like a cockpit — every gauge and control within reach, but the autopilot (the agent) does the flying.*
