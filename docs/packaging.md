# Packaging Deluno

Deluno is being built as a single-host app so it can package cleanly for both Windows and Docker.

## Docker

The repo includes:

- `Dockerfile`
- `compose.yaml`
- `.dockerignore`

Build the image:

```powershell
docker build -t deluno .
```

Run the container:

```powershell
docker run --rm -p 5099:8080 -e Storage__DataRoot=/data -v ${PWD}/artifacts/docker/data:/data deluno
```

The Dockerfile follows the current ASP.NET Core container guidance by using `mcr.microsoft.com/dotnet/sdk:10.0` for build and `mcr.microsoft.com/dotnet/aspnet:10.0` for runtime. For .NET 10, default tags are Ubuntu-based.

## Windows

The repo includes `scripts/publish-windows.ps1` to create a self-contained single-file publish.

Run:

```powershell
.\scripts\publish-windows.ps1
```

That publishes Deluno to `artifacts/publish/win-x64`.

The script enables:

- self-contained publish
- single-file output
- ReadyToRun compilation
- native library self-extract support

## Installer Direction

The publish output is intended to become the payload for a later installer step. The likely path is:

- `Inno Setup` for the first Windows installer
- optional Windows Service registration
- migration to `WiX` later if we need a more enterprise-oriented installer
