# TUI Architecture Audit

**Date:** 2025-01-10  
**Auditor:** Claude  
**Scope:** `src/` directory (TUI layer only, core/ is read-only submodule)

---

## Executive Summary

The TUI has **12 architectural issues** across 4 categories. Most bugs reported by users are symptoms of these structural problems, not isolated defects.

**Key Finding:** The TUI lacks a proper integration layer. Instead of a clean adapter between core and Spectre.Console, concerns are smeared across Program.cs with inline lambdas, dead code, and competing interception mechanisms.

---

## Category 1: Dead Code & Phantom Infrastructure

### Issue 1: InterventionManager is Dead Code
**Location:** `src/InterventionManager.cs` (106 lines)

**Problem:** The entire class is never instantiated. No `new InterventionManager()` exists anywhere in the codebase.

**What it was supposed to do:**
- Pause/resume agent execution
- Tool call interception with skip/edit capability
- Message injection (`:inject` command)

**What actually happens:**
- Program.cs has its own inline `ToolInterceptor` lambda (lines 117-139)
- No `:pause`, `:resume`, `:skip`, `:inject` commands exist
- TuiObserver line 128 lies: "Diff snapshotting is handled by InterventionManager's ToolInterceptor"

**Fix:** Either delete it or make it the actual integration point. See recommendations.

---

### Issue 2: TuiConfig Has 5 Orphan Settings
**Location:** `src/TuiConfig.cs`

These settings are loaded, displayed in `:config`, saved to disk, and **never used**:

| Setting | Declared Behavior | Actual Behavior |
|---------|-------------------|-----------------|
| `ThinkingMode` | "full, condensed, hidden" | Never read. TuiObserver always renders thinking the same way |
| `AutoShowDiffs` | bool, default true | Never checked. Diffs only show on `:diff` command |
| `MaxToolOutputLines` | int, default 20 | Hardcoded limits in TuiObserver (5, 6, 8, 100) |
| `SubAgents` | SubAgentConfig | Zero references. Sub-agent feature doesn't exist |
| `Theme` | "default, monochrome, dark" | Never applied to any Spectre.Console config |

**Impact:** Config theater. Users can set values, see them echoed back, and nothing changes.

---

## Category 2: Conflicting Responsibilities & Duplicate Logic

### Issue 3: ToolInterceptor Set From Two Places, One Wins
**Location:** `src/Program.cs:117-139`, `src/InterventionManager.cs:39-76`

`AgentControl.ToolInterceptor` is a single `Func<ToolCall, ToolCall?>?`. Both places try to set it:

1. **Program.cs:** Sets it inline every loop for git checkpoint + DiffViewer.Snapshot
2. **InterventionManager:** Has `InstallToolInterceptor()` for DiffViewer.Snapshot + skip/edit logic

Only Program.cs's version ever runs because InterventionManager is never instantiated. If someone wires up InterventionManager, it would **clobber** the git checkpoint logic.

**Root cause:** No single owner for the interception pipeline.

---

### Issue 4: IAgentObserver.OnToolCallExecuting Is a No-Op
**Location:** `src/TuiObserver.cs:126-130`

```csharp
public ToolCall? OnToolCallExecuting(ToolCall call, int step)
{
    // Diff snapshotting is handled by InterventionManager's ToolInterceptor
    return call;
}
```

This interface method exists in core but is vestigial. The actual interception happens via `agent.Control.ToolInterceptor` (a delegate), not the observer method. Two mechanisms, one dead.

**Recommendation:** Remove from interface or make it the primary mechanism.

---

### Issue 5: FormatToolArgs Duplicated Between TUI and Core
**Location:** `src/TuiObserver.cs:320-336`, `core/src/Agent.cs:283-297`

Both files have nearly identical switch statements for formatting tool arguments:

```csharp
// TuiObserver
toolName.ToLowerInvariant() switch
{
    "read" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
    "write" => args.TryGetProperty("path", out var w) ? w.GetString() ?? "" : "",
    "run" or "bash" => args.TryGetProperty("command", out var c)
        ? Truncate(c.GetString() ?? "", 80) : "",
    ...
}
```

**Risk:** If core adds a new tool, TUI display silently breaks or shows raw JSON.

---

### Issue 6: SessionManager Reimplements Core's Log Format
**Location:** `src/SessionManager.cs:39-50`

SessionManager defines its own `JsonlEntry` record with `[JsonPropertyName]` attributes and parses logs independently. If core's `SessionLogger` changes its format, SessionManager breaks silently.

**What should happen:** Core exposes the log entry types; TUI references them directly.

---

## Category 3: Lifecycle & State Bugs

### Issue 7: Observer State Leaks Across Model Switches
**Location:** `src/Program.cs:80-81`

When switching models (`:model` command):
```csharp
if (newModel != null) { resolved = newModel; agent = null; }
```

The `TuiObserver` is NOT reset. `TotalTokens`, `TotalThinkingTokens`, `CurrentStep` carry forward from the previous model's run. The status bar shows cumulative stats across different models.

**Fix:** Reset observer on model switch, or track per-model stats.

---

### Issue 8: No Cancellation Path During Agent Execution
**Location:** `src/Program.cs:52-56`, `99`

```csharp
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    console.MarkupLine("[yellow]Use :quit to exit.[/]");
};
```

During `console.Status().StartAsync()` (the spinner), the REPL isn't accepting input. Ctrl+C prints "Use :quit" but you can't type `:quit` while the agent is running. The `CancellationTokenSource` created on line 99 is never connected to any user input.

