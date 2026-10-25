using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using AForge.Video;
using AForge.Video.DirectShow;

namespace QrScope;

public sealed class MainForm : Form
{
    FilterInfoCollection devs;
    VideoCaptureDevice cam;
    VideoCapabilities[] caps = Array.Empty<VideoCapabilities>();

    readonly ComboBox cbCam = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly ComboBox cbRes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly Button btnRefresh = new() { Text = "Refresh", AutoSize = true };
    readonly Button btnStart = new() { Text = "Start", AutoSize = true };
    readonly Button btnProps = new() { Text = "Camera settings...", AutoSize = true };
    readonly Button btnSnap = new() { Text = "Snapshot", AutoSize = true };
    readonly CheckBox chkZx = new() { Text = "ZXing cross-check", Checked = true, AutoSize = true };
    readonly CheckBox chkLog = new() { Text = "Log CSV", AutoSize = true };
    readonly CheckBox chkHold = new() { Text = "Hold display", AutoSize = true };
    readonly PictureBox pbVideo = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly PictureBox pbMap = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    readonly Label legend = new()
    {
        Dock = DockStyle.Bottom, Height = 34, AutoSize = false,
        Text = "Map: red = dark where light expected, magenta = light where dark expected, olive/pale yellow = marginal contrast, blue = function patterns."
    };
    readonly RichTextBox txt = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Both, WordWrap = false,
        Font = new Font("Consolas", 9f), BackColor = Color.White, Text = "Waiting for a QR code..."
    };
    readonly SplitContainer rightSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1, IsSplitterFixed = true, Panel2MinSize = 150 };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, BorderStyle = BorderStyle.Fixed3D };

    volatile bool zxOn = true, hold, snap;
    StreamWriter log;
    DateTime lastUi = DateTime.MinValue, lastLog = DateTime.MinValue, fpsT = DateTime.Now;
    readonly Queue<bool> detQ = new(), decQ = new();
    int frames; double fps;
    string lastReport = "";
    static readonly string OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QrScope");

    [DllImport("user32.dll")] static extern int GetScrollPos(IntPtr h, int bar);
    [DllImport("user32.dll")] static extern int SetScrollPos(IntPtr h, int bar, int pos, bool redraw);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public MainForm()
    {
        Text = "QrScope - QR code diagnostic scanner";
        Width = 1450; Height = 880;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 0) };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Camera", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, cbCam,
            new Label { Text = "Resolution", AutoSize = true, Margin = new Padding(8, 7, 3, 0) }, cbRes,
            btnRefresh, btnStart, btnProps, btnSnap, chkZx, chkLog, chkHold
        });
        foreach (Control c in top.Controls) if (c is CheckBox) c.Margin = new Padding(8, 7, 3, 0);

        rightSplit.Panel1.Controls.Add(pbMap);
        rightSplit.Panel1.Controls.Add(legend);
        rightSplit.Panel2.Controls.Add(txt);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 760 };
        split.Panel1.Controls.Add(pbVideo);
        split.Panel2.Controls.Add(rightSplit);

        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(top);

        rightSplit.SizeChanged += (s, e) => AdjustMap();
        Shown += (s, e) => AdjustMap();
        btnRefresh.Click += (s, e) => RefreshDevices();
        btnStart.Click += (s, e) => { if (cam == null) StartCam(); else StopCam(); };
        btnProps.Click += (s, e) =>
        {
            try { cam?.DisplayPropertyPage(Handle); }
            catch (Exception ex) { status.Text = "No driver property page: " + ex.Message; }
        };
        btnSnap.Click += (s, e) => snap = true;
        chkZx.CheckedChanged += (s, e) => zxOn = chkZx.Checked;
        chkHold.CheckedChanged += (s, e) => hold = chkHold.Checked;
        chkLog.CheckedChanged += (s, e) => ToggleLog();
        cbCam.SelectedIndexChanged += (s, e) => LoadResolutions();
        FormClosing += (s, e) => { StopCam(); log?.Dispose(); };

        RefreshDevices();
    }

    // ------------------------------------------------------------------ camera handling
    void RefreshDevices()
    {
        cbCam.Items.Clear();
        try
        {
            devs = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            foreach (FilterInfo d in devs) cbCam.Items.Add(d.Name);
        }
        catch (Exception ex) { status.Text = "Camera enumeration failed: " + ex.Message; }
        if (cbCam.Items.Count > 0) cbCam.SelectedIndex = 0; else status.Text = "No cameras found.";
    }

    void LoadResolutions()
    {
        cbRes.Items.Clear();
        caps = Array.Empty<VideoCapabilities>();
        if (cbCam.SelectedIndex < 0 || devs == null) return;
        try
        {
            caps = new VideoCaptureDevice(devs[cbCam.SelectedIndex].MonikerString).VideoCapabilities;
            int pick = 0; long bestArea = -1;
            for (int i = 0; i < caps.Length; i++)
            {
                var c = caps[i];
                cbRes.Items.Add($"{c.FrameSize.Width}x{c.FrameSize.Height} @ {c.AverageFrameRate} fps");
                long area = (long)c.FrameSize.Width * c.FrameSize.Height;
                if (area <= 1920L * 1080 && area > bestArea) { bestArea = area; pick = i; }
            }
            if (cbRes.Items.Count > 0) cbRes.SelectedIndex = pick;
        }
        catch (Exception ex) { status.Text = "Could not read camera modes: " + ex.Message; }
    }

    void StartCam()
    {
        if (cbCam.SelectedIndex < 0) return;
        try
        {
            cam = new VideoCaptureDevice(devs[cbCam.SelectedIndex].MonikerString);
            if (caps.Length > 0 && cbRes.SelectedIndex >= 0) cam.VideoResolution = caps[cbRes.SelectedIndex];
            cam.NewFrame += OnFrame;
            cam.VideoSourceError += (s, e) => Ui(() => status.Text = "Camera error: " + e.Description);
            cam.Start();
            btnStart.Text = "Stop"; cbCam.Enabled = cbRes.Enabled = false;
        }
        catch (Exception ex) { status.Text = "Start failed: " + ex.Message; cam = null; }
    }

    void StopCam()
    {
        if (cam == null) return;
        try { cam.NewFrame -= OnFrame; cam.SignalToStop(); cam.WaitForStop(); } catch { }
        cam = null;
        btnStart.Text = "Start"; cbCam.Enabled = cbRes.Enabled = true;
    }

    void Ui(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ frame pipeline (camera thread)
    void OnFrame(object sender, NewFrameEventArgs e)
    {
        try
        {
            var src = e.Frame;
            int w = src.Width, h = src.Height;
            var an = Analyzer.Run(ToGray(src), w, h, zxOn);
            var shown = new Bitmap(src);
            Overlay(shown, an);
            if (snap) { snap = false; SaveSnapshot(src, an); }

            Bitmap map = null; string rep = null;
            if (!hold && an.Found && (DateTime.Now - lastUi).TotalMilliseconds > 200)
            {
                lastUi = DateTime.Now;
                if (an.Recv != null) map = RenderMap(an);
                rep = an.Report;
            }
            if (log != null && an.Found && (DateTime.Now - lastLog).TotalMilliseconds > 200)
            {
                lastLog = DateTime.Now;
                WriteLog(an);
            }
            Ui(() => UpdateUi(shown, an, map, rep));
        }
        catch (Exception ex) { Ui(() => status.Text = "Frame error: " + ex.Message); }
    }

    static byte[] ToGray(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var g = new byte[w * h];
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w * 3);
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = x * 3;
                    g[o + x] = (byte)((row[i + 2] * 77 + row[i + 1] * 150 + row[i] * 29) >> 8);
                }
            }
            return g;
        }
        finally { bmp.UnlockBits(data); }
    }

    static void Overlay(Bitmap bmp, Analysis a)
    {
        if (!a.Found) return;
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(a.Decoded ? Color.Lime : Color.Orange, 3);
        if (a.Quad != null) g.DrawPolygon(pen, a.Quad);
        if (a.Finders != null) foreach (var p in a.Finders) g.DrawEllipse(Pens.Cyan, p.X - 7, p.Y - 7, 14, 14);
        if (a.Diff != null && a.H != null)
        {
            float r = (float)Math.Max(3, a.ModulePx * 0.6);
            using var red = new SolidBrush(Color.FromArgb(170, 255, 0, 0));
            for (int y = 0; y < a.Size; y++)
                for (int x = 0; x < a.Size; x++)
                    if (a.Diff[y, x])
                    {
                        var p = Analyzer.MapPt(a.H, x + .5, y + .5);
                        g.FillEllipse(red, p.X - r / 2, p.Y - r / 2, r, r);
                    }
        }
    }

    static Bitmap RenderMap(Analysis a)
    {
        int n = a.Size, qz = 4, sc = Math.Max(4, 640 / (n + 2 * qz));
        int W = (n + 2 * qz) * sc;
        var bmp = new Bitmap(W, W);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        var cache = new Dictionary<int, SolidBrush>();
        SolidBrush B(Color c)
        {
            int k = c.ToArgb();
            if (!cache.TryGetValue(k, out var b)) cache[k] = b = new SolidBrush(c);
            return b;
        }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool ink = a.Recv[y, x];
                Color c;
                if (a.Diff != null && a.Diff[y, x]) c = ink ? Color.Red : Color.Magenta;
                else if (a.Margin[y, x] < 0.25f) c = ink ? Color.FromArgb(150, 130, 0) : Color.FromArgb(255, 250, 170);
                else if (a.Lay.Reg[y, x] != Zone.Data) c = ink ? Color.FromArgb(25, 45, 110) : Color.FromArgb(225, 235, 255);
                else c = ink ? Color.Black : Color.White;
                g.FillRectangle(B(c), (qz + x) * sc, (qz + y) * sc, sc, sc);
            }
        foreach (var b in cache.Values) b.Dispose();
        return bmp;
    }

    // ------------------------------------------------------------------ UI thread
    static int Pct(Queue<bool> q) => q.Count == 0 ? 0 : q.Count(x => x) * 100 / q.Count;
    static void Enq(Queue<bool> q, bool v) { q.Enqueue(v); while (q.Count > 60) q.Dequeue(); }

    void UpdateUi(Bitmap frame, Analysis an, Bitmap map, string rep)
    {
        var old = pbVideo.Image; pbVideo.Image = frame; old?.Dispose();
        Enq(detQ, an.Found); Enq(decQ, an.Decoded);
        frames++;
        var now = DateTime.Now;
        if ((now - fpsT).TotalSeconds >= 1) { fps = frames / (now - fpsT).TotalSeconds; frames = 0; fpsT = now; }
        status.Text = $"{fps:F1} fps | analysis {an.Ms:F0} ms | located {Pct(detQ)}% | decoded {Pct(decQ)}% (last {decQ.Count} frames) | " +
                      (an.Found ? (an.Decoded ? "LIVE: decoded" : "LIVE: located, NOT decodable") : "LIVE: no QR in view") +
                      (log != null ? " | logging" : "");
        if (map != null) { var o = pbMap.Image; pbMap.Image = map; o?.Dispose(); }
        if (rep != null && rep != lastReport) { SetReport(rep); lastReport = rep; }
    }

    /// <summary>Keep the QR map area square (height = width of the right column); the rest goes to the report.</summary>
    void AdjustMap()
    {
        int max = rightSplit.Height - rightSplit.Panel2MinSize - rightSplit.SplitterWidth;
        if (max < 60) return;
        int want = rightSplit.Width + legend.Height;
        try { rightSplit.SplitterDistance = Math.Max(rightSplit.Panel1MinSize, Math.Min(want, max)); } catch (InvalidOperationException) { }
    }

    void SetReport(string rep)
    {
        var green = Color.FromArgb(0, 150, 0); var red = Color.FromArgb(200, 0, 0);
        int pos = GetScrollPos(txt.Handle, 1);
        SendMessage(txt.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);          // WM_SETREDRAW off
        txt.Clear();
        using var bold = new Font(txt.Font, FontStyle.Bold);
        foreach (var raw in rep.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            Color c = Color.Black; Font fnt = txt.Font;
            if (line.StartsWith("\u2713")) c = green;
            else if (line.StartsWith("\u2717")) c = red;
            else if (line.StartsWith("=== RESULT: DECODED")) { c = green; fnt = bold; }
            else if (line.StartsWith("=== RESULT")) { c = red; fnt = bold; }
            else if (line.StartsWith("CHECKLIST"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+) of (\d+)");
                c = m.Success && m.Groups[1].Value == m.Groups[2].Value ? green : red; fnt = bold;
            }
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { c = Color.FromArgb(30, 60, 140); fnt = bold; }
            txt.SelectionStart = txt.TextLength; txt.SelectionLength = 0;
            txt.SelectionColor = c; txt.SelectionFont = fnt;
            txt.AppendText(line + "\n");
        }
        SendMessage(txt.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);           // redraw on
        SetScrollPos(txt.Handle, 1, pos, true);
        SendMessage(txt.Handle, 0x115, (IntPtr)(4 + 0x10000 * pos), IntPtr.Zero);
        txt.Invalidate();
    }

    // ------------------------------------------------------------------ logging / snapshots
    void ToggleLog()
    {
        if (chkLog.Checked)
        {
            Directory.CreateDirectory(OutDir);
            var path = Path.Combine(OutDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            log = new StreamWriter(path) { AutoFlush = true };
            log.WriteLine("time,decoded,version,ecc,mask,cw_errors,worst_block_pct,damaged_modules,marginal_modules,module_px,tilt_deg,rotation_deg,contrast_pct,sharp_pct,quiet_min,inverted,mirrored,zxing_ok,analysis_ms");
            status.Text = "Logging to " + path;
        }
        else { log?.Dispose(); log = null; }
    }

    void WriteLog(Analysis a)
    {
        var ci = CultureInfo.InvariantCulture;
        string ecc = a.Ecc >= 0 ? QrLayout.EccName[a.Ecc] : "?";
        string zx = a.ZxRan ? (a.ZxText != null ? "1" : "0") : "";
        try
        {
            log?.WriteLine(string.Join(",", DateTime.Now.ToString("HH:mm:ss.fff", ci), a.Decoded ? 1 : 0, a.Version, ecc, a.Mask, a.CwErrors, a.WorstPct,
                a.DamagedModules, a.MarginalModules, a.ModulePx.ToString("F2", ci), a.TiltDeg.ToString("F1", ci), a.RotDeg.ToString("F1", ci),
                a.ContrastPct.ToString("F0", ci), a.SharpPct.ToString("F0", ci), a.QuietMin, a.Inverted ? 1 : 0, a.Mirrored ? 1 : 0, zx, a.Ms.ToString("F0", ci)));
        }
        catch { }
    }

    void SaveSnapshot(Bitmap src, Analysis an)
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            var stem = Path.Combine(OutDir, $"snap_{DateTime.Now:yyyyMMdd_HHmmss}");
            src.Save(stem + ".png", ImageFormat.Png);
            File.WriteAllText(stem + ".txt", an.Report);
            Ui(() => status.Text = "Saved " + stem + ".png/.txt");
        }
        catch (Exception ex) { Ui(() => status.Text = "Snapshot failed: " + ex.Message); }
    }
}
