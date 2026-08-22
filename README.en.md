# delta-force-tune

Windows **system-layer** frame-rate tuning for *Delta Force* (三角洲行动), designed to be
driven by an AI agent (Claude Code / Codex / WorkBuddy / Doubao, etc.) or plain CLI.

- ✅ **System layer only, fully reversible** — touches Windows settings (registry, power
  plans, services, boot config). Every write is backed up first; one-command restore.
- ✅ **Never touches the game** — no game-file edits, no process injection, no anti-cheat
  interaction, no virtualization toggling, no GPU model spoofing.
- ✅ **Pure PowerShell 5.1** — ships with Windows 10/11, zero dependencies, no install.
- ✅ **JSON output** — structured results for agents and scripts.
- ✅ **MIT licensed, clean-room implementation.**

## Why this project exists

Existing tools in this space (e.g. DeltaForceBooster) use a proprietary EULA that forbids
modification and redistribution. This project is written from scratch against the *public
feature list* only, releases under a permissive license, and deliberately does less:
no GUI, no telemetry, no auto-updater — one script, plug and play.

## Files

| File | Purpose |
|---|---|
| `delta-optimizer.ps1` | Core engine: detect / apply / restore, 22 optimizations + 3 read-only health checks |
| `tuning-experiment.ps1` | A/B auto-tuning: baseline sampling, stability check, 3 candidate groups, rule-based keep/revert, CSV export |
| `SKILL.md` | Agent skill instructions: detect → explain → confirm → apply → report, with hard red lines |

## Quick start (CLI)

```powershell
# 1. Detect (read-only)
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Detect -Json

# 2. Apply (explain to the user and get consent first; -Force means "user agreed")
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply -Preset balanced -Force -Json

# 3. Restore
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json
```

## Quick start (AI agent)

Send an agent that can run PowerShell the instruction:

```text
Read SKILL.md in this repo and follow its workflow to tune Delta Force frame rates.
```

## A/B auto-tuning

```powershell
# Baseline first (game running, fixed map/graphics/route; 3 samples)
powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Baseline -Json

# Test candidate groups one by one
powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Test -Group group-1 -Json

# Report + CSV export
powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Report -Json
```

Decisions are rule-based (avg FPS / 1% low / P99 frame time / stutter count vs baseline):
keep on measurable gain, **auto-revert otherwise**. No conclusion is formed when samples
are insufficient or the baseline is unstable (CV > 0.05). Requires Microsoft PresentMon
for real sampling — install it yourself (`winget install Microsoft.PresentMon`); the tool
will never download or run installers for you.

## Safety

- Every reversible change is backed up to `%LocalAppData%\DeltaOptimizer\backup` before
  writing; restore is per-item and restores exact original values (deleting values that
  did not exist before).
- Admin-required items fail loudly in non-admin sessions.
- No fixed FPS promises — results vary by hardware; controversial items are unchecked by default.
- No code-signing certificate; SmartScreen may warn about unknown publisher (normal for
  personal open-source projects).

## License

MIT. Written from scratch; no derivative relationship to any existing tool's code or docs.
