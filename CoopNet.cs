using System;
using System.Net;
using System.Net.Sockets;

namespace GrannyCoop
{
    internal enum CoopRole
    {
        Off,
        Host,
        Join,
    }

    /// <summary>
    /// Dependency-free UDP session for two players. The host owns the port and
    /// the authoritative world; the client talks to one address. Both sides
    /// send their own player state 20 times a second, unreliable — a lost
    /// position packet is replaced by the next one, never retransmitted.
    /// Keep-alive is implicit in that traffic; five silent seconds drop the peer.
    /// </summary>
    internal sealed class CoopNet : IDisposable
    {
        public const byte HostId = 0;
        public const byte ClientId = 1;

        private const double HelloIntervalSec = 0.5;
        private const double PeerTimeoutSec = 5.0;

        private UdpClient _udp;
        private IPEndPoint _peer;      // host: the client; join: the host
        private IPEndPoint _bindAddr;  // join only, the address to dial
        private DateTime _lastHeardUtc = DateTime.MinValue;
        private DateTime _lastHelloUtc = DateTime.MinValue;
        private uint _seq;

        public CoopRole Role { get; }
        public string LocalName { get; }
        public string RemoteName { get; private set; } = "";
        public bool Connected { get; private set; }
        public string LastError { get; private set; } = "";

        /// <summary>Raised on the main thread from Tick() for State and Scene.</summary>
        public event Action<MsgReader> AppMessage;
        /// <summary>Raised when Connected flips.</summary>
        public event Action<bool> ConnectionChanged;

        public CoopNet(CoopRole role, string localName, string address, int port)
        {
            Role = role;
            LocalName = localName;

            try
            {
                var ip = Resolve(address);
                if (role == CoopRole.Host)
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                }
                else
                {
                    _bindAddr = new IPEndPoint(ip, port);
                    _peer = _bindAddr;
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                }
                _udp.Client.Blocking = false;
                _udp.Client.ReceiveBufferSize = 64 * 1024;
                LoggerUtil.Log($"COOP net {(role == CoopRole.Host ? $"listening on udp/{port}" : $"dialling {_bindAddr}")}");
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                LoggerUtil.Log("COOP net FAILED: " + LastError);
                _udp = null;
            }
        }

