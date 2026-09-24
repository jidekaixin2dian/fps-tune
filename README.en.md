# fps-tune

[简体中文](README.md) · [English](README.en.md) · [Download latest](https://github.com/jidekaixin2dian/fps-tune/releases/latest)

A **system-layer** frame-rate tuning bench for Windows gamers:
detect → explain → confirm → apply → restore, every step reversible and every claim measurable.
C# WPF + .NET 10; `FpsTune.exe` doubles as a headless CLI (command line / AI-agent entry point).

> The UI is currently Chinese-only. The CLI, catalog and this README are readable without it —
> and an English locale is a standing "good first issue".

![Overview: live load plus the state of all 33 items](assets/screenshots/01-overview.png)

![release](https://img.shields.io/github/v/release/jidekaixin2dian/fps-tune)
![license](https://img.shields.io/github/license/jidekaixin2dian/fps-tune)
![platform](https://img.shields.io/badge/Windows-10%2F11%20x64-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD5)

Version line 0.1 Beta: usable today, still converging; breaking changes are called out in release notes.
1.x (including 1.6.X) is **unmaintained** (frozen at `legacy/1.x`); use 0.1 Beta.

## It tunes Windows, never the game

| What it does | What it never does |
|---|---|
| Edits registry / power plans / service start types / boot config | Touches any file inside a game folder |
| Backs up the original value first (including "did not exist") | Injects into processes, reads game memory |
| Locates the game EXE for path-level adaptation | Interacts with anti-cheat, spoofs hardware |
| Samples on your machine, conclusions from measurement | Toggles virtualization-based security, fakes GPU IDs |
| Restores per item or all at once | Sends telemetry, downloads anything unasked |

Not game-specific — works for *Delta Force*, *Counter-Strike 2*, *VALORANT*, *APEX*, *PUBG*,
*Call of Duty* and most other PC titles. Games are found via uninstall registry entries and known
install directories; running processes' paths are never read. Use `-Game` for an unrecognized title.

## Download

Pick one from [Releases](https://github.com/jidekaixin2dian/fps-tune/releases/latest) (Windows 10/11 x64):

| Artifact | Notes |
|---|---|
| `FpsTune-Setup-<version>.exe` | Inno Setup installer, bundles the .NET runtime — **recommended** |
| `FpsTune-Portable-<version>.zip` | Unzip and run; requires the [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `SHA256SUMS-v<version>.txt` | Verify with `Get-FileHash -Algorithm SHA256 <file>` |

There is **no code-signing certificate**, so SmartScreen will warn about an unknown publisher on
first launch — normal for a personal open-source project. Choose "More info → Run anyway".
Update checks only hit `api.github.com` when you press the button in Settings, and any download
asks for consent and verifies SHA-256 first.

## Screenshots

| Detection: read-only health check + live meters | Optimization: presets, categories, per-item side effects |
|---|---|
| ![Detection](assets/screenshots/02-detect.png) | ![Optimization](assets/screenshots/03-optimize.png) |

| Performance sessions: local sampling + heuristic insight | A/B experiment: baseline, three candidate groups, rule-based verdict |
|---|---|
| ![Sessions](assets/screenshots/04-session.png) | ![A/B experiment](assets/screenshots/05-ab-experiment.png) |

Also included: backups & logs (audit of every write),
settings (theme / tray / global hotkey Ctrl+Alt+F / per-game auto-apply profiles).
Dark, light and system themes; borderless custom title bar; first-run guided setup.

## Quick start

### GUI

Run `FpsTune.exe`. Start with the Detection tab (read-only, safe), then pick a preset in the
Optimization tab, review each item, tick the consent box and apply. "Restore all" undoes everything.

### CLI

Passing arguments puts `FpsTune.exe` into headless mode (no window, stdout, exit code 0/1):

```powershell
.\FpsTune.exe -Detect -Json                       # read-only detection
.\FpsTune.exe -Detect -Game <path-to-game.exe> -Json
.\FpsTune.exe -Apply -Preset balanced -Json       # ask the user first
.\FpsTune.exe -Apply -Items id1,id2 -Json         # specific items only
.\FpsTune.exe -Restore -Json                      # restore everything
.\FpsTune.exe -ListRestore -Json                  # list available backups
.\FpsTune.exe -Version                            # version + build commit
```

Items that need admin rights fail loudly in a non-elevated terminal (the exe runs as asInvoker and
never auto-prompts UAC). Of the 33 items, 22 require admin and 13 need a reboot to fully apply;
the `balanced` preset contains 27.

### AI agent

```text
Run: git clone https://github.com/jidekaixin2dian/fps-tune.git
Then read SKILL.md in the cloned directory and strictly follow its workflow to tune my frame rates.
```

`SKILL.md` is the single operating procedure, with hard red lines: explain before applying,
never promise FPS, always stay reversible.

## Measurements, not promises

Performance sessions sample CPU / memory / GPU / VRAM locally — data stays on your machine,
exports to JSON/CSV, and two sessions can be compared side by side. The A/B wizard closes the loop:

```powershell
.\FpsTune.exe -Experiment -Baseline -Json
.\FpsTune.exe -Experiment -Test -Group group-1 -Json
.\FpsTune.exe -Experiment -Report -Json
```

Verdicts are rule-based (avg FPS / 1% low / P99 frame time / stutter count vs baseline):
keep on measurable gain, **auto-revert otherwise**. No conclusion is drawn when samples are
insufficient or the baseline is unstable (CV > 0.05). Real sampling needs the official PresentMon
CLI — install it yourself (`winget install Intel.PresentMon.Console`); the tool will never download
or run an installer for you. `-Simulate` walks the whole flow safely first.

That is why this README contains no "+30 FPS guaranteed" claim: results depend on your hardware,
driver and in-game settings, and controversial items ship unchecked by default.

## Why this project exists

Closed-source tools in this space ship proprietary EULAs that forbid modification and
redistribution, and don't disclose what they change. This project was written from scratch against
the *public feature list* only, is MIT licensed, and puts all 33 items and presets in
`catalog/catalog.json` — you can read exactly which keys get touched.

## Architecture

```
catalog/catalog.json          single source of truth: 33 items + presets
FpsTune.Wpf/
  Core/                       OptimizationCatalog / NativeOptimizationEngine /
                              BackupService / DetectionService / ExperimentRunner
  Services/                   theme / settings / CLI host / update check
  Views/                      WPF views (console and classic layouts)
FpsTune.Wpf.Tests/            unit tests, incl. catalog consistency guards
TESTING.md                    external testing guide: full A/B, or a minimal data template
installer/                    Inno Setup packaging
docs/                         current docs (HANDOFF / ROADMAP / README index)
  dev/                        plans and dev discipline; archive/  1.x history (not current)
```

GUI and CLI share one C# engine and one dataset, validated at load time.
To add or change an item: edit `catalog/catalog.json`, then add a case to
`NativeOptimizationEngine` (apply/revert) and `DetectionService` (state read). The catalog
consistency test fails the build if implementation and data drift.

Version single source is `VersionPrefix` / `VersionSuffix` in `Directory.Build.props`; the release
script reads the full SHA from the final clean commit, embeds it in `InformationalVersion` and
verifies it against the produced binary's file version info.

## Safety & privacy

- Every reversible change is written to `%LocalAppData%\FpsTune\backup` first; restore is per item
  and restores exact original values (deleting values that did not exist before). Empty or corrupt
  backups are archived as `.stale` instead of failing the whole restore.
- All changes leave a JSON audit trail, visible in the backups/logs tab.
- No telemetry, no analytics. The only network access is the update check you trigger yourself.
- Admin-required items fail with a reason instead of being silently skipped.

## Development

```powershell
dotnet build FpsTune.Wpf/FpsTune.Wpf.csproj -c Release    # needs the .NET 10 SDK
.\FpsTune.Wpf\bin\Release\net10.0-windows\FpsTune.exe

dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release
.\FpsTune.Wpf\bin\Release\net10.0-windows\FpsTune.exe -Detect -Json   # side-effect-free smoke test
```

Where to read next:
- **`docs/README.md`** — documentation index: current docs vs the 1.x archive, with a "read this first" table per role
- `AGENTS.md` — entry point for AI agents working on this repo (hard rules, dangerous-operation list, workspace map)
- `CONTRIBUTING.md` — shortest path for human contributors
- `RELEASE.md` — release procedure; `docs/ROADMAP.md` — product direction and the five red lines
Issues are open — bug reports, new optimization items and "this explanation is unclear" are all welcome.

## License

MIT. Written from scratch; no derivative relationship to any existing tool's code or docs.

If it saved you from hunting through the registry by hand, a star is the most direct way to help.
