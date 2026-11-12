using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

// Shared wire protocol between TherapistVision (TCP server, therapist's computer) and the
// MusicTherapy VR app (TCP client, headset). THIS FILE MUST BE IDENTICAL IN BOTH PROJECTS.
//
// Framing (unchanged from v1):  [1 byte MessageType][4 bytes payload length, big-endian][payload]
//
// v2 conversation:
//   headset → computer   Hello {app, protocol, device}            (first frame on every connection)
//   computer → headset   HelloReply {accepted, reason}
//   both directions      Ping (empty) every 2 s — any frame counts as "alive"; 8 s of silence = dead
//   computer → headset   SessionStart {SessionInfo JSON}  →  Ack {status: "ok" | "queued", error}
//   computer → headset   ExportRequest                   →  ExportBegin {ready, error, files[]}
//   headset → computer   FileChunk [2 bytes file index][data] ... for every file, in order
//   headset → computer   ExportEnd
//   computer → headset   ExportConfirm {saved, error}     (headset may close only after saved = true)
namespace MusicTherapy.Net
{
    public enum MessageType : byte
    {
        SessionStart = 1,
        Ack = 2,
        ExportRequest = 3,
        // 4 = ExportResponse in protocol v1 (single CSV in one frame) — no longer used.
        Hello = 5,
        HelloReply = 6,
        Ping = 7,
        ExportBegin = 9,
        FileChunk = 10,
        ExportEnd = 11,
        ExportConfirm = 12,
    }

    public static class Protocol
    {
        public const int Version = 2;
        public const string AppId = "MusicTherapy";

        public const int DefaultTcpPort = 8080;
        public const int DiscoveryPort = 8081;
        public const string DiscoveryRequestMagic = "MUSICTHERAPY_DISCOVERY_REQUEST";
        public const string DiscoveryResponsePrefix = "MUSICTHERAPY_DISCOVERY_RESPONSE:";

        public const int HeartbeatIntervalMs = 2000;
        public const int ConnectionTimeoutMs = 8000;   // no frame at all for this long → connection is dead
        public const int HandshakeTimeoutMs = 4000;
        public const int ChunkSize = 256 * 1024;
        public const int MaxPayload = 8 * 1024 * 1024;
    }

    [Serializable]
    public class HelloMessage
    {
        public string app;
        public int protocol;
        public string device;
        public string appVersion;
    }

    [Serializable]
    public class HelloReply
    {
        public bool accepted;
        public string reason;
        public string server;
    }

    [Serializable]
    public class AckMessage
    {
        public string status;
        public string error;
    }

    [Serializable]
    public class ExportFileInfo
    {
        public string name;
        public long size;
    }

    [Serializable]
    public class ExportManifest
    {
        public bool ready;
        public string error;
        public ExportFileInfo[] files;
    }

    [Serializable]
    public class ExportConfirmation
    {
        public bool saved;
        public string error;
    }

    // What the headset offers when the therapist asks for results (headset side only).
    public class ExportPlan
    {
        public bool Ready;
        public string Error;
        public List<string> Paths = new List<string>();
    }

    // Lets the protocol code stay free of UnityEngine: Unity passes JsonUtility, tests pass
    // System.Text.Json.
    public interface IJsonSerializer
    {
        string ToJson(object value);
        T FromJson<T>(string json);
    }

    public struct Frame
    {
        public MessageType Type;
        public byte[] Payload;

        public string Text => Payload == null || Payload.Length == 0 ? "" : Encoding.UTF8.GetString(Payload);
    }

    public static class Framing
    {
        public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? "");

        // Header and payload go out in ONE Write call so frames from two threads (heartbeat
        // and request handling) can never interleave — callers still hold a write lock.
        public static void WriteFrame(Stream stream, MessageType type, byte[] payload, int count = -1)
        {
            if (payload == null) payload = Array.Empty<byte>();
            if (count < 0) count = payload.Length;

            var buffer = new byte[5 + count];
            buffer[0] = (byte)type;
            buffer[1] = (byte)(count >> 24);
            buffer[2] = (byte)(count >> 16);
            buffer[3] = (byte)(count >> 8);
            buffer[4] = (byte)count;
            Buffer.BlockCopy(payload, 0, buffer, 5, count);
            stream.Write(buffer, 0, buffer.Length);
            stream.Flush();
        }

        public static Frame ReadFrame(Stream stream)
        {
            byte[] header = ReadExact(stream, 5);
            int length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
            if (length < 0 || length > Protocol.MaxPayload)
                throw new IOException($"Invalid frame length: {length}");

            return new Frame
            {
                Type = (MessageType)header[0],
                Payload = length == 0 ? Array.Empty<byte>() : ReadExact(stream, length),
            };
        }

        public static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("Connection closed.");
                offset += read;
            }
            return buffer;
        }

        public static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        public static int ReadUInt16(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        public static string DescribeEndpoint(EndPoint endPoint) => endPoint?.ToString() ?? "?";
    }
}
