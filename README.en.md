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
| `TESTING.md` | Friend-testing guide: full A/B, or minimal FPS / GamePP data template |

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
are insufficient or the baseline is unstable (CV > 0.05). Requires the official PresentMon CLI
for real sampling — install it yourself (`winget install Intel.PresentMon.Console`); the tool
will never download or run installers for you.

## Safety

- Every reversible change is backed up to `%LocalAppData%\DeltaOptimizer\backup` before
  writing; restore is per-item and restores exact original values (deleting values that
  did not exist before).
- Admin-required items fail loudly in non-admin sessions.
- No fixed FPS promises — results vary by hardware; controversial items are unchecked by default.
- No code-signing certificate; SmartScreen may warn about unknown publisher (normal for
  personal open-source projects).

## Real A/B sampling results (firing range)

Test setup: i9-13900HX + RTX 5070 Ti Laptop GPU, Windows 11, official PresentMon
(`winget install Intel.PresentMon.Console`), fixed firing-range scene, 3 samples × 90 s
per group; baseline CV 1.18% (threshold ≤ 0.05, stable).

| Group | Avg FPS | 1% low | P99 (ms) | Stutters | Verdict |
|---|---|---|---|---|---|
| Baseline | 268.29 | 205.24 | 4.88 | 0 | — |
| group-1 scheduling | 267.57 | 207.19 | 4.83 | 0 | No meaningful gain (avg -0.3%, 1% low +1.0%), auto-reverted |
| group-2 background | 267.40 | 191.58 | 5.30 | 0 | No meaningful gain (avg -0.3%, 1% low -6.7%), auto-reverted |
| group-3 power | 265.30 | 196.69 | 5.09 | 0 | No meaningful gain (avg -1.1%, 1% low -4.2%), auto-reverted |

Conclusion: none of the three low-risk candidate groups reached the keep rule
(avg ≥ 2% or 1% low ≥ 5%) on this machine/scene, so the script auto-reverted all of
them and system settings were restored to their pre-test state.

> Friends are welcome to test too. Without PresentMon, a simple before/after
> average FPS / 1% low screenshot from GamePP or the in-game overlay is useful.
> See [TESTING.md](TESTING.md) for the template.

## License

MIT. Written from scratch; no derivative relationship to any existing tool's code or docs.
