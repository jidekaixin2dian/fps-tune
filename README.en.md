# delta-force-tune

Windows **system-layer** frame-rate tuning for Windows games, including *Delta Force*, *Valorant*,
*CS2*, *APEX* and most PC titles. Built with C# WPF + .NET 8, with PowerShell scripts retained
for compatibility/fallback.

- ✅ **System layer only, fully reversible** — touches Windows settings (registry, power
  plans, services, boot config). Every write is backed up first; one-command restore.
- ✅ **Never touches the game** — no game-file edits, no process injection, no anti-cheat
  interaction, no virtualization toggling, no GPU model spoofing.
- ✅ **C# WPF + .NET 8** — modern desktop UI, with PowerShell scripts retained for CLI/Agent compatibility.
- ✅ **Automatic backup & restore** — every write is backed up first; one-command restore.
- ✅ **MIT licensed, clean-room implementation.**

![build](https://github.com/jiaxindeyang-a11y/delta-force-tune/actions/workflows/build.yml/badge.svg)
![release](https://img.shields.io/github/v/release/jiaxindeyang-a11y/delta-force-tune)
![license](https://img.shields.io/github/license/jiaxindeyang-a11y/delta-force-tune)

## Why this project exists

Existing tools in this space (e.g. DeltaForceBooster) use a proprietary EULA that forbids
modification and redistribution. This project is written from scratch against the *public
feature list* only, releases under a permissive license, and deliberately does less:
no telemetry, no auto-updater; optional functional GUI, but the core is still one script, plug and play.

## Files

| File | Purpose |
|---|---|
| `delta-optimizer.ps1` | Core engine: detect / apply / restore, 22 optimizations + 3 read-only health checks |
| `delta-gui.ps1` | Functional WinForms GUI (visual pass later); detect / consent-apply / A/B / friend test / backup |
| `tuning-experiment.ps1` | A/B auto-tuning: baseline sampling, stability check, 3 candidate groups, rule-based keep/revert, CSV export |
| `friend-test.ps1` | Friend-test helper: one command generates a Markdown + CSV test record |
| `SKILL.md` | Agent skill instructions: detect → explain → confirm → apply → report, with hard red lines |
| `TESTING.md` | Friend-testing guide: full A/B, or minimal FPS / GamePP data template |
| `GUI_PLAN.md` | GUI-phase compliant feature plan (no spoofing, no overlay) |
| `GUI_DESIGN_PROMPT.md` | Visual design prompt for the V4 flash vision pass |

## Quick start (CLI)

```powershell
# 1. Detect (read-only)
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Detect -Json

# 2. Apply (explain to the user and get consent first; -Force means "user agreed")
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply -Preset balanced -Force -Json

# 3. Restore
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json
```

## Quick start (GUI, functional)

- Recommended installer: <https://github.com/jiaxindeyang-a11y/delta-force-tune/releases>
- Portable: unzip `DeltaForceTune-Portable.zip` and run `DeltaForceTune.exe`

Or build locally:

```powershell
cd <project-directory>
.uild-wpf.ps1 -Mode Build
.\DeltaForceTune.Wpfin\Release
et8.0-windows\DeltaForceTune.exe
```

## Quick start (AI agent)

Project repository: <https://github.com/jiaxindeyang-a11y/delta-force-tune>

If the agent is already inside the repo:

```text
Read SKILL.md in the current directory and strictly follow its workflow to tune Delta Force frame rates.
```

If the agent does not have the project yet, let it clone the repo first:

```text
Run: git clone https://github.com/jiaxindeyang-a11y/delta-force-tune.git
Then read SKILL.md in the cloned directory and strictly follow its workflow to tune Delta Force frame rates.
```

> Do not just say "optimize my FPS": the agent does not know where the project is.
> Make sure it obtains this repo first and treats SKILL.md as the only operating procedure.

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

## License

MIT. Written from scratch; no derivative relationship to any existing tool's code or docs.
