// Decoder tests for BeaconCallbacks.cs. Self-contained runner (no NUnit) so it builds
// with plain mcs and runs under mono off Windows: Tests/run_tests.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CoffLoader;

public static class BeaconCallbacksTests
{
    const int FILE = BofFileCollector.CALLBACK_FILE;
    const int WRITE = BofFileCollector.CALLBACK_FILE_WRITE;
    const int CLOSE = BofFileCollector.CALLBACK_FILE_CLOSE;
    const int SHOT = BofFileCollector.CALLBACK_SCREENSHOT;

    // ---- packet builders, mirroring the BOF sources byte for byte ----

    static byte[] BE32(uint v) { return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }
    static byte[] LE32(uint v) { return new byte[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }; }

    static byte[] Cat(params byte[][] parts)
    {
        MemoryStream ms = new MemoryStream();
        foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

    // CredBandit/ScreenshotBOF/nanodump downloadFile(): [id BE][size BE][name]
    static byte[] Open(uint id, uint size, string name) { return Cat(BE32(id), BE32(size), Ascii(name)); }
    static byte[] Chunk(uint id, byte[] data) { return Cat(BE32(id), data); }
    static byte[] Close(uint id) { return BE32(id); }

    // ScreenshotBOF downloadScreenshot(): [jpgLen LE][jpg][session LE][titleLen LE][title][userLen LE][user]
    static byte[] Screenshot(byte[] jpg, uint session, string title, string user)
    {
        return Cat(LE32((uint)jpg.Length), jpg, LE32(session),
            LE32((uint)title.Length), Ascii(title), LE32((uint)user.Length), Ascii(user));
    }

    static byte[] FakeJpeg(int size)
    {
        byte[] b = new byte[size];
        new Random(size).NextBytes(b);
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0; b[4] = 0x00; b[5] = 0x10;  // JFIF header, NUL at byte 4
        return b;
    }

    // ---- tests ----

    static void ScreenshotBofDownloadSingleChunk()
    {
        // ScreenshotBOF: fileId = rand() <= 0x7FFF, so the header starts 00 00 (the truncation trigger)
        BofFileCollector c = new BofFileCollector();
        byte[] jpg = FakeJpeg(50000);
        IsNull(c.Handle(FILE, Open(0x1234, (uint)jpg.Length, "screenshot.jpg")));
        IsNull(c.Handle(WRITE, Chunk(0x1234, jpg)));
        IsNull(c.Handle(CLOSE, Close(0x1234)));
        List<BofFile> files = c.Finish();
        Eq(1, files.Count);
        Eq("screenshot.jpg", files[0].Name);
        Eq("file", files[0].Kind);
        False(files[0].Partial);
        Bytes(jpg, files[0].Data);
    }

    static void MultiChunkDownload()
    {
        // ScreenshotBOF splits at 900 KiB; nanodump at its chunksize. Reassembly must be in order.
        BofFileCollector c = new BofFileCollector();
        byte[] data = FakeJpeg(900 * 1024 * 2 + 777);
        uint id = 0xA1B2C3D4;  // nanodump sets high bits
        c.Handle(FILE, Open(id, (uint)data.Length, "dump.dmp"));
        int chunk = 900 * 1024;
        for (int i = 0; i < data.Length; i += chunk)
        {
            int n = Math.Min(chunk, data.Length - i);
            byte[] part = new byte[n];
            Buffer.BlockCopy(data, i, part, 0, n);
            IsNull(c.Handle(WRITE, Chunk(id, part)));
        }
        IsNull(c.Handle(CLOSE, Close(id)));
        List<BofFile> files = c.Finish();
        Eq(1, files.Count);
        False(files[0].Partial);
        Bytes(data, files[0].Data);
    }

    static void InterleavedFiles()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(1, 3, "a.bin"));
        c.Handle(FILE, Open(2, 3, "b.bin"));
        c.Handle(WRITE, Chunk(2, Ascii("BBB")));
        c.Handle(WRITE, Chunk(1, Ascii("AAA")));
        c.Handle(CLOSE, Close(2));
        c.Handle(CLOSE, Close(1));
        List<BofFile> files = c.Finish();
        Eq(2, files.Count);
        Eq("b.bin", files[0].Name);  // close order
        Bytes(Ascii("BBB"), files[0].Data);
        Bytes(Ascii("AAA"), files[1].Data);
    }

    static void ZeroLengthFile()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(5, 0, "empty.txt"));
        IsNull(c.Handle(CLOSE, Close(5)));
        List<BofFile> files = c.Finish();
        Eq(1, files.Count);
        False(files[0].Partial);
        Eq(0, files[0].Data.Length);
    }

    static void TrailingNulTrimmedFromName()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Cat(BE32(7), BE32(1), Ascii("x.pfx"), new byte[] { 0 }));
        c.Handle(WRITE, Chunk(7, new byte[] { 0x42 }));
        c.Handle(CLOSE, Close(7));
        Eq("x.pfx", c.Finish()[0].Name);
    }

    static void ShortCloseIsPartialWithNote()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(9, 10, "short.bin"));
        c.Handle(WRITE, Chunk(9, Ascii("abc")));
        Contains("3 of 10 bytes", c.Handle(CLOSE, Close(9)));
        BofFile f = c.Finish()[0];
        True(f.Partial);
        Bytes(Ascii("abc"), f.Data);
    }

    static void UnclosedFileFlushedAsPartial()
    {
        // nanodump can stop mid-dump; what arrived is still worth returning.
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(11, 100, "dump.dmp"));
        c.Handle(WRITE, Chunk(11, Ascii("MDMP")));
        List<BofFile> files = c.Finish();
        Eq(1, files.Count);
        True(files[0].Partial);
        Bytes(Ascii("MDMP"), files[0].Data);
    }

    static void FinishResetsState()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(1, 1, "a"));
        c.Handle(WRITE, Chunk(1, Ascii("A")));
        c.Handle(CLOSE, Close(1));
        Eq(1, c.Finish().Count);
        Eq(0, c.Finish().Count);
        IsNull(c.Handle(FILE, Open(1, 1, "a")));  // same id reusable after Finish
    }

    static void ScreenshotBlob()
    {
        BofFileCollector c = new BofFileCollector();
        byte[] jpg = FakeJpeg(807663);  // the size task 8 leaked (EF 52 0C)
        byte[] blob = Screenshot(jpg, 1, "Screenshot", "Coin");
        Eq((byte)0xEF, blob[0]); Eq((byte)0x52, blob[1]); Eq((byte)0x0C, blob[2]); Eq((byte)0x00, blob[3]);
        IsNull(c.Handle(SHOT, blob));
        BofFile f = c.Finish()[0];
        Eq("screenshot", f.Kind);
        Eq(1, f.Session);
        Eq("Screenshot", f.Title);
        Eq("Coin", f.User);
        False(f.Partial);
        Bytes(jpg, f.Data);
    }

    static void ScreenshotKeepsImageWhenTrailerTruncated()
    {
        BofFileCollector c = new BofFileCollector();
        byte[] jpg = FakeJpeg(100);
        IsNull(c.Handle(SHOT, Cat(LE32(100), jpg, LE32(2))));  // session only, no title/user
        BofFile f = c.Finish()[0];
        Bytes(jpg, f.Data);
        Eq(2, f.Session);
        IsNull(f.User);
    }

    static void ScreenshotLengthBeyondBufferRejected()
    {
        BofFileCollector c = new BofFileCollector();
        Contains("malformed", c.Handle(SHOT, Cat(LE32(0xFFFFFFF0), Ascii("xx"))));
        Contains("malformed", c.Handle(SHOT, new byte[] { 1, 2 }));
        Eq(0, c.Finish().Count);
    }

    static void MalformedFileCallbacksNoteAndDoNotThrow()
    {
        BofFileCollector c = new BofFileCollector();
        Contains("header too short", c.Handle(FILE, new byte[] { 0, 0, 1 }));
        Contains("chunk too short", c.Handle(WRITE, new byte[] { 0 }));
        Contains("close too short", c.Handle(CLOSE, new byte[0]));
        Contains("unknown file id 77", c.Handle(WRITE, Chunk(77, Ascii("x"))));
        Contains("unknown file id 78", c.Handle(CLOSE, Close(78)));
        IsNull(c.Handle(FILE, null));
        IsNull(c.Handle(0x00, Ascii("text")));  // not a file callback
        Eq(0, c.Finish().Count);
    }

    static void DuplicateOpenIgnored()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(FILE, Open(3, 1, "first"));
        Contains("opened twice", c.Handle(FILE, Open(3, 1, "second")));
        c.Handle(WRITE, Chunk(3, Ascii("1")));
        c.Handle(CLOSE, Close(3));
        Eq("first", c.Finish()[0].Name);
    }

    static void DefaultCapFitsDefaultListener()
    {
        // Sharpire base64s the result again, so a file costs ~16/9 of its size on
        // the wire; 16 MiB of files is ~28.4 MiB, under the 32 MiB MaxContentLength.
        Eq(16L * 1024 * 1024, BofFileCollector.DefaultMaxTotalBytes);
        BofFileCollector c = new BofFileCollector();
        string note = c.Handle(FILE, Open(1, 17 * 1024 * 1024, "big.dmp"));
        Contains("MaxContentLength", note);
        Contains("save it on the target", note);
    }

    static void CapRejectsOversizedDeclaredFile()
    {
        BofFileCollector c = new BofFileCollector(1000);
        Contains("would exceed the 1000-byte limit", c.Handle(FILE, Open(1, 5000, "big.dmp")));
        IsNull(c.Handle(WRITE, Chunk(1, Ascii("ignored"))));  // dropped id: silent
        IsNull(c.Handle(CLOSE, Close(1)));
        Eq(0, c.Finish().Count);
    }

    static void CapEnforcedOnChunksWhenSizeLies()
    {
        BofFileCollector c = new BofFileCollector(10);
        c.Handle(FILE, Open(1, 4, "liar.bin"));  // declares 4, sends 12
        IsNull(c.Handle(WRITE, Chunk(1, Ascii("12345678"))));
        Contains("exceeded the 10-byte limit", c.Handle(WRITE, Chunk(1, Ascii("9abc"))));
        IsNull(c.Handle(CLOSE, Close(1)));
        Eq(0, c.Finish().Count);
        // the dropped file's bytes no longer count against the cap
        c.Handle(FILE, Open(2, 8, "ok.bin"));
        IsNull(c.Handle(WRITE, Chunk(2, Ascii("abcdefgh"))));
        IsNull(c.Handle(CLOSE, Close(2)));
        Eq(1, c.Finish().Count);
    }

    static void CapCountsScreenshots()
    {
        BofFileCollector c = new BofFileCollector(150);
        IsNull(c.Handle(SHOT, Screenshot(FakeJpeg(100), 1, "t", "u")));
        Contains("dropping screenshot", c.Handle(SHOT, Screenshot(FakeJpeg(100), 1, "t", "u")));
        Eq(1, c.Finish().Count);
    }

    static void FrameFormat()
    {
        BofFile f = new BofFile();
        f.Name = "a\"b\\cé.jpg";
        f.Kind = "file";
        f.Data = new byte[] { 0, 1, 2, 0xFF };
        f.Partial = true;
        string s = BofFileCollector.FormatFrames(new BofFile[] { f });
        Eq("\n[EMPIRE-BOF-FILE v1] {\"name\":\"a\\\"b\\\\c\\u00e9.jpg\",\"kind\":\"file\",\"size\":4,\"partial\":true}\n"
            + "AAEC/w==\n[/EMPIRE-BOF-FILE]\n", s);
        Eq("", BofFileCollector.FormatFrames(new BofFile[0]));
    }

    static void ScreenshotFrameCarriesMetadata()
    {
        BofFileCollector c = new BofFileCollector();
        c.Handle(SHOT, Screenshot(new byte[] { 9 }, 3, "Screenshot", "Coin"));
        string s = BofFileCollector.FormatFrames(c.Finish());
        Contains("{\"name\":\"screenshot.jpg\",\"kind\":\"screenshot\",\"size\":1,\"partial\":false,"
            + "\"session\":3,\"user\":\"Coin\",\"title\":\"Screenshot\"}\nCQ==\n", s);
    }

    // ---- hook routing, called through function pointers ----
    // Note: the runtime hands back the original delegate when a pointer made by
    // GetFunctionPointerForDelegate is turned back into a delegate, so these tests
    // check routing and buffer handling, not the native calling convention. That
    // part needs a real BOF run on Windows.

    static StringBuilder fakeCText = new StringBuilder();
    static List<int> fakeCTypes = new List<int>();
    static BeaconOutputHook.BeaconOutputFn fakeC = FakeCBeaconOutput;  // static: keep alive

    static void FakeCBeaconOutput(int type, IntPtr data, int len)
    {
        byte[] b = new byte[len];
        Marshal.Copy(data, b, 0, len);
        fakeCTypes.Add(type);
        fakeCText.Append(Encoding.Default.GetString(b));
    }

    static void Call(BeaconOutputHook.BeaconOutputFn fn, int type, byte[] bytes)
    {
        IntPtr p = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        fn(type, p, bytes.Length);
        Marshal.FreeHGlobal(p);
    }

    static void HookRoutesFilesAndForwardsText()
    {
        fakeCText.Length = 0; fakeCTypes.Clear();
        IntPtr hookPtr = BeaconOutputHook.Install(Marshal.GetFunctionPointerForDelegate(fakeC));
        BeaconOutputHook.BeaconOutputFn hook = (BeaconOutputHook.BeaconOutputFn)Marshal.GetDelegateForFunctionPointer(hookPtr, typeof(BeaconOutputHook.BeaconOutputFn));

        Call(hook, 0x00, Ascii("Downloading JPEG\n"));
        Call(hook, FILE, Open(0x10, 3, "s.jpg"));
        Call(hook, WRITE, Chunk(0x10, new byte[] { 0xFF, 0x00, 0xD8 }));
        Call(hook, CLOSE, Close(0x10));
        Call(hook, WRITE, Chunk(0x99, Ascii("x")));  // note goes to the text stream
        Call(hook, 0x0d, Ascii("err\n"));
        hook(0x00, IntPtr.Zero, 5);  // NULL data: ignored

        Eq("Downloading JPEG\n[!] BOF file callback: chunk for unknown file id 153\nerr\n", fakeCText.ToString());
        Eq("0,0,13", string.Join(",", fakeCTypes.ConvertAll(t => t.ToString()).ToArray()));  // file bytes never reach C
        List<BofFile> files = BeaconOutputHook.TakeFiles();
        Eq(1, files.Count);
        Bytes(new byte[] { 0xFF, 0x00, 0xD8 }, files[0].Data);
        Eq(0, BeaconOutputHook.TakeFiles().Count);
    }

    static void InstallResetsCollector()
    {
        IntPtr hookPtr = BeaconOutputHook.Install(Marshal.GetFunctionPointerForDelegate(fakeC));
        BeaconOutputHook.BeaconOutputFn hook = (BeaconOutputHook.BeaconOutputFn)Marshal.GetDelegateForFunctionPointer(hookPtr, typeof(BeaconOutputHook.BeaconOutputFn));
        Call(hook, FILE, Open(1, 1, "left-open"));
        BeaconOutputHook.Install(Marshal.GetFunctionPointerForDelegate(fakeC));  // next RunCoff
        Eq(0, BeaconOutputHook.TakeFiles().Count);
    }

    // ---- runner and asserts ----

    public static int Main()
    {
        int failed = 0, passed = 0;
        foreach (MethodInfo m in typeof(BeaconCallbacksTests).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0 || m.Name == "Main")
                continue;
            if (!char.IsUpper(m.Name[0]) || m.Name.StartsWith("Fake"))
                continue;
            try { m.Invoke(null, null); passed++; Console.WriteLine("PASS " + m.Name); }
            catch (TargetInvocationException e) { failed++; Console.WriteLine("FAIL " + m.Name + ": " + e.InnerException.Message); }
        }
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("expected <" + expected + "> got <" + actual + ">");
    }
    static void True(bool b) { if (!b) throw new Exception("expected true"); }
    static void False(bool b) { if (b) throw new Exception("expected false"); }
    static void IsNull(object o) { if (o != null) throw new Exception("expected null, got <" + o + ">"); }
    static void Contains(string needle, string hay)
    {
        if (hay == null || hay.IndexOf(needle, StringComparison.Ordinal) < 0)
            throw new Exception("expected <" + hay + "> to contain <" + needle + ">");
    }
    static void Bytes(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
            throw new Exception("length " + expected.Length + " != " + actual.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new Exception("byte " + i + " differs");
    }
}
