# fps-tune

[简体中文](README.md) · [Download latest release](https://github.com/jidekaixin2dian/fps-tune/releases/latest)

Windows **system-layer** frame-rate tuning for Windows games, including *Delta Force*, *Valorant*,
*CS2*, *APEX* and most PC titles. Built with C# WPF + .NET 8; `FpsTune.exe` also ships a
headless CLI mode for command-line / AI-agent use.

- **System layer only, fully reversible** — touches Windows settings (registry, power
  plans, services, boot config). Every write is backed up first; one-command restore.
- **Never touches the game** — no game-file edits, no process injection, no anti-cheat
  interaction, no virtualization toggling, no GPU model spoofing.
- **One engine** — the same C# engine powers the GUI and the CLI; 33 optimizations
  defined in a single catalog (`catalog/catalog.json`).
- **Automatic backup & restore** — every write is backed up first; one-command restore.
- **Traceable releases** — the post-commit release script embeds the full final Git SHA
  in `InformationalVersion` and verifies it in the produced executable metadata.
- **MIT licensed, clean-room implementation.**

![release](https://img.shields.io/github/v/release/jidekaixin2dian/fps-tune)
![license](https://img.shields.io/github/license/jidekaixin2dian/fps-tune)

## Why this project exists

Existing tools in this space (e.g. DeltaForceBooster) use a proprietary EULA that forbids
modification and redistribution. This project is written from scratch against the *public
feature list* only, releases under a permissive license, and deliberately does less:
no telemetry; in-app updates require explicit consent before download and SHA-256 verification.

## Files

| File | Purpose |
|---|---|
| `FpsTune.Wpf/` | WPF app + C# engine (.NET 10): detect / optimize / performance sessions / A-B wizard / auto-profile activity / friend test / backup / settings; `FpsTune.exe` doubles as the headless CLI (including `-Experiment` A/B orchestration) |
| `catalog/catalog.json` | Single source of truth: 33 optimization items + presets |
| `tools/friend-test.ps1` | Friend-test helper: one command generates a Markdown + CSV test record |
| `SKILL.md` | Agent skill instructions: detect → explain → confirm → apply → report, with hard red lines |
| `TESTING.md` | Friend-testing guide: full A/B, or minimal FPS / GamePP data template |
| `installer/` | Inno Setup packaging |

## Quick start (CLI)

`FpsTune.exe` enters headless CLI mode when given arguments (no GUI window, stdout output, exit code 0/1):

```powershell
# 1. Detect (read-only)
.\FpsTune.exe -Detect -Json

# 2. Apply (explain to the user and get consent first)
.\FpsTune.exe -Apply -Preset balanced -Json

# 3. Restore
.\FpsTune.exe -Restore -Json

# Also: -Version / -ListRestore -Json / -Apply -Items id1,id2 / -Restore -Items id1,id2
```

Admin-required items fail loudly in non-elevated terminals (the exe runs as asInvoker).

## Quick start (GUI)

- Requires Windows 10/11 x64.
- Standalone: download `FpsTune.exe` and run it; the .NET runtime is included.
- Recommended installer: <https://github.com/jidekaixin2dian/fps-tune/releases>
- Portable: unzip `FpsTune-Portable-*.zip`, install the [.NET 8 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), then run `FpsTune.exe`

Version 1.6 uses a console layout with top tabs for overview, detection, optimization,
performance sessions, A/B experiments, friend testing, backups/logs, and settings.
The UI supports dark, light, and system themes. Version 1.6.1 hardens executable/script
resolution, validates imported profiles, and fixes installer compiler diagnostics.
Version 1.6.2 adds a persistent compact/classic display switch, a redesigned optimization
workspace, clearer settings, shared live sampling, chart resize fixes, and native window
animations. Manual item changes now select custom mode. New application artwork and a
dedicated DPI-aware tray icon improve clarity at small sizes.
Verify downloads against the release's `SHA256SUMS-v<version>.txt` with `Get-FileHash -Algorithm SHA256`.
Game detection uses uninstall registry entries and known installation directories; it does
not read running processes' executable paths. Use `-Game` for an unrecognized game.

Or build locally:

```powershell
cd <project-directory>
.\build-wpf.ps1 -Mode Build
.\FpsTune.Wpf\bin\Release\net10.0-windows\FpsTune.exe
```

## Quick start (AI agent)

Project repository: <https://github.com/jidekaixin2dian/fps-tune>

If the agent is already inside the repo:

```text
Read SKILL.md in the current directory and follow its workflow to inspect and tune my Windows gaming settings.
```

If the agent does not have the project yet, let it clone the repo first:

```text
Run: git clone https://github.com/jidekaixin2dian/fps-tune.git
Then read SKILL.md in the cloned directory and follow its workflow to inspect and tune my Windows gaming settings.
```

> Do not just say "optimize my FPS": the agent does not know where the project is.
> Make sure it obtains this repo first and treats SKILL.md as the only operating procedure.

## A/B auto-tuning

```powershell
# Baseline first (game running, fixed map/graphics/route; 3 samples)
FpsTune.exe -Experiment -Baseline -Json

# Test candidate groups one by one
FpsTune.exe -Experiment -Test -Group group-1 -Json

# Report + CSV export
FpsTune.exe -Experiment -Report -Json
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
