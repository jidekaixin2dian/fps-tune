# fps-tune

Windows **system-layer** frame-rate tuning for Windows games, including *Delta Force (example)*, *Valorant*,
*CS2*, *APEX* and most PC titles. Built with C# WPF + .NET 8; `FpsTune.exe` also ships a
headless CLI mode for command-line / AI-agent use.

- **System layer only, fully reversible** — touches Windows settings (registry, power
  plans, services, boot config). Every write is backed up first; one-command restore.
- **Never touches the game** — no game-file edits, no process injection, no anti-cheat
  interaction, no virtualization toggling, no GPU model spoofing.
- **One engine** — the same C# engine powers the GUI and the CLI; 33 optimizations
  defined in a single catalog (`catalog/catalog.json`).
- **Automatic backup & restore** — every write is backed up first; one-command restore.
- **MIT licensed, clean-room implementation.**

![build](https://github.com/jidekaixin2dian/fps-tune/actions/workflows/build.yml/badge.svg)
![release](https://img.shields.io/github/v/release/jidekaixin2dian/fps-tune)
![license](https://img.shields.io/github/license/jidekaixin2dian/fps-tune)

## Why this project exists

Existing tools in this space (e.g. DeltaForceBooster) use a proprietary EULA that forbids
modification and redistribution. This project is written from scratch against the *public
feature list* only, releases under a permissive license, and deliberately does less:
no telemetry; in-app update checks only (nothing is downloaded without your consent).

## Files

| File | Purpose |
|---|---|
| `FpsTune.Wpf/` | WPF app + C# engine (.NET 8): detect / optimize / A-B / friend test / backup / settings; `FpsTune.exe` doubles as the headless CLI |
| `catalog/catalog.json` | Single source of truth: 33 optimization items + presets |
| `tuning-experiment.ps1` | A/B auto-tuning: baseline sampling, stability check, 3 candidate groups, rule-based keep/revert, CSV export (calls FpsTune.exe for apply/restore) |
| `tools/friend-test.ps1` | Friend-test helper: one command generates a Markdown + CSV test record |
| `SKILL.md` | Agent skill instructions: detect → explain → confirm → apply → report, with hard red lines |
| `TESTING.md` | Friend-testing guide: full A/B, or minimal FPS / GamePP data template |
| `installer/` | Inno Setup packaging |

## Quick start (CLI)

`FpsTune.exe` enters headless CLI mode when given arguments (no GUI window, stdout output, exit code 0/1):

```powershell
# 1. Detect (read-only)
FpsTune.exe -Detect -Json

# 2. Apply (explain to the user and get consent first)
FpsTune.exe -Apply -Preset balanced -Json

# 3. Restore
FpsTune.exe -Restore -Json

# Also: -Version / -ListRestore -Json / -Apply -Items id1,id2 / -Restore -Items id1,id2
```

Admin-required items fail loudly in non-elevated terminals (the exe runs as asInvoker).

## Quick start (GUI, functional)

- Recommended installer: <https://github.com/jidekaixin2dian/fps-tune/releases>
- Portable: unzip `FpsTune-Portable-*.zip` and run `FpsTune.exe`

Or build locally:

```powershell
cd <project-directory>
.\build-wpf.ps1 -Mode Build
.\FpsTune.Wpf\bin\Release\net8.0-windows\FpsTune.exe
```

## Quick start (AI agent)

Project repository: <https://github.com/jidekaixin2dian/fps-tune>

If the agent is already inside the repo:

```text
Read SKILL.md in the current directory and strictly follow its workflow to tune Delta Force (example) frame rates.
```

If the agent does not have the project yet, let it clone the repo first:

```text
Run: git clone https://github.com/jidekaixin2dian/fps-tune.git
Then read SKILL.md in the cloned directory and strictly follow its workflow to tune Delta Force (example) frame rates.
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

- Every reversible change is backed up to `%LocalAppData%\FpsTune\backup` before
  writing; restore is per-item and restores exact original values (deleting values that
  did not exist before).
- Admin-required items fail loudly in non-admin sessions.
- No fixed FPS promises — results vary by hardware; controversial items are unchecked by default.
- No code-signing certificate; SmartScreen may warn about unknown publisher (normal for
  personal open-source projects).

## License

MIT. Written from scratch; no derivative relationship to any existing tool's code or docs.
