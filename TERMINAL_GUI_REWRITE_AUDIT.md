# Terminal.Gui Rewrite Audit

Date: 2026-04-11
Branch audited: `plan/terminal-gui-rewrite`
Baseline compared: `main`
Scope: Missing behavior after Spectre.Console -> Terminal.Gui rewrite

## Executive Summary
The rewrite successfully moved rendering/input ownership to `Terminal.Gui`, but it regressed core UX and behavior from `main`.
The three major failures are:
1. Model/provider startup flow is incomplete and no longer matches old behavior.
2. Several dialog/button flows are wired incorrectly, so user actions appear to do nothing.
3. Main chat surface is not actually scrollable and visual presentation regressed significantly.

No local edits were committed during this audit; this file is a handoff plan only.

## Confirmed Breakages

### 1) Startup and model/provider flow regressions
Files:
- `src/Program.cs`
- `src/Dialogs/ModelSelectionDialog.cs`
- Deleted old references: `src/ModelSelector.cs`, `src/EndpointSetup.cs` (from `main`)

Findings:
- Startup now always opens model selection dialog and ignores legacy/default flow logic from `main`:
  - `tui.json` `default_model` fallback behavior is not preserved.
  - `models.json` `default_model` behavior is not preserved.
  - First-run/no-provider setup path from old flow is gone.
- Provider/model list is present but reduced behavior around default resolution/manual fallback.
- Manual entry lacks old context-window resolution logic (old code attempted config resolve then endpoint query, then fallback).

Impact:
- Users with configured defaults are forced through selection every run.
- First-run onboarding and provider ergonomics are worse than `main`.

---

### 2) Dialog controls and button actions are unreliable/non-functional
Files:
- `src/Dialogs/ModelSelectionDialog.cs`
- `src/Dialogs/SessionsDialog.cs`
- `src/Dialogs/TokensDialog.cs`
- `src/SkillBrowser.cs`

Findings:
- Dialog run loops are not consistently stopped when actions complete.
  - Several handlers set local state but do not call `Application.RequestStop(...)` for the dialog they are in.
  - This causes “button pressed but nothing happens” behavior.
- Buttons are created with `Title` instead of canonical `Text` usage in current Terminal.Gui examples/docs.
- `SkillBrowser.Browse` contains unreachable code (`if (true)` path) and returns without a proper dialog completion pattern.
- `SessionsDialog` detail/index behavior has indexing mismatch risk versus old `:sessions N` semantics.

Impact:
- Core interaction confidence is broken: users cannot trust select/cancel flows.

---

### 3) Chat area is not a true scrollable message viewport
Files:
- `src/Views/MainWindow.cs`
- `src/Observers/TerminalGuiObserver.cs`

Findings:
- `_chatContent` is a plain `View`, not wrapped in `ScrollView`.
- Messages are stacked by `Pos.Bottom` but there is no controlled content-size update + auto-scroll behavior.
- Mouse wheel / keyboard scroll history behavior from old app is absent.
- Input history function exists (`NavigateHistory`) but is not bound to keys.

Impact:
- The biggest user-reported issue (scrolling) remains unresolved in rewrite.
- Long sessions become hard to use.

---

### 4) Visual regression (“ugly” vs old simple/cute UI)
Files:
- `src/Views/MessageViews.cs`
- `src/Views/MainWindow.cs`
- `src/Observers/TerminalGuiObserver.cs`

Findings:
- Message views use basic frames/labels with minimal hierarchy and no coherent theme.
- Status line is plain text and lacks old compact contextual clarity.
- Tool/result rendering uses hardcoded truncation and no clear severity treatment.
- Old Spectre look had clear panel differentiation; Terminal.Gui rewrite currently looks raw/default.

Impact:
- Readability and perceived quality dropped, even where functionally equivalent.

---

### 5) History/tokens/session behavior drift from main
Files:
- `src/TuiController.cs`
- `src/Dialogs/TokensDialog.cs`
- `src/Dialogs/SessionsDialog.cs`

Findings:
- `:history` uses `msg.Content` directly; potential nullability warning and rough formatting.
- Token dialog uses simplistic estimations and does not mirror old `Compaction.EstimateTokens` strategy.
- Sessions dialog parses raw JSONL lines directly instead of relying on core `SessionLogReader` abstraction already provided in `core/src/SessionLogger.cs`.

