using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.Server;


[assembly: ModInfo("VintageRCon",
        Authors = new string[] { "Shijikori" },
        Description = "Provides a Source RCON server for server remote management and administration.",
        Version = "2.0.2")]
namespace VintageRCon
{
    //An RCON Packet object
    // See: https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
    class RCONPacket {

        public Int32 Id {get;set;}
        public Int32 Type {get;set;}
        public string Body {get;set;}
        
        public RCONPacket() {
            Id = 0;
            Type = 0;
            Body = "";
        }

        public RCONPacket(Byte[] data) {
            // Strict validation of RCON packet structure
            // Structure: Size(4) + ID(4) + Type(4) + Body(N) + Null(1) + Null(1)
            
            if (data.Length < 14) { // Min size (10) + 4 bytes for size field itself
                 throw new FormatException("Packet buffer too small");
            }

            Int32 size = BinaryPrimitives.ReadInt32LittleEndian(data[0..4]);
            
            if (data.Length < size + 4) {
                throw new FormatException("Packet buffer smaller than declared size");
            }
            
            Id = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
            Type = BinaryPrimitives.ReadInt32LittleEndian(data[8..12]);
            
            // Validate double null terminator
            // The packet size field does not include itself (4 bytes).
            // So total data length is size + 4.
            // The last two bytes of the buffer must be 0x00.
            int totalLength = size + 4;
            if (data[totalLength - 1] != 0x00 || data[totalLength - 2] != 0x00) {
                throw new FormatException("Packet not properly null terminated");
            }
            
            // Body starts at offset 12 and ends at totalLength - 2
            int bodyLength = totalLength - 2 - 12;
            if (bodyLength > 0) {
                Body = Encoding.UTF8.GetString(data, 12, bodyLength);
            } else {
                Body = "";
            }
        }

        public RCONPacket(Int32 id, Int32 type, string body) {
            Id = id;
            Type = type;
            Body = body;
        }

