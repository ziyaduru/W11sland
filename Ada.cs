// Ada: a tiny "dynamic island" media widget for the Windows desktop (Nord style).
//  - Reads now-playing info from Windows' own media API (SMTC): Spotify, browsers, VLC...
//  - 5-band spectrum equalizer from WASAPI loopback + a hand-written FFT.
//  - Per-app volume (mouse wheel), media controls, jump to the playing app / browser tab.
//  - Pomodoro focus mode (middle click or "Ada.exe --focus").
//  - Draws only when something changes (or while an animation runs): ~0 CPU/GPU when idle.
//  - Lives on the desktop layer (above wallpaper, below normal windows).
// MIT License - see LICENSE.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

static class Program
{
    public const string FocusEventName = "Ada_FocusToggle";

    [STAThread]
    static void Main(string[] args)
    {
        // "Ada.exe --focus" (e.g. from the YASB bar): tell the running Ada to toggle focus mode
        bool focus = Array.IndexOf(args, "--focus") >= 0;
        EventWaitHandle ev;
        if (focus && EventWaitHandle.TryOpenExisting(FocusEventName, out ev)) { ev.Set(); return; }

        bool created;
        using (new Mutex(true, "Ada_MediaIsland_SingleInstance", out created))
        {
            if (!created) return;
            SetProcessDPIAware();
            Application.Run(new AdaForm(focus));
        }
    }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

// ---- UI text: Turkish when Windows is in Turkish, English otherwise ----
static class UiText
{
    // ADA_LANG=en / ADA_LANG=tr overrides the Windows display language
    static readonly bool Tr = (Environment.GetEnvironmentVariable("ADA_LANG") ??
                               System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "tr";
    public static string Min(int n)       { return n + (Tr ? " dk" : " min"); }
    public static string Volume(int pct)  { return Tr ? "Ses %" + pct : "Volume " + pct + "%"; }
    public static string Muted            { get { return Tr ? "Sessiz" : "Muted"; } }
    public static string BreakTime(int n) { return (Tr ? "Mola zamanı · " : "Break time · ") + Min(n); }
    public static string BreakOver        { get { return Tr ? "Mola bitti" : "Break is over"; } }
}

// ---- what is playing right now (immutable snapshot) ----
class MediaState
{
    public bool HasSession, Playing;
    public string Title = "", Artist = "";
    public string SourceApp = "";            // AUMID of the playing app, e.g. "Spotify.exe"
    public double PositionS, DurationS;      // seconds
    public DateTime Stamp;                   // when PositionS was measured
    public byte[] Thumb;                     // cover image bytes (only when track changes)
    public string TrackKey { get { return Title + "\u0001" + Artist; } }
}

// ---- background poller: talks to SMTC off the UI thread ----
class MediaWatcher
{
    public event Action<MediaState> Changed;
    GlobalSystemMediaTransportControlsSessionManager mgr;
    string lastKey = "";
    byte[] lastThumb;

    static T Wait<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(3);
        return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T);
    }

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    void Loop()
    {
        while (true)
        {
            try { Poll(); } catch { mgr = null; }   // session vanished etc. -> retry next tick
            Thread.Sleep(500);
        }
    }

    void Poll()
    {
        if (mgr == null) mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        var st = new MediaState { Stamp = DateTime.UtcNow };
        var s = mgr == null ? null : mgr.GetCurrentSession();
        if (s != null)
        {
            var props = Wait(s.TryGetMediaPropertiesAsync());
            var pb = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            st.HasSession = props != null && !string.IsNullOrEmpty(props.Title);
            st.Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (props != null) { st.Title = props.Title ?? ""; st.Artist = props.Artist ?? ""; }
            st.SourceApp = s.SourceAppUserModelId ?? "";
            st.DurationS = (tl.EndTime - tl.StartTime).TotalSeconds;
            // SMTC position is a snapshot taken at LastUpdatedTime; extrapolate while playing
            double pos = (tl.Position - tl.StartTime).TotalSeconds;
            if (st.Playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            st.PositionS = Math.Max(0, Math.Min(pos, st.DurationS > 0 ? st.DurationS : pos));

            if (st.TrackKey != lastKey)
            {
                lastKey = st.TrackKey;
                lastThumb = (props != null && props.Thumbnail != null) ? ReadAll(props.Thumbnail) : null;
            }
            st.Thumb = lastThumb;
        }
        var h = Changed; if (h != null) h(st);
    }

    static byte[] ReadAll(IRandomAccessStreamReference r)
    {
        try
        {
            var stream = Wait(r.OpenReadAsync());
            uint n = (uint)stream.Size;
            var rd = new DataReader(stream);
            var load = rd.LoadAsync(n);
            while (load.Status == AsyncStatus.Started) Thread.Sleep(3);
            var buf = new byte[n]; rd.ReadBytes(buf);
            return buf;
        }
        catch { return null; }
    }

    // media buttons (fire and forget, off the UI thread)
    public void Command(int which)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var s = mgr == null ? null : mgr.GetCurrentSession();
                if (s == null) return;
                if (which < 0) Wait(s.TrySkipPreviousAsync());
                else if (which == 0) Wait(s.TryTogglePlayPauseAsync());
                else Wait(s.TrySkipNextAsync());
                Thread.Sleep(150);
                Poll();
            }
            catch { }
        });
    }
}

