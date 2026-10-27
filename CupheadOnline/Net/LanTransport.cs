using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace CupheadOnline.Net
{
    /// <summary>
    /// LAN / direct-IP fallback for SteamNetManager. Online play goes through
    /// Steam lobbies and Steam P2P (Valve's relay); this exists for players on
    /// the same network who can't use Steam matchmaking, and for the same-PC
    /// lab, where two game copies share one Steam account and Steam P2P cannot
    /// connect an account to itself.
    ///
    /// The host listens on TCP; every packet is a length-prefixed frame, so
    /// "unreliable" sends are simply delivered reliably and in order. Each LAN
    /// peer gets a synthetic CSteamID so the rest of the session code (which
    /// keys peers by Steam ID) works unchanged. A closed socket is reported as
    /// a Disconnect packet from that peer.
    /// </summary>
    internal static class LanTransport
    {
        public const int DefaultPort = 27140;
        const int MaxFrame = 1 << 20;

        // Individual-account Steam IDs in universe 1 that no real account uses
        // (account IDs 0x7F000000+), so they never collide with a lobby member.
        const ulong FakeIdBase = 0x0110000100000000UL | 0x7F000000UL;
        public static readonly CSteamID HostId = new CSteamID(FakeIdBase);

        sealed class Conn
        {
            public Socket Socket;
            public CSteamID Id;
            public byte[] Buf = new byte[64 * 1024];
            public int Len;
            public bool Closed;
        }

        static Socket _listener;
        static readonly List<Conn> _conns = new List<Conn>(4);
        static readonly Queue<CSteamID> _accepted = new Queue<CSteamID>();
        static uint _nextGuest = 1;

        public static bool Active => _listener != null || _conns.Count > 0;
        public static bool IsListening => _listener != null;
        public static int Port { get; private set; }
        public static string RemoteAddress { get; private set; }

        public static bool IsLanId(CSteamID id) =>
            (id.m_SteamID & 0xFFFFFFFFFF000000UL) == (FakeIdBase & 0xFFFFFFFFFF000000UL);

        public static string Describe(CSteamID id) =>
            id == HostId ? "LAN host" : "LAN player " + (id.m_SteamID - FakeIdBase);

        public static bool Listen(int port, out string error)
        {
            Stop();
            error = null;
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(IPAddress.Any, port));
                s.Listen(4);
                s.Blocking = false;
                _listener = s;
                Port = port;
                Plugin.Log.LogInfo("[LAN] Listening on TCP " + port + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not open LAN port " + port + ": " + ex.Message;
                Plugin.Log.LogWarning("[LAN] " + error);
                return false;
            }
        }

        public static bool Connect(string address, int port, out string error)
        {
            Stop();
            error = null;
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(address, out ip))
                {
                    var hosts = Dns.GetHostAddresses(address);
                    ip = Array.Find(hosts, a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) throw new Exception("no IPv4 address for " + address);
                }
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var ar = s.BeginConnect(new IPEndPoint(ip, port), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000) || !s.Connected)
                {
                    s.Close();
                    throw new Exception("no answer from " + address + ":" + port);
                }
                s.EndConnect(ar);
                s.NoDelay = true;
                s.Blocking = false;
                _conns.Add(new Conn { Socket = s, Id = HostId });
                Port = port;
                RemoteAddress = address;
                Plugin.Log.LogInfo("[LAN] Connected to " + address + ":" + port + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not reach " + address + ":" + port + " (" + ex.Message + ").";
                Plugin.Log.LogWarning("[LAN] " + error);
                return false;
            }
        }

        /// <summary>Accepts pending connections; returns the new peers' IDs.</summary>
        public static bool TryAccept(out CSteamID peer)
        {
            peer = CSteamID.Nil;
            if (_listener != null)
            {
                try
                {
                    while (_listener.Poll(0, SelectMode.SelectRead))
                    {
                        var s = _listener.Accept();
                        s.NoDelay = true;
                        s.Blocking = false;
                        var id = new CSteamID(FakeIdBase + _nextGuest++);
                        _conns.Add(new Conn { Socket = s, Id = id });
                        _accepted.Enqueue(id);
                        Plugin.Log.LogInfo("[LAN] " + Describe(id) + " connected from " + s.RemoteEndPoint + ".");
                    }
                }
                catch (SocketException) { }
            }
            if (_accepted.Count == 0)
                return false;
            peer = _accepted.Dequeue();
            return true;
        }

        public static void Send(CSteamID target, byte[] data, int length)
        {
            var c = Find(target);
            if (c == null || c.Closed) return;
            var frame = new byte[length + 4];
            frame[0] = (byte)length; frame[1] = (byte)(length >> 8); frame[2] = (byte)(length >> 16); frame[3] = (byte)(length >> 24);
            Buffer.BlockCopy(data, 0, frame, 4, length);
            try
            {
                // The socket is non-blocking; loop until the kernel took it all
                // so a frame is never split across a later send.
                int sent = 0;
                c.Socket.Blocking = true;
                while (sent < frame.Length)
                    sent += c.Socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                c.Socket.Blocking = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[LAN] Send to " + Describe(target) + " failed: " + ex.Message);
                c.Closed = true;
            }
        }

        /// <summary>Delivers each complete frame (and a Disconnect for each closed socket).</summary>
        public static void Drain(Action<byte[], int, CSteamID> deliver, int budget)
        {
            for (int i = _conns.Count - 1; i >= 0; i--)
            {
                var c = _conns[i];
                if (!c.Closed)
                {
                    try
                    {
                        while (c.Socket.Available > 0)
                        {
                            if (c.Len == c.Buf.Length)
                                Array.Resize(ref c.Buf, Math.Min(c.Buf.Length * 2, MaxFrame + 4));
                            int n = c.Socket.Receive(c.Buf, c.Len, c.Buf.Length - c.Len, SocketFlags.None);
                            if (n <= 0) { c.Closed = true; break; }
                            c.Len += n;
                        }
                        // A readable socket with nothing to read was closed by the peer.
                        if (!c.Closed && c.Socket.Poll(0, SelectMode.SelectRead) && c.Socket.Available == 0)
                            c.Closed = true;
                    }
                    catch (SocketException) { c.Closed = true; }
                    catch (ObjectDisposedException) { c.Closed = true; }
                }

                int off = 0;
                while (budget > 0 && c.Len - off >= 4)
                {
                    int len = c.Buf[off] | (c.Buf[off + 1] << 8) | (c.Buf[off + 2] << 16) | (c.Buf[off + 3] << 24);
                    if (len < 0 || len > MaxFrame) { c.Closed = true; c.Len = 0; off = 0; break; }
                    if (c.Len - off - 4 < len) break;
                    var pkt = new byte[len];
                    Buffer.BlockCopy(c.Buf, off + 4, pkt, 0, len);
                    off += 4 + len;
                    budget--;
                    if (len > 0) deliver(pkt, len, c.Id);
                }
                if (off > 0)
                {
                    Buffer.BlockCopy(c.Buf, off, c.Buf, 0, c.Len - off);
                    c.Len -= off;
                }

                if (c.Closed && c.Len < 4)
                {
                    try { c.Socket.Close(); } catch { }
                    _conns.RemoveAt(i);
                    Plugin.Log.LogInfo("[LAN] " + Describe(c.Id) + " disconnected.");
                    deliver(new[] { (byte)PacketType.Disconnect }, 1, c.Id);
                }
            }
        }

        public static void Close(CSteamID id)
        {
            var c = Find(id);
            if (c == null) return;
            try { c.Socket.Close(); } catch { }
            _conns.Remove(c);
        }

        public static void Stop()
        {
            if (_listener != null)
            {
                try { _listener.Close(); } catch { }
                _listener = null;
            }
            foreach (var c in _conns)
                try { c.Socket.Close(); } catch { }
            _conns.Clear();
            _accepted.Clear();
            _nextGuest = 1;
        }

        static Conn Find(CSteamID id)
        {
            for (int i = 0; i < _conns.Count; i++)
                if (_conns[i].Id == id) return _conns[i];
            return null;
        }

        /// <summary>This PC's LAN IPv4 addresses, for the host's status line.</summary>
        public static string LocalAddresses()
        {
            try
            {
                var list = new List<string>();
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        list.Add(a.ToString());
                return list.Count == 0 ? "127.0.0.1" : string.Join(", ", list.ToArray());
            }
            catch { return "127.0.0.1"; }
        }
    }
}
