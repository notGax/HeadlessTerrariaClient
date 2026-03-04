# HeadlessTerrariaClient

Terraria version: 1.4.5.5  
Headless Terraria client/bot for connecting to Terraria servers without the game UI.

Current focus: movement + teleportation (useful for AFK/farm workflows).

## Runtime Input

Non-scan mode CLI order:

```powershell
HeadlessTerrariaClient <username> <ip> <port> <password> [version]
```

You can pass only part of it; missing values are loaded from `client.config.json`.

Examples:

```powershell
# Full CLI
dotnet .\out_fixed39\HeadlessTerrariaClient.dll YourBotName example.server.host 25565 your-server-password

# Partial CLI (rest from client.config.json)
dotnet .\out_fixed39\HeadlessTerrariaClient.dll YourBotName

# Config-only
dotnet .\out_fixed39\HeadlessTerrariaClient.dll
```

`version` defaults to `318` if not provided in CLI/config.

## Config File

Create `client.config.json` in either:

1. Current working directory
2. Executable directory

Use the included example file: `client.config.json.example`.

```json
{
  "Username": "YourBotName",
  "ServerIp": "example.server.host",
  "ServerPort": 25565,
  "Password": "your-server-password",
  "Version": "318"
}
```

## Scan Mode

Unaffected by runtime config changes:

```powershell
HeadlessTerrariaClient scan <password> [startVersion] [endVersion]
```

## Build

```powershell
dotnet build .\HeadlessTerrariaClient\HeadlessTerrariaClient.csproj -c Release
```

## Publish

### Windows (framework-dependent output folder)

```powershell
dotnet publish .\HeadlessTerrariaClient\HeadlessTerrariaClient.csproj -c Release -o out_fixed39
```

### Linux ARM64 (self-contained single-file)

```powershell
dotnet publish .\HeadlessTerrariaClient\HeadlessTerrariaClient.csproj -c Release -r linux-arm64 --self-contained true /p:PublishSingleFile=true -o out_linux_arm64_sc
```

### Linux ARM32 (self-contained single-file)

```powershell
dotnet publish .\HeadlessTerrariaClient\HeadlessTerrariaClient.csproj -c Release -r linux-arm --self-contained true /p:PublishSingleFile=true -o out_linux_arm_sc
```

### Full architecture release matrix

- Windows: `win-x86`, `win-x64`, `win-arm`, `win-arm64`
- Linux: `linux-x86`, `linux-x64`, `linux-arm`, `linux-arm64`

Notes:
- `win-arm` and `linux-x86` are framework-dependent DLL-based builds in the release assets.
- Other targets are self-contained single-file executables.

## Linux Run (self-contained targets)

```bash
chmod +x ./HeadlessTerrariaClient
./HeadlessTerrariaClient <username> <ip> <port> <password>
```

## Security / Repo Hygiene

1. Do not commit real credentials.
2. Keep only `client.config.json.example` in git.
3. Keep local `client.config.json` ignored.
