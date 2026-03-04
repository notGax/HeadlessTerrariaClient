using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HeadlessTerrariaClient.Examples;
using HeadlessTerrariaClient.Terraria;
using HeadlessTerrariaClient.Terraria.ID;

namespace HeadlessTerrariaClient
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Probe mode:
            // dotnet ... scan <password> [startVersion] [endVersion]
            if (args.Length >= 1 && string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
            {
                string probePassword = args.Length >= 2 ? args[1] : "gaxryktparry";
                int startVersion = 250;
                int endVersion = 400;

                if (args.Length >= 3 && int.TryParse(args[2], out int parsedStart))
                {
                    startVersion = parsedStart;
                }
                if (args.Length >= 4 && int.TryParse(args[3], out int parsedEnd))
                {
                    endVersion = parsedEnd;
                }

                await ScanVersions("play.notgax.app", 25565, probePassword, startVersion, endVersion);
                return;
            }

            if (args.Length > 5)
            {
                PrintUsage("Too many arguments.");
                Environment.ExitCode = 1;
                return;
            }

            StartupConfig cliConfig = ParseCliRuntimeConfig(args, out string cliError);
            if (!string.IsNullOrEmpty(cliError))
            {
                PrintUsage(cliError);
                Environment.ExitCode = 1;
                return;
            }

            bool configLoaded = TryLoadConfigFile(out StartupConfig fileConfig, out string configPath, out string configError);
            if (!string.IsNullOrEmpty(configError))
            {
                PrintUsage(configError);
                Environment.ExitCode = 1;
                return;
            }

            StartupConfig resolved = MergeConfig(cliConfig, fileConfig);
            if (string.IsNullOrWhiteSpace(resolved.Version))
            {
                resolved.Version = "318";
            }

            string validationError = ValidateRuntimeConfig(resolved);
            if (!string.IsNullOrEmpty(validationError))
            {
                if (!configLoaded)
                {
                    PrintUsage($"{validationError} Also could not find client.config.json in current directory or executable directory.");
                }
                else
                {
                    PrintUsage($"{validationError} Loaded config file: {configPath}");
                }
                Environment.ExitCode = 1;
                return;
            }

            await new SimpleChatClient(
                resolved.Version,
                resolved.Password,
                resolved.Username,
                resolved.ServerIp,
                resolved.ServerPort.Value).Start();
        }

        private static StartupConfig ParseCliRuntimeConfig(string[] args, out string error)
        {
            error = "";
            StartupConfig config = new StartupConfig();

            if (args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0]))
            {
                config.Username = args[0].Trim();
            }
            if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
            {
                config.ServerIp = args[1].Trim();
            }
            if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
            {
                if (int.TryParse(args[2], out int parsedPort))
                {
                    config.ServerPort = parsedPort;
                }
                else
                {
                    error = $"Invalid port in CLI argument: '{args[2]}'.";
                    return config;
                }
            }
            if (args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3]))
            {
                config.Password = args[3];
            }
            if (args.Length >= 5 && !string.IsNullOrWhiteSpace(args[4]))
            {
                config.Version = args[4].Trim();
            }

            return config;
        }

        private static bool TryLoadConfigFile(out StartupConfig config, out string loadedPath, out string error)
        {
            config = new StartupConfig();
            loadedPath = "";
            error = "";

            string[] paths = GetConfigSearchPaths();
            string discoveredPath = "";
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    discoveredPath = path;
                    break;
                }
            }

            if (string.IsNullOrEmpty(discoveredPath))
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(discoveredPath);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = $"Config file '{discoveredPath}' must contain a JSON object.";
                    return false;
                }

                config.Username = GetStringProperty(root, "Username");
                config.ServerIp = GetStringProperty(root, "ServerIp");
                config.Password = GetStringProperty(root, "Password");
                config.Version = GetStringProperty(root, "Version");
                config.ServerPort = GetIntProperty(root, "ServerPort");

                loadedPath = discoveredPath;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to read config file '{discoveredPath}': {ex.Message}";
                return false;
            }
        }

        private static StartupConfig MergeConfig(StartupConfig cli, StartupConfig file)
        {
            return new StartupConfig
            {
                Username = Pick(cli.Username, file.Username),
                ServerIp = Pick(cli.ServerIp, file.ServerIp),
                Password = Pick(cli.Password, file.Password),
                Version = Pick(cli.Version, file.Version),
                ServerPort = cli.ServerPort ?? file.ServerPort
            };
        }

        private static string ValidateRuntimeConfig(StartupConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Username))
            {
                return "Missing username.";
            }
            if (string.IsNullOrWhiteSpace(config.ServerIp))
            {
                return "Missing server IP/host.";
            }
            if (!config.ServerPort.HasValue || config.ServerPort.Value < 1 || config.ServerPort.Value > 65535)
            {
                return "Missing or invalid server port (must be 1-65535).";
            }
            if (string.IsNullOrWhiteSpace(config.Password))
            {
                return "Missing server password.";
            }

            return "";
        }

        private static string[] GetConfigSearchPaths()
        {
            string fileName = "client.config.json";
            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            string exeDir = AppContext.BaseDirectory ?? "";
            string exePath = Path.Combine(exeDir, fileName);

            if (string.Equals(cwdPath, exePath, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { cwdPath };
            }
            return new[] { cwdPath, exePath };
        }

        private static string Pick(string primary, string fallback)
        {
            return string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        }

        private static string GetStringProperty(JsonElement root, string name)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out JsonElement property))
            {
                return "";
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? "";
            }
            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.GetRawText();
            }
            if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            {
                return property.GetBoolean().ToString();
            }
            return "";
        }

        private static int? GetIntProperty(JsonElement root, string name)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out JsonElement property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int parsedNumber))
            {
                return parsedNumber;
            }
            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out int parsedString))
            {
                return parsedString;
            }
            return null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static void PrintUsage(string error)
        {
            Console.WriteLine(error);
            Console.WriteLine("Usage (runtime):");
            Console.WriteLine("  HeadlessTerrariaClient <username> <ip> <port> <password> [version]");
            Console.WriteLine("  Missing arguments are loaded from client.config.json");
            Console.WriteLine("");
            Console.WriteLine("Usage (scan):");
            Console.WriteLine("  HeadlessTerrariaClient scan <password> [startVersion] [endVersion]");
            Console.WriteLine("");
            Console.WriteLine("client.config.json example:");
            Console.WriteLine("  {");
            Console.WriteLine("    \"Username\": \"YourBotName\",");
            Console.WriteLine("    \"ServerIp\": \"example.server.host\",");
            Console.WriteLine("    \"ServerPort\": 25565,");
            Console.WriteLine("    \"Password\": \"your-server-password\",");
            Console.WriteLine("    \"Version\": \"318\"");
            Console.WriteLine("  }");
        }

        private static async Task ScanVersions(string host, int port, string password, int startVersion, int endVersion)
        {
            if (startVersion > endVersion)
            {
                int tmp = startVersion;
                startVersion = endVersion;
                endVersion = tmp;
            }

            Console.WriteLine($"Scanning Terraria protocol versions {startVersion}..{endVersion} on {host}:{port}");

            for (int version = startVersion; version <= endVersion; version++)
            {
                ProbeResult result = await ProbeVersion(host, port, version, password);
                Console.WriteLine($"[{version}] {result.Status}{(string.IsNullOrWhiteSpace(result.Detail) ? "" : $" - {result.Detail}")}");

                // If hello/password handshake is accepted, stop scan immediately.
                if (result.Status == "Accepted")
                {
                    break;
                }
            }
        }

        private static async Task<ProbeResult> ProbeVersion(string host, int port, int version, string password)
        {
            try
            {
                using TcpClient client = new TcpClient();
                Task connectTask = client.ConnectAsync(host, port);
                Task timeoutTask = Task.Delay(3500);
                Task completed = await Task.WhenAny(connectTask, timeoutTask);
                if (completed == timeoutTask)
                {
                    return new ProbeResult("Timeout", "connect timeout");
                }

                using NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 3500;
                stream.WriteTimeout = 3500;

                // Send packet 1 (Hello): "Terraria{version}"
                SendPacket(stream, MessageID.Hello, writer => writer.Write("Terraria" + version));

                // Read up to a few packets to complete hello/password handshake.
                for (int i = 0; i < 4; i++)
                {
                    byte[] packet = ReadPacket(stream);
                    if (packet == null || packet.Length == 0)
                    {
                        return new ProbeResult("NoResponse", "");
                    }

                    byte messageType = packet[0];
                    using MemoryStream ms = new MemoryStream(packet);
                    using BinaryReader reader = new BinaryReader(ms);
                    reader.ReadByte(); // skip message type

                    switch (messageType)
                    {
                        case MessageID.RequestPassword:
                            SendPacket(stream, MessageID.SendPassword, writer => writer.Write(password));
                            continue;
                        case MessageID.Kick:
                        {
                            string kickText = ReadKickText(reader);
                            return new ProbeResult("Kick", kickText);
                        }
                        case MessageID.PlayerInfo:
                            return new ProbeResult("Accepted", "reached PlayerInfo");
                        case MessageID.StatusText:
                        {
                            // Often means we're progressing through connection state.
                            string status = "";
                            try
                            {
                                reader.ReadInt32();
                                status = NetworkText.Deserialize(reader).ToDiagnosticString();
                            }
                            catch
                            {
                                status = "status packet";
                            }
                            return new ProbeResult("Accepted", status);
                        }
                        default:
                            return new ProbeResult("Packet", $"id={messageType}");
                    }
                }

                return new ProbeResult("Unknown", "");
            }
            catch (Exception ex)
            {
                return new ProbeResult("Error", ex.GetType().Name);
            }
        }

        private static void SendPacket(NetworkStream stream, byte messageType, Action<BinaryWriter> writePayload)
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            writer.Write((short)0); // length placeholder
            writer.Write(messageType);
            writePayload?.Invoke(writer);

            long end = ms.Position;
            ms.Position = 0;
            writer.Write((short)end);
            writer.Flush();

            byte[] data = ms.ToArray();
            stream.Write(data, 0, data.Length);
        }

        private static byte[] ReadPacket(NetworkStream stream)
        {
            byte[] lenBuf = new byte[2];
            int got = ReadExact(stream, lenBuf, 0, 2);
            if (got < 2)
            {
                return null;
            }

            int len = BitConverter.ToUInt16(lenBuf, 0);
            if (len <= 2)
            {
                return Array.Empty<byte>();
            }

            byte[] payload = new byte[len - 2];
            got = ReadExact(stream, payload, 0, payload.Length);
            if (got < payload.Length)
            {
                return null;
            }
            return payload;
        }

        private static int ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }
            return total;
        }

        private static string ReadKickText(BinaryReader reader)
        {
            try
            {
                NetworkText text = NetworkText.Deserialize(reader);
                return text.ToDiagnosticString();
            }
            catch
            {
                try
                {
                    return reader.ReadString();
                }
                catch
                {
                    return "";
                }
            }
        }

        private readonly struct ProbeResult
        {
            public string Status { get; }
            public string Detail { get; }

            public ProbeResult(string status, string detail)
            {
                Status = status;
                Detail = detail;
            }
        }

        private sealed class StartupConfig
        {
            public string Username { get; set; } = "";
            public string ServerIp { get; set; } = "";
            public int? ServerPort { get; set; } = null;
            public string Password { get; set; } = "";
            public string Version { get; set; } = "";
        }
    }
}
