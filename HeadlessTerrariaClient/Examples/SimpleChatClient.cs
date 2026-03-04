using System;
using HeadlessTerrariaClient;
using HeadlessTerrariaClient.Terraria;
using HeadlessTerrariaClient.Terraria.Chat;
using HeadlessTerrariaClient.Client;
using HeadlessTerrariaClient.Utility;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using HeadlessTerrariaClient.Terraria.ID;

namespace HeadlessTerrariaClient.Examples
{
    /// <summary>
    /// Very simple example client that connects to a server and prints chat messages to the console
    /// </summary>
    public class SimpleChatClient
    {
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly string _versionToken;
        private readonly string _serverPassword;
        private readonly string _playerName;
        private volatile bool _groundLockEnabled = false;
        private Vector2 _groundLockPosition;
        private DateTime _lastGroundLockSync = DateTime.MinValue;
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);

        public SimpleChatClient(
            string versionToken = "318",
            string serverPassword = "gaxryktparry",
            string playerName = "Gax",
            string serverIp = "play.notgax.app",
            int serverPort = 25565)
        {
            _versionToken = versionToken;
            _serverPassword = serverPassword;
            _playerName = playerName;
            _serverIp = serverIp;
            _serverPort = serverPort;
        }
        
        public async Task Start()
        {
            // Create a new client
            HeadlessClient HeadlessClient = new HeadlessClient();

            // Random client UUID
            HeadlessClient.clientUUID = Guid.NewGuid().ToString();

            // Assaign world reference
            HeadlessClient.World = new ClientWorld();

            // Name the player
            HeadlessClient.LocalPlayer.name = _playerName;
            // This example client drives movement itself. Avoid global periodic syncs that
            // can resend stale position and cause server snapback/bobbing.
            HeadlessClient.Settings.AutoSyncPlayerControl = false;
            HeadlessClient.Settings.AutoSyncPlayerZone = false;
            HeadlessClient.Settings.AutoSyncPlayerLife = false;

            // Password-protected server support.
            HeadlessClient.Settings.ServerPassword = _serverPassword;
            if (string.Equals(Environment.GetEnvironmentVariable("HTC_TRACE_PACKETS"), "1", StringComparison.OrdinalIgnoreCase))
            {
                HeadlessClient.Settings.TraceJoinPackets = true;
            }

            if (int.TryParse(_versionToken, out int parsedVersion))
            {
                HeadlessClient.Settings.VersionNumber = parsedVersion;
                HeadlessClient.Settings.HelloString = "";
                Console.WriteLine($"Connecting to {_serverIp}:{_serverPort} using Terraria network version {parsedVersion} (Hello=\"Terraria{parsedVersion}\")");
            }
            else
            {
                string helloString = _versionToken.StartsWith("Terraria", StringComparison.OrdinalIgnoreCase)
                    ? _versionToken
                    : $"Terraria{_versionToken}";
                HeadlessClient.Settings.HelloString = helloString;
                Console.WriteLine($"Connecting to {_serverIp}:{_serverPort} using custom Hello=\"{helloString}\"");
            }

            // Softcore player, Default appearence, and Default inventory
            HeadlessClient.LocalPlayer.LoadDefaultPlayer();

            HeadlessClient.WorldDataRecieved += client =>
            {
                Console.WriteLine($"World data received: {client.World.CurrentWorld?.worldName}");
            };
            HeadlessClient.FinishedConnectingToServer += client =>
            {
                Console.WriteLine("FinishedConnectingToServer received");
            };
            HeadlessClient.ClientConnectionCompleted += client =>
            {
                Console.WriteLine($"Client connection completed. In world: {client.World.CurrentWorld?.worldName}");
                EnsureLocalPositionInitialized(client);
                TrySnapToGround(client);
                _groundLockPosition = client.LocalPlayer.position;
                _groundLockEnabled = HasSanePlayerPosition(client, client.LocalPlayer.position);
                _lastGroundLockSync = DateTime.MinValue;
                if (_groundLockEnabled)
                {
                    client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                }
                else
                {
                    Console.WriteLine(
                        $"afkbot: join-fix -> waiting for sane local position ({client.LocalPlayer.position.X:0},{client.LocalPlayer.position.Y:0})");
                }

                string selfTestCommands = Environment.GetEnvironmentVariable("HTC_SELFTEST_COMMANDS");
                if (!string.IsNullOrWhiteSpace(selfTestCommands))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1500);
                        string[] commands = selfTestCommands.Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (string cmd in commands)
                        {
                            Console.WriteLine($"[selftest] {cmd}");
                            await HandleAfkBotCommand(client, new ChatMessage(client.LocalPlayer.whoAmI, cmd));
                            await Task.Delay(1200);
                        }
                    });
                }
            };
            HeadlessClient.OnUpdate += client =>
            {
                if (!_groundLockEnabled || !client.IsInWorld)
                {
                    return;
                }

                if ((DateTime.UtcNow - _lastGroundLockSync).TotalMilliseconds < 250)
                {
                    return;
                }

                if (!HasSanePlayerPosition(client, client.LocalPlayer.position))
                {
                    // Never send controls from an unsynced local position, or server can snap us
                    // toward map corner/top-left.
                    EnsureLocalPositionInitialized(client);
                    if (!HasSanePlayerPosition(client, client.LocalPlayer.position))
                    {
                        return;
                    }
                }

                // Keep controls idle while in lock mode.
                client.LocalPlayer.controlUp = false;
                client.LocalPlayer.controlDown = false;
                client.LocalPlayer.controlLeft = false;
                client.LocalPlayer.controlRight = false;
                client.LocalPlayer.controlJump = false;
                client.LocalPlayer.controlUseItem = false;
                client.LocalPlayer.velocity = Vector2.Zero;
                client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                _lastGroundLockSync = DateTime.UtcNow;
            };

            // Run code when a chat message is recived
            HeadlessClient.ChatMessageRecieved += (HeadlessClient client, ChatMessage message) =>
            {
                // Messages of id 255 are not from another player
                if (message.AuthorIndex != 255)
                {
                    Player sender = client.World.Players[message.AuthorIndex];
                    Console.Write($"<{sender.name}>");
                    message.WriteToConsole();
                    Console.Write("\n");
                }
                else
                {
                    message.WriteToConsole();
                    Console.Write("\n");
                }

                _ = HandleAfkBotCommand(client, message);
            };

            // Connect to a server
            await HeadlessClient.Connect(_serverIp, (short)_serverPort);

            await Task.Delay(Timeout.Infinite);
        }

        private async Task HandleAfkBotCommand(HeadlessClient client, ChatMessage message)
        {
            string text = message.Text?.Trim() ?? string.Empty;
            if (!text.StartsWith("afkbot", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Console.WriteLine("afkbot command missing action (move|attack|dropall).");
                return;
            }

            string action = parts[1].ToLowerInvariant();
            await _commandLock.WaitAsync();
            try
            {
                switch (action)
                {
                    case "move":
                    {
                        // Syntax:
                        // afkbot move
                        // afkbot move 10
                        // afkbot move left 10
                        // afkbot move right 10
                        // afkbot move up 10
                        // afkbot move down 10
                        int dirX = Random.Shared.Next(0, 2) == 0 ? -1 : 1;
                        int dirY = 0;
                        int ticks = 12;

                        if (parts.Length >= 3)
                        {
                            if (string.Equals(parts[2], "left", StringComparison.OrdinalIgnoreCase))
                            {
                                dirX = -1;
                                dirY = 0;
                            }
                            else if (string.Equals(parts[2], "right", StringComparison.OrdinalIgnoreCase))
                            {
                                dirX = 1;
                                dirY = 0;
                            }
                            else if (string.Equals(parts[2], "up", StringComparison.OrdinalIgnoreCase))
                            {
                                dirX = 0;
                                dirY = -1;
                            }
                            else if (string.Equals(parts[2], "down", StringComparison.OrdinalIgnoreCase))
                            {
                                dirX = 0;
                                dirY = 1;
                            }
                            else if (int.TryParse(parts[2], out int parsedTicks))
                            {
                                ticks = parsedTicks;
                            }
                        }

                        if (parts.Length >= 4 && int.TryParse(parts[3], out int parsedTicks2))
                        {
                            ticks = parsedTicks2;
                        }

                        ticks = Math.Clamp(ticks, 1, 200);

                        _groundLockEnabled = false;
                        client.LocalPlayer.controlUp = dirY < 0;
                        client.LocalPlayer.controlDown = dirY > 0;
                        client.LocalPlayer.controlJump = dirY < 0;
                        client.LocalPlayer.controlUseItem = false;
                        client.LocalPlayer.controlLeft = dirX < 0;
                        client.LocalPlayer.controlRight = dirX > 0;
                        if (dirX != 0)
                        {
                            client.LocalPlayer.direction = dirX;
                        }

                        int moved = 0;
                        float stepX = dirX * 2f;
                        float stepY = dirY * 3f;
                        for (int i = 0; i < ticks; i++)
                        {
                            float nextX = client.LocalPlayer.position.X + stepX;
                            float nextY = client.LocalPlayer.position.Y;

                            // Use tile cache for floor-following when available.
                            if (dirX != 0 && TryFindStandingY(client, nextX, client.LocalPlayer.position.Y, out float groundedY))
                            {
                                nextY = groundedY;
                            }
                            else if (dirY != 0)
                            {
                                nextY += stepY;
                            }

                            if (!CanOccupy(client, nextX, nextY))
                            {
                                break;
                            }

                            client.LocalPlayer.position = new Vector2(nextX, nextY);
                            client.LocalPlayer.velocity = new Vector2(stepX, stepY);
                            client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                            moved++;
                            await Task.Delay(90);
                        }

                        client.LocalPlayer.controlLeft = false;
                        client.LocalPlayer.controlRight = false;
                        client.LocalPlayer.controlUp = false;
                        client.LocalPlayer.controlDown = false;
                        client.LocalPlayer.controlJump = false;
                        client.LocalPlayer.velocity = Vector2.Zero;
                        client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                        client.SendData(MessageID.SyncPlayerZone, client.LocalPlayer.whoAmI);
                        _groundLockPosition = client.LocalPlayer.position;
                        _groundLockEnabled = true;
                        string moveDir = dirY < 0 ? "up" : dirY > 0 ? "down" : dirX < 0 ? "left" : "right";
                        Console.WriteLine($"afkbot: move -> {moveDir} ({moved}/{ticks} ticks)");
                        break;
                    }
                    case "attack":
                    {
                        _groundLockEnabled = false;
                        // Simulate a quick use-item press with currently selected item.
                        client.LocalPlayer.controlUseItem = true;
                        client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                        await Task.Delay(120);
                        client.LocalPlayer.controlUseItem = false;
                        client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                        _groundLockPosition = client.LocalPlayer.position;
                        _groundLockEnabled = true;
                        Console.WriteLine("afkbot: attack");
                        break;
                    }
                    case "dropall":
                    case "throwall":
                    {
                        _groundLockEnabled = false;
                        int dropped = 0;
                        Vector2 basePos = client.LocalPlayer.position + new Vector2(0f, -16f);
                        for (int slot = 0; slot < client.LocalPlayer.inventory.Length; slot++)
                        {
                            Item item = client.LocalPlayer.inventory[slot];
                            if (item == null || item.type <= 0 || item.stack <= 0)
                            {
                                continue;
                            }

                            Vector2 vel = new Vector2((float)(Random.Shared.NextDouble() * 6.0 - 3.0), -2f);
                            client.SpawnItem(item.type, item.stack, basePos, vel, bypassTShock: false);

                            item.type = 0;
                            item.stack = 0;
                            item.prefix = 0;
                            item.active = false;
                            client.LocalPlayer.inventory[slot] = item;
                            client.SendData(MessageID.SyncEquipment, client.LocalPlayer.whoAmI, slot);
                            dropped++;
                        }
                        _groundLockPosition = client.LocalPlayer.position;
                        _groundLockEnabled = true;
                        Console.WriteLine($"afkbot: dropall -> dropped {dropped} item stacks.");
                        break;
                    }
                    case "help":
                    {
                        Console.WriteLine("afkbot: commands -> help, move, attack, dropall, tp");
                        break;
                    }
                    case "tp":
                    case "teleport":
                    {
                        if (parts.Length < 3)
                        {
                            Console.WriteLine("afkbot: tp requires a player name. Example: afkbot tp KaluBihari");
                            break;
                        }

                        string requestedName = string.Join(' ', parts, 2, parts.Length - 2).Trim();
                        Player target = FindPlayerByName(client, requestedName);
                        if (target == null)
                        {
                            Console.WriteLine($"afkbot: tp target not found: {requestedName}");
                            break;
                        }
                        if (!HasSanePlayerPosition(client, target.position))
                        {
                            Console.WriteLine(
                                $"afkbot: tp -> target position not synced yet (idx={target.whoAmI} active={target.active} pos={target.position.X:0},{target.position.Y:0}); try again.");
                            break;
                        }

                        _groundLockEnabled = false;
                        client.LocalPlayer.controlUp = false;
                        client.LocalPlayer.controlDown = false;
                        client.LocalPlayer.controlLeft = false;
                        client.LocalPlayer.controlRight = false;
                        client.LocalPlayer.controlJump = false;
                        client.LocalPlayer.controlUseItem = false;

                        // Snap close to target player's current location, but always resolve
                        // to a valid standing tile so stale Y data doesn't teleport us into sky.
                        Vector2 requestedPos = target.position + new Vector2(12f, 0f);
                        bool usedSafeResolve = TryResolveSafeTeleportPosition(client, requestedPos, out Vector2 targetPos);
                        if (!usedSafeResolve)
                        {
                            // Fallback: if tiles are not loaded around target, still trust synced
                            // player position and teleport using clamped coordinates.
                            targetPos = ClampToWorldPosition(client, requestedPos);
                            Console.WriteLine(
                                $"afkbot: tp -> target tiles not loaded/safe yet; using synced pos (requested={requestedPos.X:0},{requestedPos.Y:0} clamped={targetPos.X:0},{targetPos.Y:0}).");
                        }
                        client.LocalPlayer.position = targetPos;
                        client.LocalPlayer.velocity = Vector2.Zero;

                        // Send a few updates to make server/state convergence reliable.
                        for (int i = 0; i < 3; i++)
                        {
                            client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                            await Task.Delay(80);
                        }

                        client.SendData(MessageID.SyncPlayerZone, client.LocalPlayer.whoAmI);
                        _groundLockPosition = client.LocalPlayer.position;
                        _groundLockEnabled = true;
                        Console.WriteLine(
                            $"afkbot: tp -> {target.name} (requested={requestedPos.X:0},{requestedPos.Y:0} {(usedSafeResolve ? "resolved" : "fallback")}={targetPos.X:0},{targetPos.Y:0})");
                        break;
                    }
                    default:
                        Console.WriteLine($"afkbot: unknown action '{action}'. Supported: help, move, attack, dropall, tp.");
                        break;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        private static bool TrySnapToGround(HeadlessClient client)
        {
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                return false;
            }

            int tileX = (int)(client.LocalPlayer.position.X / 16f);
            tileX = Math.Clamp(tileX, 1, world.maxTilesX - 2);

            int startY = (int)(client.LocalPlayer.position.Y / 16f) - 12;
            startY = Math.Clamp(startY, 1, world.maxTilesY - 2);

            int endY = Math.Min(world.maxTilesY - 2, startY + 120);
            for (int y = startY; y <= endY; y++)
            {
                if (IsSolidTile(world, tileX, y) && !IsSolidTile(world, tileX, y - 1))
                {
                    client.LocalPlayer.position = new Vector2(client.LocalPlayer.position.X, y * 16f - 48f);
                    client.LocalPlayer.velocity = Vector2.Zero;
                    client.SendData(MessageID.PlayerControls, client.LocalPlayer.whoAmI);
                    return true;
                }
            }

            return false;
        }

        private static void EnsureLocalPositionInitialized(HeadlessClient client)
        {
            if (HasSanePlayerPosition(client, client.LocalPlayer.position))
            {
                return;
            }

            if (!TryGetSpawnPosition(client, out Vector2 spawnPos))
            {
                return;
            }

            client.LocalPlayer.position = spawnPos;
            client.LocalPlayer.velocity = Vector2.Zero;
            Console.WriteLine(
                $"afkbot: join-fix -> forced spawn position ({spawnPos.X:0},{spawnPos.Y:0}) from invalid local pos.");
        }

        private static bool TryGetSpawnPosition(HeadlessClient client, out Vector2 spawnPos)
        {
            spawnPos = client.LocalPlayer.position;
            World world = client.World?.CurrentWorld;
            if (world == null)
            {
                return false;
            }

            if (world.spawnTileX <= 0 || world.spawnTileY <= 0)
            {
                return false;
            }

            float minX = 16f;
            float maxX = world.maxTilesX * 16f - 32f;
            float minY = 16f;
            float maxY = world.maxTilesY * 16f - 64f;

            spawnPos = new Vector2(
                Math.Clamp(world.spawnTileX * 16f, minX, maxX),
                Math.Clamp(world.spawnTileY * 16f - 48f, minY, maxY));
            return true;
        }

        private static bool TryResolveSafeTeleportPosition(HeadlessClient client, Vector2 desired, out Vector2 resolved)
        {
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                resolved = desired;
                return true;
            }

            float minX = 16f;
            float maxX = world.maxTilesX * 16f - 32f;
            float minY = 16f;
            float maxY = world.maxTilesY * 16f - 64f;

            float x = Math.Clamp(desired.X, minX, maxX);
            float y = Math.Clamp(desired.Y, minY, maxY);

            if (TryFindStandingY(client, x, y, out float groundedY) && CanOccupy(client, x, groundedY))
            {
                resolved = new Vector2(x, groundedY);
                return true;
            }

            float localY = Math.Clamp(client.LocalPlayer.position.Y, minY, maxY);
            if (TryFindStandingY(client, x, localY, out groundedY) && CanOccupy(client, x, groundedY))
            {
                resolved = new Vector2(x, groundedY);
                return true;
            }

            if (TryFindStandingYWide(client, x, out groundedY) && CanOccupy(client, x, groundedY))
            {
                resolved = new Vector2(x, groundedY);
                return true;
            }

            resolved = new Vector2(x, y);
            return false;
        }

        private static Vector2 ClampToWorldPosition(HeadlessClient client, Vector2 desired)
        {
            World world = client.World?.CurrentWorld;
            if (world == null)
            {
                return desired;
            }

            float minX = 16f;
            float maxX = world.maxTilesX * 16f - 32f;
            float minY = 16f;
            float maxY = world.maxTilesY * 16f - 64f;
            return new Vector2(
                Math.Clamp(desired.X, minX, maxX),
                Math.Clamp(desired.Y, minY, maxY));
        }

        private static bool TryFindStandingYWide(HeadlessClient client, float xPixels, out float yPixels)
        {
            yPixels = client.LocalPlayer.position.Y;
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                return false;
            }

            int tileX = Math.Clamp((int)(xPixels / 16f), 1, world.maxTilesX - 2);
            for (int y = 2; y <= world.maxTilesY - 3; y++)
            {
                if (IsSolidTile(world, tileX, y) && !IsSolidTile(world, tileX, y - 1))
                {
                    yPixels = y * 16f - 48f;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindStandingY(HeadlessClient client, float xPixels, float aroundYPixels, out float yPixels)
        {
            yPixels = aroundYPixels;
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                return false;
            }

            int tileX = Math.Clamp((int)(xPixels / 16f), 1, world.maxTilesX - 2);
            int centerY = Math.Clamp((int)(aroundYPixels / 16f), 2, world.maxTilesY - 3);

            int minY = Math.Max(2, centerY - 20);
            int maxY = Math.Min(world.maxTilesY - 3, centerY + 40);

            for (int y = minY; y <= maxY; y++)
            {
                if (IsSolidTile(world, tileX, y) && !IsSolidTile(world, tileX, y - 1))
                {
                    yPixels = y * 16f - 48f;
                    return true;
                }
            }

            return false;
        }

        private static bool HasGroundBelow(HeadlessClient client, Vector2 pos)
        {
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                return true;
            }

            int footY = (int)((pos.Y + 50f) / 16f);
            int leftX = (int)((pos.X + 2f) / 16f);
            int rightX = (int)((pos.X + 18f) / 16f);

            return IsSolidTile(world, leftX, footY) || IsSolidTile(world, rightX, footY);
        }

        private static bool CanOccupy(HeadlessClient client, float xPixels, float yPixels)
        {
            World world = client.World?.CurrentWorld;
            if (world == null || world.Tiles == null)
            {
                return true;
            }

            int leftX = Math.Clamp((int)((xPixels + 2f) / 16f), 0, world.maxTilesX - 1);
            int rightX = Math.Clamp((int)((xPixels + 18f) / 16f), 0, world.maxTilesX - 1);
            int topY = Math.Clamp((int)((yPixels + 2f) / 16f), 0, world.maxTilesY - 1);
            int bottomY = Math.Clamp((int)((yPixels + 40f) / 16f), 0, world.maxTilesY - 1);

            for (int tx = leftX; tx <= rightX; tx++)
            {
                for (int ty = topY; ty <= bottomY; ty++)
                {
                    if (IsSolidTile(world, tx, ty))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Player FindPlayerByName(HeadlessClient client, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return null;
            }

            Player exact = null;
            Player partial = null;
            for (int i = 0; i < client.World.Players.Length; i++)
            {
                Player plr = client.World.Players[i];
                if (plr == null || !plr.active || string.IsNullOrWhiteSpace(plr.name))
                {
                    continue;
                }

                if (string.Equals(plr.name, requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    exact = plr;
                    break;
                }

                if (partial == null &&
                    plr.name.IndexOf(requestedName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = plr;
                }
            }

            return exact ?? partial;
        }

        private static bool HasSanePlayerPosition(HeadlessClient client, Vector2 pos)
        {
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
            {
                return false;
            }

            World world = client.World?.CurrentWorld;
            if (world == null)
            {
                return pos.X >= 0f && pos.Y >= 0f;
            }

            float maxX = world.maxTilesX * 16f;
            float maxY = world.maxTilesY * 16f;
            if (pos.X < -32f || pos.Y < 0f || pos.X > maxX + 32f || pos.Y > maxY + 32f)
            {
                return false;
            }

            // On large worlds, near-left-edge X is usually unsynced junk for remote players.
            if (world.spawnTileX > 200 && pos.X < 128f)
            {
                return false;
            }

            // Near-origin coordinates are commonly a transient unsynced state for remote players.
            if (pos.X < 32f && pos.Y < 32f && (world.spawnTileX > 4 || world.spawnTileY > 4))
            {
                return false;
            }

            return true;
        }

        private static bool IsSolidTile(World world, int x, int y)
        {
            if (x < 0 || y < 0 || x >= world.maxTilesX || y >= world.maxTilesY)
            {
                return false;
            }

            Tile t = world.Tiles[x, y];
            if (t == null || !t.GetTileActive())
            {
                return false;
            }

            ushort type = t.tileType;
            if (type >= TileID.Count)
            {
                return false;
            }

            return TileID.IsTileSolid[type] || TileID.IsTileSolidTop[type];
        }
    }
}
