using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GrannyCoop
{
    /// <summary>
    /// Wire format, v1. Every datagram starts with the 4-byte magic, one type
    /// byte and a 4-byte sequence number. Datagrams are small (well under the
    /// 1200-byte MTU budget) and never fragmented at this layer.
    /// </summary>
    internal enum MsgType : byte
    {
        Hello = 1,   // client -> host, repeated until answered
        Welcome = 2, // host -> client
        Reject = 3,  // host -> client
        State = 4,   // both ways, 20 Hz, unreliable by design
        Scene = 5,   // host -> client, scene the host is in
        Bye = 6,     // both ways, best effort on leave
        Granny = 7,  // host -> client, Granny transform + state, 20 Hz
        Door = 8,    // both ways, door state changes, on demand
        Item = 9,    // both ways, inventory have-flags, on change + snapshots
    }

    internal static class Protocol
    {
        public const uint Magic = 0x31435047; // "GCP1" little-endian
        public const byte Version = 1;
        public const int MaxDatagram = 1024;

        // State flags
        public const byte FlagPlayerValid = 1 << 0;
        public const byte FlagPlayerInBed = 1 << 1;

        // Granny flags
        public const byte FlagGrannyValid = 1 << 0;
        public const byte FlagGrannyMoving = 1 << 1;
        public const byte FlagGrannyAttacking = 1 << 2;
        public const byte FlagGrannySeesPlayer = 1 << 3;

        /// <summary>
        /// Canonical fingerprint of a (hash, value) set, for the QA status line:
        /// sorted by hash so discovery order never matters, then mixed with
        /// FNV-1a. Two ends holding the same world state must print the same
        /// string, which makes a soak run a plain diff of the two logs.
        /// </summary>
        public static string Fingerprint(List<KeyValuePair<uint, byte>> pairs)
        {
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));
            uint h = 2166136261u;
            for (int i = 0; i < pairs.Count; i++)
            {
                uint k = pairs[i].Key;
                for (int s = 0; s < 32; s += 8) h = (h ^ ((k >> s) & 0xFF)) * 16777619u;
                h = (h ^ pairs[i].Value) * 16777619u;
            }
            return pairs.Count + ":" + h.ToString("X8");
        }
    }

    internal sealed class MsgWriter : IDisposable
    {
        private readonly MemoryStream _ms = new MemoryStream(128);
        private readonly BinaryWriter _bw;

        public MsgWriter(MsgType type, uint seq)
        {
            _bw = new BinaryWriter(_ms, Encoding.UTF8, true);
            _bw.Write(Protocol.Magic);
            _bw.Write((byte)type);
            _bw.Write(seq);
        }

        public MsgWriter Byte(byte v) { _bw.Write(v); return this; }
        public MsgWriter Float(float v) { _bw.Write(v); return this; }
        public MsgWriter U32(uint v) { _bw.Write(v); return this; }
        public MsgWriter Str(string v) { _bw.Write(v ?? ""); return this; }
        public MsgWriter Bytes(byte[] v) { _bw.Write(v ?? Array.Empty<byte>()); return this; }

        public byte[] ToArray() { _bw.Flush(); return _ms.ToArray(); }

        public void Dispose() { _bw.Dispose(); _ms.Dispose(); }
    }

    internal sealed class MsgReader
    {
        private readonly BinaryReader _br;

        public MsgType Type { get; }
        public uint Seq { get; }
        public bool Valid { get; }

        public MsgReader(byte[] data, int length)
        {
            try
            {
                var ms = new MemoryStream(data, 0, length, false);
                _br = new BinaryReader(ms, Encoding.UTF8);
                Valid = _br.ReadUInt32() == Protocol.Magic;
                Type = (MsgType)_br.ReadByte();
                Seq = _br.ReadUInt32();
            }
            catch
            {
                Valid = false;
            }
        }

        public byte Byte() { try { return _br.ReadByte(); } catch { return 0; } }
        public float Float() { try { return _br.ReadSingle(); } catch { return 0f; } }
        public uint U32() { try { return _br.ReadUInt32(); } catch { return 0u; } }
        public string Str() { try { return _br.ReadString(); } catch { return ""; } }
    }
}
