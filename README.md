# little_helper_tui

Terminal UI frontend for [little_helper_core](./core).

## Architecture

```
little_helper_tui/           <- this repo
  core/                      <- git submodule -> little_helper_core
  src/
    TUI.cs                   <- Terminal.Gui / Spectre.Console frontend
    ...
  little_helper_tui.sln
```

The TUI references `core/src/` as a linked project (ProjectReference to `core/src/little_helper_core.csproj`).
This gives it access to all the core types: `Agent`, `ModelClient`, `AgentResult`, `ThinkingLog`, etc.

## What the TUI will do

The CLI (`little_helper_core`) is a batch tool: prompt in, result out.
The TUI wraps the same engine with an interactive terminal experience:

- **Chat view** — scrollable conversation with syntax-highlighted tool output
- **Thinking panel** — collapsible side panel showing model reasoning (from `ThinkingLog`)
- **File diff view** — inline diff of files changed by the agent
- **Token counter** — live display of tokens used, thinking tokens, step count
- **Model switcher** — hot-swap models mid-conversation from `~/.little_helper/models.json`
- **Skill browser** — list and load skills without restarting

## Tech stack

- .NET 8
- [Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui) or [Spectre.Console](https://spectreconsole.net/) (TBD)
- References little_helper_core as a submodule

## Setup

```bash
git clone --recurse-submodules https://github.com/sleepyeldrazi/little_helper_tui.git
cd little_helper_tui
dotnet build
```

## Status

Pre-alpha. Core engine is complete (62 tests passing). TUI not yet implemented.