        private static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            var addrs = Dns.GetHostAddresses(host);
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            if (addrs.Length > 0) return addrs[0];
            throw new Exception("cannot resolve '" + host + "'");
        }

        public void Tick()
        {
            if (_udp == null) return;

            try
            {
                while (_udp.Available > 0)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref from);
                    Handle(data, data.Length, from);
                }
            }
            catch (SocketException) { /* transient on non-blocking sockets */ }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { LoggerUtil.Log("COOP receive error: " + e.GetType().Name + ": " + e.Message); }

            var now = DateTime.UtcNow;

            if (Connected && (now - _lastHeardUtc).TotalSeconds > PeerTimeoutSec)
            {
                LoggerUtil.Log($"COOP peer '{RemoteName}' timed out after {PeerTimeoutSec:F0}s of silence");
                SetConnected(false);
                RemoteName = "";
                if (Role == CoopRole.Join) _lastHelloUtc = DateTime.MinValue;
            }

            if (Role == CoopRole.Join && !Connected &&
                (now - _lastHelloUtc).TotalSeconds >= HelloIntervalSec)
            {
                _lastHelloUtc = now;
                SendHello();
            }
        }

        private void Handle(byte[] data, int length, IPEndPoint from)
        {
            var r = new MsgReader(data, length);
            if (!r.Valid) return;

            // On the host, only the current peer may talk; before a peer is set,
            // any HELLO claims the slot. On the client, only the host may talk.
            bool fromPeer = _peer != null && SameEndpoint(from, _peer);
            if (!fromPeer)
            {
                if (Role == CoopRole.Join) return;
                if (r.Type != MsgType.Hello) return;
            }

            switch (r.Type)
            {
                case MsgType.Hello:
                    if (Role != CoopRole.Host) return;
                    HandleHello(r, from);
                    return;

                case MsgType.Welcome:
                    if (Role != CoopRole.Join) return;
                    _lastHeardUtc = DateTime.UtcNow;
                    var version = r.Byte();
                    var playerId = r.Byte();
                    RemoteName = r.Str();
                    if (version != Protocol.Version)
                    {
                        LoggerUtil.Log($"COOP host answered with protocol v{version}, we speak v{Protocol.Version}");
                        return;
                    }
                    LoggerUtil.Log($"COOP joined host '{RemoteName}' as player {playerId}");
                    SetConnected(true);
                    return;

                case MsgType.Reject:
                    _lastHeardUtc = DateTime.UtcNow;
                    LastError = r.Str();
                    LoggerUtil.Log("COOP host rejected join: " + LastError);
                    return;

                case MsgType.State:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Scene:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Granny:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Door:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Item:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Catch:
                    _lastHeardUtc = DateTime.UtcNow;
                    AppMessage?.Invoke(r);
                    return;

                case MsgType.Bye:
                    LoggerUtil.Log("COOP peer said goodbye");
                    RemoteName = "";
                    SetConnected(false);
                    return;
            }
        }

        private void HandleHello(MsgReader r, IPEndPoint from)
        {
            var version = r.Byte();
            var name = r.Str();

            if (version != Protocol.Version)
            {
                SendTo(from, new MsgWriter(MsgType.Reject, NextSeq()).Str("protocol v" + Protocol.Version + " required").ToArray());
                return;
            }

            if (Connected && _peer != null && !SameEndpoint(from, _peer) &&
                (DateTime.UtcNow - _lastHeardUtc).TotalSeconds < PeerTimeoutSec)
            {
                SendTo(from, new MsgWriter(MsgType.Reject, NextSeq()).Str("session full").ToArray());
                LoggerUtil.Log($"COOP rejected '{name}' from {from}: session full");
                return;
            }

            bool isNew = !Connected || _peer == null || !SameEndpoint(from, _peer);
            _peer = from;
            _lastHeardUtc = DateTime.UtcNow;
            RemoteName = name;

            SendTo(from, new MsgWriter(MsgType.Welcome, NextSeq())
                .Byte(Protocol.Version)
                .Byte(ClientId)
                .Str(LocalName)
                .ToArray());

            if (isNew)
            {
                LoggerUtil.Log($"COOP client '{name}' joined from {from}");
                SetConnected(true);
            }
        }

        private void SendHello()
        {
            SendTo(_bindAddr, new MsgWriter(MsgType.Hello, NextSeq())
                .Byte(Protocol.Version)
                .Str(LocalName)
                .ToArray());
        }

        public bool SendState(byte flags, float x, float y, float z, float yaw, float pitch)
        {
            if (!Connected || _peer == null) return false;
            var w = new MsgWriter(MsgType.State, NextSeq())
                .Byte(Role == CoopRole.Host ? HostId : ClientId)
                .Byte(flags)
                .Float(x).Float(y).Float(z)
                .Float(yaw).Float(pitch);
            SendTo(_peer, w.ToArray());
            return true;
        }

        public void SendScene(string scene)
        {
            if (Role != CoopRole.Host || !Connected || _peer == null) return;
            SendTo(_peer, new MsgWriter(MsgType.Scene, NextSeq()).Str(scene).ToArray());
        }

        /// <summary>Host only: the authoritative Granny transform, 20 Hz.</summary>
        public bool SendGranny(byte flags, float x, float y, float z, float yaw)
        {
            if (Role != CoopRole.Host || !Connected || _peer == null) return false;
            var w = new MsgWriter(MsgType.Granny, NextSeq())
                .Byte(flags)
                .Float(x).Float(y).Float(z)
                .Float(yaw);
            SendTo(_peer, w.ToArray());
            return true;
        }

        /// <summary>Either side: one door message. The body is built by DoorSync;
        /// this only wraps it in the usual header, so the payload layout stays in
        /// one place.</summary>
        public bool SendDoor(byte[] body)
        {
            if (body == null || body.Length == 0) return false;
            if (!Connected || _peer == null) return false;
            var w = new MsgWriter(MsgType.Door, NextSeq()).Bytes(body);
            SendTo(_peer, w.ToArray());
            return true;
        }

        /// <summary>Either side: one inventory message. Same envelope as
        /// SendDoor; the body layout stays in ItemSync.</summary>
        public bool SendItem(byte[] body)
        {
            if (body == null || body.Length == 0) return false;
            if (!Connected || _peer == null) return false;
            var w = new MsgWriter(MsgType.Item, NextSeq()).Bytes(body);
            SendTo(_peer, w.ToArray());
            return true;
        }

        /// <summary>Either side: this player was caught (alive=0) or is back
        /// (alive=1). cause is one of CatchSync.Cause*; it is only informational
        /// and lets the peer log why the other player dropped.</summary>
        public bool SendCatch(byte alive, byte cause)
        {
            if (!Connected || _peer == null) return false;
            SendTo(_peer, new MsgWriter(MsgType.Catch, NextSeq())
                .Byte(alive).Byte(cause).ToArray());
            return true;
        }

        public void SendBye()
        {
            if (!Connected || _peer == null) return;
            try { SendTo(_peer, new MsgWriter(MsgType.Bye, NextSeq()).ToArray()); } catch { }
        }

        private void SendTo(IPEndPoint to, byte[] data)
        {
            try { _udp?.Send(data, data.Length, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private uint NextSeq() => ++_seq;

        private static bool SameEndpoint(IPEndPoint a, IPEndPoint b)
        {
            return a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address);
        }

        private void SetConnected(bool value)
        {
            if (Connected == value) return;
            Connected = value;
            ConnectionChanged?.Invoke(value);
        }

        public void Dispose()
        {
            try { SendBye(); } catch { }
            try { _udp?.Close(); } catch { }
            _udp = null;
        }
    }

    internal static class LoggerUtil
    {
        public static void Log(string msg) => MelonLoader.MelonLogger.Msg(msg);
    }
}
