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
        Version = "1.0.0")]
namespace VintageRCon
{
    //An RCON Packet object
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
            //loading the packet assuming it is data meant for RCON connexion
            //we may get exceptions in the case it isn't.
            Int32 size = BinaryPrimitives.ReadInt32LittleEndian(data[0..4]);
            if (size < 10) {
                Id = 0;
                Type = 0;
                Body = "";
            }
            else {
                Id = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
                Type = BinaryPrimitives.ReadInt32LittleEndian(data[8..12]);
                Body = Encoding.UTF8.GetString(data[12..(size+4)]).Trim('\0');
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
            Byte[] head = new Byte[12];
            var span = new Span<byte>(head);
            Byte[] body = Encoding.UTF8.GetBytes(Body); //out of concern for accuracy of the size section, we make the array of bytes now and use it going forward
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0,4), body.Length + 10); //enscribe the size of the packet in the header
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4,4), Id);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8,4), Type);
            Byte[] message = new byte[12 + body.Length + 2];
            System.Buffer.BlockCopy(head, 0, message, 0, 12);
            System.Buffer.BlockCopy(body, 0, message, 12, body.Length);
            System.Buffer.BlockCopy(Encoding.UTF8.GetBytes("\0\0"), 0, message, 12 + body.Length, 2); //since C# does not null terminate strings, we append two null characters to the end of the packet to comply with protocol spec
            return message;
        }
    }

    public class RConServerThread {
        public ICoreServerAPI Api {get;}
        public ILogger Logger {get;}
        private TcpListener? _server;
        private int _port;
        private IPAddress _ip;
        private string _password;
        private int _timeout;

        public RConServerThread(ICoreServerAPI api, IPAddress ip, int port, string password, int timeout) {
            Api = api;
            Logger = api.Logger;
            _ip = ip;
            _port = port;
            _password = password;
            _timeout = timeout;
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
                    Socket socket = await _server.AcceptSocketAsync(token);
                    Logger.Notification("RCon connexion received");
                    tasks.Add(Task.Run(() => { HandleSocket(socket, Api, _password, _timeout, token); }, token));
                    var templ = new List<Task>();
                    //we keep up with the tasks we start. the list is cleaned up on every connexion.
                    //in some cases, this will cause the list to be large and remain so but in typical usage, only one or two tasks will linger.
                    //the impact of this is unclear to me but doesn't seem to be significant.
                    foreach (var task in tasks) {
                        if (task.IsCompleted) {
                            task.Dispose();
                        }
                        else {
                            templ.Add(task);
                        }
                    }
                    tasks = templ;
                    token.ThrowIfCancellationRequested();
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

        internal static void HandleSocket(Socket socket, ICoreServerAPI api, string password, int timeout, CancellationToken token) {
            socket.ReceiveTimeout = (60000 * timeout); //timeout is the amount of minutes we're waiting for
            api.Logger.Notification("RCon socket thread started");
            Byte[] buf = null!;
            int rps = 0;
            List<RCONPacket> rpl = new List<RCONPacket>();
            bool authentified = false;
            try {
                while (!token.IsCancellationRequested) {
                    buf = new Byte[4096];
                    socket.Receive(buf);
                    if (BitConverter.ToInt32(buf[0..4]) == 0) {
                        //if size is 0, it is likely that the connexion timed out or the client sent a wrong packet.
                        //simply proceeding to killing the connexion is a simple, in spec, way to respond.
                        api.Logger.Notification("RCon connexion dropped");
                        break;
                    }
                    RCONPacket packet = new RCONPacket(buf);
                    if (packet.Type == 3) { //authentication request
                        rpl.Clear();
                        authentified = packet.Body == password;
                        RCONPacket resp = new RCONPacket(packet.Id, 0, "");
                        rpl.Add(resp);
                        var data = resp.Serialize();
                        socket.Send(data);
                        if (authentified) {
                            resp = new RCONPacket(packet.Id, 2, "");
                        }
                        else {
                            resp = new RCONPacket(-1, 2, "");
                        }
                        rpl.Add(resp);
                        data = resp.Serialize();
                        socket.Send(data);
                        if (!authentified) break;
                    }
                    else if (!authentified) { //unauthentified request which isn't for authentication
                        RCONPacket resp = new RCONPacket(-1, 2, "");
                        socket.Send(resp.Serialize());
                        break;
                    }
                    else if (packet.Type == 2) { //command execution request
                        rps = 0;
                        rpl.Clear();
                        if (packet.Body == "") {
                            socket.Send(new RCONPacket(packet.Id, 0, "").Serialize());
                            continue;
                        }
                        string[] data = packet.Body.Split();
                        CmdArgs args = null!;
                        if (data.Length == 1) {
                            args = new CmdArgs();
                        }
                        else {
                            args = new CmdArgs(data[1..(data.Length)]);
                        }
                        api.Logger.Notification("Handling RCon Command /{0} {1}", new object[] {data[0], string.Join(' ', data[1..(data.Length)])}); //no idea why this works but it does
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
                                (TextCommandResult result)=>{
                                if (result.StatusMessage.Length > 4083) {
                                    for (int i = 0; i < ((int)result.StatusMessage.Length / 4083); i++) {
                                        if (i == (int)(result.StatusMessage.Length / 4083)) {
                                            rpl.Add(new RCONPacket(packet.Id, 0, result.StatusMessage[(i * 4083)..(result.StatusMessage.Length)]));
                                        }
                                        else {
                                            rpl.Add(new RCONPacket(packet.Id, 0, result.StatusMessage[(i * 4083)..]));
                                        }
                                    }
                                }
                                else {
                                    rpl.Add(new RCONPacket(packet.Id, 0, result.StatusMessage));
                                }
                                });
                        socket.Send(rpl[0].Serialize());
                    }
                    else if (packet.Type == 0) { //get more data request (according to spec)
                        if (rps >= (rpl.Count - 1) || rpl.Count == 0) {
                            var resp = new RCONPacket();
                            resp.Id = packet.Id;
                            var message = resp.Serialize();
                            socket.Send(message);
                            if (rps > 0 || rpl.Count > 0) {
                                rps = 0;
                                rpl.Clear();
                            }
                        }
                        else {
                            var message = rpl[rps + 1].Serialize();
                            socket.Send(message);
                            rps++;
                        }
                    }
                }
                rpl.Clear();
                api.Logger.Notification("RCon socket closed");
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
            }
            catch (Exception e) {
                if (e is OperationCanceledException) {
                    api.Logger.Notification("Shutting down RCon connexion"); //we don't really care about a disconnect that doesn't happen because of cancellation
                }
                rpl.Clear();
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
            }
            finally {
                socket.Dispose();
            }
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
                rcst = new RConServerThread(api, IPAddress.Any, config.Port, config.Password, config.Timeout);
            }
            else {
                rcst = new RConServerThread(api, IPAddress.Parse(config.IP), config.Port, config.Password, config.Timeout);
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