// ---- which process belongs to the media source ----
static class SourceApp
{
    // "Spotify.exe" -> "spotify";  "Brave.XYZ" / "MSEdge" / "Chrome" -> "brave" / "msedge" / "chrome"
    public static string ProcessKey(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(aumid).ToLowerInvariant();
        int i = aumid.IndexOfAny(new[] { '.', '!', '_' });
        return (i > 0 ? aumid.Substring(0, i) : aumid).ToLowerInvariant();
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    static string TitleOf(IntPtr h) { var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    static bool Has(string haystack, string needle)
    {
        return !string.IsNullOrEmpty(needle) && haystack.IndexOf(needle.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    static void Front(IntPtr h)
    {
        ShowWindow(h, IsIconic(h) ? 9 : 5);   // SW_RESTORE : SW_SHOW
        SetForegroundWindow(h);               // allowed: we just received the user's click
    }

    // Bring the window (and for browsers: the tab) that is playing to the front.
    // Runs off the UI thread because the tab search can take ~200 ms.
    public static void Activate(string aumid, string mediaTitle)
    {
        ThreadPool.QueueUserWorkItem(_ => { try { ActivateNow(aumid, mediaTitle); } catch { } });
    }

    static void ActivateNow(string aumid, string mediaTitle)
    {
        string key = ProcessKey(aumid);
        if (key.Length == 0) return;
        var procs = System.Diagnostics.Process.GetProcessesByName(key);
        var pids = new HashSet<uint>();
        foreach (var p in procs) pids.Add((uint)p.Id);

        // all visible, titled top-level windows of the app, most recently used first (z-order)
        var wins = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pids.Contains(pid) && IsWindowVisible(h) && TitleOf(h).Length > 0) wins.Add(h);
            return true;
        }, IntPtr.Zero);

        if (wins.Count > 0)
        {
            // 1) a window whose active tab / title already shows the song
            foreach (var h in wins) if (Has(TitleOf(h), mediaTitle)) { Front(h); return; }

            // 2) browsers: look through every window's tab strip (UI Automation) and switch to that tab
            if (!string.IsNullOrEmpty(mediaTitle))
                foreach (var h in wins)
                {
                    var tabs = System.Windows.Automation.AutomationElement.FromHandle(h).FindAll(
                        System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.ControlTypeProperty,
                            System.Windows.Automation.ControlType.TabItem));
                    foreach (System.Windows.Automation.AutomationElement t in tabs)
                    {
                        if (!Has(t.Current.Name, mediaTitle)) continue;
                        if (IsIconic(h)) ShowWindow(h, 9);
                        object pat;
                        if (t.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out pat))
                            ((System.Windows.Automation.SelectionItemPattern)pat).Select();
                        Front(h);
                        return;
                    }
                }

            // 3) nothing matched: the most recently used window of the app
            Front(wins[0]);
            return;
        }
        try
        {
            // window is hidden (e.g. Spotify closed to tray): starting the exe again brings it back
            if (procs.Length > 0) { System.Diagnostics.Process.Start(procs[0].MainModule.FileName); return; }
        }
        catch { }
        try { System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + aumid); } catch { }
    }
}

// ---- per-app volume through the Windows audio session API (Core Audio / WASAPI) ----
static class AppVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int NotImpl1(); int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
    }

    // IAudioSessionControl (9 methods) + IAudioSessionControl2 extras; only GetProcessId is used
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int N1(); int N2(); int N3(); int N4(); int N5(); int N6(); int N7(); int N8(); int N9();
        int N10(); int N11();
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CD74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int N1(); int N2(); int N3(); int N4();
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int N6();
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        int N8(); int N9(); int N10(); int N11();
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static Guid ctx = Guid.Empty;

    internal static IMMDevice Speakers()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);   // eRender, eMultimedia
        return dev;
    }

    // all audio sessions whose process name matches the media source
    static List<ISimpleAudioVolume> Sessions(string aumid)
    {
        var res = new List<ISimpleAudioVolume>();
        string key = SourceApp.ProcessKey(aumid);
        if (key.Length == 0) return res;
        var iid = typeof(IAudioSessionManager2).GUID; object o;
        if (Speakers().Activate(ref iid, 1 /* CLSCTX_INPROC */, IntPtr.Zero, out o) != 0) return res;
        IAudioSessionEnumerator e; ((IAudioSessionManager2)o).GetSessionEnumerator(out e);
        int n; e.GetCount(out n);
        for (int i = 0; i < n; i++)
        {
            object s; e.GetSession(i, out s);
            uint pid; ((IAudioSessionControl2)s).GetProcessId(out pid);
            if (pid == 0) continue;
            string name;
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { continue; }
            if (name == key || name.StartsWith(key)) res.Add((ISimpleAudioVolume)s);
        }
        return res;
    }

    static IAudioEndpointVolume Master()
    {
        var iid = typeof(IAudioEndpointVolume).GUID; object o;
        Speakers().Activate(ref iid, 1, IntPtr.Zero, out o);
        return (IAudioEndpointVolume)o;
    }

    // read: app volume if the app has an audio session, otherwise the master volume
    public static void Get(string aumid, out float level, out bool mute, out bool perApp)
    {
        level = 1; mute = false; perApp = false;
        try
        {
            var s = Sessions(aumid);
            if (s.Count > 0) { s[0].GetMasterVolume(out level); s[0].GetMute(out mute); perApp = true; return; }
            var m = Master(); m.GetMasterVolumeLevelScalar(out level); m.GetMute(out mute);
        }
        catch { }
    }

    public static void Set(string aumid, float level)
    {
        level = Math.Max(0, Math.Min(1, level));
        try
        {
            var s = Sessions(aumid);
            if (s.Count > 0) { foreach (var v in s) { v.SetMasterVolume(level, ref ctx); if (level > 0) v.SetMute(false, ref ctx); } return; }
            var m = Master(); m.SetMasterVolumeLevelScalar(level, ref ctx); if (level > 0) m.SetMute(false, ref ctx);
        }
        catch { }
    }

    public static void SetMute(string aumid, bool mute)
    {
        try
        {
            var s = Sessions(aumid);
            if (s.Count > 0) { foreach (var v in s) v.SetMute(mute, ref ctx); return; }
            Master().SetMute(mute, ref ctx);
        }
        catch { }
    }
}

// ---- mini equalizer: WASAPI loopback (exactly what the speakers play) -> FFT -> 5 log-spaced bands ----
// Runs on its own thread and only while music is playing; otherwise it sleeps on an event (0 CPU).
class AudioViz
{
    public const int Bands = 5;
    const int N = 2048;                        // FFT size: 2048 / 48 kHz = 43 ms window, 23 Hz per bin
    // band edges in Hz, each band 3x wider than the previous one (equal width on a log axis)
    static readonly float[] Edges = { 50, 150, 450, 1300, 4000, 12000 };
    // dB that maps to an empty / full bar. Pink noise at -18 dBFS reads ~-24 dB per band (~0.64),
    // so typical mastered music sits around 2/3 and only transients hit the top.
    public const float FloorDb = -52, RangeDb = 44;

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    readonly float[] ring = new float[N];      // last N mono samples (circular)
    int ringPos;
    readonly float[] re = new float[N], im = new float[N], hann = new float[N];
    readonly float[] target = new float[Bands];
    public readonly double[] LastDb = new double[Bands];   // for calibration / debugging
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile bool want;
    float[] fbuf = new float[0]; short[] sbuf = new short[0];

