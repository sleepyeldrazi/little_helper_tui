# Agent Instructions

**Before modifying any code in this repository, you MUST read:**

## Local Project Documentation

1. **[README.md](./README.md)** — TUI architecture, features, and commands
2. **[core/README.md](./core/README.md)** — Core library design principles and non-negotiable rules

## External Research Context

The following research analysis informs architectural decisions for this project:

3. **[coding-harness-feedback/conclusion.md](https://raw.githubusercontent.com/sleepyeldrazi/coding-harness-feedback/main/conclusion.md)** — Comprehensive analysis of coding agent harnesses (opencode, pi, hermes, forgecode) with research-backed recommendations for local/smaller models

4. **[coding-harness-feedback/README.md](https://raw.githubusercontent.com/sleepyeldrazi/coding-harness-feedback/main/README.md)** — Project overview and quick reference for harness suitability

## Critical Design Rules (from core/README.md)

- **One System Message, Under 1000 Tokens** — "Lost in the Middle" shows 30%+ degradation on buried content
- **5 Tools Maximum** — Small models can't reliably select from more
- **No File Listing Extraction** — Model uses `write("path", content)`, we write it
- **Verification via Skills, Not Core Loop** — The `verify` skill handles build/test
- **Stall = 5 Repeated Observations** — Kill the loop after 5 identical tool outcomes
- **No Truncation of Tool Output** — Full file reads, full command output. Context compaction handles overflow
- **State Machine, Not Free-Form Loop** — FSM yields 63.73% success vs 40.3% for ReAct
- **Files ≤ 300 Lines, Single Responsibility**

## Research-Backed Patterns

From the coding harness analysis:

- **Observation masking over summarization** — JetBrains research shows 2.6% higher solve rates, 52% cheaper
- **Generate → Verify → Repair** — ATLAS achieves 74.6% with 14B models via this pipeline
- **Small specialists for mechanical subproblems** — Route grep/read/run to small models, reserve large models for synthesis
- **State machine with explicit error states** — StateFlow: 63.73% success vs 40.3% for ReAct

---

*When in doubt, re-read [core/README.md](./core/README.md) Rule #6: "No Truncation of Tool Output"*
