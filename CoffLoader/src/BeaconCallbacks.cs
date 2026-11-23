using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CoffLoader
{
    // A file a BOF sent back through BeaconOutput (CALLBACK_FILE* or CALLBACK_SCREENSHOT).
    public sealed class BofFile
    {
        public string Name;
        public string Kind;          // "file" or "screenshot"
        public byte[] Data;
        public bool Partial;         // closed short of its declared size, or never closed
        public long DeclaredSize;    // -1 when the callback carries no declared size
        public int Session = -1;     // screenshot only
        public string User;          // screenshot only
        public string Title;         // screenshot only
    }

    // Decodes Cobalt Strike file and screenshot callbacks into whole files.
    // Pure managed code over byte[], so it can be unit tested off Windows.
    // Every byte here is BOF-controlled: malformed input yields a note, never an exception.
    public sealed class BofFileCollector
    {
        public const int CALLBACK_FILE = 0x02;
        public const int CALLBACK_SCREENSHOT = 0x03;
        public const int CALLBACK_FILE_WRITE = 0x08;
        public const int CALLBACK_FILE_CLOSE = 0x09;

        public const long DefaultMaxTotalBytes = 100L * 1024 * 1024;

        private sealed class PendingFile
        {
            public string Name;
            public long DeclaredSize;
            public MemoryStream Buffer = new MemoryStream();
        }

        private readonly long maxTotalBytes;
        private long totalBytes;
        private readonly Dictionary<uint, PendingFile> open = new Dictionary<uint, PendingFile>();
        private readonly HashSet<uint> dropped = new HashSet<uint>();
        private readonly List<BofFile> finished = new List<BofFile>();

        public BofFileCollector() : this(DefaultMaxTotalBytes) { }

        public BofFileCollector(long maxTotalBytes)
        {
            this.maxTotalBytes = maxTotalBytes;
        }

        public static bool IsFileCallback(int type)
        {
            return type == CALLBACK_FILE || type == CALLBACK_SCREENSHOT
                || type == CALLBACK_FILE_WRITE || type == CALLBACK_FILE_CLOSE;
        }

        // Handles one callback. Returns a note for the operator (malformed input,
        // cap exceeded), or null when there is nothing to say.
        public string Handle(int type, byte[] data)
        {
            if (data == null)
                return null;
            switch (type)
            {
                case CALLBACK_FILE: return OnOpen(data);
                case CALLBACK_FILE_WRITE: return OnWrite(data);
                case CALLBACK_FILE_CLOSE: return OnClose(data);
                case CALLBACK_SCREENSHOT: return OnScreenshot(data);
                default: return null;
            }
        }

        // Completed files plus any still open (flagged partial). Clears the collector.
        public List<BofFile> Finish()
        {
            foreach (KeyValuePair<uint, PendingFile> entry in open)
                finished.Add(ToBofFile(entry.Value, true));
            open.Clear();
            dropped.Clear();
            List<BofFile> result = new List<BofFile>(finished);
            finished.Clear();
            totalBytes = 0;
            return result;
        }

        private string OnOpen(byte[] data)
        {
            // [fileId BE32][fileSize BE32][name]
            if (data.Length < 8)
                return Note("file header too short (" + data.Length + " bytes)");
            uint id = ReadBE32(data, 0);
            long size = ReadBE32(data, 4);
            string name = DecodeName(data, 8, data.Length - 8);
            if (open.ContainsKey(id) || dropped.Contains(id))
                return Note("file id " + id + " opened twice; ignoring the second open of " + name);
            if (totalBytes + size > maxTotalBytes)
            {
                dropped.Add(id);
                return Note("dropping " + name + ": " + size + " bytes would exceed the "
                    + maxTotalBytes + "-byte limit");
            }
            PendingFile file = new PendingFile();
            file.Name = name;
            file.DeclaredSize = size;
            open[id] = file;
            return null;
        }

        private string OnWrite(byte[] data)
        {
            // [fileId BE32][chunk]
            if (data.Length < 4)
                return Note("file chunk too short (" + data.Length + " bytes)");
            uint id = ReadBE32(data, 0);
            if (dropped.Contains(id))
                return null;
            PendingFile file;
            if (!open.TryGetValue(id, out file))
                return Note("chunk for unknown file id " + id);
            int chunk = data.Length - 4;
            if (totalBytes + chunk > maxTotalBytes)
            {
                open.Remove(id);
                dropped.Add(id);
                totalBytes -= file.Buffer.Length;
                return Note("dropping " + file.Name + ": exceeded the " + maxTotalBytes + "-byte limit");
            }
            file.Buffer.Write(data, 4, chunk);
            totalBytes += chunk;
            return null;
        }

        private string OnClose(byte[] data)
        {
            // [fileId BE32]
            if (data.Length < 4)
                return Note("file close too short (" + data.Length + " bytes)");
            uint id = ReadBE32(data, 0);
            if (dropped.Remove(id))
                return null;
            PendingFile file;
            if (!open.TryGetValue(id, out file))
                return Note("close for unknown file id " + id);
            open.Remove(id);
            bool partial = file.Buffer.Length != file.DeclaredSize;
            finished.Add(ToBofFile(file, partial));
            return partial
                ? Note(file.Name + " closed with " + file.Buffer.Length + " of " + file.DeclaredSize + " bytes")
                : null;
        }

        private string OnScreenshot(byte[] data)
        {
            // [jpgLen LE32][jpg][session LE32][titleLen LE32][title][userLen LE32][user]
            int offset = 0;
            int jpgLen;
            if (!TryReadLE32Length(data, ref offset, out jpgLen))
                return Note("screenshot header malformed (" + data.Length + " bytes)");
            if (totalBytes + jpgLen > maxTotalBytes)
                return Note("dropping screenshot: " + jpgLen + " bytes would exceed the "
                    + maxTotalBytes + "-byte limit");
            byte[] jpg = new byte[jpgLen];
            Buffer.BlockCopy(data, offset, jpg, 0, jpgLen);
            offset += jpgLen;

            BofFile shot = new BofFile();
            shot.Name = "screenshot.jpg";
            shot.Kind = "screenshot";
            shot.Data = jpg;
            shot.DeclaredSize = jpgLen;

            // The trailer is metadata; keep the image even when it is short or malformed.
            int titleLen, userLen;
            if (offset + 4 <= data.Length)
            {
                shot.Session = (int)ReadLE32(data, offset);
                offset += 4;
                if (TryReadLE32Length(data, ref offset, out titleLen))
                {
                    shot.Title = DecodeName(data, offset, titleLen);
                    offset += titleLen;
                    if (TryReadLE32Length(data, ref offset, out userLen))
                        shot.User = DecodeName(data, offset, userLen);
                }
            }
            totalBytes += jpgLen;
            finished.Add(shot);
            return null;
        }

        private static BofFile ToBofFile(PendingFile pending, bool partial)
        {
            BofFile file = new BofFile();
            file.Name = pending.Name;
            file.Kind = "file";
            file.Data = pending.Buffer.ToArray();
            file.DeclaredSize = pending.DeclaredSize;
            file.Partial = partial;
            return file;
        }

        // Reads a LE32 length at offset and checks that many bytes follow it.
        private static bool TryReadLE32Length(byte[] data, ref int offset, out int length)
        {
            length = 0;
            if (offset + 4 > data.Length)
                return false;
            uint raw = ReadLE32(data, offset);
            if (raw > (uint)(data.Length - offset - 4))
                return false;
            length = (int)raw;
            offset += 4;
            return true;
        }

        private static uint ReadBE32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static uint ReadLE32(byte[] b, int o)
        {
            return b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        }

        private static string DecodeName(byte[] data, int offset, int count)
        {
            // BOFs pass ANSI char* names, some with a trailing NUL.
            while (count > 0 && data[offset + count - 1] == 0)
                count--;
            return Encoding.Default.GetString(data, offset, count);
        }

        private static string Note(string message)
        {
            return "[!] BOF file callback: " + message + "\n";
        }

        // The framed text the Empire server extracts from RunCOFF output. The format
        // is a contract with the server's parser; bump the version tag on any change.
        public const string FrameStart = "[EMPIRE-BOF-FILE v1] ";
        public const string FrameEnd = "[/EMPIRE-BOF-FILE]";

        public static string FormatFrames(IEnumerable<BofFile> files)
        {
            StringBuilder sb = new StringBuilder();
            foreach (BofFile file in files)
            {
                sb.Append('\n').Append(FrameStart).Append('{');
                sb.Append("\"name\":").Append(JsonString(file.Name));
                sb.Append(",\"kind\":").Append(JsonString(file.Kind));
                sb.Append(",\"size\":").Append(file.Data.Length);
                sb.Append(",\"partial\":").Append(file.Partial ? "true" : "false");
                if (file.Kind == "screenshot")
                {
                    sb.Append(",\"session\":").Append(file.Session);
                    sb.Append(",\"user\":").Append(JsonString(file.User));
                    sb.Append(",\"title\":").Append(JsonString(file.Title));
                }
                sb.Append("}\n");
                sb.Append(Convert.ToBase64String(file.Data)).Append('\n');
                sb.Append(FrameEnd).Append('\n');
            }
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            if (s == null)
                return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    // Native side: installed in place of the C BeaconOutput so file and screenshot
    // callbacks reach the collector, while text still goes to the C buffer that
    // BeaconPrintf writes to (which keeps text ordering intact).
    public static unsafe class BeaconOutputHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void BeaconOutputFn(int type, IntPtr data, int len);

        // Static so the GC keeps the delegate alive while native code holds its pointer.
        private static BeaconOutputFn hook = Hook;
        private static BeaconOutputFn cOutput;
        private static BofFileCollector collector = new BofFileCollector();

        public static IntPtr Install(IntPtr cBeaconOutput)
        {
            cOutput = (BeaconOutputFn)Marshal.GetDelegateForFunctionPointer(
                cBeaconOutput, typeof(BeaconOutputFn));
            collector = new BofFileCollector();
            return Marshal.GetFunctionPointerForDelegate(hook);
        }

        public static List<BofFile> TakeFiles()
        {
            return collector.Finish();
        }

        private static void Hook(int type, IntPtr data, int len)
        {
            if (data == IntPtr.Zero || len < 0)
                return;
            if (!BofFileCollector.IsFileCallback(type))
            {
                cOutput(type, data, len);
                return;
            }
            byte[] bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, len);
            string note = collector.Handle(type, bytes);
            if (note != null)
                WriteText(note);
        }

        private static void WriteText(string text)
        {
            byte[] bytes = Encoding.Default.GetBytes(text);
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                cOutput(0x0, buffer, bytes.Length);  // C BeaconOutput copies the bytes
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
