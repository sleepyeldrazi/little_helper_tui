# TUI Architecture Fix Plan

Based on the architecture audit, verified against source code.

## Triage

### DROPPED (not fixing)

- **Issue 5 (FormatToolArgs duplication)**: Nice-to-have, not broken. Core's version is
  internal/private. Both are simple switches. If core adds a tool, TUI display silently
  falls back to truncated JSON -- not a crash, just less pretty. File a note to core
  to expose a public formatter later.

- **Issue 6 (SessionManager log format)**: Would require core making SessionLogger
  types public. The current approach works -- if core changes the format, TUI parsing
  degrades gracefully (returns null entries which are skipped). File an issue on core,
  don't block on this.

- **Issue 10 (Agent resets messages)**: This is intentional. Core's RunAsync is
  stateless by design. The TUI observer keeps its own display history. Multi-turn
  persistence is a future feature, not a bug. Already documented in README TODO.

### DEFERRED (future work, not this round)

- **Issue 3 (ToolInterceptor composition)**: Right now Program.cs owns the interceptor
  and it works. Composition is only needed when InterventionManager gets wired up.
  Fix InterventionManager (or delete it) first, then worry about composition.

- **Issue 8 (No cancellation path)**: This requires solving the Spectre spinner input
  capture problem. The spinner owns the console during agent runs. Options:
  a) Background thread polling Console.KeyAvailable, b) `:pause`/`:resume` commands
  in a separate pane, c) switch to Spectre.Live<> which supports keyboard events.
  This is a UX redesign, not a quick fix. Defer to a dedicated sprint.

### KEEPING (fixing now)

The remaining 8 issues are real, verified, and fixable without core changes:

---

## Fix 1: Delete InterventionManager.cs (Issue 1)

**Why:** 106 lines of dead code. Never instantiated anywhere. Its ToolInterceptor
definition would clobber Program.cs's if ever wired up. The comment in TuiObserver
line 128 references it incorrectly.

**What to do:**
- Delete `src/InterventionManager.cs`
- Remove the lying comment in TuiObserver line 128
- Change TuiObserver.OnToolCallExecuting to just return `call` (no comment about InterventionManager)
- Update README -- remove InterventionManager from architecture listing

**Risk:** None. The class is unused.

---

## Fix 2: Wire up orphan TuiConfig settings (Issue 2 + Issue 12)

**Why:** 6 settings are loaded, displayed in `:config`, but never used. This is config theater.

**What to do:**

### 2a: ThinkingMode -> TuiObserver
- Pass `TuiConfig` to `TuiObserver` (or store it on a static/shared reference)
- In `OnModelResponse`, replace the hardcoded logic with:
  - `"hidden"`: skip the thinking panel entirely
  - `"condensed"`: show first+last 3 lines (current behavior)
  - `"full"`: show complete thinking content

### 2b: AutoShowDiffs -> Program.cs
- After the agent run completes, check `_tuiConfig.AutoShowDiffs`
- If true and the agent wrote files, auto-call `DiffViewer.ShowLastDiff()`

### 2c: MaxToolOutputLines -> TuiObserver
- Replace hardcoded values (5, 6, 8) in TuiObserver render methods with
  `_tuiConfig.MaxToolOutputLines` (for generic output) and reasonable
  derived values for read (2/3) and write (1/2 of max).

### 2d: SubAgents -> REMOVE from TuiConfig
- Delete the `SubAgentConfig` class
- Delete the `SubAgents` property from TuiConfig
- The feature doesn't exist and there's no plan to add it soon

### 2e: Theme -> TuiObserver / Program.cs
- After loading config, apply theme:
  - `"default"`: current colors (no change)
  - `"monochrome"`: replace all `Color.X` and `[color]` with `Color.Grey` and `[dim]`
  - `"dark"`: use darker panel borders, softer text colors
- This is a best-effort mapping, not a full theme engine

### 2f: MaxSteps + Streaming -> ClientFactory
- Pass `TuiConfig` (or just the two values) to `CreateAgent()`
- Use `_tuiConfig.MaxSteps` instead of hardcoded `30`
- Use `_tuiConfig.Streaming` for `AgentConfig.EnableStreaming`

---

## Fix 3: Fix TuiObserver.OnToolCallExecuting comment (Issue 4 partial)

**Why:** The comment says "Diff snapshotting is handled by InterventionManager's
ToolInterceptor" which is false. Diff snapshotting is handled by Program.cs's inline
lambda on the ToolInterceptor delegate.

**What to do:**
- Remove the misleading comment
- Keep the method as a no-op pass-through (`return call;`) since core calls it
  and it's part of the interface
