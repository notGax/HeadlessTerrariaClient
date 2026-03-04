# HeadlessTerrariaClient
Terraria version 1.4.5.5
Headless Terraria client/bot for connecting to Terraria servers without the game UI.
CURRENTLY ONLY SUPPORTS MOVEMENT AND TELEPORTATION. GOOD FOR AFK FARMS.

## Runtime Input (new)

Non-scan mode uses this CLI order:

```powershell
HeadlessTerrariaClient <username> <ip> <port> <password> [version]
```

You can pass only part of it; missing values are loaded from `client.config.json`.

Examples:

```powershell
# Full CLI
dotnet .\out_fixed39\HeadlessTerrariaClient.dll notgax play.notgax.app 25565 mypass

# Partial CLI (rest from client.config.json)
dotnet .\out_fixed39\HeadlessTerrariaClient.dll notgax

# Config-only
dotnet .\out_fixed39\HeadlessTerrariaClient.dll
```

`version` defaults to `318` if not supplied in CLI/config.

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