    public AudioViz()
    {
        for (int i = 0; i < N; i++) hann[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (N - 1));
        new Thread(Loop) { IsBackground = true, Name = "viz", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public bool Want
    {
        get { return want; }
        set { want = value; if (value) wake.Set(); }
    }

    // latest bar heights 0..1 (written by the capture thread, read by the UI)
    public void Read(float[] dst) { Array.Copy(target, dst, Bands); }

    void Loop()
    {
        IAudioClient client = null; IAudioCaptureClient cap = null;
        int channels = 2, rate = 48000; bool isFloat = true;
        var sinceData = System.Diagnostics.Stopwatch.StartNew();
        var sinceFft = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (!want)
            {
                Close(ref client, ref cap);
                Array.Clear(target, 0, Bands);
                wake.WaitOne();
                continue;
            }
            try
            {
                if (client == null) { Open(out client, out cap, out channels, out rate, out isFloat); sinceData.Restart(); }
                uint packet;
                Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                while (packet > 0)
                {
                    IntPtr data; uint frames, flags; ulong p1, p2;
                    Marshal.ThrowExceptionForHR(cap.GetBuffer(out data, out frames, out flags, out p1, out p2));
                    Push(data, (int)frames, channels, isFloat, (flags & 2) != 0);   // 2 = AUDCLNT_BUFFERFLAGS_SILENT
                    cap.ReleaseBuffer(frames);
                    sinceData.Restart();
                    Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                }
            }
            catch
            {
                // e.g. default output device changed (headphones plugged in): reopen after a moment
                Close(ref client, ref cap);
                Array.Clear(target, 0, Bands);
                Thread.Sleep(1000);
                continue;
            }
            if (sinceFft.ElapsedMilliseconds >= 10)
            {
                sinceFft.Restart();
                // Windows delivers no packets at all while nothing is playing -> bars fall to zero
                if (sinceData.ElapsedMilliseconds > 120) Array.Clear(target, 0, Bands); else Analyze(rate);
            }
            Thread.Sleep(5);
        }
    }

    void Open(out IAudioClient client, out IAudioCaptureClient cap, out int channels, out int rate, out bool isFloat)
    {
        client = null; cap = null;
        var iid = typeof(IAudioClient).GUID; object o;
        Marshal.ThrowExceptionForHR(AppVolume.Speakers().Activate(ref iid, 1, IntPtr.Zero, out o));
        client = (IAudioClient)o;
        IntPtr fmt;
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out fmt));
        try
        {
            // WAVEFORMATEX: tag(2) channels(2) rate(4) avgBytes(4) blockAlign(2) bits(2) cbSize(2) [+ extensible part]
            int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
            channels = Marshal.ReadInt16(fmt, 2);
            rate = Marshal.ReadInt32(fmt, 4);
            int bits = Marshal.ReadInt16(fmt, 14);
            if (tag == 0xFFFE) tag = Marshal.ReadInt32(fmt, 24);   // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID starts with the real tag
            isFloat = tag == 3 && bits == 32;
            if (!isFloat && !(tag == 1 && bits == 16)) throw new NotSupportedException("mix format");
            // shared mode, LOOPBACK flag, 200 ms buffer
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero));
        }
        finally { Marshal.FreeCoTaskMem(fmt); }
        var ciid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out o));
        cap = (IAudioCaptureClient)o;
        Marshal.ThrowExceptionForHR(client.Start());
    }

    static void Close(ref IAudioClient client, ref IAudioCaptureClient cap)
    {
        try { if (client != null) client.Stop(); } catch { }
        if (cap != null) { try { Marshal.ReleaseComObject(cap); } catch { } cap = null; }
        if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } client = null; }
    }

    // interleaved frames -> mono samples in the ring buffer
    void Push(IntPtr data, int frames, int ch, bool isFloat, bool silent)
    {
        int n = frames * ch;
        if (!silent)
        {
            if (isFloat) { if (fbuf.Length < n) fbuf = new float[n]; Marshal.Copy(data, fbuf, 0, n); }
            else { if (sbuf.Length < n) sbuf = new short[n]; Marshal.Copy(data, sbuf, 0, n); }
        }
        float inv = 1f / ch;
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            if (!silent)
                for (int c = 0; c < ch; c++) s += isFloat ? fbuf[f * ch + c] : sbuf[f * ch + c] / 32768f;
            ring[ringPos] = s * inv;
            ringPos = (ringPos + 1) % N;
        }
    }

    void Analyze(int rate)
    {
        // oldest -> newest, multiplied by a Hann window (softens the cut edges -> less spectral leakage)
        for (int i = 0; i < N; i++) { re[i] = ring[(ringPos + i) % N] * hann[i]; im[i] = 0; }
        Fft(re, im);
        float df = rate / (float)N;
        float norm = 4f / N;               // a full-scale sine ends up at magnitude ~1 (0 dB)
        for (int b = 0; b < Bands; b++)
        {
            int k0 = Math.Max(1, (int)(Edges[b] / df));
            int k1 = Math.Min(N / 2, Math.Max(k0 + 1, (int)(Edges[b + 1] / df)));
            // total power in the band. Summing (not averaging) over log-spaced bands makes
            // pink noise (energy falling 3 dB/octave, like most music) come out flat.
            double p = 0;
            for (int k = k0; k < k1; k++) { double a = re[k] * norm, c = im[k] * norm; p += a * a + c * c; }
            double db = 10 * Math.Log10(p + 1e-12);
            LastDb[b] = db;
            target[b] = (float)Math.Max(0, Math.Min(1, (db - FloorDb) / RangeDb));
        }
    }

    // in-place iterative radix-2 FFT (Cooley-Tukey)
    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)              // bit-reversal permutation
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
        }
        for (int len = 2; len <= n; len <<= 1)           // butterflies: 2-point, 4-point, ... n-point
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}

