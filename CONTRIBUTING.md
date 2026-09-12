# Contributing

Users need nothing but Civil 3D. Maintainers need the tools below.

## Build the engine

- .NET 8 SDK builds `net472` (Civil 3D 2022-2024, reference assemblies come from NuGet) and `net8.0-windows` (2025-2026).
- .NET 10 SDK builds `net10.0-windows` (2027).
- Autodesk assemblies are the `Digi-Ants.*` NuGet packages (one version per generation, `ExcludeAssets="runtime"`); nothing from Autodesk is copied into the output or the repository.

```powershell
scripts\build.ps1                          # every target the installed SDKs can build, then installs the bundle
scripts\build.ps1 -Targets net8.0-windows  # one target
scripts\build.ps1 -Release                 # + dist\Civil3DFactory.bundle.zip and the exe tools, tags v<Version> when the tree is clean
```

The version is `<Version>` in `engine\Civil3DFactory.csproj`. The bundle goes to
`%APPDATA%\Autodesk\ApplicationPlugins\Civil3DFactory.bundle\Contents\<generation>\`; `current-version.json` next to it records commit and source path.

Members missing from the 2022 API are wrapped in `engine\Compat.cs` (reflection on net472, direct calls on net8 / net10).
Keep WPF and `Autodesk.Windows` out of the engine: accoreconsole crashes while scanning such an assembly at NETLOAD.

## Build the exe tools

Python 3.10+ with `pip install pyinstaller PySide6-Essentials pywin32`, then

```powershell
tools\dwg-attribute-editor\build_exe.ps1
tools\dwg-titleblock-plotter\build_exe.ps1
dotnet build tools\pkt-forge\PktForge.csproj -c Release -o tools\pkt-forge\bin\out
```

Each `build_exe.ps1` compiles the tool's accoreconsole plugin (`Cad*Plugin`), packs it into a one-file exe and copies the exe to the tool folder.
The exe files are git-ignored; releases carry them.

## Adding or changing an op

1. Implement it as `static JsonNode RunNode<Name>(JsonObject a, Document doc)` in `nodes\<op_id>\<Name>Node.cs` (partial class `Ops`), or as a small op in `engine\Ops*.cs`.
2. Register it in `Ops.Registry` (engine\Ops.cs, or `RegisterStyleOps` / the static constructor in `Ops.Cli.cs`) with an English `Description` and the compact `Parameters` notation: `name(required) other?(default 5)`.
3. Give it a contract in `nodes\node.json` (`inputs`, `parameters`, `outputs`, `effect`) and a `test.json` in its folder (fixture, assertions, status). `-Help <op>` prints the contract, so it must be true.
4. Run `scripts\contract-audit.ps1` (declared parameters vs. keys the implementation reads) and fix any `[drift]`.
5. Add or update an example under `examples\` and run it on the demo drawing. `-Help` finds examples by op name.

Rules that cause rework when broken: ops only change memory - persistence is `save_dwg`; a result with `ok: true` and empty output is a failure;
never resolve names by "take the first match" - throw with the list of candidates; open sample line groups for write before reading their sources.

## Blacklist gate

`scripts\gate.ps1` runs as the pre-commit hook (`git config core.hooksPath scripts/githooks`, done by `install.ps1`).
It rejects the legacy engine prefix, developer-machine paths, private terms from `scripts\blacklist.local.txt` (git-ignored, your own list), CJK text
(`scripts\gate.config.json` sets `error`), and unexpected top-level entries. `scripts\gate.ps1 -All` scans the whole tree before a release.

## Lookup order for agents (what SKILL.md tells them)

examples -> skills -> `-Help <op>` (op layer, generated from the engine at run time) -> tools (exe layer).
If none of them covers the task, propose a new op; do not write a one-off script that reimplements an existing op.

## Commits

One unit of work per commit, message in English, the gate must pass. Release: bump `<Version>`, `scripts\build.ps1 -Release`, push the tag, upload
`dist\Civil3DFactory.bundle.zip`, `DWGAttributeEditor.exe` and `DWGTitleblockPlotter.exe` to the GitHub release - `install.ps1` downloads exactly those names.