        /*
         * Returns the serialized data as bytes, compliant with RCON protocol.
         */
        public Byte[] Serialize() {
            Byte[] bodyBytes = Encoding.UTF8.GetBytes(Body);
            int totalSize = 12 + bodyBytes.Length + 2; // 4(Size) + 4(ID) + 4(Type) + Body + 2(Nulls)
            
            Byte[] message = new byte[totalSize];
            var span = message.AsSpan();
            
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0,4), bodyBytes.Length + 10); // Size: Body + 10
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4,4), Id);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8,4), Type);
            
            bodyBytes.CopyTo(span.Slice(12));
            
            // Last two bytes are already 0x00 from new byte[], but explicit is better
            span[totalSize - 2] = 0x00;
            span[totalSize - 1] = 0x00;
            
            return message;
        }
    }

    public class RConServerThread {
        private const int MaxPacketSize = 4096;
        // 4096 (Max Packet) - 4 (Size) - 4 (ID) - 4 (Type) - 2 (Nulls) = 4082 Body Max
        // We use this to safely split large responses into multiple packets.
        private const int MaxBodySize = 4082; 

        public ICoreServerAPI Api {get;}
        public ILogger Logger {get;}
        private TcpListener? _server;
        private int _port;
        private IPAddress _ip;
        private string _password;
        private int _timeout;
        private int _maxConnections;

        public RConServerThread(ICoreServerAPI api, IPAddress ip, int port, string password, int timeout, int maxConnections) {
            Api = api;
            Logger = api.Logger;
            _ip = ip;
            _port = port;
            _password = password;
            _timeout = timeout;
            _maxConnections = maxConnections;
        }

        public void Init() {
            try {
                TcpListener server = new TcpListener(_ip, _port);
                server.Start();
                _server = server;
                Logger.Notification($"RCon Listener started on port {_port}");
            }
            catch (SocketException e) {
                Logger.Error(e);
                this.Dispose();
            }
        }

        public async void StartListenerAsync(CancellationToken token) {
            if (_server is null) {
                Logger.Error("Could not start listening for sockets");
                Logger.Notification($"obj:{_server}");
                return;
            }
            Logger.Notification("Listening for RCon connexions...");
            var tasks = new List<Task>();
            try {
                while (!token.IsCancellationRequested) {
                    try {
                        Socket socket = await _server.AcceptSocketAsync(token);
                        
                        // Cleanup completed tasks to get accurate count
                        var templ = new List<Task>();
                        foreach (var task in tasks) {
                            if (task.IsCompleted) {
                                task.Dispose();
                            }
                            else {
                                templ.Add(task);
                            }
                        }
                        tasks = templ;

                        if (tasks.Count >= _maxConnections) {
                            Logger.Warning($"RCon connection rejected from {socket.RemoteEndPoint}: Max concurrent connections ({_maxConnections}) reached.");
                            socket.Close();
                            continue;
                        }

                        Logger.Notification($"RCon connection received from {socket.RemoteEndPoint}");
                        tasks.Add(HandleSocketAsync(socket, Api, _password, _timeout, token));
                    }
                    catch (Exception e) {
                        if (e is OperationCanceledException) throw;
                        Logger.Error($"Error accepting RCon connection: {e.Message}");
                        // Wait a bit before retrying to avoid tight loop in case of persistent error
                        await Task.Delay(1000, token);
                    }
                }
            }
            catch (Exception e) {
                if (e is OperationCanceledException) {
                    Logger.Notification("Shutting down RCon listener...");
                }
            }
            finally {
                await Task.WhenAll(tasks);
            }
        }

        /*
         * Handles an individual RCON client connection.
         * Security Features:
         * - Async I/O with cancellation support
         * - Strict packet size and structure validation (Valve Spec)
         * - Double null-terminator enforcement
         * - Idle timeouts
         * - Exception filtering for stability
         * - UTF-8 safe packet splitting
         */
        internal static async Task HandleSocketAsync(Socket socket, ICoreServerAPI api, string password, int timeout, CancellationToken token) {
            socket.NoDelay = true;
            api.Logger.Notification("RCon socket thread started");
            
            // Clamp timeout between 1 minute and 24 hours (1440 minutes)
            // This prevents immediate cancellation (min) and slot exhaustion by zombies (max)
            int safeTimeout = Math.Clamp(timeout, 1, 1440);
            
            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            List<string> storedChunks = new List<string>();

            try {
                // Set initial timeout
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(safeTimeout));
                
                bool isSessionAuthenticated = false;
                while (!token.IsCancellationRequested) {
                    // 1. Read Packet Size (4 bytes)
                    byte[] sizeBuffer = new byte[4];
                    int bytesRead = 0;
                    while (bytesRead < 4) {
                        int read = await socket.ReceiveAsync(sizeBuffer.AsMemory(bytesRead, 4 - bytesRead), SocketFlags.None, linkedCts.Token);
                        if (read == 0) return; // Disconnected
                        bytesRead += read;
                    }
                    
                    // Reset timeout on successful activity
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(safeTimeout));
                    
                    int packetSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
                    
                    // 2. Validate Size (Strict MaxPacketSize byte limit per Valve Spec)
                    if (packetSize < 10 || packetSize > MaxPacketSize) {
                        api.Logger.Warning($"Invalid RCon packet size: {packetSize}. Closing connection.");
                        return;
                    }

                    // 3. Read Packet Body (Size bytes)
                    byte[] packetBuffer = new byte[packetSize + 4]; // +4 to include the size header we already read for the RCONPacket constructor
                    BinaryPrimitives.WriteInt32LittleEndian(packetBuffer.AsSpan(0, 4), packetSize);
                    
                    bytesRead = 0;
                    while (bytesRead < packetSize) {
                        int read = await socket.ReceiveAsync(packetBuffer.AsMemory(4 + bytesRead, packetSize - bytesRead), SocketFlags.None, linkedCts.Token);
                        if (read == 0) return; // Disconnected
                        bytesRead += read;
                    }

                    RCONPacket packet = new RCONPacket(packetBuffer);
                    
                    // Strict Packet Type Validation
                    if (packet.Type != 3 && packet.Type != 2 && packet.Type != 0) {
                        api.Logger.Warning($"Invalid RCon packet type: {packet.Type}. Closing connection.");
                        return;
                    }
                    
                    if (packet.Type == 3) { // Authentication
                        bool authenticated = IsPasswordCorrect(packet.Body, password);
                        
                        // Send SERVERDATA_RESPONSE_VALUE (empty)
                        await socket.SendAsync(new RCONPacket(packet.Id, 0, "").Serialize(), SocketFlags.None, linkedCts.Token);
                        
                        // Send SERVERDATA_AUTH_RESPONSE
                        await socket.SendAsync(new RCONPacket(authenticated ? packet.Id : -1, 2, "").Serialize(), SocketFlags.None, linkedCts.Token);
                        
                        if (!authenticated) {
                            await Task.Delay(2000, linkedCts.Token); // Delay to prevent brute-force
                            return;
                        }
                        isSessionAuthenticated = true;
                    }
                    else if (packet.Type == 2) { // Command
                        if (!isSessionAuthenticated) {
                            // Client tried to send command without authenticating first
                            api.Logger.Warning("RCon client attempted command without authentication. Closing connection.");
                            return;
                        }

                        if (string.IsNullOrEmpty(packet.Body)) {
                            await socket.SendAsync(new RCONPacket(packet.Id, 0, "").Serialize(), SocketFlags.None, linkedCts.Token);
                            continue;
                        }
                        
                        string[] data = packet.Body.Split();
                        CmdArgs args = data.Length == 1 ? new CmdArgs() : new CmdArgs(data[1..]);

                        api.Logger.Notification($"RCon Handling Command '/{packet.Body}' from {socket.RemoteEndPoint}");

                        var tcs = new TaskCompletionSource<TextCommandResult>();
                        storedChunks.Clear();

                        api.Event.EnqueueMainThreadTask(() => {
                            try {
                                api.ChatCommands.Execute(data[0],
                                    new TextCommandCallingArgs() {
                                        Caller = new Caller {
                                            Type = EnumCallerType.Console,
                                            CallerRole = "admin",
                                            CallerPrivileges = new string[] {"*"},
                                            FromChatGroupId = GlobalConstants.ConsoleGroup
                                        },
                                        RawArgs = args,
                                    },
                                    (TextCommandResult result) => {
                                        tcs.TrySetResult(result);
                                    });
                            } catch (Exception ex) {
                                tcs.TrySetException(ex);
                            }
                        }, "RConCommand");

                        try {
                            TextCommandResult result = await tcs.Task.WaitAsync(linkedCts.Token);
                            
                            string message = result.StatusMessage ?? "";
                            byte[] messageBytes = Encoding.UTF8.GetBytes(message);

                            // Split into MaxBodySize byte chunks (safe margin below MaxPacketSize)
                            if (messageBytes.Length > MaxBodySize) {
                                int currentPos = 0;
                                while (currentPos < messageBytes.Length) {
                                    int length = Math.Min(MaxBodySize, messageBytes.Length - currentPos);
                                    
                                    // UTF-8 Safety: Ensure we don't split inside a multi-byte character
                                    if (currentPos + length < messageBytes.Length) {
                                        // If the first byte of the NEXT chunk is a continuation byte (0b10xxxxxx),
                                        // we have split a character. Backtrack until we find the start.
                                        while (length > 0 && (messageBytes[currentPos + length] & 0xC0) == 0x80) {
                                            length--;
                                        }
                                    }
                                    
                                    if (length == 0) {
                                        // Should not happen unless a single character is larger than MaxBodySize (impossible for UTF-8)
                                        // or logic error. Force progress to avoid infinite loop.
                                        length = Math.Min(MaxBodySize, messageBytes.Length - currentPos);
                                    }

                                    string chunk = Encoding.UTF8.GetString(messageBytes, currentPos, length);
                                    storedChunks.Add(chunk);
                                    currentPos += length;
                                }
                                await socket.SendAsync(new RCONPacket(packet.Id, 0, storedChunks[0]).Serialize(), SocketFlags.None, linkedCts.Token); //send the first chunk only
                                storedChunks.RemoveAt(0);
                                storedChunks.TrimExcess();
                            } else {
                                await socket.SendAsync(new RCONPacket(packet.Id, 0, message).Serialize(), SocketFlags.None, linkedCts.Token);
                            }
                        } catch (Exception ex) {
                            api.Logger.Error("Error executing RCon command: " + ex.Message);
                            await socket.SendAsync(new RCONPacket(packet.Id, 0, "Error executing command").Serialize(), SocketFlags.None, linkedCts.Token);
                        }
                    }
                    else if (packet.Type == 0) {
                        if (!isSessionAuthenticated) {
                            // Client tried to send command without authenticating first
                            api.Logger.Warning("RCon client attempted command without authentication. Closing connection.");
                            return;
                        }
                        if (storedChunks.Count != 0) {
                            await socket.SendAsync(new RCONPacket(packet.Id, 0, storedChunks[0]).Serialize(), SocketFlags.None, linkedCts.Token); //send the first chunk in the list
                            storedChunks.RemoveAt(0); // removing the chunk that was just sent
                            storedChunks.TrimExcess(); // making sure that capacity aligns with count just in case
                        }
                        else {
                            await socket.SendAsync(new RCONPacket(packet.Id, 0, "").Serialize(), SocketFlags.None, linkedCts.Token);
                        }
                    }
                }
            }
            catch (FormatException fe) {
                api.Logger.Warning($"RCon Malformed Packet: {fe.Message}. Closing connection.");
            }
            catch (OperationCanceledException) {
                // Expected on timeout or shutdown
            }
            catch (Exception e) {
                if (!(e is SocketException) && !(e is ObjectDisposedException)) {
                    api.Logger.Error($"RCon Error: {e.Message}");
                }
            }
            finally {
                try {
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                    socket.Dispose();
                } catch {}
                api.Logger.Notification("RCon socket closed");
            }
        }

        private static bool IsPasswordCorrect(string input, string password) {
            // Constant-time comparison to prevent timing attacks
            if (input.Length != password.Length) return false;
            int result = 0;
            for (int i = 0; i < input.Length; i++) {
                result |= input[i] ^ password[i];
            }
            return result == 0;
        }

        public void Dispose() {
            if (_server is not null) _server.Stop();
            _server = null;
        }
    }

    public class VintageRCon {
        public ICoreServerAPI Api {get;}
        public VRConCfg Config {get;}
        private RConServerThread rcst = null!;
        private CancellationTokenSource cts = new CancellationTokenSource();

        public VintageRCon(ICoreServerAPI api, VRConCfg config) {
            Config = config;
            Api = api;
            Api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, OnRunGame);
            if (config.IP is null) {
                rcst = new RConServerThread(api, IPAddress.Any, config.Port, config.Password, config.Timeout, config.MaxConnections);
            }
            else {
                rcst = new RConServerThread(api, IPAddress.Parse(config.IP), config.Port, config.Password, config.Timeout, config.MaxConnections);
            }
        }
        public void OnRunGame() {
            Api.Logger.Notification("Starting RCon Listener...");
            rcst.Init();
            if (rcst is null) {
                Api.Logger.Error("RCon Listener failed to start!");
                return;
            }
            else rcst.StartListenerAsync(cts.Token);
        }
        public void Dispose() {
            cts.Cancel();
            rcst.Dispose();
        }
    }

    public class VintageRConMod : ModSystem
    {
        internal const string ConfigFile = "vsrcon.json";
        internal ICoreServerAPI Api = null!;
        internal static VRConCfg Config { get; set ;} = null!;
        public static VintageRCon? VRCon {get; private set;}

        public override bool ShouldLoad(EnumAppSide side) {
            return side.IsServer();
        }

        public override void StartServerSide(ICoreServerAPI api) {
            Api = api;
            try {
                Config = Api.LoadModConfig<VRConCfg>(ConfigFile);
                if (Config is null) {
                    Config = new VRConCfg();
                    Api.StoreModConfig(Config, ConfigFile);
                    Api.Logger.Warning($"{ConfigFile} was not found in configs, has been created with default values.");
                }
                if (Config.Password == "") {
                    Api.Logger.Notification(Config.Password);
                    Api.Logger.Warning("An RCon password has not been set in config file. RCon will be unavailable. Please set an RCon password and restart the server.");
                    return;
                }
            }
            catch (Exception e) {
                Api.Logger.Error(e);
                Api.Logger.Error("Failed to load configs");
                return;
            }

            VRCon = new VintageRCon(Api, Config);
        }

        public override void Dispose() {
            VRCon?.Dispose();
            VRCon = null;
        }
    }
}