// ---- the island ----
class AdaForm : Form
{
    // window: fixed size, transparent outside the pill (and click-through there)
    const int WinW = 540, WinH = 74;
    const int IdleW = 56, IdleH = 32, OpenH = 56, CtrlW = 150;   // 4 controls: prev, play/pause, next, volume
    const int Pad = 8, CoverS = 40, Gap = 12, RightPad = 20;
    const int BarW = 3, BarGap = 3, BarsW = AudioViz.Bands * BarW + (AudioViz.Bands - 1) * BarGap, BarsSpace = BarsW + 12;
    // time-based smoothing: x += (target - x) * (1 - e^(-K*dt))  (1st-order low-pass toward the target)
    const float K = 11f;        // higher = snappier, lower = softer

    // Nord palette
    static readonly Color Bg = Color.FromArgb(46, 52, 64), Border = Color.FromArgb(136, 192, 208);
    static readonly Color Snow = Color.FromArgb(236, 239, 244), Dim = Color.FromArgb(129, 161, 193), Track = Color.FromArgb(76, 86, 106);
    static readonly Color Frost = Color.FromArgb(136, 192, 208);

    readonly MediaWatcher watcher = new MediaWatcher();
    readonly VsyncLoop anim;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };

    MediaState media = new MediaState();
    Image cover; Color accent = Frost;
    DateTime pausedSince = DateTime.MinValue;
    bool hover;

    // animated values: current -> target
    float w = IdleW, h = IdleH, ctrlA = 0, textA = 0, tw = IdleW, th = IdleH, tctrlA = 0, ttextA = 0;
    // volume: real level, the level shown (glides toward it) and how much "volume mode" is visible
    float vol = 1, volShown = 1, volA = 0, tvolA = 0;
    bool muted;
    readonly System.Windows.Forms.Timer volHide = new System.Windows.Forms.Timer { Interval = 1400 };
    int lastProgressPx = -1;
    // equalizer: raw band targets from the capture thread, smoothed bar heights, overall visibility
    readonly AudioViz viz = new AudioViz();
    readonly float[] vizT = new float[AudioViz.Bands], vizL = new float[AudioViz.Bands];
    float vizA = 0;

    // focus mode (pomodoro): 25 min focus -> 5 min break -> done
    enum Phase { None, Focus, Break }
    const int FocusMin = 25, BreakMin = 5;
    static readonly Color FocusCol = Color.FromArgb(163, 190, 140), BreakCol = Color.FromArgb(235, 203, 139);
    Phase phase = Phase.None;
    DateTime phaseStart, phaseEnd, bannerUntil = DateTime.MinValue;
    string bannerText = ""; bool bannerBreak;
    float fA = 0, tfA = 0;                       // visibility of the focus label in the small pill
    readonly bool startFocus;
    Font fFocus = new Font("Segoe UI Variable Text Semibold", 9.5f);
    static readonly string GTimer = char.ConvertFromUtf32(0xF051B), GCoffee = char.ConvertFromUtf32(0xF0176);

    Font fTitle = new Font("Segoe UI Variable Text Semibold", 10f);
    Font fArtist = new Font("Segoe UI Variable Text", 8.5f);
    Font fGlyph = new Font("JetBrainsMono NFP", 13f);
    Font fGlyphBig = new Font("JetBrainsMono NFP", 15f);

    static readonly string GPrev = char.ConvertFromUtf32(0xF04AE), GNext = char.ConvertFromUtf32(0xF04AD);
    static readonly string GPlay = char.ConvertFromUtf32(0xF040A), GPause = char.ConvertFromUtf32(0xF03E4);
    static readonly string GNote = char.ConvertFromUtf32(0xF075A);
    static readonly string GVolHigh = char.ConvertFromUtf32(0xF057E), GVolMid = char.ConvertFromUtf32(0xF0580);
    static readonly string GVolLow = char.ConvertFromUtf32(0xF057F), GVolOff = char.ConvertFromUtf32(0xF0581);
    string VolGlyph() { return muted || vol < 0.005f ? GVolOff : vol < 0.34f ? GVolLow : vol < 0.67f ? GVolMid : GVolHigh; }

    public AdaForm(bool startFocus)
    {
        this.startFocus = startFocus;
        // other processes ("Ada.exe --focus") signal this event to toggle focus mode
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.FocusEventName);
        new Thread(() => { while (true) { ev.WaitOne(); try { BeginInvoke((Action)ToggleFocus); } catch { } } }) { IsBackground = true }.Start();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(wa.Left + (wa.Width - WinW) / 2, wa.Bottom - WinH - 2, WinW, WinH);

        anim = new VsyncLoop(this, Step);
        volHide.Tick += (s, e) => { volHide.Stop(); tvolA = 0; Kick(); };
        tick.Tick += (s, e) => { KeepOnDesktop(); FocusTick(); UpdateTargets(); RenderIfProgressMoved(); };
        watcher.Changed += st => { try { BeginInvoke((Action)(() => OnMedia(st))); } catch { } };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x8000000;   // LAYERED | TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        KeepOnDesktop();
        Render();
        watcher.Start();
        tick.Start();
        if (startFocus) ToggleFocus();
    }

    // ---------- focus mode ----------
    void ToggleFocus()
    {
        if (phase == Phase.None) StartPhase(Phase.Focus, FocusMin);
        else { phase = Phase.None; bannerUntil = DateTime.MinValue; }
        UpdateTargets(); Kick();
    }

    void StartPhase(Phase p, int minutes)
    {
        phase = p;
        phaseStart = DateTime.UtcNow;
        // "--fast" (testing only): one "minute" lasts one second
        bool fast = Array.IndexOf(Environment.GetCommandLineArgs(), "--fast") >= 0;
        phaseEnd = phaseStart.AddSeconds(minutes * (fast ? 1 : 60));
    }

    void Banner(string text, bool isBreak)
    {
        bannerText = text; bannerBreak = isBreak;
        bannerUntil = DateTime.UtcNow.AddSeconds(7);
        System.Media.SystemSounds.Asterisk.Play();   // a soft cue: you may not be looking at the island
    }

    // once a second: phase changes + redraw so the border keeps filling
    void FocusTick()
    {
        if (phase != Phase.None && DateTime.UtcNow >= phaseEnd)
        {
            if (phase == Phase.Focus) { StartPhase(Phase.Break, BreakMin); Banner(UiText.BreakTime(BreakMin), true); }
            else { phase = Phase.None; Banner(UiText.BreakOver, false); }
            Kick();
        }
        if ((phase != Phase.None || fA > 0) && !anim.Running) Render();
    }

    float FocusFraction()
    {
        if (phase == Phase.None) return 0;
        double total = (phaseEnd - phaseStart).TotalSeconds;
        return (float)Math.Max(0, Math.Min(1, (DateTime.UtcNow - phaseStart).TotalSeconds / total));
    }

    bool BannerOn { get { return DateTime.UtcNow < bannerUntil; } }

    string FocusLabel()
    {
        if (BannerOn) return bannerText;
        int left = (int)Math.Ceiling((phaseEnd - DateTime.UtcNow).TotalMinutes);
        return UiText.Min(Math.Max(1, left));
    }
    string FocusGlyph() { return BannerOn ? (bannerBreak ? GCoffee : GTimer) : (phase == Phase.Break ? GCoffee : GTimer); }
    Color FocusColor() { return (BannerOn ? bannerBreak : phase == Phase.Break) ? BreakCol : FocusCol; }

    // ---------- state ----------
    void OnMedia(MediaState st)
    {
        bool trackChanged = st.TrackKey != media.TrackKey;
        bool statusChanged = st.Playing != media.Playing || st.HasSession != media.HasSession;
        if (st.Playing) pausedSince = DateTime.MinValue;
        else if (media.Playing || pausedSince == DateTime.MinValue) pausedSince = DateTime.UtcNow;
        media = st;

        if (trackChanged)
        {
            if (cover != null) { cover.Dispose(); cover = null; }
            if (st.Thumb != null)
                try
                {
                    // scale the (often 300+ px) thumbnail down ONCE, with rounded corners baked in;
                    // per-frame drawing is then a cheap 40x40 copy instead of a bicubic resample
                    using (var full = Image.FromStream(new MemoryStream(st.Thumb)))
                    {
                        accent = AccentOf(full);
                        var scaled = new Bitmap(CoverS, CoverS, PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(full, 0, 0, CoverS, CoverS);
                        }
                        var small = new Bitmap(CoverS, CoverS, PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(small))
                        using (var clip = Round(new RectangleF(0, 0, CoverS, CoverS), 10))
                        using (var tb = new TextureBrush(scaled))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.FillPath(tb, clip);       // anti-aliased rounded edge
                        }
                        scaled.Dispose();
                        cover = small;
                    }
                }
                catch { cover = null; }
            if (cover == null) accent = Frost;
            textA = 0;   // new track: content fades in again
        }
        UpdateTargets();
        if (trackChanged || statusChanged) Kick(); else RenderIfProgressMoved();
    }

    void UpdateTargets()
    {
        bool recentlyPaused = pausedSince != DateTime.MinValue && (DateTime.UtcNow - pausedSince).TotalSeconds < 8;
        bool open = media.HasSession && (media.Playing || recentlyPaused || hover);
        bool banner = BannerOn;
        if (banner) open = false;                     // the "break time" message briefly takes over
        bool focusPill = !open && (banner || phase != Phase.None);
        float ntw, nth, nText, nCtrl, nF = 0;
        if (focusPill)
        {
            nth = IdleH; nText = 0; nCtrl = 0; nF = 1;
            using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
                ntw = 14 + 18 + 6 + g.MeasureString(FocusLabel(), fFocus).Width + 14;   // pad, glyph, gap, text, pad
        }
        else if (open)
        {
            nth = OpenH;
            ntw = Math.Min(WinW - 8 - CtrlW, Pad + CoverS + Gap + TextWidth() + BarsSpace + RightPad);
            ntw = Math.Max(ntw, 270);
            if (hover) ntw += CtrlW;
            nText = 1; nCtrl = hover ? 1 : 0;
        }
        else { ntw = IdleW; nth = IdleH; nText = 0; nCtrl = 0; }
        // listen to the speakers only while music plays and the island is open
        bool wantViz = open && media.Playing;
        if (wantViz != viz.Want) { viz.Want = wantViz; Kick(); }
        if (Math.Abs(ntw - tw) > 0.5f || nth != th || nText != ttextA || nCtrl != tctrlA || nF != tfA)
        {
            tw = ntw; th = nth; ttextA = nText; tctrlA = nCtrl; tfA = nF;
            Kick();
        }
    }

    float TextWidth()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
        {
            float a = g.MeasureString(media.Title, fTitle).Width;
            float c = media.Artist.Length > 0 ? g.MeasureString(media.Artist, fArtist).Width : 0;
            return Math.Max(a, c);
        }
    }

    // ---------- animation ----------
    // frames are paced by the display refresh (vsync) via VsyncLoop
    readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();

    void Kick()
    {
        if (anim.Running) return;
        clock.Restart();
        anim.Start();
    }

    // returns false once everything has settled (the vsync loop then goes idle)
    bool Step()
    {
        float dt = (float)clock.Elapsed.TotalSeconds; clock.Restart();
        dt = Math.Min(dt, 0.05f);                     // a hiccup never causes a jump
        float k = 1f - (float)Math.Exp(-K * dt);      // same speed no matter how uneven the frames are
        float kText = 1f - (float)Math.Exp(-K * 0.8f * dt);

        w += (tw - w) * k; h += (th - h) * k;
        ctrlA += (tctrlA - ctrlA) * k;
        // text waits until the pill is mostly open, so it never looks squeezed
        float openness = Math.Max(0, Math.Min(1, (w - IdleW) / Math.Max(1, tw - IdleW)));
        float textGoal = ttextA > 0 ? (openness > 0.55f ? 1 : 0) : 0;
        textA += (textGoal - textA) * kText;

        volA += (tvolA - volA) * k;
        volShown += (vol - volShown) * (1f - (float)Math.Exp(-K * 1.5f * dt));
        // focus label: like the media text, it waits until the pill has (nearly) its new width
        float fGoal = tfA > 0 && Math.Abs(tw - w) < 12 ? 1 : 0;
        fA += (fGoal - fA) * kText;

        bool done = Math.Abs(tw - w) < 0.3f && Math.Abs(th - h) < 0.3f && Math.Abs(tctrlA - ctrlA) < 0.005f && Math.Abs(textGoal - textA) < 0.005f
                    && Math.Abs(tvolA - volA) < 0.005f && Math.Abs(vol - volShown) < 0.002f && Math.Abs(fGoal - fA) < 0.005f;
        if (done) { w = tw; h = th; ctrlA = tctrlA; textA = textGoal; volA = tvolA; volShown = vol; fA = fGoal; }

        // equalizer bars: fast attack (35 ms), slow release (220 ms) -> punchy but not jittery,
        // the same trick a VU meter's ballistics use
        bool vizOn = viz.Want;
        viz.Read(vizT);
        float vizGoal = vizOn ? 1 : (media.HasSession ? 0.45f : 0);   // paused: dim resting dots
        vizA += (vizGoal - vizA) * k;
        float up = 1f - (float)Math.Exp(-dt / 0.035f), down = 1f - (float)Math.Exp(-dt / 0.22f);
        bool barsMoving = false;
        for (int i = 0; i < AudioViz.Bands; i++)
        {
            float t = vizOn ? vizT[i] : 0;
            vizL[i] += (t - vizL[i]) * (t > vizL[i] ? up : down);
            if (Math.Abs(t - vizL[i]) > 0.002f) barsMoving = true;
        }
        bool vizBusy = vizOn || barsMoving || Math.Abs(vizGoal - vizA) > 0.005f;

        // shape animations run at full refresh rate; the equalizer alone is fine at ~60 fps
        anim.MinIntervalMs = done ? 14 : 0;
        Render();
        return !done || vizBusy;
    }

    void RenderIfProgressMoved()
    {
        if (anim.Running || !media.HasSession || textA < 0.5f) return;
        int px = ProgressPx(ProgressWidth());
        if (px != lastProgressPx) Render();
    }

    double Position()
    {
        double p = media.PositionS;
        if (media.Playing) p += (DateTime.UtcNow - media.Stamp).TotalSeconds;
        return media.DurationS > 0 ? Math.Min(p, media.DurationS) : p;
    }
    float ProgressWidth() { return w - (Pad + CoverS + Gap) - RightPad - CtrlW * ctrlA; }
    int ProgressPx(float width) { return media.DurationS > 0 ? (int)(width * Position() / media.DurationS) : 0; }

    // ---------- drawing ----------
    RectangleF Pill() { return new RectangleF((WinW - w) / 2f, (WinH - h) / 2f, w, h); }

    // one DIB section shared by GDI+ (draws into it) and UpdateLayeredWindow (reads it):
    // no per-frame bitmap allocation or GetHbitmap copy (same trick as NordMenu)
    LayeredSurface surface;
    void Render()
    {
        if (surface == null)
        {
            surface = new LayeredSurface(WinW, WinH);
            surface.G.SmoothingMode = SmoothingMode.AntiAlias;
            surface.G.PixelOffsetMode = PixelOffsetMode.HighQuality;   // sub-pixel accurate edges while animating
            surface.G.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            surface.G.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }
        var g = surface.G;
        g.Clear(Color.Transparent);
        Draw(g);
        surface.Present(this);
    }

    void Draw(Graphics g)
    {
        var r = Pill();
        using (var path = Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(Color.FromArgb(232, Bg))) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(55, Border), 1f)) g.DrawPath(p, path);
        }

        // focus mode: the border fills clockwise from the top as the session goes by
        float frac = FocusFraction();
        if (phase != Phase.None && frac > 0.001f)
            using (var pen = new Pen(Color.FromArgb(230, phase == Phase.Break ? BreakCol : FocusCol), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                DrawPartialBorder(g, pen, r, frac);

        // focus label (small pill): glyph + remaining minutes / break message
        if (fA > 0.01f)
        {
            int fa = (int)(255 * fA);
            var col = FocusColor();
            var gr = new RectangleF(r.X + 12, r.Y, 22, r.Height);
            DrawCentered(g, FocusGlyph(), fGlyph, Color.FromArgb(fa, col), gr);
            var ffmt = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None };
            using (var b = new SolidBrush(Color.FromArgb(fa, Snow)))
                g.DrawString(FocusLabel(), fFocus, b, new RectangleF(gr.Right + 4, r.Y + (1 - fA) * 3, 400, r.Height), ffmt);
        }

        // idle content: a note (or pause sign) that fades out as the pill opens
        float idleA = 1 - Math.Max(0, Math.Min(1, (w - IdleW) / 40f));
        if (tfA > 0) idleA = 0;
        if (idleA > 0.01f)
        {
            string glyph = media.HasSession && !media.Playing ? GPause : GNote;
            var col = media.HasSession ? accent : Dim;
            DrawCentered(g, glyph, fGlyph, Color.FromArgb((int)(220 * idleA), col), r);
        }
        if (textA < 0.01f) return;
        int a = (int)(255 * textA);

        // cover (rounded square), vertically centered
        float cs = Math.Min(CoverS, h - 2 * Pad);
        var cr = new RectangleF(r.X + Pad, r.Y + (h - cs) / 2f, cs, cs);
        if (cover != null)
        {
            var dst = Rectangle.Round(cr);
            if (textA > 0.995f && dst.Width == CoverS && dst.Height == CoverS)
            {
                // steady state (every equalizer frame): plain 1:1 copy, no resampling
                var im = g.InterpolationMode; var po = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(cover, dst);
                g.InterpolationMode = im; g.PixelOffsetMode = po;
            }
            else
                using (var ia = new ImageAttributes())   // fading in / pill still growing
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = textA });
                    g.DrawImage(cover, dst, 0, 0, cover.Width, cover.Height, GraphicsUnit.Pixel, ia);
                }
        }
        else
        {
            using (var clip = Round(cr, 10)) using (var b = new SolidBrush(Color.FromArgb(a, Track))) g.FillPath(b, clip);
            DrawCentered(g, GNote, fGlyph, Color.FromArgb(a, accent), cr);
        }

        // two text lines: title (bright) and artist (dim); text slides up a little as it fades in
        float tx = cr.Right + Gap, textRight = r.Right - RightPad - Math.Max(CtrlW * ctrlA, BarsSpace);
        float colW = Math.Max(10, textRight - tx);
        float slide = (1 - textA) * 4;
        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        // second line cross-fades between the artist and "Volume NN%" while the volume changes
        using (var bt = new SolidBrush(Color.FromArgb(a, Snow)))
        using (var ba = new SolidBrush(Color.FromArgb((int)(a * 0.9f * (1 - volA)), Dim)))
        using (var bv = new SolidBrush(Color.FromArgb((int)(a * volA), accent)))
        {
            g.DrawString(media.Title, fTitle, bt, new RectangleF(tx, r.Y + 7 + slide, colW, 20), fmt);
            if (media.Artist.Length > 0 && volA < 0.99f)
                g.DrawString(media.Artist, fArtist, ba, new RectangleF(tx + 1, r.Y + 25 + slide + volA * 3, colW, 18), fmt);
            if (volA > 0.01f)
                g.DrawString((muted ? UiText.Muted : UiText.Volume((int)Math.Round(volShown * 100))), fArtist, bv,
                             new RectangleF(tx + 1, r.Y + 25 + slide + (1 - volA) * 3, colW, 18), fmt);
        }

        // bottom line: song progress, morphing into a volume bar while the volume changes
        float pw = ProgressWidth();
        float py = r.Bottom - 10;
        using (var pt = new Pen(Color.FromArgb((int)(a * 0.7f), Track), 2f + volA) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pt, tx + 1, py, tx + 1 + pw, py);
        int done = ProgressPx(pw);
        lastProgressPx = done;
        float fill = done + ((muted ? 0 : volShown) * pw - done) * volA;   // progress -> volume
        if (fill > 1)
            using (var pf = new Pen(Color.FromArgb(a, accent), 2f + volA) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pf, tx + 1, py, tx + 1 + fill, py);

        // equalizer: 5 rounded bars (low -> high frequency) growing from the center line,
        // at the right end of the text rows; they step aside when the hover controls appear
        float eqA = textA * vizA * (1 - ctrlA);
        if (eqA > 0.01f)
        {
            float bx = r.Right - RightPad - BarsW + BarW / 2f, cy = r.Y + 24;
            using (var pen = new Pen(Color.FromArgb((int)(255 * eqA), accent), BarW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < AudioViz.Bands; i++)
                {
                    float len = Math.Max(0.01f, vizL[i] * 20);   // the round caps add BarW, so a 0-length bar is a dot
                    float x = bx + i * (BarW + BarGap);
                    g.DrawLine(pen, x, cy - len / 2, x, cy + len / 2);
                }
        }

        // controls (hover); clipped to the pill, because while the pill shrinks faster than
        // the controls fade, they would otherwise poke out past its right edge
        if (ctrlA > 0.01f)
        {
            int ca = (int)(255 * ctrlA * textA);
            var rects = ControlRects();
            using (var clip = Round(r, r.Height / 2))
            {
                var st = g.Save();
                g.SetClip(clip);
                DrawCentered(g, GPrev, fGlyph, Color.FromArgb(ca, Snow), rects[0]);
                DrawCentered(g, media.Playing ? GPause : GPlay, fGlyphBig, Color.FromArgb(ca, accent), rects[1]);
                DrawCentered(g, GNext, fGlyph, Color.FromArgb(ca, Snow), rects[2]);
                DrawCentered(g, VolGlyph(), fGlyph, Color.FromArgb(ca, muted ? Dim : Snow), rects[3]);
                g.Restore(st);
            }
        }
    }

    RectangleF[] ControlRects()
    {
        var r = Pill();
        float x0 = r.Right - 14 - CtrlW * ctrlA, cw = (CtrlW - 4) / 4f;
        var res = new RectangleF[4];
        for (int i = 0; i < 4; i++) res[i] = new RectangleF(x0 + i * cw, r.Y, cw, r.Height);
        return res;
    }

    RectangleF CoverRect()
    {
        var r = Pill();
        float cs = Math.Min(CoverS, h - 2 * Pad);
        return new RectangleF(r.X + Pad, r.Y + (h - cs) / 2f, cs, cs);
    }

    static void DrawCentered(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
    }

    // Draws the first `frac` (0..1) of the pill outline, starting at top center, clockwise.
    static void DrawPartialBorder(Graphics g, Pen pen, RectangleF r, float frac)
    {
        float rad = r.Height / 2, cx = r.X + r.Width / 2;
        using (var p = new GraphicsPath())
        {
            p.AddLine(cx, r.Y, r.Right - rad, r.Y);
            p.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 180);
            p.AddLine(r.Right - rad, r.Bottom, r.X + rad, r.Bottom);
            p.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 90, 180);
            p.AddLine(r.X + rad, r.Y, cx, r.Y);
            p.Flatten(null, 0.25f);                       // arcs -> short straight segments
            var pts = p.PathPoints;
            float total = 0;
            for (int i = 1; i < pts.Length; i++) total += Dist(pts[i - 1], pts[i]);
            float want = total * frac, run = 0;
            var outPts = new List<PointF> { pts[0] };
            for (int i = 1; i < pts.Length && run < want; i++)
            {
                float d = Dist(pts[i - 1], pts[i]);
                if (run + d >= want)
                {   // cut the last segment exactly where the progress ends
                    float t = d > 0 ? (want - run) / d : 0;
                    outPts.Add(new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t));
                    break;
                }
                outPts.Add(pts[i]); run += d;
            }
            if (outPts.Count >= 2) g.DrawLines(pen, outPts.ToArray());
        }
    }
    static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // dominant-ish cover color, pushed toward a soft, readable tone on Nord background
    static Color AccentOf(Image img)
    {
        using (var tiny = new Bitmap(1, 1))
        {
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(img, 0, 0, 1, 1);
            }
            var c = tiny.GetPixel(0, 0);
            float hue = c.GetHue(), sat = Math.Max(0.35f, Math.Min(0.6f, c.GetSaturation() * 1.4f));
            return FromHsl(hue, sat, 0.68f);
        }
    }
    static Color FromHsl(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60f) % 2 - 1)), m = l - c / 2;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // ---------- mouse ----------
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hover = true;
        ReadVolume();   // so the speaker icon shows the real state
        UpdateTargets();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; UpdateTargets(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) { ToggleFocus(); return; }   // middle click: focus mode on/off
        if (e.Button != MouseButtons.Left || !media.HasSession) return;
        // cover -> bring the playing app (Spotify, browser...) to the front
        if (textA > 0.5f && CoverRect().Contains(e.Location)) { SourceApp.Activate(media.SourceApp, media.Title); return; }
        if (ctrlA < 0.5f) return;
        var rects = ControlRects();
        for (int i = 0; i < 3; i++)
            if (rects[i].Contains(e.Location)) { watcher.Command(i - 1); return; }
        if (rects[3].Contains(e.Location))
        {
            ReadVolume();
            muted = !muted;
            AppVolume.SetMute(media.SourceApp, muted);
            ShowVolume();
        }
    }

    // wheel over the island: +-5% volume of the playing app
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!media.HasSession) return;
        ReadVolume();
        vol = (float)Math.Round(Math.Max(0, Math.Min(1, vol + Math.Sign(e.Delta) * 0.05f)) * 20) / 20f;
        muted = false;
        AppVolume.Set(media.SourceApp, vol);
        ShowVolume();
    }

    void ReadVolume()
    {
        float lv; bool mu, perApp;
        AppVolume.Get(media.SourceApp, out lv, out mu, out perApp);
        if (volA < 0.01f) volShown = lv;   // don't animate from a stale value
        vol = lv; muted = mu;
    }

    void ShowVolume()
    {
        tvolA = 1;
        volHide.Stop(); volHide.Start();
        Kick();
    }

    // ---------- desktop layer ----------
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    // Put the window directly above the highest desktop window (Progman / WorkerW).
    void KeepOnDesktop()
    {
        if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--topmost")
        { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); return; }   // testing only
        IntPtr desk = IntPtr.Zero;
        var sb = new System.Text.StringBuilder(32);
        for (IntPtr w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDNEXT))
        {
            sb.Length = 0; GetClassName(w, sb, 32);
            string c = sb.ToString();
            if (c == "Progman" || c == "WorkerW") { desk = w; break; }   // first hit from the top = highest
        }
        if (desk == IntPtr.Zero) return;
        if (GetWindow(Handle, GW_HWNDNEXT) == desk) return;          // already right above it
        IntPtr above = GetWindow(desk, GW_HWNDPREV);
        if (above == Handle) return;
        SetWindowPos(Handle, above == IntPtr.Zero ? IntPtr.Zero : above, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }
}