- The interception pipeline in core works like this:
  1. Observer's OnToolCallExecuting is called first (returns call unchanged)
  2. Then Control.ToolInterceptor is called (can return null to skip, or modified call)
  3. If ToolInterceptor returns non-null, it overrides the observer result
- This is fine. The observer method is available for future use (e.g. logging,
  approval prompts). Just make the comment accurate.

---

## Fix 4: Observer state leak on model switch (Issue 7)

**Why:** When switching models with `:model`, the observer's token counts and step
counter carry over from the previous model. Shows misleading cumulative stats.

**What to do:**
- In Program.cs, when `newModel != null`, also reset the observer:
  ```csharp
  if (newModel != null) { resolved = newModel; agent = null; observer = new TuiObserver(); }
  ```
- Pass TuiConfig reference to the new observer so settings persist across resets

---

## Fix 5: SessionLogger per-prompt -> per-session (Issue 9)

**Why:** Every prompt creates a new log file. The `:sessions` browser shows fragmented
per-prompt runs instead of actual sessions. This makes session browsing nearly useless.

**What to do:**
- Move SessionLogger creation out of the while loop
- Create it once on first prompt, or when model is selected/switched
- Dispose it only on program exit or `:reset`
- On `:reset`, create a new logger for the fresh session
- This means the logger needs to be a class field, not a local variable

```csharp
// At the top of Main, after model selection:
SessionLogger? logger = null;

// In the prompt loop:
logger ??= new SessionLogger(modelId, workingDir);

// In HandleCommand, for :reset:
case ":reset":
    logger?.Dispose();
    logger = null;  // Will be recreated on next prompt
    ...
```

---

## Fix 6: Console.Error redirect -> OnError (Issue 11 partial fix)

**Why:** Core uses Console.Error.WriteLine in ~12 places. The TUI captures stderr,
deduplicates (hiding real repeated errors), truncates to 5 lines, and displays it.
This is a workaround for core not having proper error routing.

**What to do (TUI side, no core changes):**
- Keep the stderr redirect (it's necessary to prevent Spectre corruption)
- Remove `.Distinct()` from the stderr processing -- repeated errors are meaningful
- Increase the truncation from 5 to 10 lines
- Add a `[stderr]` prefix to distinguish these from observer-reported errors

**What to do (core side, file issue):**
- Add `OnError(message)` to the observer routing in core's Agent.cs alongside
  the existing `Log()` calls
- This is a core repo change, not done in this PR

---

## Fix 7: AgentConfig now respects TuiConfig (covered by Fix 2f)

Already addressed in Fix 2f. ClientFactory.CreateAgent takes TuiConfig values
for MaxSteps and EnableStreaming.

---

## Fix 8: Minor README updates

- Remove InterventionManager from architecture listing
- Remove `Space=pause/resume` from the TODO section (not implemented)
- Update `:config` output to note which settings are active vs not yet wired

---

## Execution Order

```
Phase A: Delete dead code + fix comments (low risk, high impact)
  1. Delete InterventionManager.cs
  2. Fix TuiObserver.OnToolCallExecuting comment
  3. Remove SubAgentConfig from TuiConfig
  4. Update README

Phase B: Wire up existing config (medium risk, medium effort)
  5. Pass TuiConfig to ClientFactory (MaxSteps, Streaming)
  6. Pass TuiConfig to TuiObserver (ThinkingMode, MaxToolOutputLines, Theme)
  7. AutoShowDiffs in Program.cs after agent run
  8. Fix observer leak on model switch

Phase C: Lifecycle fixes (medium risk, careful testing needed)
  9. SessionLogger per-session instead of per-prompt
  10. Fix stderr processing (remove Distinct, increase limit)
```

---

## Files Modified

| File | Changes |
|------|---------|
| `src/InterventionManager.cs` | DELETE |
| `src/TuiObserver.cs` | Accept TuiConfig, use ThinkingMode/MaxToolOutputLines/Theme, fix comment |
| `src/TuiConfig.cs` | Remove SubAgentConfig, clean up |
| `src/Program.cs` | Fix observer reset on model switch, per-session logger, auto-show diffs, stderr fix, pass config to factory |
| `src/ClientFactory.cs` | Accept TuiConfig or derived values for MaxSteps/EnableStreaming |
| `README.md` | Remove InterventionManager, update TODO |

## Estimated Effort

- Phase A: 30 minutes (deletion + comment fixes)
- Phase B: 1-2 hours (wiring config through, testing each setting)
- Phase C: 1 hour (logger lifecycle + stderr fix)
- Total: ~3-4 hours