Impact:
- Feature parity exists only partially; fidelity/robustness regressed.

---

### 6) Build warnings show quality gaps in rewritten files
From `dotnet build little_helper_tui.sln`:
- `src/SkillBrowser.cs(33,9): CS0162 Unreachable code`
- Nullability warnings in `src/Views/MainWindow.cs`, `src/Dialogs/SessionsDialog.cs`, `src/TuiController.cs`
- (Unrelated package advisory warning also present)

Impact:
- Signals unresolved control-flow and nullable-safety issues in the rewrite.

## Root Cause Summary
1. Rewrite replaced architecture quickly but did not fully map old behavioral contracts from `main`.
2. Modal dialog lifecycle patterns in Terminal.Gui were inconsistently implemented.
3. The hardest UX requirement (chat scrolling with large history) was not completed, only partially scaffolded.
4. Visual polish was deferred, but this made regression highly visible.

## Implementation Plan (Handoff)

### Phase 1: Restore functional correctness first
1. Fix modal dialog lifecycle everywhere.
   - Standardize: on successful action and cancel action, set result state + `Application.RequestStop(dialogInstance)`.
   - Remove dead/unreachable control-flow (`SkillBrowser`).
2. Normalize button definitions to `Button.Text` usage.
3. Re-implement startup model flow parity from `main`:
   - Respect `tui.json` `default_model`.
   - Fallback to `models.json` `default_model`.
   - If no providers configured, enter setup/manual path.
   - If resolve fails, fallback to picker/manual behavior consistent with old flow.

Success criteria:
- Model select/manual/cancel works every time.
- All dialog buttons reliably close and return state.
- Defaults behave like old app.

---

### Phase 2: Implement real scrollable chat surface
1. Convert main chat layout to `ScrollView` + content container.
2. Route all chat insertions through one method in `MainWindow`:
   - append view
   - recompute content size
   - auto-scroll to bottom (unless user intentionally scrolled up)
3. Bind keyboard history (`Up/Down`) and scroll keys (PgUp/PgDn or similar).
4. Add mouse wheel handling for chat scroll.

Success criteria:
- Long transcript is navigable by mouse and keyboard.
- New messages auto-follow at bottom by default.

---

### Phase 3: Recover “simple + cute” visual language
1. Define a small, explicit color scheme set for:
   - user message
   - assistant message
   - tool success
   - tool error
   - thinking/log/status
2. Improve message card spacing/title consistency.
3. Keep style minimal and clean (not overdesigned), matching old app tone.
4. Tighten status line with model/step/token summary.

Success criteria:
- Quick scan readability matches or beats old Spectre output.
- Visual hierarchy obvious at a glance.

---

### Phase 4: Feature parity polish
1. `:sessions` to use core `SessionLogReader` where possible (reduce format drift risk).
2. `:tokens` to align token estimation with core helpers where feasible.
3. `:history` formatting cleanup + null-safe content handling.
4. Verify `:diff`, `:files`, `:skills`, `:model`, `:reset`, `:cancel` end-to-end.

Success criteria:
- Command behavior matches expectations from README/main.

---

### Phase 5: Verification
Run at minimum:
- `dotnet build little_helper_tui.sln`
- Manual smoke flow:
  - start app with and without configured providers/defaults
  - select model, cancel, manual entry
  - run one prompt, confirm messages render
  - scroll long output
  - execute key commands (`:tokens`, `:sessions`, `:skills`, `:model`, `:reset`, `:quit`)

Target:
- No new warnings introduced by fixes.
- Existing rewrite warnings reduced/eliminated where touched.

## Suggested File-Level Work Queue
1. `src/Program.cs` (startup/default model logic)
2. `src/Dialogs/ModelSelectionDialog.cs` (close/selection/manual flow)
3. `src/Views/MainWindow.cs` (ScrollView + input history keybindings)
4. `src/Observers/TerminalGuiObserver.cs` (append via unified API + scroll update)
5. `src/Views/MessageViews.cs` (presentation cleanup)
6. `src/SkillBrowser.cs`, `src/Dialogs/SessionsDialog.cs`, `src/Dialogs/TokensDialog.cs` (parity and reliability)

## Notes for Resume
- Worktree was clean at handoff.
- No code changes were applied in this audit pass.
- This document is based on direct comparison against `main` and inspection/build of current branch files.
