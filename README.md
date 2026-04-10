# little_helper_tui

Terminal UI frontend for [little_helper_core](./core).

Spectre.Console REPL -- pick a model, type prompts, watch the agent work, intervene when needed.

**[Read the letter](./letter.md)** for the story behind the project -- why C#, how the architecture was researched, what works and doesn't work with LLM agents, and where this is going next.

## Architecture

```
little_helper_tui/
  core/                    <- git submodule -> little_helper_core (read-only)
  src/
    Program.cs             <- REPL loop, command dispatch
    InputHandler.cs        <- custom readline with cursor editing + tab-complete
    TuiObserver.cs         <- IAgentObserver: renders agent activity via Spectre
    ClientFactory.cs       <- routes OpenAI/Anthropic client based on ApiType
    ModelSelector.cs       <- model picker from ~/.little_helper/models.json
    StatusBar.cs           <- step count, tokens, agent state
    TokenBudget.cs         <- bar chart + table of context window usage
    SessionManager.cs      <- browse/resume past sessions from JSONL logs
    SkillBrowser.cs        <- browse + inject SKILL.md files
    DiffViewer.cs          <- unified diff for agent file writes
    InterventionManager.cs <- pause/resume, tool interception
    ModelArena.cs          <- A/B test two models side-by-side
    TuiConfig.cs           <- ~/.little_helper/tui.json loader
  little_helper_tui.sln
```

Core is a read-only git submodule. The TUI wraps it -- when core doesn't provide something, we file an issue on the core repo.

---

## Prerequisites

- .NET 10 SDK
- A model endpoint (Ollama, OpenRouter, Kimi, Anthropic, etc.)
- Config at `~/.little_helper/models.json`

## Setup

```bash
git clone --recurse-submodules https://github.com/sleepyeldrazi/little_helper_tui.git
cd little_helper_tui
dotnet build
```

## Run

```bash
dotnet run --project src
```

Or publish a single binary:

```bash
dotnet publish src -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

Then symlink it somewhere on your PATH:

```bash
ln -s $(pwd)/src/bin/Release/net10.0/linux-x64/publish/little_helper_tui ~/.local/bin/little
```

---

## Input

Custom readline with full editing:

| Key | Action |
|-----|--------|
| Left/Right | Move cursor |
| Home/End, Ctrl+A/E | Jump to start/end |
| Ctrl+Left/Right | Jump by word |
| Ctrl+W | Delete word back |
| Ctrl+U / Ctrl+K | Clear before/after cursor |
| Tab | Complete file path (~/, ./, absolute) |
| Up/Down | History navigation |
| Escape | Clear line |
| Ctrl+C / Ctrl+D | Exit |

---

## Commands

Type any command at the `>` prompt.

| Command | Description |
|---------|-------------|
| `:model [name]` | Switch model (picker if no name) |
| `:tokens` | Token budget bar chart + breakdown |
| `:history` | Conversation history table |
| `:sessions` | Browse past sessions |
| `:sessions N` | Show session #N with transcript |
| `:skills` | Browse and inject skills |
| `:diff` | Show diff for last file write |
| `:arena` | A/B test two models |
| `:config` | Show TUI config |
| `:reset` | Reset conversation |
| `:help` | Show commands |
| `:quit` | Exit |

During agent runs: **Ctrl+C** = cancel.

---

## Configuration

### `~/.little_helper/models.json`

Defines model providers. Each provider has a `base_url`, optional `api_key`, `headers`, and `models` list. Supports `api_type: "openai"` (default) and `api_type: "anthropic"` for Anthropic-compatible endpoints.

Example:

```json
{
  "providers": {
    "local": {
      "base_url": "http://localhost:11434/v1",
      "models": [
        { "id": "qwen3:14b", "context_window": 32768 }
      ]
    },
    "kimi-coding": {
      "base_url": "https://api.kimi.com/coding/v1",
      "api_key": "...",
      "api_type": "openai",
      "headers": { "User-Agent": "claude-cli/1.0.0" },
      "models": [
        { "id": "k2p5", "context_window": 131072 }
      ]
    },
    "anthropic": {
      "base_url": "https://api.anthropic.com",
      "api_key": "...",
      "api_type": "anthropic",
      "models": [
        { "id": "claude-sonnet-4-20250514", "context_window": 200000 }
      ]
    }
  }
}
```

### `~/.little_helper/tui.json`

TUI-specific settings. Auto-generated on first run.

```json
{
  "thinking_mode": "condensed",
  "show_token_budget": true,
  "auto_show_diffs": true,
  "max_tool_output_lines": 20,
  "max_steps": 30,
  "default_model": null,
  "theme": "default"
}
```

Set `default_model` to a model id (e.g. `"qwen3:14b"`) to skip the model picker on startup.

---

## Features

### Agent Loop with Live Progress

The agent runs in the background. Spectre.Console renders a spinner while `TuiObserver` drains events -- tool calls, thinking, responses -- into panels as they happen. Stderr from the core is captured and shown cleanly after the run.

### Token Budget

`:tokens` shows a Spectre bar chart breaking down context window usage by category (system, user, assistant, tools, thinking, available) plus a table with percentages.

### Session History

All runs are logged as JSONL in `~/.little_helper/logs/`. `:sessions` shows a table of recent runs. `:sessions N` shows the full transcript with user/assistant messages.

### Skill Injection

`:skills` lists SKILL.md files from `~/.little_helper/skills/` and `./.little_helper/skills/`. Preview a skill, inject it into your next prompt.

### Diff Viewer

When the agent writes a file, the TUI snapshots the original content. `:diff` shows a unified diff with added/removed lines color-coded.

### Model Arena

`:arena` picks two models and runs them in parallel on the same prompt. Shows a comparison table (steps, tokens, time, files changed) when both finish.

---

## TODO

These are in the codebase but not yet fully functional:

- **Intervention (Space=pause/resume)** -- key listener is wired but Spectre.Console's Status spinner captures console input, blocking `Console.KeyAvailable`. Needs a different approach (e.g. background thread with raw terminal input, or `:pause`/`:resume` commands typed in a separate pane).
- **Auto-show diffs** -- `tui.json` has the setting but `:diff` is manual only right now.
- **Thinking mode toggle** -- config field exists in `tui.json` but observer always renders condensed. Needs wiring to respect `thinking_mode: full/condensed/hidden`.
- **Publish as single binary** -- `dotnet publish` command works but hasn't been tested end-to-end.

---

## Status

Alpha. 2,048 LOC across 14 source files. Build: 0 warnings, 0 errors.

Core submodule: public types, IAgentObserver, streaming, Anthropic support, pause/resume, session logging, tilde expansion in tool paths.

---

*The TUI should feel like a cockpit -- every gauge and control within reach, but the autopilot (the agent) does the flying.*
