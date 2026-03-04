using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using ArkNetwork;
using System.Threading.Tasks;
using HeadlessTerrariaClient.Terraria;
using HeadlessTerrariaClient.Terraria.ID;
using HeadlessTerrariaClient.Terraria.Chat;
using HeadlessTerrariaClient.Utility;
using System.Net.Sockets;
using System.Numerics;

// Much of packet documentation is from Terraria's NetMessage.cs and from https://tshock.readme.io/docs/multiplayer-packet-structure
namespace HeadlessTerrariaClient.Client
{
    /// <summary>
    /// A Terraria client without any overhead
    /// </summary>
    public class HeadlessClient
    {
        /// <summary>
        /// The TCP client to connect to the server with
        /// </summary>
        public ArkTCPClient TCPClient { get; private set; }

        private bool DisconnectFromServer { get; set; }

        /// <summary>
        /// Current state in the connection protocol
        /// </summary>
        public ConnectionState ConnectionState { get; private set; }

        /// <summary>
        /// Buffer used for writing to the NetworkStream
        /// </summary>
        public byte[] WriteBuffer = new byte[131070];

        /// <summary>
        /// Buffer used for reading from the NetworkStream
        /// </summary>
        public byte[] ReadBuffer = new byte[131070];

        public MemoryStream MemoryStreamWrite { get; private set; }
        public BinaryWriter MessageWriter { get; private set; }

        public MemoryStream MemoryStreamRead { get; private set; }
        public BinaryReader MessageReader { get; private set; }

        /// <summary>
        /// Event called after the WorldData packet is received
        /// </summary>
        public Action<HeadlessClient> WorldDataRecieved { get; set; }

        /// <summary>
        /// Event called after the FinishedConnectingToServer packet is received
        /// </summary>
        public Action<HeadlessClient> FinishedConnectingToServer { get; set; }

        /// <summary>
        /// Event called after the CompleteConnectionAndSpawn packet is received
        /// </summary>
        public Action<HeadlessClient> ClientConnectionCompleted { get; set; }

        /// <summary>
        /// Event called every time the game loop runs
        /// </summary>
        public Action<HeadlessClient> OnUpdate { get; set; }

        /// <summary>
        /// Event called when a chat message is received
        /// </summary>
        public Action<HeadlessClient, ChatMessage> ChatMessageRecieved { get; set; }

        /// <summary>
        /// Event called when another player manipulates a tile.
        /// Returns a boolean of whether or not to process this tile event normally
        /// </summary>
        public Func<HeadlessClient, TileManipulation, bool> TileManipulationMessageRecieved { get; set; }

        /// <summary>
        /// Event called when any packet is received
        /// </summary>
        public Action<HeadlessClient, RawIncomingPacket> NetMessageReceived { get; set; }

        /// <summary>
        /// Event called when any packet is sent
        /// </summary>
        public Action<HeadlessClient, RawOutgoingPacket> NetMessageSent { get; set; }

        /// <summary>
        /// A reference to a ClientWorld
        /// </summary>
        public ClientWorld World { get; set; }

        /// <summary>
        /// The current index of this client's player
        /// </summary>
        private int myPlayer = 0;

        /// <summary>
        /// The current Player object for this client
        /// </summary>
        public Player LocalPlayer
        {
            get
            {
                return World.Players[myPlayer];
            }
        }

        /// <summary>
        /// The GUID for this client
        /// </summary>
        public string clientUUID { get; set; }

        /// <summary>
        /// this game doodoo
        /// </summary>
        public bool ServerSideCharacter { get; private set; }

        /// <summary>
        /// why is this here
        /// </summary>
        public ulong LobbyId { get; private set; }

        /// <summary>
        /// The version of the game to use
        /// </summary>
        public int VersionNumber
        {
            get
            {
                return Settings.VersionNumber;
            }
        }

        // Terraria network version 318 expects legacy packet layouts for several messages.
        private bool UseLegacy318Wire => VersionNumber <= 318;

        /// <summary>
        /// Returns whether or not the client is in a world
        /// </summary>
        public bool IsInWorld { get; private set; }

        /// <summary>
        /// Dynamic settings object
        /// </summary>
        public dynamic Settings { get; private set; }

        private Action<BinaryReader>[] _packetHandlers = new Action<BinaryReader>[MessageID.Count];
        private int lastPacketLength = 0;
        private long _currentPacketStart = 0;
        private int _currentPacketLength = 0;
        private readonly DateTime[] _lastPlayerControlTrace = new DateTime[256];
        private bool hasReceivedWorldData = false;
        private bool requestedInitialTileData = false;
        private static readonly int[] SyncEquipmentLoginSlots = BuildSyncEquipmentLoginSlots();

        public HeadlessClient()
        {
            ConnectionState = ConnectionState.None;
            Settings = new Settings();
            SetDefaultSettings();
            SetupMessageHandlers();
        }

        /// <summary>
        /// Sets the default settings
        /// </summary>
        public void SetDefaultSettings()
        {
            // Printing out anything to Console.WriteLine
            Settings.PrintAnyOutput = true;
            Settings.PrintPlayerId = false;
            Settings.PrintWorldJoinMessages = true;
            Settings.PrintUnknownPackets = false;
            Settings.PrintKickMessage = true;
            Settings.PrintDisconnectMessage = true;

            // Automatically send the SpawnPlayer packet
            Settings.SpawnPlayer = true;

            // Run a seperate game loop
            Settings.RunGameLoop = true;
            Settings.UpdateTimeout = 200;

            // Automatically send some information to the server that vanilla clients usually send, this can prevent some detection by anti-cheats
            Settings.AutoSyncPlayerZone = true;
            Settings.AutoSyncPlayerControl = true;
            Settings.AutoSyncPlayerLife = true;
            Settings.AutoSyncPeriod = 250;
            Settings.LastSyncPeriod = DateTime.Now;

            // Load the actual tiles of the world, if this is set to false it will still have to decompress the tile section and will still keep track of what tile sectiosn you have loaded, but won't fill any tiles
            Settings.LoadTileSections = true;

            // Completely ignore all tile chunk packets
            Settings.IgnoreTileChunks = false;

            // Server password for MessageID.RequestPassword/SendPassword flow.
            Settings.ServerPassword = "";

            // Terraria network version (Terraria{version} in MessageID.Hello).
            Settings.VersionNumber = 318;

            // Optional full hello string override, e.g. "Terraria318" or "Terraria1.4.5.5".
            // If empty, the client sends "Terraria{VersionNumber}".
            Settings.HelloString = "";

            // Team selection: 0=None, 1=Red, 2=Green, 3=Blue, 4=Yellow.
            Settings.Team = 3;

            // Outgoing item ownership claims (MessageID.ItemOwner / id=22).
            // Keep disabled by default so the headless client does not lock nearby drops.
            Settings.AllowItemOwnershipClaim = false;

            // Extra packet-level diagnostics while joining (disabled once in-world).
            Settings.TraceJoinPackets = false;
        }

        private static int[] BuildSyncEquipmentLoginSlots()
        {
            List<int> slots = new List<int>(60);

            // Minimal vanilla-safe login sync:
            // inventory + coins + ammo + hand only (0..58).
            // Keep login payload minimal to avoid large pre-join bursts on busy worlds.
            void AddRange(int startInclusive, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    slots.Add(startInclusive + i);
                }
            }

            AddRange(0, 59);

            return slots.ToArray();
        }

        /// <summary>
        /// Connects to a terraria server
        /// <param name="address">the address to connect to, both IP and domain names are supported</param>
        /// <param name="port">the port to connect on</param>
        /// </summary>
        public async Task Connect(string address, short port)
        {
            if (!ResolveIP(address, out IPAddress ipAddress))
            {
                throw new ArgumentException($"Could not resolve ip {address}");
            }

            TCPClient = new ArkTCPClient(ipAddress, ReadBuffer, port, OnReceive);
            MemoryStreamWrite = new MemoryStream(WriteBuffer);
            MessageWriter = new BinaryWriter(MemoryStreamWrite);

            await ConnectToServer();
            BeginJoiningWorld();

            if (Settings.RunGameLoop)
            {
                Task.Run(
                    async () =>
                    {
                        while (true)
                        {
                            await Update();
                            await Task.Delay(Settings.UpdateTimeout);
                        }
                    });
            }

        }