// Runs a frame callback once per display refresh while there is something to animate
// (background thread waits on DwmFlush = next vsync, then runs the frame on the UI thread).
class VsyncLoop
{
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    readonly Control owner;
    readonly Func<bool> frame;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile bool running;
    public bool Running { get { return running; } }
    // skip vsyncs until this much time has passed (0 = every refresh; 14 = ~60 fps on a 240 Hz screen)
    public volatile int MinIntervalMs;
    readonly System.Diagnostics.Stopwatch since = System.Diagnostics.Stopwatch.StartNew();

    public VsyncLoop(Control owner, Func<bool> frame)
    {
        this.owner = owner; this.frame = frame;
        new Thread(Loop) { IsBackground = true, Name = "vsync" }.Start();
    }

    public void Start() { running = true; wake.Set(); }

    void Loop()
    {
        var done = new ManualResetEventSlim();
        while (true)
        {
            wake.WaitOne();
            while (running)
            {
                // throttled (equalizer only): sleep most of the gap instead of waking on every
                // 240 Hz vsync, then DwmFlush lines the frame up with the next refresh
                int min = MinIntervalMs;
                if (min > 0) { int wait = min - (int)since.ElapsedMilliseconds - 3; if (wait > 0) Thread.Sleep(wait); }
                if (DwmFlush() != 0) Thread.Sleep(4);
                if (since.Elapsed.TotalMilliseconds < min) continue;
                since.Restart();
                done.Reset();
                try { owner.BeginInvoke((Action)(() => { try { running = frame(); } finally { done.Set(); } })); }
                catch { running = false; break; }
                done.Wait(100);
            }
        }
    }
}