**Impact:** Only way to stop a runaway agent is `kill -9`.

---

### Issue 9: SessionLogger Created Per-Prompt, Not Per-Session
**Location:** `src/Program.cs:98`, `180`

```csharp
var logger = new SessionLogger(modelId, workingDir);
// ...
finally { logger.Dispose(); }
```

Every user prompt creates a new log file. But the TUI implies continuous conversation (observer keeps history, agent has `_messages`). The `:sessions` browser shows fragmented per-prompt runs, not actual sessions.

**Expected:** One log file per `:reset` or program start.

---

### Issue 10: Agent Resets Messages on Every RunAsync Call
**Location:** `core/src/Agent.cs:58`

```csharp
public async Task<AgentResult> RunAsync(string userPrompt, CancellationToken ct = default)
{
    var state = AgentState.Planning;
    _messages = new List<ChatMessage>();  // <-- reset every time
```

Each call to `RunAsync()` starts a fresh conversation. The TUI observer accumulates state, but the agent has no memory between prompts. Multi-turn conversation is an illusion maintained only by the UI.

**Decision needed:** Is this intentional (stateless agent) or a bug? Current UX implies continuity.

---

## Category 4: Fragile Hacks

### Issue 11: Console.Error Redirect is a Core Design Flaw Workaround
**Location:** `src/Program.cs:147-195`

```csharp
// Redirect stderr so core's Console.Error.WriteLine doesn't corrupt Spectre spinner
var originalStderr = Console.Error;
var stderrCapture = new StringWriter();
Console.SetError(stderrCapture);
try { ... }
finally { Console.SetError(originalStderr); }

// Then fish it back out, deduplicate, truncate
var stderrLines = stderrCapture.ToString()
    .Split('\n', ...)
    .Distinct()
    .Take(5)
```

Core's `Agent.Log()` writes to `Console.Error`. The TUI captures this, deduplicates (which could hide real repeated errors), truncates to 5 lines, and displays it.

**Root cause:** Core should use `IAgentObserver.OnError()` instead of `Console.Error.WriteLine`.

---

### Issue 12: AgentFactory Ignores TuiConfig
**Location:** `src/ClientFactory.cs:60-72`

```csharp
var config = new AgentConfig(
    ModelEndpoint: resolved.BaseUrl,
    ModelName: resolved.ModelId,
    MaxContextTokens: resolved.ContextWindow,
    MaxSteps: 30,  // <-- hardcoded, ignores TuiConfig.MaxSteps
    MaxRetries: 2,
    StallThreshold: 5,
    WorkingDirectory: workingDir,
    Temperature: resolved.Temperature,
    ApiKey: ...,
    ExtraHeaders: ...
    // EnableStreaming: ???  // <-- not set, defaults to false
);
```

TuiConfig has `MaxSteps`, `Streaming` settings that are never passed to the agent.

---

## Recommendations

### Immediate (Low Risk)

1. **Delete InterventionManager.cs** or rename to `ARCHIVED_InterventionManager.cs` until it's wired up
2. **Remove orphan TuiConfig settings** or implement them:
   - `ThinkingMode`: Wire up to TuiObserver.OnModelResponse()
   - `AutoShowDiffs`: Auto-trigger diff after write operations
   - `MaxToolOutputLines`: Replace hardcoded limits
   - `SubAgents`: Remove until feature exists
   - `Theme`: Apply to AnsiConsole settings
3. **Fix TuiObserver comment** on line 128 (lies about InterventionManager)

### Short Term (Medium Risk)

4. **Create a real IntegrationLayer class** that owns:
   - Single ToolInterceptor that composes git checkpoint + diff + optional skip/edit
   - TuiConfig application
   - Pause/resume state machine (wire up Space key during spinner)
   - Proper cancellation token wiring
5. **Deduplicate FormatToolArgs** by having core expose a formatter, or TUI use core's internal one
6. **SessionManager should use core's types** instead of redefining JsonlEntry

### Architectural (Higher Risk)

7. **Fix core's Console.Error usage** -- add debug logging interface that routes through observer
8. **Decide on session semantics:**
   - Option A: Stateless (current behavior, document it)
   - Option B: Stateful (pass history between RunAsync calls)
9. **Unify interception:** Pick one mechanism (observer method OR control delegate) and kill the other

---

## File Health Summary

| File | Lines | Issues | Verdict |
|------|-------|--------|---------|
| Program.cs | 353 | 5, 8, 9, 11, 12 | Too many concerns |
| TuiObserver.cs | 343 | 4, 5, 7 | OK, but has lies |
| InterventionManager.cs | 106 | 1, 3 | Dead |
| TuiConfig.cs | 116 | 2, 12 | Config theater |
| SessionManager.cs | 255 | 6, 9 | Reimplements core |
| ClientFactory.cs | 73 | 12 | Ignores config |
| InputHandler.cs | 393 | - | Actually solid |
| GitCheckpoint.cs | 172 | - | Clean |
| DiffViewer.cs | 156 | - | Clean |
| StatusBar.cs | 61 | - | Clean |
| TokenBudget.cs | 114 | - | Clean |
| SkillBrowser.cs | 76 | - | Clean |
| ModelArena.cs | 126 | - | Clean |
| ModelSelector.cs | 116 | - | Clean |

---

## Related Memory

From `.memory`:
- Spectre markup parser crashes on square brackets in data
- Console.Error corrupts Spectre spinner (the reason for the redirect hack)
- Console.SetCursorPosition breaks past terminal width

These gotchas explain some of the defensive coding, but don't justify the architectural rot.