        /// <summary>
        /// Connects the TCP client
        /// </summary>
        private async Task ConnectToServer()
        {
            await TCPClient.Connect();
            int retryCount = 0;
            while (!TCPClient.client.Connected || TCPClient.NetworkStream == null)
            {
                retryCount++;
                if (retryCount > 10)
                {
                    if (Settings.PrintAnyOutput && Settings.PrintConnectionMessages)
                    {
                        Console.WriteLine($"Error connecting to {TCPClient.IPAddress}:{TCPClient.port}");
                    }
                    return;
                }
                else
                {
                    await Task.Delay(100);
                }
            }

            MemoryStreamRead = new MemoryStream(ReadBuffer);
            MessageReader = new BinaryReader(MemoryStreamRead);
        }

        /// <summary>
        /// Starts joining a world
        /// </summary>
        private void BeginJoiningWorld()
        {
            SendData(1);
            ConnectionState = ConnectionState.SyncingPlayer;
        }


        /// <summary>
        /// Parses a string for the ip
        /// </summary>
        /// <param name="remoteAddress">IP address or domain name as a string</param>
        /// <param name="address">the IPAddress object resolved</param>
        /// <returns>whether the IP or domain name was valid</returns>
        public bool ResolveIP(string remoteAddress, out IPAddress address)
        {
            if (IPAddress.TryParse(remoteAddress, out address))
            {
                return true;
            }
            IPAddress[] addressList = Dns.GetHostEntry(remoteAddress).AddressList;
            for (int i = 0; i < addressList.Length; i++)
            {
                if (addressList[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    address = addressList[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Handler for receiving bytes from the server
        /// </summary>
        /// <param name="bytesRead">number of byets read</param>
        public void OnReceive(int bytesRead)
        {
            if (MemoryStreamRead == null)
            {
                MemoryStreamRead = new MemoryStream(ReadBuffer);
                MessageReader = new BinaryReader(MemoryStreamRead);
            }
            MemoryStreamRead.Seek(0, SeekOrigin.Begin);

            int dataLeftToRecieve = bytesRead;
            int currentReadIndex = 0;

            while (dataLeftToRecieve >= 2)
            {
                int nextPacketLength = BitConverter.ToUInt16(ReadBuffer, currentReadIndex);
                if (nextPacketLength == 0)
                    break;
                if (dataLeftToRecieve >= nextPacketLength)
                {
                    long position = MemoryStreamRead.Position;
                    lastPacketLength = nextPacketLength - 2;
                    GetData(currentReadIndex + 2, nextPacketLength - 2);
                    MemoryStreamRead.Position = position + nextPacketLength;
                    dataLeftToRecieve -= nextPacketLength;
                    currentReadIndex += nextPacketLength;
                    continue;
                }
                break;
            }
        }

        /// <summary>
        /// Simple update loop
        /// </summary>
        public async Task Update()
        {
            if (IsInWorld)
            {
                // This can bypass some anti-cheats that attempt to block headless clients
                if ((int)(DateTime.Now - (DateTime)Settings.LastSyncPeriod).TotalMilliseconds > Settings.AutoSyncPeriod)
                {
                    if (Settings.AutoSyncPlayerControl)
                    {
                        SendData(MessageID.PlayerControls, myPlayer);
                    }
                    if (Settings.AutoSyncPlayerZone)
                    {
                        SendData(MessageID.SyncPlayerZone, myPlayer);
                    }
                    if (Settings.AutoSyncPlayerLife)
                    {
                        SendData(MessageID.PlayerLife, myPlayer);
                    }
                    Settings.LastSyncPeriod = DateTime.Now;
                }
            }
            OnUpdate?.Invoke(this);
        }

        /// <summary>
        /// Disconnects the client from the server
        /// </summary>
        public async void Disconnect()
        {
            if (Settings.PrintAnyOutput && Settings.PrintDisconnectMessage)
            {
                Console.WriteLine($"Disconnected from world {World.CurrentWorld?.worldName}");
            }

            TCPClient.Exit = true;

            await TCPClient.ClientLoop;

            try
            {
                TCPClient.client.Shutdown(SocketShutdown.Both);
            }
            catch { }
            try
            {
                TCPClient.client.Close();
                TCPClient = null;
                Settings = null;
            }
            catch { }
            try
            {
                MemoryStreamWrite.Close();
            }
            catch { }
            try
            {
                MemoryStreamRead.Close();
            }
            catch { }
        }


        /// <summary>
        /// Handler for a packet being received
        /// </summary>
        /// <param name="start">position of the first byte of the packet in ReadBuffer</param>
        /// <param name="length">length of the packet</param>
        public void GetData(int start, int length)
        {
            if (TCPClient == null)
            {
                return;
            }

            _currentPacketStart = start;
            _currentPacketLength = length;
            MessageReader.BaseStream.Position = start;

            BinaryReader reader = MessageReader;

            byte messageType = reader.ReadByte();

            if (Settings.TraceJoinPackets)
            {
                Console.WriteLine($"[IN ] id={messageType} ({MessageID.GetName(messageType)}) len={length}");
            }

            if (NetMessageReceived != null)
            {
                RawIncomingPacket packet = new RawIncomingPacket
                {
                    ReadBuffer = ReadBuffer,
                    Reader = reader,
                    MessageType = messageType,
                    ContinueWithPacket = true
                };

                NetMessageReceived?.Invoke(this, packet);

                if (!packet.ContinueWithPacket)
                {
                    return;
                }
            }

            HandlePacket(messageType);
        }

        /// <summary>
        /// Sends data to the server
        /// </summary>
        /// <param name="messageType">type of message to be sent</param>
        public void SendData(int messageType, int number = 0, float number2 = 0, float number3 = 0, float number4 = 0, int number5 = 0)
        {
            if (TCPClient == null)
                return;
            lock (WriteBuffer)
            {
                if (messageType == MessageID.ItemOwner && !(bool)Settings.AllowItemOwnershipClaim)
                {
                    if (Settings.TraceJoinPackets)
                    {
                        Console.WriteLine("[OUT] blocked id=22 (ItemOwner): ownership locking disabled");
                    }
                    return;
                }

                BinaryWriter writer = MessageWriter;

                writer.Seek(2, SeekOrigin.Begin);

                writer.Write((byte)messageType);
                switch (messageType)
                {
                    case MessageID.Hello:
                        string helloString = (string)Settings.HelloString;
                        if (string.IsNullOrWhiteSpace(helloString))
                        {
                            helloString = "Terraria" + VersionNumber.ToString();
                        }
                        writer.Write(helloString);
                        break;
                    case MessageID.SendPassword:
                        writer.Write((string)Settings.ServerPassword);
                        break;
                    case MessageID.SyncPlayer:
                    {
                        Player plr = World.Players[number];

                        // Some servers reject empty or overlong names very early in login.
                        if (string.IsNullOrWhiteSpace(plr.name))
                        {
                            plr.name = "Gax";
                        }
                        else if (plr.name.Length > 20)
                        {
                            plr.name = plr.name.Substring(0, 20);
                        }

                        if (Settings.PrintAnyOutput)
                        {
                            Console.WriteLine($"Sending SyncPlayer name=\"{plr.name}\" len={plr.name.Length}");
                        }

                        writer.Write((byte)number);

                        // skin variant
                        writer.Write((byte)plr.skinVariant);

                        // voice variant + pitch offset (1.4.4+/1.4.5+ player info format)
                        writer.Write(plr.voiceVariant);
                        writer.Write(plr.voicePitchOffset);

                        // hair
                        writer.Write((byte)plr.hairType);

                        // name
                        writer.Write(plr.name);

                        // hair dye
                        writer.Write((byte)plr.hairDye);

                        // accessory/armor visibility flags
                        writer.Write((ushort)0);

                        // hide misc
                        writer.Write((byte)0);

                        // hairColor
                        writer.Write(plr.hairColor);

                        // skinColor
                        writer.Write(plr.skinColor);

                        // eyeColor
                        writer.Write(plr.eyeColor);

                        // shirtColor
                        writer.Write(plr.shirtColor);

                        // underShirtColor
                        writer.Write(plr.underShirtColor);

                        // pantsColor
                        writer.Write(plr.pantsColor);

                        // shoeColor
                        writer.Write(plr.shoeColor);

                        BitsByte bitsByte7 = (byte)0;
                        if (plr.difficulty == 1)
                        {
                            bitsByte7[0] = true;
                        }
                        else if (plr.difficulty == 2)
                        {
                            bitsByte7[1] = true;
                        }
                        else if (plr.difficulty == 3)
                        {
                            bitsByte7[3] = true;
                        }

                        // plr.extraAccessory;
                        bitsByte7[2] = plr.extraAccessory;
                        writer.Write(bitsByte7);

                        BitsByte bitsByte8 = (byte)0;
                        //plr.UsingBiomeTorches;
                        bitsByte8[0] = false;
                        //plr.happyFunTorchTime;
                        bitsByte8[1] = false;
                        //plr.unlockedBiomeTorches;
                        bitsByte8[2] = false;
                        //plr.unlockedSuperCart;
                        bitsByte8[3] = false;
                        //plr.enabledSuperCart;
                        bitsByte8[4] = false;
                        writer.Write(bitsByte8);

                        // extra consumable/permanent upgrade flags in newer versions.
                        BitsByte bitsByte10 = (byte)0;
                        writer.Write(bitsByte10);
                    }
                    break;
                    case MessageID.ClientUUID:
                        writer.Write(clientUUID);
                        break;
                    case MessageID.PlayerLife:
                    {
                        Player plr = World.Players[number];
                        writer.Write((byte)number);
                        //statLife
                        writer.Write((short)plr.statLife);
                        //statLifeMax
                        writer.Write((short)plr.statLifeMax);
                        break;
                    }
                    case MessageID.PlayerMana:
                    {
                        Player plr = World.Players[number];
                        writer.Write((byte)number);
                        //statMana
                        writer.Write((short)plr.statMana);
                        //statManaMax
                        writer.Write((short)plr.statManaMax);
                        break;
                    }
                    case MessageID.SyncPlayerBuffs:
                    {
                        writer.Write((byte)number);
                        for (int n = 0; n < 22; n++)
                        {
                            // buffType[n]
                            writer.Write((ushort)0);
                        }
                        break;
                    }
                    case MessageID.RequestWorldData:
                        break;
                    case MessageID.SyncEquipment:
                    {
                        Player plr = World.Players[number];
                        int slotIndex = (int)number2;
                        Item item = (slotIndex >= 0 && slotIndex < plr.inventory.Length) ? plr.inventory[slotIndex] : new Item();
                        if (item == null)
                        {
                            item = new Item();
                        }
                        // player index
                        writer.Write((byte)number);
                        writer.Write((short)slotIndex);
                        writer.Write((short)item.stack);
                        writer.Write((byte)item.prefix);
                        writer.Write((short)item.type);
                        writer.Write((byte)0); // slot flags (favorited/blocked)
                    }
                    break;
                    case MessageID.ClientSyncedInventory:
                        writer.Write((byte)number);
                        break;
                    case MessageID.SyncLoadout:
                        writer.Write((byte)number);
                        writer.Write((byte)number2);
                        break;
                    case MessageID.SpawnTileData:
                        writer.Write((int)number);
                        writer.Write((int)number2);
                        writer.Write((byte)Math.Clamp((int)number3, 0, 4));
                        break;
                    case MessageID.RequestChestOpen:
                        writer.Write((short)number);
                        writer.Write((short)number2);
                        break;
                    case MessageID.PlayerSpawn:
                    {
                        byte team = (byte)Math.Clamp((int)Settings.Team, 0, 4);
                        writer.Write((byte)number);
                        writer.Write((short)World.CurrentWorld.spawnTileX);
                        writer.Write((short)World.CurrentWorld.spawnTileY);
                        // 1.4.5.x layout:
                        // respawnTimer (int), numberOfDeathsPVE (short), numberOfDeathsPVP (short), team (byte), context (byte)
                        writer.Write(0); // respawnTimer
                        writer.Write((short)0); // deaths PVE
                        writer.Write((short)0); // deaths PVP
                        writer.Write(team);
                        writer.Write((byte)number2); // PlayerSpawnContext
                        break;
                    }
                    case MessageID.PlayerActive:
                    {
                        writer.Write((byte)number);
                        writer.Write((byte)(number2 != 0 ? 1 : 0));
                        break;
                    }
                    case MessageID.PlayerTeam:
                    {
                        writer.Write((byte)number);
                        writer.Write((byte)number2);
                        break;
                    }
                    case MessageID.PlayerControls:
                    {
                        Player plr = World.Players[number];
                        writer.Write((byte)number);
                        // Control flags
                        BitsByte control = (byte)0;
                        control[0] = plr.controlUp;
                        control[1] = plr.controlDown;
                        control[2] = plr.controlLeft;
                        control[3] = plr.controlRight;
                        control[4] = plr.controlJump;
                        control[5] = plr.controlUseItem;
                        control[6] = plr.direction == 1;
                        writer.Write(control);

                        // MiscDataSet1 flags. Bit 2 means velocity is included.
                        BitsByte pulley = (byte)0;
                        // Terraria 1.4.5.x servers are strict about PlayerUpdate layout.
                        // Always include velocity payload (zero when idle) for deterministic parsing.
                        bool hasVelocity = true;
                        pulley[2] = hasVelocity;
                        pulley[4] = plr.gravDir >= 0;
                        writer.Write(pulley);
                        // Always send modern player-update layout. Some servers that report 318
                        // still expect 1.4.5-style fields and will misparse legacy packets.
                        // MiscDataSet2
                        writer.Write((byte)0);
                        // MiscDataSet3
                        writer.Write((byte)0);
                        // Selected Item
                        writer.Write((byte)Math.Clamp(plr.selectedItem, 0, 58));
                        writer.Write(plr.position);
                        writer.Write(plr.velocity);

                        break;
                    }
                    case MessageID.SyncPlayerZone:
                    {
                        writer.Write((byte)number);
                        // 1.4.5.x zones packet: zone1..zone5 bits + townNPCs byte
                        writer.Write((byte)0); // zone1
                        writer.Write((byte)0); // zone2
                        writer.Write((byte)0); // zone3
                        writer.Write((byte)0); // zone4
                        writer.Write((byte)0); // zone5
                        writer.Write((byte)0); // nearby town NPC count
                        break;
                    }
                    case MessageID.ItemOwner:
                    {
                        writer.Write((short)number);
                        // ItemOwner payload: short itemIndex + byte ownerPlayer.
                        // Convention: number3 != 0 means "explicit owner provided in number2".
                        // Otherwise default to 255 (release/unowned).
                        byte owner = number3 != 0
                            ? (byte)Math.Clamp((int)number2, 0, 255)
                            : (byte)255;
                        writer.Write(owner);
                        break;
                    }
                    case MessageID.ReleaseItemOwnership:
                    {
                        // Payload: short itemIndex
                        writer.Write((short)number);
                        break;
                    }
                    case MessageID.TileManipulation:
                    {
                        // TileManipulationID
                        writer.Write((byte)number);
                        // tileX
                        writer.Write((short)number2);
                        // tileY
                        writer.Write((short)number3);
                        // flags 1
                        writer.Write((short)number4);
                        // flags 2
                        writer.Write((byte)number5);
                        break;
                    }
                    case MessageID.PaintTile:
                    case MessageID.PaintWall:
                    {
                        writer.Write((short)number);
                        writer.Write((short)number2);
                        writer.Write((byte)number3);
                        break;
                    };
                    case MessageID.SyncItem:
                    {
                        Item item7 = World.Items[number];
                        writer.Write((short)number);
                        writer.Write(item7.position);
                        writer.Write(item7.velocity);
                        writer.Write((short)item7.stack);
                        writer.Write(item7.prefix);
                        writer.Write((byte)number2);
                        short value5 = 0;
                        if (item7.active && item7.stack > 0)
                        {
                            value5 = (short)item7.GetNetID();
                        }
                        writer.Write(value5);
                        break;
                    }
                }

                int length = (int)MemoryStreamWrite.Position;
                writer.Seek(0, SeekOrigin.Begin);
                writer.Write((short)length);

                if (Settings.TraceJoinPackets)
                {
                    Console.WriteLine($"[OUT] id={messageType} ({MessageID.GetName(messageType)}) len={length}");
                }


                if (NetMessageSent != null)
                {
                    RawOutgoingPacket packet = new RawOutgoingPacket
                    {
                        WriteBuffer = WriteBuffer,
                        Writer = writer,
                        MessageType = messageType,
                        ContinueWithPacket = true
                    };

                    NetMessageSent?.Invoke(this, packet);

                    if (!packet.ContinueWithPacket)
                    {
                        return;
                    }
                }
                TCPClient.Send(WriteBuffer, length);
            }
        }

        /// <summary>
        /// Sets the code to be run when a packet is received of that type
        /// </summary>
        /// <param name="packetType">packet id for code to be run for</param>
        /// <param name="action">callback for when that packet is received</param>
        public void SetPacketHandler(int packetType, Action<BinaryReader> action)
        {
            _packetHandlers[packetType] = action;
        }

        private void HandlePacket(int packetType)
        {
            // Sanity check
            if (packetType < 0 || packetType >= MessageID.Count)
            {
                return;
            }

            Action<BinaryReader> handler = _packetHandlers[packetType];

            if (handler != null)
            {
                handler.Invoke(MessageReader);
            }
        }

        private void SetupMessageHandlers()
        {
            SetPacketHandler(MessageID.PlayerInfo, HandlePlayerInfo);
            SetPacketHandler(MessageID.NetModules, HandleNetModules);
            SetPacketHandler(MessageID.WorldData, HandleWorldData);
            SetPacketHandler(MessageID.CompleteConnectionAndSpawn, HandleCompleteConnectionAndSpawn);
            SetPacketHandler(MessageID.FinishedConnectingToServer, HandleFinishedConnectingToServer);
            SetPacketHandler(MessageID.Kick, HandleKick);
            SetPacketHandler(MessageID.StatusText, HandleStatusText);
            SetPacketHandler(MessageID.NPCKillCountDeathTally, HandleNPCKillCountDeathTally);
            SetPacketHandler(MessageID.TileSection, HandleTileSection);
            SetPacketHandler(MessageID.TileManipulation, HandleTileManipulation);
            SetPacketHandler(MessageID.SyncPlayer, HandleSyncPlayer);
            SetPacketHandler(MessageID.PlayerSpawn, HandlePlayerSpawn);
            SetPacketHandler(MessageID.PlayerActive, HandlePlayerActive);
            SetPacketHandler(MessageID.PlayerControls, HandlePlayerControls);
            SetPacketHandler(MessageID.PlayerLife, HandlePlayerLife);
            SetPacketHandler(MessageID.PlayerMana, HandlePlayerMana);
            SetPacketHandler(MessageID.SyncNPC, HandleSyncNPC);
            SetPacketHandler(MessageID.ReleaseItemOwnership, HandleReleaseItemOwnership);
            SetPacketHandler(MessageID.SyncProjectile, HandleSyncProjectile);
            SetPacketHandler(MessageID.KillProjectile, HandleKillProjectile);
            SetPacketHandler(MessageID.SyncEquipment, HandleSyncEquipment);
            SetPacketHandler(MessageID.SyncItem, HandleSyncItem);
            SetPacketHandler(MessageID.SyncItemDespawn, HandleSyncItemDespawn);
            SetPacketHandler(MessageID.InstancedItem, HandleInstancedItem);
            SetPacketHandler(MessageID.ItemPosition, HandleItemPosition);
            SetPacketHandler(MessageID.ItemOwner, HandleItemOwner);
            SetPacketHandler(MessageID.SyncPlayerItemRotation, HandleSyncPlayerItemRotation);
            SetPacketHandler(MessageID.UpdateSign, HandleUpdateSign);
            SetPacketHandler(MessageID.ChestName, HandleChestName);
            SetPacketHandler(MessageID.PlaceChest, HandlePlaceChest);
            SetPacketHandler(MessageID.RequestPassword, HandleRequestPassword);
        }

        private void HandleRequestPassword(BinaryReader reader)
        {
            if (Settings.PrintAnyOutput)
            {
                Console.WriteLine("Server requested password, sending configured password");
            }
            SendData(MessageID.SendPassword);
        }

        private void HandlePlayerInfo(BinaryReader reader)
        {
            int playerIndex = reader.ReadByte();
            bool ServerWantsToRunCheckBytesInClientLoopThread = reader.ReadBoolean();

            if (Settings.PrintAnyOutput && Settings.PrintPlayerId)
            {
                Console.WriteLine($"Player id of {playerIndex}");
            }

            if (myPlayer != playerIndex)
            {
                // Swap players
                World.Players[playerIndex] = World.Players[myPlayer].Clone();
                World.Players[myPlayer].Reset();

                World.Players[playerIndex].whoAmI = playerIndex;
                World.Players[myPlayer].whoAmI = myPlayer;
                myPlayer = playerIndex;
            }

            LocalPlayer.active = true;

            SendData(MessageID.SyncPlayer, playerIndex);

            for (int i = 0; i < SyncEquipmentLoginSlots.Length; i++)
            {
                SendData(MessageID.SyncEquipment, playerIndex, SyncEquipmentLoginSlots[i]);
            }

            // 1.4.5.x loadout index sync during login.
            SendData(MessageID.SyncLoadout, playerIndex, 0);

            ConnectionState = ConnectionState.RequestingWorldData;

            if (Settings.PrintAnyOutput && Settings.PrintWorldJoinMessages)
            {
                Console.WriteLine("Requesting world data");
            }

            SendData(MessageID.RequestWorldData);
            TryBeginTileDataRequest();
        }
        private void HandleNetModules(BinaryReader reader)
        {
            ushort netModule = reader.ReadUInt16();
            switch (netModule)
            {
                case NetModuleID.Liquid:
                {
                    int liquidUpdateCount = MessageReader.ReadUInt16();
                    for (int i = 0; i < liquidUpdateCount; i++)
                    {
                        int num2 = MessageReader.ReadInt32();
                        byte liquid = MessageReader.ReadByte();
                        byte liquidType = MessageReader.ReadByte();
                        //Tile tile = Main.tile[num3, num4];
                        //if (tile != null)
                        //{
                        //    tile.liquid = liquid;
                        //    tile.liquidType(liquidType);
                        //}
                    }
                    break;
                }
                case NetModuleID.Text:
                {
                    int authorIndex = reader.ReadByte();
                    NetworkText networkText = NetworkText.Deserialize(reader);
                    ChatMessageRecieved?.Invoke(this, new ChatMessage(authorIndex, networkText.ToString()));
                    break;
                }
            }
        }
        private void HandleWorldData(BinaryReader reader)
        {
            World.CurrentWorld.time = reader.ReadInt32();
            BitsByte bitsByte20 = reader.ReadByte();
            World.CurrentWorld.dayTime = bitsByte20[0];
            World.CurrentWorld.bloodMoon = bitsByte20[1];
            World.CurrentWorld.eclipse = bitsByte20[2];
            World.CurrentWorld.moonPhase = reader.ReadByte();
            World.CurrentWorld.maxTilesX = reader.ReadInt16();
            World.CurrentWorld.maxTilesY = reader.ReadInt16();
            World.CurrentWorld.spawnTileX = reader.ReadInt16();
            World.CurrentWorld.spawnTileY = reader.ReadInt16();
            World.CurrentWorld.worldSurface = reader.ReadInt16();
            World.CurrentWorld.rockLayer = reader.ReadInt16();
            World.CurrentWorld.worldID = reader.ReadInt32();
            World.CurrentWorld.worldName = reader.ReadString();
            World.CurrentWorld.GameMode = reader.ReadByte();
            World.CurrentWorld.worldUUID = new Guid(reader.ReadBytes(16));
            World.CurrentWorld.worldGenVer = reader.ReadUInt64();
            World.CurrentWorld.moonType = reader.ReadByte();

            // World Background 0
            reader.ReadByte();
            // World Background 1
            reader.ReadByte();
            // World Background 1
            reader.ReadByte();
            // World Background 1
            reader.ReadByte();
            // World Background 1
            reader.ReadByte();
            // World Background 2
            reader.ReadByte();
            // World Background 3
            reader.ReadByte();
            // World Background 4
            reader.ReadByte();
            // World Background 5
            reader.ReadByte();
            // World Background 6
            reader.ReadByte();
            // World Background 7
            reader.ReadByte();
            // World Background 8
            reader.ReadByte();
            // World Background 9
            reader.ReadByte();

            World.CurrentWorld.iceBackStyle = reader.ReadByte();
            World.CurrentWorld.jungleBackStyle = reader.ReadByte();
            World.CurrentWorld.hellBackStyle = reader.ReadByte();
            World.CurrentWorld.windSpeedTarget = reader.ReadSingle();
            World.CurrentWorld.numClouds = reader.ReadByte();

            for (int i = 0; i < 3; i++)
            {
                // treeX[i]
                reader.ReadInt32();
            }
            for (int i = 0; i < 4; i++)
            {
                // treeStyle[i]
                reader.ReadByte();
            }
            for (int i = 0; i < 3; i++)
            {
                // caveBackX[i]
                reader.ReadInt32();
            }
            for (int i = 0; i < 4; i++)
            {
                // caveBackStyle[i]
                reader.ReadByte();
            }

            for (int i = 0; i < 13; i++)
            {
                // some tree variation doodoo
                reader.ReadByte();
            }

            World.CurrentWorld.maxRaining = reader.ReadSingle();
            World.CurrentWorld.raining = World.CurrentWorld.maxRaining > 0f;

            BitsByte bitsByte21 = reader.ReadByte();
            World.CurrentWorld.shadowOrbSmashed = bitsByte21[0];
            World.CurrentWorld.downedBoss1 = bitsByte21[1];
            World.CurrentWorld.downedBoss2 = bitsByte21[2];
            World.CurrentWorld.downedBoss3 = bitsByte21[3];
            World.CurrentWorld.hardMode = bitsByte21[4];
            World.CurrentWorld.downedClown = bitsByte21[5];
            ServerSideCharacter = bitsByte21[6];
            World.CurrentWorld.downedPlantBoss = bitsByte21[7];
            //if (Main.ServerSideCharacter)
            //{
            //    Main.ActivePlayerFileData.MarkAsServerSide();
            //}
            BitsByte bitsByte22 = reader.ReadByte();
            World.CurrentWorld.downedMechBoss1 = bitsByte22[0];
            World.CurrentWorld.downedMechBoss2 = bitsByte22[1];
            World.CurrentWorld.downedMechBoss3 = bitsByte22[2];
            World.CurrentWorld.downedMechBossAny = bitsByte22[3];
            World.CurrentWorld.cloudBGActive = (bitsByte22[4] ? 1 : 0);
            World.CurrentWorld.crimson = bitsByte22[5];
            World.CurrentWorld.pumpkinMoon = bitsByte22[6];
            World.CurrentWorld.snowMoon = bitsByte22[7];
            BitsByte bitsByte23 = reader.ReadByte();
            World.CurrentWorld.fastForwardTime = bitsByte23[1];
            //UpdateTimeRate();
            bool num265 = bitsByte23[2];
            World.CurrentWorld.downedSlimeKing = bitsByte23[3];
            World.CurrentWorld.downedQueenBee = bitsByte23[4];
            World.CurrentWorld.downedFishron = bitsByte23[5];
            World.CurrentWorld.downedMartians = bitsByte23[6];
            World.CurrentWorld.downedAncientCultist = bitsByte23[7];
            BitsByte bitsByte24 = reader.ReadByte();
            World.CurrentWorld.downedMoonlord = bitsByte24[0];
            World.CurrentWorld.downedHalloweenKing = bitsByte24[1];
            World.CurrentWorld.downedHalloweenTree = bitsByte24[2];
            World.CurrentWorld.downedChristmasIceQueen = bitsByte24[3];
            World.CurrentWorld.downedChristmasSantank = bitsByte24[4];
            World.CurrentWorld.downedChristmasTree = bitsByte24[5];
            World.CurrentWorld.downedGolemBoss = bitsByte24[6];
            World.CurrentWorld.BirthdayPartyManualParty = bitsByte24[7];
            BitsByte bitsByte25 = reader.ReadByte();
            World.CurrentWorld.downedPirates = bitsByte25[0];
            World.CurrentWorld.downedFrost = bitsByte25[1];
            World.CurrentWorld.downedGoblins = bitsByte25[2];
            World.CurrentWorld.Sandstorm.Happening = bitsByte25[3];
            World.CurrentWorld.DD2.Ongoing = bitsByte25[4];
            World.CurrentWorld.DD2.DownedInvasionT1 = bitsByte25[5];
            World.CurrentWorld.DD2.DownedInvasionT2 = bitsByte25[6];
            World.CurrentWorld.DD2.DownedInvasionT3 = bitsByte25[7];
            BitsByte bitsByte26 = reader.ReadByte();
            World.CurrentWorld.combatBookWasUsed = bitsByte26[0];
            World.CurrentWorld.LanternNightManualLanterns = bitsByte26[1];
            World.CurrentWorld.downedTowerSolar = bitsByte26[2];
            World.CurrentWorld.downedTowerVortex = bitsByte26[3];
            World.CurrentWorld.downedTowerNebula = bitsByte26[4];
            World.CurrentWorld.downedTowerStardust = bitsByte26[5];
            World.CurrentWorld.forceHalloweenForToday = bitsByte26[6];
            World.CurrentWorld.forceXMasForToday = bitsByte26[7];
            BitsByte bitsByte27 = reader.ReadByte();
            World.CurrentWorld.boughtCat = bitsByte27[0];
            World.CurrentWorld.boughtDog = bitsByte27[1];
            World.CurrentWorld.boughtBunny = bitsByte27[2];
            World.CurrentWorld.freeCake = bitsByte27[3];
            World.CurrentWorld.drunkWorld = bitsByte27[4];
            World.CurrentWorld.downedEmpressOfLight = bitsByte27[5];
            World.CurrentWorld.downedQueenSlime = bitsByte27[6];
            World.CurrentWorld.getGoodWorld = bitsByte27[7];
            BitsByte bitsByte28 = reader.ReadByte();
            World.CurrentWorld.tenthAnniversaryWorld = bitsByte28[0];
            World.CurrentWorld.dontStarveWorld = bitsByte28[1];
            World.CurrentWorld.downedDeerclops = bitsByte28[2];
            World.CurrentWorld.notTheBeesWorld = bitsByte28[3];
            World.CurrentWorld.SavedOreTiers_Copper = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Iron = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Silver = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Gold = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Cobalt = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Mythril = reader.ReadInt16();
            World.CurrentWorld.SavedOreTiers_Adamantite = reader.ReadInt16();
            if (num265)
            {
                //Main.StartSlimeRain();
            }
            else
            {
                //Main.StopSlimeRain();
            }
            World.CurrentWorld.invasionType = reader.ReadSByte();
            LobbyId = reader.ReadUInt64();
            World.CurrentWorld.Sandstorm.IntendedSeverity = reader.ReadSingle();

            hasReceivedWorldData = true;

            if (Settings.PrintAnyOutput && Settings.PrintWorldJoinMessages)
            {
                Console.WriteLine($"World bounds: {World.CurrentWorld.maxTilesX}x{World.CurrentWorld.maxTilesY} tiles | spawn=({World.CurrentWorld.spawnTileX},{World.CurrentWorld.spawnTileY})");
            }

            TryBeginTileDataRequest();
        }

        private void TryBeginTileDataRequest()
        {
            if (requestedInitialTileData || !hasReceivedWorldData || ConnectionState != ConnectionState.RequestingWorldData)
            {
                return;
            }

            requestedInitialTileData = true;

            if (Settings.PrintAnyOutput && Settings.PrintWorldJoinMessages)
            {
                Console.WriteLine($"Joining world \"{World.CurrentWorld.worldName}\"");
            }

            ConnectionState = ConnectionState.RequestingTileData;

            World.CurrentWorld.SetupTiles(Settings.LoadTileSections);
            WorldDataRecieved?.Invoke(this);

            // Terraria player position is top-left; spawn tile is floor reference.
            LocalPlayer.position = new Vector2(
                World.CurrentWorld.spawnTileX * 16f + 8f - 10f,
                World.CurrentWorld.spawnTileY * 16f - 42f);
            LocalPlayer.velocity = Vector2.Zero;
            LocalPlayer.direction = 1;
            LocalPlayer.gravDir = 1;

            // Initial tile request uses spawn sentinel coordinates in modern protocol flow.
            SendData(
                MessageID.SpawnTileData,
                -1,
                -1,
                Math.Clamp((int)Settings.Team, 0, 4));
        }
        private void HandleCompleteConnectionAndSpawn(BinaryReader reader)
        {
            if (Settings.SpawnPlayer)
            {
                // Ensure sane vanilla defaults before first state sync.
                if (LocalPlayer.statLifeMax < 100)
                {
                    LocalPlayer.statLifeMax = 100;
                }
                if (LocalPlayer.statLife <= 0)
                {
                    LocalPlayer.statLife = LocalPlayer.statLifeMax;
                }
                if (LocalPlayer.statManaMax < 20)
                {
                    LocalPlayer.statManaMax = 20;
                }
                if (LocalPlayer.statMana < 0)
                {
                    LocalPlayer.statMana = 0;
                }
                if (LocalPlayer.statMana > LocalPlayer.statManaMax)
                {
                    LocalPlayer.statMana = LocalPlayer.statManaMax;
                }

                // Ensure our very first PlayerControls sync carries a valid in-world position.
                // If this is left at (0,0), some servers snap the player to world corner/top-left.
                if (TryGetWorldSpawnPosition(World.CurrentWorld, out Vector2 spawnPos))
                {
                    LocalPlayer.position = spawnPos;
                    LocalPlayer.velocity = Vector2.Zero;
                }

                // Terraria 1.4.5.x: initial join must use SpawningIntoWorld (context = 1).
                if (Settings.PrintAnyOutput && Settings.PrintWorldJoinMessages)
                {
                    Console.WriteLine($"Sending PlayerSpawn: tile=({World.CurrentWorld.spawnTileX},{World.CurrentWorld.spawnTileY}) team={Math.Clamp((int)Settings.Team, 0, 4)} context=1");
                }
                SendData(MessageID.PlayerSpawn, myPlayer, 1);
                SendData(MessageID.PlayerActive, myPlayer, 1);
                SendData(MessageID.SyncPlayerZone, myPlayer);
                SendData(MessageID.PlayerControls, myPlayer);

                // Send core state packets after spawn completes (safe point in join state machine).
                SendData(MessageID.ClientUUID);
                SendData(MessageID.PlayerLife, myPlayer);
                SendData(MessageID.PlayerMana, myPlayer);
                SendData(MessageID.SyncPlayerBuffs, myPlayer);
                SendData(MessageID.ClientSyncedInventory, myPlayer);

                // Temporarily commenting this out since this doesn't actually do anything lmfao

                //for (int i = 0; i < 40; i++)
                //{
                //    SendData(MessageID.SyncEquipment, myPlayer, i);
                //}

                //SendData(MessageID.SyncPlayerZone, myPlayer);
                //SendData(MessageID.PlayerControls, myPlayer);
                //SendData(MessageID.ClientSyncedInventory, myPlayer);
            }
            IsInWorld = true;
            ClientConnectionCompleted?.Invoke(this);

            ConnectionState = ConnectionState.Connected;
        }
        private void HandleFinishedConnectingToServer(BinaryReader reader)
        {
            // Vanilla clients send team sync shortly after this packet.
            SendData(MessageID.PlayerTeam, myPlayer, Math.Clamp((int)Settings.Team, 0, 4));
            FinishedConnectingToServer?.Invoke(this);
        }
        private void HandleKick(BinaryReader reader)
        {
            IsInWorld = false;
            string reason = "";
            string reasonDiagnostics = "";
            try
            {
                // Newer versions send NetworkText in kick packets.
                NetworkText kickText = NetworkText.Deserialize(reader);
                reason = kickText.ToString();
                reasonDiagnostics = kickText.ToDiagnosticString();
            }
            catch
            {
                try
                {
                    // Older versions may send a plain string.
                    reason = reader.ReadString();
                }
                catch
                {
                    reason = "";
                }
            }

            if (Settings.PrintAnyOutput && Settings.PrintKickMessage)
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    Console.WriteLine($"Kicked from world {World.CurrentWorld?.worldName}: {reason}");
                    if (!string.IsNullOrWhiteSpace(reasonDiagnostics) && reasonDiagnostics != reason)
                    {
                        Console.WriteLine($"Kick diagnostics: {reasonDiagnostics}");
                    }
                }
                else
                {
                    Console.WriteLine($"Kicked from world {World.CurrentWorld?.worldName}");
                }
            }
        }
        private void HandleStatusText(BinaryReader reader)
        {
            int statusMax = reader.ReadInt32();
            NetworkText statusText = NetworkText.Deserialize(reader);
            byte flags = reader.ReadByte();

            if (Settings.PrintAnyOutput && Settings.PrintWorldJoinMessages)
            {
                Console.WriteLine($"StatusText: {statusText.ToDiagnosticString()} ({flags}/{statusMax})");
            }
        }
        private void HandleNPCKillCountDeathTally(BinaryReader reader)
        {
            short npcType = reader.ReadInt16();
            int npcKillCount = reader.ReadInt32();
        }
        private void HandleTileSection(BinaryReader reader)
        {

            if (!Settings.IgnoreTileChunks)
            {
                // lastPacketLength includes the message id byte, but reader.BaseStream.Position is
                // already advanced past message id. Subtract one to avoid reading into next packet.
                World.CurrentWorld.DecompressTileSection(ReadBuffer, (int)reader.BaseStream.Position, lastPacketLength - 1, Settings.LoadTileSections);
            }
        }
        private void HandleTileManipulation(BinaryReader reader)
        {
            byte action = reader.ReadByte();
            int tileX = reader.ReadInt16();
            int tileY = reader.ReadInt16();
            int flags = reader.ReadInt16();
            int flags2 = reader.ReadByte();

            bool? handleManpipulation = TileManipulationMessageRecieved?.Invoke(this, new TileManipulation(action, tileX, tileY, flags, flags2));
            if (!handleManpipulation.HasValue || handleManpipulation.Value)
            {
                TileManipulationHandler.Handle(this, action, tileX, tileY, flags, flags2);
            }
        }
        private void HandleSyncPlayer(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();

            if (whoAreThey == myPlayer && !ServerSideCharacter)
            {
                return;
            }

            // skin variant
            World.Players[whoAreThey].skinVariant = reader.ReadByte();

            // voice variant + pitch offset
            World.Players[whoAreThey].voiceVariant = reader.ReadByte();
            World.Players[whoAreThey].voicePitchOffset = reader.ReadSingle();

            // hair
            World.Players[whoAreThey].hairType = reader.ReadByte();

            World.Players[whoAreThey].name = reader.ReadString();

            // hair dye
            World.Players[whoAreThey].hairDye = reader.ReadByte();

            // accessory/armor visibility flags
            ushort hideVisualFlags = reader.ReadUInt16();

            // hide misc
            BitsByte hideMisc = reader.ReadByte();

            // hairColor
            World.Players[whoAreThey].hairColor = reader.ReadRGB();

            // skinColor
            World.Players[whoAreThey].skinColor = reader.ReadRGB();

            // eyeColor
            World.Players[whoAreThey].eyeColor = reader.ReadRGB();

            // shirtColor
            World.Players[whoAreThey].shirtColor = reader.ReadRGB();

            // underShirtColor
            World.Players[whoAreThey].underShirtColor = reader.ReadRGB();

            // pantsColor
            World.Players[whoAreThey].pantsColor = reader.ReadRGB();

            // shoeColor
            World.Players[whoAreThey].shoeColor = reader.ReadRGB();

            BitsByte bitsByte7 = reader.ReadByte();

            BitsByte bitsByte8 = reader.ReadByte();

            BitsByte bitsByte10 = reader.ReadByte();
        }
        private void HandlePlayerActive(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();
            bool active = reader.ReadByte() == 1;

            World.Players[whoAreThey].active = active;
        }
        private void HandlePlayerSpawn(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();
            short spawnTileX = reader.ReadInt16();
            short spawnTileY = reader.ReadInt16();
            int respawnTimer = reader.ReadInt32();
            short numberOfDeathsPVE = reader.ReadInt16();
            short numberOfDeathsPVP = reader.ReadInt16();
            byte team = reader.ReadByte();
            byte spawnContext = reader.ReadByte();

            if (whoAreThey < 0 || whoAreThey >= World.Players.Length)
            {
                return;
            }

            Player plr = World.Players[whoAreThey];
            plr.active = true;
            plr.controlUp = false;
            plr.controlDown = false;
            plr.controlLeft = false;
            plr.controlRight = false;
            plr.controlJump = false;
            plr.controlUseItem = false;
            plr.velocity = Vector2.Zero;
            plr.position = new Vector2(spawnTileX * 16f + 8f - 10f, spawnTileY * 16f - 42f);

            if (Settings.PrintAnyOutput && Settings.TraceJoinPackets && whoAreThey == myPlayer)
            {
                Console.WriteLine(
                    $"PlayerSpawn sync for local player: tile=({spawnTileX},{spawnTileY}) timer={respawnTimer} team={team} context={spawnContext}");
            }
        }
        private void HandlePlayerControls(BinaryReader reader)
        {
            long packetEnd = _currentPacketStart + _currentPacketLength;
            byte whoAreThey = reader.ReadByte();
            if (whoAreThey < 0 || whoAreThey >= World.Players.Length)
            {
                reader.BaseStream.Position = packetEnd;
                return;
            }
            Player plr = World.Players[whoAreThey];
            Vector2 previousPosition = plr.position;

            BitsByte control = reader.ReadByte();
            BitsByte pulley = reader.ReadByte();
            plr.controlUp = control[0];
            plr.controlDown = control[1];
            plr.controlLeft = control[2];
            plr.controlRight = control[3];
            plr.controlJump = control[4];
            plr.controlUseItem = control[5];
            plr.direction = (control[6] ? 1 : (-1));
            if (pulley[0])
            {
                plr.pulley = true;
                plr.pulleyDir = (byte)((!pulley[1]) ? 1u : 2u);
            }
            else
            {
                plr.pulley = false;
            }
            plr.vortexStealthActive = pulley[3];
            plr.gravDir = (pulley[4] ? 1 : (-1));
            //plr.TryTogglingShield(bitsByte15[5]);
            plr.ghost = pulley[6];

            // Servers in the wild may send either legacy or modern player-update layout
            // even when negotiated version is 318. Detect format by payload shape.
            long payloadStart = reader.BaseStream.Position;
            long remaining = packetEnd - payloadStart;
            bool parsed = false;

            // Strong hint from field logs on some 318 networks:
            // total msg len 15 => remaining 9 (legacy without velocity)
            // total msg len 23 => remaining 17 (legacy with velocity)
            if (remaining == 9 || remaining == 17)
            {
                parsed = TryParsePlayerControlsLegacySized(reader, plr, hasVelocity: remaining == 17);
            }

            if (!parsed)
            {
                reader.BaseStream.Position = payloadStart;
                parsed = TryParsePlayerControlsModern(reader, plr, pulley, packetEnd);
            }
            if (!parsed)
            {
                reader.BaseStream.Position = payloadStart;
                parsed = TryParsePlayerControlsLegacy(reader, plr, pulley, packetEnd);
            }

            if (!parsed)
            {
                // Skip malformed payload and keep previous state.
                reader.BaseStream.Position = packetEnd;
                plr.position = previousPosition;
                plr.velocity = Vector2.Zero;
                return;
            }

            if (!IsSanePositionForWorld(World.CurrentWorld, plr.position))
            {
                // Guard against packet-layout mismatch/desync producing impossible coordinates.
                plr.position = previousPosition;
                plr.velocity = Vector2.Zero;
                return;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("HTC_TRACE_PLAYER_CONTROLS"), "1", StringComparison.OrdinalIgnoreCase))
            {
                int idx = whoAreThey;
                if (idx >= 0 && idx < _lastPlayerControlTrace.Length)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - _lastPlayerControlTrace[idx]).TotalMilliseconds >= 500)
                    {
                        _lastPlayerControlTrace[idx] = now;
                        Console.WriteLine(
                            $"[IN-PC] who={idx} name=\"{plr.name}\" active={plr.active} len={_currentPacketLength} pos=({plr.position.X:0},{plr.position.Y:0})");
                    }
                }
            }
        }

        private static bool TryParsePlayerControlsLegacySized(BinaryReader reader, Player plr, bool hasVelocity)
        {
            long startPos = reader.BaseStream.Position;
            long endPos = reader.BaseStream.Length;
            long expected = hasVelocity ? 17 : 9;
            if (endPos - startPos != expected)
            {
                return false;
            }

            byte selectedItem = reader.ReadByte();
            Vector2 position = reader.ReadVector2();
            Vector2 velocity = hasVelocity ? reader.ReadVector2() : Vector2.Zero;

            plr.selectedItem = selectedItem;
            plr.position = position;
            plr.velocity = velocity;
            plr.PotionOfReturnOriginalUsePosition = null;
            plr.PotionOfReturnHomePosition = null;
            plr.tryKeepingHoveringUp = false;
            plr.IsVoidVaultEnabled = false;
            plr.isSitting = false;
            plr.downedDD2EventAnyDifficulty = false;
            plr.isPettingAnimal = false;
            plr.isTheAnimalBeingPetSmall = false;
            plr.tryKeepingHoveringDown = false;
            return true;
        }

        private static bool TryParsePlayerControlsModern(BinaryReader reader, Player plr, BitsByte pulley, long packetEnd)
        {
            long startPos = reader.BaseStream.Position;
            long endPos = packetEnd;

            // Modern layout minimum after base flags:
            // sitting(1) + misc(1) + selected(1) + position(8) = 11 bytes.
            if (endPos - startPos < 11)
            {
                return false;
            }

            BitsByte sitting = reader.ReadByte();
            BitsByte what = reader.ReadByte();
            byte selectedItem = reader.ReadByte();
            Vector2 position = reader.ReadVector2();
            Vector2 velocity = Vector2.Zero;

            if (pulley[2])
            {
                if (endPos - reader.BaseStream.Position < 8)
                {
                    reader.BaseStream.Position = startPos;
                    return false;
                }
                velocity = reader.ReadVector2();
            }

            Vector2? potionOriginalUsePosition = null;
            Vector2? potionHomePosition = null;
            if (sitting[6] && endPos - reader.BaseStream.Position >= 16)
            {
                potionOriginalUsePosition = reader.ReadVector2();
                potionHomePosition = reader.ReadVector2();
            }
            else if (sitting[6])
            {
                reader.BaseStream.Position = startPos;
                return false;
            }

            // Require full payload consumption for format certainty.
            if (reader.BaseStream.Position != endPos)
            {
                reader.BaseStream.Position = startPos;
                return false;
            }

            plr.selectedItem = selectedItem;
            plr.position = position;
            plr.velocity = velocity;
            plr.PotionOfReturnOriginalUsePosition = potionOriginalUsePosition;
            plr.PotionOfReturnHomePosition = potionHomePosition;
            plr.tryKeepingHoveringUp = sitting[0];
            plr.IsVoidVaultEnabled = sitting[1];
            plr.isSitting = sitting[2];
            plr.downedDD2EventAnyDifficulty = sitting[3];
            plr.isPettingAnimal = sitting[4];
            plr.isTheAnimalBeingPetSmall = sitting[5];
            plr.tryKeepingHoveringDown = sitting[7];

            return true;
        }

        private static bool TryParsePlayerControlsLegacy(BinaryReader reader, Player plr, BitsByte pulley, long packetEnd)
        {
            long startPos = reader.BaseStream.Position;
            long endPos = packetEnd;

            // Legacy layout minimum after base flags:
            // selected(1) + position(8) = 9 bytes.
            if (endPos - startPos < 9)
            {
                return false;
            }

            byte selectedItem = reader.ReadByte();
            Vector2 position = reader.ReadVector2();
            Vector2 velocity = Vector2.Zero;

            if (pulley[2])
            {
                if (endPos - reader.BaseStream.Position < 8)
                {
                    reader.BaseStream.Position = startPos;
                    return false;
                }
                velocity = reader.ReadVector2();
            }

            // Require full payload consumption for format certainty.
            if (reader.BaseStream.Position != endPos)
            {
                reader.BaseStream.Position = startPos;
                return false;
            }

            plr.selectedItem = selectedItem;
            plr.position = position;
            plr.velocity = velocity;
            plr.PotionOfReturnOriginalUsePosition = null;
            plr.PotionOfReturnHomePosition = null;
            plr.tryKeepingHoveringUp = false;
            plr.IsVoidVaultEnabled = false;
            plr.isSitting = false;
            plr.downedDD2EventAnyDifficulty = false;
            plr.isPettingAnimal = false;
            plr.isTheAnimalBeingPetSmall = false;
            plr.tryKeepingHoveringDown = false;

            return true;
        }
        private static bool TryGetWorldSpawnPosition(World world, out Vector2 spawnPos)
        {
            spawnPos = Vector2.Zero;
            if (world == null || world.maxTilesX <= 0 || world.maxTilesY <= 0)
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
                Math.Clamp(world.spawnTileX * 16f + 8f - 10f, minX, maxX),
                Math.Clamp(world.spawnTileY * 16f - 42f, minY, maxY));
            return true;
        }
        private static bool IsSanePositionForWorld(World world, Vector2 pos)
        {
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
            {
                return false;
            }
            if (world == null || world.maxTilesX <= 0 || world.maxTilesY <= 0)
            {
                return pos.X >= -64f && pos.Y >= -64f;
            }

            float maxX = world.maxTilesX * 16f;
            float maxY = world.maxTilesY * 16f;
            return pos.X >= -64f && pos.Y >= -64f && pos.X <= maxX + 64f && pos.Y <= maxY + 64f;
        }
        private void HandlePlayerLife(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();
            Player plr = World.Players[whoAreThey];

            plr.statLife = reader.ReadInt16();
            plr.statLifeMax = reader.ReadInt16();
            if (plr.statLifeMax < 100)
            {
                plr.statLifeMax = 100;
            }
        }
        private void HandlePlayerMana(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();
            Player plr = World.Players[whoAreThey];
            plr.statMana = reader.ReadInt16();
            plr.statManaMax = reader.ReadInt16();
        }
        private void HandleSyncNPC(BinaryReader reader)
        {
            int npcIndex = reader.ReadInt16();
        }
        private void HandleReleaseItemOwnership(BinaryReader reader)
        {
            int itemIndex = reader.ReadInt16();
            if (itemIndex < 0 || itemIndex >= World.Items.Length)
            {
                return;
            }

            Item item = World.Items[itemIndex];
            if (item == null)
            {
                item = new Item();
                World.Items[itemIndex] = item;
            }

            // Server notifies clients that this item is now unowned.
            item.owner = 255;
        }
        private void HandleItemOwner(BinaryReader reader)
        {
            int itemIndex = reader.ReadInt16();
            int owner = reader.ReadByte();
            if (itemIndex < 0 || itemIndex >= World.Items.Length)
            {
                return;
            }

            Item item = World.Items[itemIndex];
            if (item == null)
            {
                item = new Item();
                World.Items[itemIndex] = item;
            }
            item.owner = owner;

            // Do not let the headless client keep ground item ownership.
            // This prevents nearby real players from being blocked while the bot is on-screen.
            if (owner == myPlayer && !(bool)Settings.AllowItemOwnershipClaim)
            {
                SendData(MessageID.ReleaseItemOwnership, itemIndex);
            }
        }
        private void HandleSyncProjectile(BinaryReader reader)
        {
        }
        private void HandleKillProjectile(BinaryReader reader)
        {
        }
        private void HandleSyncEquipment(BinaryReader reader)
        {
            long packetEnd = _currentPacketStart + _currentPacketLength;
            byte whoAreThey = reader.ReadByte();

            Player plr = World.Players[whoAreThey];

            lock (plr)
            {
                short inventorySlot;
                short stack;
                byte prefix;
                short type;

                if (UseLegacy318Wire)
                {
                    inventorySlot = reader.ReadByte();
                    stack = reader.ReadInt16();
                    prefix = reader.ReadByte();
                    type = reader.ReadInt16();
                }
                else
                {
                    inventorySlot = reader.ReadInt16();
                    stack = reader.ReadInt16();
                    prefix = reader.ReadByte();
                    type = reader.ReadInt16();
                    if (reader.BaseStream.Position < packetEnd)
                    {
                        _ = reader.ReadByte(); // slot flags
                    }
                }

                if (inventorySlot < 0 || inventorySlot >= plr.inventory.Length)
                {
                    return;
                }
                Item item = plr.inventory[inventorySlot];

                if (item == null)
                {
                    item = new Item(type, stack, prefix);
                }
                else
                {
                    item.type = type;
                    item.prefix = prefix;
                    item.stack = stack;
                }
                plr.inventory[inventorySlot] = item;
            }
        }
        private void HandleSyncItem(BinaryReader reader)
        {
            int itemId = reader.ReadInt16();
            Vector2 itemPosition = reader.ReadVector2();
            Vector2 itemVelocity = reader.ReadVector2();
            int itemStack = reader.ReadInt16();
            int itemPrefix = reader.ReadByte();
            int noDelay = reader.ReadByte();
            int netID = reader.ReadInt16();

            if (netID == 0)
            {
                World.Items[itemId].active = false;
                return;
            }

            Item item2 = World.Items[itemId];
            item2.SetTypeFromNetID(netID);
            item2.prefix = itemPrefix;
            item2.stack = itemStack;
            item2.position = itemPosition;
            item2.velocity = itemVelocity;
            item2.active = true;
            item2.instanced = false;
            item2.whoIsThisInstancedItemFor = 0;
            item2.owner = 255;
        }
        private void HandleSyncItemDespawn(BinaryReader reader)
        {
            int itemId = reader.ReadInt16();
            if (itemId < 0 || itemId >= World.Items.Length)
            {
                return;
            }

            Item item = World.Items[itemId];
            if (item == null)
            {
                item = new Item();
                World.Items[itemId] = item;
            }

            item.active = false;
            item.stack = 0;
            item.owner = 255;
            item.velocity = Vector2.Zero;
        }
        private void HandleInstancedItem(BinaryReader reader)
        {
            int itemId = reader.ReadInt16();
            Vector2 itemPosition = reader.ReadVector2();
            Vector2 itemVelocity = reader.ReadVector2();
            int itemStack = reader.ReadInt16();
            int itemPrefix = reader.ReadByte();
            int noDelay = reader.ReadByte();
            int netID = reader.ReadInt16();

            if (netID == 0)
            {
                World.Items[itemId].active = false;
                return;
            }

            Item item2 = World.Items[itemId];
            item2.SetTypeFromNetID(netID);
            item2.prefix = itemPrefix;
            item2.stack = itemStack;
            item2.position = itemPosition;
            item2.velocity = itemVelocity;
            item2.active = true;

            // the item is only on our client, which is cringe as fuck
            item2.instanced = true;
            item2.whoIsThisInstancedItemFor = myPlayer;
            item2.owner = myPlayer;

            // what do i even do for this
            // stays around for 10 seconds? this is cringe.
            // item2.keepTime = 600;
        }
        private void HandleItemPosition(BinaryReader reader)
        {
            long packetEnd = _currentPacketStart + _currentPacketLength;
            int itemId = reader.ReadInt16();
            if (itemId < 0 || itemId >= World.Items.Length)
            {
                return;
            }

            Item item = World.Items[itemId];
            if (item == null)
            {
                item = new Item();
                World.Items[itemId] = item;
            }

            long remaining = packetEnd - reader.BaseStream.Position;
            if (remaining >= 8)
            {
                item.position = reader.ReadVector2();
                remaining = packetEnd - reader.BaseStream.Position;
            }

            // Some protocol variants include velocity in this packet as well.
            if (remaining >= 8)
            {
                item.velocity = reader.ReadVector2();
            }

            if (item.type > 0 && item.stack > 0)
            {
                item.active = true;
            }
        }
        private void HandleSyncPlayerItemRotation(BinaryReader reader)
        {
            byte whoAreThey = reader.ReadByte();

            float rotation = reader.ReadSingle();
            short animation = reader.ReadInt16();
        }
        private void HandleUpdateSign(BinaryReader reader)
        {
            int signId = reader.ReadInt16();
            int x = reader.ReadInt16();
            int y = reader.ReadInt16();
            string text = reader.ReadString();
            byte whoDidThis = reader.ReadByte();
            byte signFlags = reader.ReadByte();

            if (signId >= 0 && signId < 1000)
            {
                if (World.CurrentWorld.Signs[signId] == null)
                {
                    World.CurrentWorld.Signs[signId] = new Sign();
                }
                World.CurrentWorld.Signs[signId].text = text;
                World.CurrentWorld.Signs[signId].x = x;
                World.CurrentWorld.Signs[signId].y = y;
            }
        }
        private void HandleChestName(BinaryReader reader)
        {
            int chestId = reader.ReadInt16();
            int chestX = reader.ReadInt16();
            int chestY = reader.ReadInt16();
            if (chestId >= 0 && chestId < 8000)
            {
                Chest targetChest = World.CurrentWorld.Chests[chestId];
                if (targetChest == null)
                {
                    targetChest = new Chest();
                    targetChest.x = chestX;
                    targetChest.y = chestY;
                    World.CurrentWorld.Chests[chestId] = targetChest;
                }
                else if (targetChest.x != chestX || targetChest.y != chestY)
                {
                    return;
                }
                targetChest.Name = reader.ReadString();
            }
        }
        private void HandlePlaceChest(BinaryReader reader)
        {
            int action = reader.ReadByte();
            int chestX = reader.ReadInt16();
            int chestY = reader.ReadInt16();
            int style = reader.ReadInt16();
            int chestId = reader.ReadInt16();

            switch (action)
            {
                case 0:
                    if (chestId == -1)
                    {
                        World.CurrentWorld.KillTile(chestX, chestY);
                        break;
                    }
                    World.CurrentWorld.PlaceChestDirect(chestX, chestY, 21, style, chestId);
                    break;
                case 2:
                    if (chestId == -1)
                    {
                        World.CurrentWorld.KillTile(chestX, chestY);
                        break;
                    }
                    World.CurrentWorld.PlaceDresserDirect(chestX, chestY, 88, style, chestId);
                    break;
                case 4:
                    if (chestId == -1)
                    {
                        World.CurrentWorld.KillTile(chestX, chestY);
                        break;
                    }
                    World.CurrentWorld.PlaceChestDirect(chestX, chestY, 467, style, chestId);
                    break;
                default:
                    World.CurrentWorld.KillChestDirect(chestX, chestY, chestId);
                    World.CurrentWorld.KillTile(chestX, chestY);
                    break;
            }
        }
    }
}