// A 32-bit DIB section that GDI+ draws into directly and UpdateLayeredWindow presents.
class LayeredSurface
{
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int size, w, h; public short planes, bits; public int compression, imageSize, xppm, yppm, clrUsed, clrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte op, flags, alpha, format; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND b, int flags);

    readonly IntPtr memDC;
    readonly int w, h;
    readonly Bitmap bmp;     // must stay referenced: if the GC finalizes it, G silently draws into nothing
    public readonly Graphics G;

    public LayeredSurface(int width, int height)
    {
        w = width; h = height;
        var bi = new BITMAPINFOHEADER { size = 40, w = width, h = -height, planes = 1, bits = 32 };   // top-down
        IntPtr bits;
        IntPtr screen = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screen);
        IntPtr dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);   // shares the DIB memory
        G = Graphics.FromImage(bmp);
    }

    public void Present(Form f)
    {
        G.Flush(FlushIntention.Sync);
        var pos = new POINT { x = f.Left, y = f.Top };
        var size = new SIZE { cx = w, cy = h };
        var src = new POINT();
        var blend = new BLEND { alpha = 255, format = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(f.Handle, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }
}

// Pushes an ARGB bitmap to a layered window (true anti-aliased transparency).
static class Layered
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte op, flags, alpha, format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND b, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    public static void Apply(Form f, Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var src = new POINT();
        var pos = new POINT { x = f.Left, y = f.Top };
        var blend = new BLEND { op = 0, flags = 0, alpha = 255, format = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(f.Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }
}
