# ADF → .NET Reference Implementation

A reference implementation showing how a credit union running Corelation KeyStone can migrate Azure Data Factory pipelines to .NET C# (Durable Functions), with acceptance tests as the completion gate. It runs entirely on a developer machine using synthetic data. **It is a reference implementation, not production-ready code.**

See [docs/PRD.md](docs/PRD.md) for scope and [docs/progress.md](docs/progress.md) for build status. The full README and migration playbook are written in Phase 9.

## Prerequisites (Windows 10/11)

```powershell
winget install Microsoft.DotNet.SDK.10 Docker.DockerDesktop Microsoft.Azure.FunctionsCoreTools Microsoft.PowerShell
```

Set WSL 2 memory to at least 8 GB in `%UserProfile%\.wslconfig` (Db2 alone needs about 4 GB), then `wsl --shutdown`:

```ini
[wsl2]
memory=8GB
```

Keep the repo on the Windows file system (e.g. `C:\src`), not inside a WSL distro.

## Quick start

```powershell
./build.ps1 up     # containers; first start of Db2 takes 1-3 minutes
./build.ps1 build
./build.ps1 test
```

Aspire dashboard (traces/logs): <http://localhost:18888>.
