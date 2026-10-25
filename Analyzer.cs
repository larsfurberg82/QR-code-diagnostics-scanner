using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace QrScope;

public sealed class BlockInfo { public int Data, Ec, Errors; public bool Ok; }

public sealed class Dec
{
    public int Ecc, Mask, Errors, FailedBlocks;
    public bool Ok;
    public int[] Stream;
    public BlockInfo[] Blocks;
    public byte[] Data;
}

public sealed class Analysis
{
    public bool Found, Decoded, Inverted, Mirrored, FormatBrute, ZxRan;
    public PointF[] Finders, Quad;
    public int Version, Size, Mask = -1, Ecc = -1;
    public int Fmt1 = -1, Fmt2 = -1, Ver1 = -1, Ver2 = -1;
    public int CwErrors, WorstPct, DamagedModules, MarginalModules, QuietMin = -1;
    public double ModulePx, TiltDeg, RotDeg, ContrastPct, SharpPct = -1, Ms;
    public string Text = "", ZxText, Report = "";
    public double[] H;
    public QrLayout Lay;
    public bool[,] Recv, Diff;
    public float[,] Margin;
    public string Details;
    public List<(bool ok, string text)> Checks = new();
}

sealed class Cand { public double X, Y, Ms; public int N; }

public static class Analyzer
{
    static readonly string[] MaskDesc =
    {
        "(x+y) mod 2 = 0", "y mod 2 = 0", "x mod 3 = 0", "(x+y) mod 3 = 0", "(x/3 + y/2) mod 2 = 0",
        "(x*y) mod 2 + (x*y) mod 3 = 0", "((x*y) mod 2 + (x*y) mod 3) mod 2 = 0", "((x+y) mod 2 + (x*y) mod 3) mod 2 = 0"
    };
    static readonly string[] ZoneName = { "data/EC", "finder", "separator", "timing", "alignment", "format info", "version info", "dark module" };
    static readonly string[] Side = { "top", "right", "bottom", "left" };
    static readonly int[] FmtTab = Enumerable.Range(0, 32).Select(k => QrLayout.FormatBits(k >> 3, k & 7)).ToArray();
    static readonly bool[] P3 = { true, false, true, true, true, false, true, false, false, false, false };

    static int Pop(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    // ===================================================================== entry point
    public static Analysis Run(byte[] g, int w, int h, bool zx)
    {
        var sw = Stopwatch.StartNew();
        Cand[] tri = null;
        for (int pass = 0; pass < 2 && tri == null; pass++)
            tri = PickTriple(FindFinders(Binarize(g, w, h, pass == 1), w, h));

        Analysis best = null;
        if (tri == null)
        {
            best = new Analysis { Report = "No QR finder patterns found in this frame." };
        }
        else
        {
            Cand A = tri[0], B = tri[1], C = tri[2];
            double cross = (B.X - A.X) * (C.Y - A.Y) - (B.Y - A.Y) * (C.X - A.X);
            Cand tr = cross > 0 ? B : C, bl = cross > 0 ? C : B;

            var cands = new List<(double s, int v, bool mir)>();
            for (int m = 0; m < 2; m++)
                for (int v = 1; v <= 40; v++)
                    cands.Add((m == 0 ? Stage1(g, w, h, A, tr, bl, v) : Stage1(g, w, h, A, bl, tr, v), v, m == 1));

            foreach (var c in cands.OrderBy(c => c.s).Take(3))
            {
                if (c.s >= 1e8) continue;
                var a = c.mir ? Deep(g, w, h, A, bl, tr, c.v, true) : Deep(g, w, h, A, tr, bl, c.v, false);
                if (best == null) best = a;
                if (a.Decoded) { best = a; break; }
            }
            best ??= new Analysis
            {
                Found = true,
                Finders = new[] { new PointF((float)A.X, (float)A.Y), new PointF((float)B.X, (float)B.Y), new PointF((float)C.X, (float)C.Y) },
                Report = "Finder patterns located, but no plausible QR version could be matched (format info unreadable at every size)."
            };
        }

        if (zx && best.Found)
        {
            best.ZxRan = true;
            best.ZxText = ZxDecode(g, w, h);
            if (best.Details != null)
            {
                best.Checks.Add((best.ZxText != null, best.ZxText != null ? "Reference decoder (ZXing) reads this frame" : "Reference decoder (ZXing) FAILED on this frame"));
                var sb = new StringBuilder(best.Details);
                sb.AppendLine().AppendLine("REFERENCE DECODER (ZXing.Net, TryHarder)");
                sb.AppendLine("  " + (best.ZxText != null ? "reads it: " + Clip(best.ZxText, 120) : "FAILED to read this frame"));
                if (best.ZxText != null && best.Decoded && best.ZxText != best.Text) sb.AppendLine("  NOTE: ZXing text differs from our decode (ECI/charset handling?).");
                if (best.ZxText == null && best.Decoded) sb.AppendLine("  NOTE: our RS decode succeeded but ZXing did not: a typical scanner may struggle with detection/geometry.");
                best.Details = sb.ToString();
            }
        }
        if (best.Details != null) best.Report = Compose(best);
        best.Ms = sw.Elapsed.TotalMilliseconds;
        return best;
    }

    static IEnumerable<string> Wrap(string t, int width)
    {
        for (int i = 0; i < t.Length; i += width) yield return t.Substring(i, Math.Min(width, t.Length - i));
    }

    static string Compose(Analysis a)
    {
        var sb = new StringBuilder();
        sb.AppendLine(a.Decoded ? "=== RESULT: DECODED ===" : "=== RESULT: LOCATED BUT NOT DECODABLE ===");
        sb.AppendLine();
        sb.AppendLine("MESSAGE");
        string msg = null;
        if (a.Decoded) { msg = a.Text; if (msg.Length == 0) sb.AppendLine("  (empty message)"); }
        else if (a.ZxText != null) { sb.AppendLine("  (our decoder failed; ZXing read this:)"); msg = a.ZxText; }
        else sb.AppendLine("  (not decodable in this frame)");
        if (!string.IsNullOrEmpty(msg))
        {
            string esc = msg.Length > 600 ? msg.Substring(0, 600) + "..." : msg;
            esc = esc.Replace("\r", "\\r").Replace("\n", "\\n");
            foreach (var l in Wrap(esc, 88)) sb.AppendLine("  " + l);
        }
        sb.AppendLine();
        int pass = a.Checks.Count(c => c.ok), tot = a.Checks.Count;
        sb.AppendLine($"CHECKLIST: {pass} of {tot} checks passed");
        foreach (var c in a.Checks) sb.AppendLine((c.ok ? "\u2713 " : "\u2717 ") + c.text);
        sb.AppendLine();
        sb.Append(a.Details);
        return sb.ToString();
    }

    static string ZxDecode(byte[] g, int w, int h)
    {
        try
        {
            var r = new ZXing.BarcodeReaderGeneric
            {
                Options = new ZXing.Common.DecodingOptions
                {
                    PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE },
                    TryHarder = true,
                    TryInverted = true
                }
            };
            return r.Decode(new ZXing.PlanarYUVLuminanceSource(g, w, h, 0, 0, w, h, false))?.Text;
        }
        catch { return null; }
    }

    static string Clip(string s, int max)
    {
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > max ? s.Substring(0, max) + "..." : s;
    }

    // ===================================================================== image helpers
    static double Lum(byte[] g, int w, int h, double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        int x0 = Math.Clamp(ix, 0, w - 1), x1 = Math.Clamp(ix + 1, 0, w - 1);
        int y0 = Math.Clamp(iy, 0, h - 1), y1 = Math.Clamp(iy + 1, 0, h - 1);
        return (g[y0 * w + x0] * (1 - fx) + g[y0 * w + x1] * fx) * (1 - fy) + (g[y1 * w + x0] * (1 - fx) + g[y1 * w + x1] * fx) * fy;
    }

    static bool[] Binarize(byte[] g, int w, int h, bool inv)
    {
        int r = Math.Max(8, Math.Min(w, h) / 16);
        int W1 = w + 1;
        var I = new int[W1 * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int rs = 0;
            for (int x = 0; x < w; x++) { rs += g[y * w + x]; I[(y + 1) * W1 + x + 1] = I[y * W1 + x + 1] + rs; }
        }
        var b = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long s = I[y1 * W1 + x1] - I[y0 * W1 + x1] - I[y1 * W1 + x0] + I[y0 * W1 + x0];
                double mean = (double)s / ((y1 - y0) * (x1 - x0));
                double diff = inv ? g[y * w + x] - mean : mean - g[y * w + x];
                b[y * w + x] = diff > 10;
            }
        }
        return b;
    }

    // ===================================================================== finder pattern detection
    static bool Ratio(int a, int b, int c, int d, int e, out double ms)
    {
        int t = a + b + c + d + e;
        ms = t / 7.0;
        if (a == 0 || b == 0 || c == 0 || d == 0 || e == 0 || t < 7) return false;
        double v = ms * 0.6;
        return Math.Abs(a - ms) < v && Math.Abs(b - ms) < v && Math.Abs(c - 3 * ms) < 3 * v && Math.Abs(d - ms) < v && Math.Abs(e - ms) < v;
    }

    static bool Cross(bool[] bin, int w, int h, int x, int y, int dx, int dy, out double center, out double ms)
    {
        center = 0; ms = 0;
        bool In(int a, int b) => a >= 0 && b >= 0 && a < w && b < h;
        if (!In(x, y) || !bin[y * w + x]) return false;
        var c = new int[5];
        int px = x, py = y;
        while (In(px, py) && bin[py * w + px]) { c[2]++; px -= dx; py -= dy; }
        int back = c[2];
        if (!In(px, py)) return false;
        while (In(px, py) && !bin[py * w + px]) { c[1]++; px -= dx; py -= dy; }
        if (!In(px, py)) return false;
        while (In(px, py) && bin[py * w + px]) { c[0]++; px -= dx; py -= dy; }
        px = x + dx; py = y + dy;
        int fwd = 0;
        while (In(px, py) && bin[py * w + px]) { c[2]++; fwd++; px += dx; py += dy; }
        if (!In(px, py)) return false;
        while (In(px, py) && !bin[py * w + px]) { c[3]++; px += dx; py += dy; }
        if (!In(px, py)) return false;
        while (In(px, py) && bin[py * w + px]) { c[4]++; px += dx; py += dy; }
        if (!Ratio(c[0], c[1], c[2], c[3], c[4], out ms)) return false;
        double pos = dx != 0 ? x : y;
        center = ((pos - (back - 1)) + (pos + fwd) + 1) / 2.0;
        return true;
    }

    static void AddCand(List<Cand> l, double x, double y, double ms)
    {
        foreach (var c in l)
            if (Math.Abs(c.X - x) <= c.Ms * 1.2 && Math.Abs(c.Y - y) <= c.Ms * 1.2)
            {
                c.X = (c.X * c.N + x) / (c.N + 1); c.Y = (c.Y * c.N + y) / (c.N + 1); c.Ms = (c.Ms * c.N + ms) / (c.N + 1); c.N++;
                return;
            }
        if (l.Count < 300) l.Add(new Cand { X = x, Y = y, Ms = ms, N = 1 });
    }

    static List<Cand> FindFinders(bool[] bin, int w, int h)
    {
        var res = new List<Cand>();
        var st = new int[w + 2]; var ln = new int[w + 2];
        for (int y = 0; y < h; y++)
        {
            int o = y * w, nr = 0, s = 0;
            for (int x = 1; x <= w; x++)
                if (x == w || bin[o + x] != bin[o + x - 1]) { st[nr] = s; ln[nr] = x - s; nr++; s = x; }
            bool c0 = bin[o];
            for (int k = 0; k + 4 < nr; k++)
            {
                if (!(c0 ^ ((k & 1) == 1))) continue;            // run k must be dark
                if (!Ratio(ln[k], ln[k + 1], ln[k + 2], ln[k + 3], ln[k + 4], out double ms)) continue;
                double cx = st[k + 2] + ln[k + 2] / 2.0;
                if (!Cross(bin, w, h, (int)cx, y, 0, 1, out double cy, out double ms2)) continue;
                if (!Cross(bin, w, h, (int)cx, (int)cy, 1, 0, out double cx2, out double ms3)) continue;
                AddCand(res, cx2, cy, (ms + ms2 + ms3) / 3);
            }
        }
        res.RemoveAll(c => c.N < 2);
        res.Sort((a, b) => b.N.CompareTo(a.N));
        if (res.Count > 12) res.RemoveRange(12, res.Count - 12);
        return res;
    }

    static double Dist(Cand a, Cand b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Returns {corner(TL), B, C} of the best right-angle triple.</summary>
    static Cand[] PickTriple(List<Cand> c)
    {
        Cand[] best = null; double bs = 1e9;
        for (int i = 0; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++)
                for (int k = j + 1; k < c.Count; k++)
                {
                    var p = new[] { c[i], c[j], c[k] };
                    double dij = Dist(p[0], p[1]), dik = Dist(p[0], p[2]), djk = Dist(p[1], p[2]);
                    int corner; double l1, l2, hyp;
                    if (djk >= dij && djk >= dik) { corner = 0; l1 = dij; l2 = dik; hyp = djk; }
                    else if (dik >= dij) { corner = 1; l1 = dij; l2 = djk; hyp = dik; }
                    else { corner = 2; l1 = dik; l2 = djk; hyp = dij; }
                    double r = Math.Min(l1, l2) / Math.Max(l1, l2);
                    if (r < 0.55) continue;
                    double avg = (l1 + l2) / 2, hr = hyp / avg;
                    if (hr < 1.15 || hr > 1.75) continue;
                    Cand A = p[corner], B = p[(corner + 1) % 3], C = p[(corner + 2) % 3];
                    double cos = ((B.X - A.X) * (C.X - A.X) + (B.Y - A.Y) * (C.Y - A.Y)) / (Dist(A, B) * Dist(A, C));
                    if (Math.Abs(cos) > 0.4) continue;
                    double msMax = Math.Max(p[0].Ms, Math.Max(p[1].Ms, p[2].Ms)), msMin = Math.Min(p[0].Ms, Math.Min(p[1].Ms, p[2].Ms));
                    if (msMax / msMin > 1.7) continue;
                    if (avg / ((p[0].Ms + p[1].Ms + p[2].Ms) / 3) < 10) continue;
                    double sc = (1 - r) + Math.Abs(hr - 1.414) + Math.Abs(cos) + (msMax / msMin - 1) * 0.5;
                    if (sc < bs) { bs = sc; best = new[] { A, B, C }; }
                }
        return best;
    }

    // ===================================================================== geometry
    static double[] Aff(Cand tl, Cand tr, Cand bl, int n)
    {
        double s = n - 7;
        return new[] { tl.X, tl.Y, (tr.X - tl.X) / s, (tr.Y - tl.Y) / s, (bl.X - tl.X) / s, (bl.Y - tl.Y) / s };
    }

    static (double x, double y) MapA(double[] a, double u, double v) =>
        (a[0] + (u - 3.5) * a[2] + (v - 3.5) * a[4], a[1] + (u - 3.5) * a[3] + (v - 3.5) * a[5]);

    public static (double x, double y) Apply(double[] H, double u, double v)
    {
        double d = H[6] * u + H[7] * v + H[8];
        return ((H[0] * u + H[1] * v + H[2]) / d, (H[3] * u + H[4] * v + H[5]) / d);
    }

    public static PointF MapPt(double[] H, double u, double v)
    {
        var (x, y) = Apply(H, u, v);
        return new PointF((float)x, (float)y);
    }

    static double[] Homography(List<double[]> p)
    {
        var M = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double u = p[i][0], v = p[i][1], x = p[i][2], y = p[i][3];
            M[2 * i, 0] = u; M[2 * i, 1] = v; M[2 * i, 2] = 1; M[2 * i, 6] = -x * u; M[2 * i, 7] = -x * v; M[2 * i, 8] = x;
            M[2 * i + 1, 3] = u; M[2 * i + 1, 4] = v; M[2 * i + 1, 5] = 1; M[2 * i + 1, 6] = -y * u; M[2 * i + 1, 7] = -y * v; M[2 * i + 1, 8] = y;
        }
        for (int c = 0; c < 8; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 8; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
            if (Math.Abs(M[piv, c]) < 1e-12) return null;
            for (int k = 0; k < 9; k++) (M[c, k], M[piv, k]) = (M[piv, k], M[c, k]);
            double d = M[c, c];
            for (int k = c; k < 9; k++) M[c, k] /= d;
            for (int r = 0; r < 8; r++)
                if (r != c) { double f = M[r, c]; if (f != 0) for (int k = c; k < 9; k++) M[r, k] -= f * M[c, k]; }
        }
        return new[] { M[0, 8], M[1, 8], M[2, 8], M[3, 8], M[4, 8], M[5, 8], M[6, 8], M[7, 8], 1.0 };
    }

    /// <summary>Mean luminance of finder cores (dark) and of the light ring, using the affine estimate.</summary>
    static (double D, double L) FinderLevels(byte[] g, int w, int h, double[] aff, int n)
    {
        double D = 0, L = 0;
        var cs = new[] { (3.5, 3.5), (n - 3.5, 3.5), (3.5, n - 3.5) };
        foreach (var (cu, cv) in cs)
            for (int dv = -2; dv <= 2; dv++)
                for (int du = -2; du <= 2; du++)
                {
                    var (px, py) = MapA(aff, cu + du, cv + dv);
                    double l = Lum(g, w, h, px, py);
                    int m = Math.Max(Math.Abs(du), Math.Abs(dv));
                    if (m <= 1) D += l / 27; else L += l / 48;
                }
        return (D, L);
    }

    /// <summary>Cheap version/orientation scoring using only format/version info regions (low = good).</summary>
    static double Stage1(byte[] g, int w, int h, Cand tl, Cand tr, Cand bl, int v)
    {
        int n = 17 + 4 * v;
        var aff = Aff(tl, tr, bl, n);
        var (D, L) = FinderLevels(g, w, h, aff, n);
        if (Math.Abs(L - D) < 12) return 1e9;
        bool inv = D > L; double thr = (D + L) / 2;
        bool Dark(int x, int y)
        {
            var (px, py) = MapA(aff, x + .5, y + .5);
            double l = Lum(g, w, h, px, py);
            return inv ? l > thr : l < thr;
        }
        int f0 = 0, f1 = 0;
        for (int i = 0; i < 15; i++)
        {
            var (x0, y0) = QrLayout.FormatXY(n, 0, i); if (Dark(x0, y0)) f0 |= 1 << i;
            var (x1, y1) = QrLayout.FormatXY(n, 1, i); if (Dark(x1, y1)) f1 |= 1 << i;
        }
        int d0 = 99, d1 = 99;
        foreach (int t in FmtTab) { d0 = Math.Min(d0, Pop(f0 ^ t)); d1 = Math.Min(d1, Pop(f1 ^ t)); }
        double s = d0 + d1;
        if (v >= 7)
        {
            int vb = QrLayout.VersionBits(v), v0 = 0, v1 = 0;
            for (int i = 0; i < 18; i++)
            {
                var (x0, y0) = QrLayout.VersionXY(n, 0, i); if (Dark(x0, y0)) v0 |= 1 << i;
                var (x1, y1) = QrLayout.VersionXY(n, 1, i); if (Dark(x1, y1)) v1 |= 1 << i;
            }
            s += Pop(v0 ^ vb) + Pop(v1 ^ vb);
        }
        return s;
    }

    // ===================================================================== decode
    static Dec TryDecode(QrLayout L, bool[,] recv, int ecc, int mask)
    {
        int nb = L.RawCodewords;
        var stream = new int[nb];
        for (int i = 0; i < nb * 8; i++)
        {
            int x = L.Order[2 * i], y = L.Order[2 * i + 1];
            if (recv[y, x] ^ QrLayout.MaskBit(mask, x, y)) stream[i >> 3] |= 0x80 >> (i & 7);
        }
        var map = L.BlockMap(ecc);
        int ecLen = QrLayout.EcPerBlock(L.Version, ecc);
        var d = new Dec { Ecc = ecc, Mask = mask, Stream = (int[])stream.Clone(), Blocks = new BlockInfo[map.Length] };
        var data = new List<byte>();
        for (int j = 0; j < map.Length; j++)
        {
            var idx = map[j];
            var cw = new int[idx.Length];
            for (int k = 0; k < idx.Length; k++) cw[k] = stream[idx[k]];
            var pos = new List<int>();
            int e = Rs.Decode(cw, ecLen, pos);
            var bi = new BlockInfo { Data = idx.Length - ecLen, Ec = ecLen };
            if (e < 0) { d.FailedBlocks++; }
            else
            {
                bi.Ok = true; bi.Errors = e; d.Errors += e;
                for (int k = 0; k < idx.Length; k++) d.Stream[idx[k]] = cw[k];
            }
            d.Blocks[j] = bi;
            for (int k = 0; k < bi.Data; k++) data.Add((byte)cw[k]);
        }
        d.Ok = d.FailedBlocks == 0;
        d.Data = data.ToArray();
        return d;
    }

    static string ParseData(int ver, byte[] d, out string segs, out int usedBits, out string pad)
    {
        var sb = new StringBuilder(); var sg = new List<string>(); var bytes = new List<byte>();
        int p = 0, tot = d.Length * 8;
        int Bits(int c) { int v = 0; for (int i = 0; i < c; i++) { v = (v << 1) | ((d[p >> 3] >> (7 - (p & 7))) & 1); p++; } return v; }
        int Cc(int a, int b, int c) => ver <= 9 ? a : ver <= 26 ? b : c;
        void Flush()
        {
            if (bytes.Count == 0) return;
            try { sb.Append(new UTF8Encoding(false, true).GetString(bytes.ToArray())); }
            catch { sb.Append(Encoding.Latin1.GetString(bytes.ToArray())); }
            bytes.Clear();
        }
        const string AN = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        bool term = false;
        while (tot - p >= 4)
        {
            int mode = Bits(4);
            if (mode == 0) { term = true; break; }
            if (mode == 1)
            {
                int c = Bits(Cc(10, 12, 14)); int need = 10 * (c / 3) + (c % 3 == 1 ? 4 : c % 3 == 2 ? 7 : 0);
                if (need > tot - p) { sg.Add("numeric (truncated)"); break; }
                sg.Add($"Numeric x{c}"); Flush();
                for (int i = 0; i < c / 3; i++) sb.Append(Bits(10).ToString("D3"));
                if (c % 3 == 2) sb.Append(Bits(7).ToString("D2")); else if (c % 3 == 1) sb.Append(Bits(4).ToString("D1"));
            }
            else if (mode == 2)
            {
                int c = Bits(Cc(9, 11, 13)); int need = 11 * (c / 2) + 6 * (c % 2);
                if (need > tot - p) { sg.Add("alphanumeric (truncated)"); break; }
                sg.Add($"Alphanumeric x{c}"); Flush();
                for (int i = 0; i < c / 2; i++) { int v = Bits(11); sb.Append(AN[Math.Min(44, v / 45)]).Append(AN[v % 45]); }
                if (c % 2 == 1) sb.Append(AN[Math.Min(44, Bits(6))]);
            }
            else if (mode == 4)
            {
                int c = Bits(Cc(8, 16, 16));
                if (8 * c > tot - p) { sg.Add("byte (truncated)"); break; }
                sg.Add($"Byte x{c}");
                for (int i = 0; i < c; i++) bytes.Add((byte)Bits(8));
            }
            else if (mode == 8)
            {
                int c = Bits(Cc(8, 10, 12));
                if (13 * c > tot - p) { sg.Add("kanji (truncated)"); break; }
                sg.Add($"Kanji x{c} (not decoded)"); p += 13 * c;
            }
            else if (mode == 7)
            {
                int f = Bits(8), val = (f & 0x80) == 0 ? f : (f & 0xC0) == 0x80 ? ((f & 0x3F) << 8) | Bits(8) : ((f & 0x1F) << 16) | Bits(16);
                sg.Add($"ECI {val}");
            }
            else if (mode == 3) { Bits(16); sg.Add("Structured append"); }
            else if (mode == 5) sg.Add("FNC1 (1st)");
            else if (mode == 9) { Bits(8); sg.Add("FNC1 (2nd)"); }
            else { sg.Add($"INVALID mode 0b{Convert.ToString(mode, 2).PadLeft(4, '0')}"); break; }
        }
        Flush();
        usedBits = p;
        segs = sg.Count == 0 ? "(none)" : string.Join(", ", sg);
        pad = "n/a";
        if (term)
        {
            int pb = (8 - p % 8) % 8;
            bool ok = pb == 0 || Bits(pb) == 0;
            int first = p / 8, bad = -1;
            for (int i = first; i < d.Length; i++) if (d[i] != (((i - first) % 2 == 0) ? 0xEC : 0x11)) { bad = i; break; }
            pad = !ok ? "non-zero bits before first pad byte" : bad >= 0 ? $"non-standard pad byte at data codeword {bad} (expected alternating 0xEC/0x11)" : "OK (terminator + alternating 0xEC/0x11)";
        }
        else pad = "no terminator (data fills capacity)";
        return sb.ToString();
    }

    static int Penalty(bool[,] m, int n)
    {
        int res = 0;
        for (int dir = 0; dir < 2; dir++)
            for (int a = 0; a < n; a++)
            {
                bool Get(int b) => dir == 0 ? m[a, b] : m[b, a];
                bool prev = Get(0); int run = 1;
                for (int b = 1; b < n; b++)
                {
                    bool c = Get(b);
                    if (c == prev) run++; else { if (run >= 5) res += 3 + run - 5; run = 1; prev = c; }
                }
                if (run >= 5) res += 3 + run - 5;
                for (int b = 0; b + 11 <= n; b++)
                {
                    bool p1 = true, p2 = true;
                    for (int k = 0; k < 11; k++) { bool c = Get(b + k); if (c != P3[k]) p1 = false; if (c != P3[10 - k]) p2 = false; }
                    if (p1) res += 40;
                    if (p2) res += 40;
                }
            }
        int dark = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (m[y, x]) dark++;
                if (x < n - 1 && y < n - 1 && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) res += 3;
            }
        double pct = dark * 100.0 / (n * n);
        int pk = (int)(pct / 5) * 5;
        res += Math.Min(Math.Abs(pk - 50), Math.Abs(pk + 5 - 50)) / 5 * 10;
        return res;
    }

    // ===================================================================== deep analysis of one candidate
    static Analysis Deep(byte[] g, int w, int h, Cand tl, Cand tr, Cand bl, int v, bool mir)
    {
        var a = new Analysis { Found = true, Version = v, Mirrored = mir };
        var L = QrLayout.Get(v);
        int n = L.Size; a.Size = n; a.Lay = L;
        a.Finders = new[] { new PointF((float)tl.X, (float)tl.Y), new PointF((float)tr.X, (float)tr.Y), new PointF((float)bl.X, (float)bl.Y) };

        var aff = Aff(tl, tr, bl, n);
        var (D0, L0) = FinderLevels(g, w, h, aff, n);
        bool inv0 = D0 > L0;

        // --- homography from 3 finders + (bottom-right alignment pattern, or synthetic 4th point)
        var corr = new List<double[]> { new[] { 3.5, 3.5, tl.X, tl.Y }, new[] { n - 3.5, 3.5, tr.X, tr.Y }, new[] { 3.5, n - 3.5, bl.X, bl.Y } };
        bool alignOk = false;
        if (v >= 2)
        {
            double Score(double cu, double cv)
            {
                double sl = 0, sd = 0;
                for (int j = -2; j <= 2; j++)
                    for (int i = -2; i <= 2; i++)
                    {
                        var (px, py) = MapA(aff, cu + i, cv + j);
                        double l = Lum(g, w, h, px, py);
                        if (Math.Max(Math.Abs(i), Math.Abs(j)) == 1) sl += l; else sd += l;
                    }
                double s = sl / 8 - sd / 17;
                return inv0 ? -s : s;
            }
            double c0 = n - 6.5, bu = c0, bv = c0, bs = double.MinValue;
            for (double dv = -3; dv <= 3; dv += 0.5)
                for (double du = -3; du <= 3; du += 0.5)
                { double s = Score(c0 + du, c0 + dv); if (s > bs) { bs = s; bu = c0 + du; bv = c0 + dv; } }
            double cu1 = bu, cv1 = bv;
            for (double dv = -0.5; dv <= 0.5; dv += 0.125)
                for (double du = -0.5; du <= 0.5; du += 0.125)
                { double s = Score(cu1 + du, cv1 + dv); if (s > bs) { bs = s; bu = cu1 + du; bv = cv1 + dv; } }
            if (bs > 0.35 * Math.Abs(L0 - D0))
            {
                var (px, py) = MapA(aff, bu, bv);
                corr.Add(new[] { n - 6.5, n - 6.5, px, py });
                alignOk = true;
            }
        }
        if (!alignOk) { var (px, py) = MapA(aff, n - 3.5, n - 3.5); corr.Add(new[] { n - 3.5, n - 3.5, px, py }); }
        var H = Homography(corr);
        if (H == null) { a.Report = "Degenerate geometry."; return a; }
        a.H = H;

        // --- sample module luminance grid
        var lum = new double[n, n];
        double[] ou = { 0, .25, -.25, 0, 0 }, ov = { 0, 0, 0, .25, -.25 };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double s = 0;
                for (int k = 0; k < 5; k++) { var (px, py) = Apply(H, x + .5 + ou[k], y + .5 + ov[k]); s += Lum(g, w, h, px, py); }
                lum[y, x] = s / 5;
            }

        // --- per-finder dark/light levels -> threshold planes
        var fc = new[] { (3, 3), (n - 4, 3), (3, n - 4) };
        var fd = new double[3]; var fl = new double[3];
        double dRing = 0, dCore = 0;
        for (int k = 0; k < 3; k++)
        {
            var (cx, cy) = fc[k];
            double sd = 0, sl = 0, sr = 0;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    double l = lum[cy + dy, cx + dx];
                    if (m <= 1) sd += l; else if (m == 2) sl += l; else sr += l;
                }
            fd[k] = sd / 9; fl[k] = sl / 16; dRing += sr / 24 / 3; dCore += lum[cy, cx] / 3;
        }
        double mD = fd.Average(), mL = fl.Average();
        bool inv = mD > mL; a.Inverted = inv;
        a.ContrastPct = Math.Abs(mL - mD) / 255 * 100;
        double Plane(double[] f, double u, double v2) { double s = n - 7; return f[0] + (f[1] - f[0]) * (u - 3.5) / s + (f[2] - f[0]) * (v2 - 3.5) / s; }
        (double thr, double hc) Th(double u, double v2)
        {
            double d = Plane(fd, u, v2), l = Plane(fl, u, v2);
            return ((d + l) / 2, Math.Max(4, Math.Abs(l - d) / 2));
        }

        var recv = new bool[n, n]; var mar = new float[n, n];
        int marg = 0, lightN = 0, clipHi = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (thr, hc) = Th(x + .5, y + .5);
                double l = lum[y, x];
                bool ink = inv ? l > thr : l < thr;
                recv[y, x] = ink;
                float m = (float)(Math.Abs(l - thr) / hc);
                mar[y, x] = m;
                if (m < 0.25f) marg++;
                if (!ink) { lightN++; if (l >= 250) clipHi++; }
            }
        a.Recv = recv; a.Margin = mar; a.MarginalModules = marg;

        // --- format info
        var fr = new int[2];
        for (int i = 0; i < 15; i++)
        {
            var (x0, y0) = QrLayout.FormatXY(n, 0, i); if (recv[y0, x0]) fr[0] |= 1 << i;
            var (x1, y1) = QrLayout.FormatXY(n, 1, i); if (recv[y1, x1]) fr[1] |= 1 << i;
        }
        int[] bk = { -1, -1 }, bd = { 99, 99 };
        for (int c = 0; c < 2; c++)
            for (int k = 0; k < 32; k++) { int d = Pop(fr[c] ^ FmtTab[k]); if (d < bd[c]) { bd[c] = d; bk[c] = k; } }
        a.Fmt1 = bd[0]; a.Fmt2 = bd[1];
        var ks = new List<int>();
        if (bd[0] <= 3) ks.Add(bk[0]);
        if (bd[1] <= 3 && !ks.Contains(bk[1])) ks.Add(bk[1]);
        if (ks.Count == 0) { a.FormatBrute = true; ks = Enumerable.Range(0, 32).ToList(); }

        Dec dec = null;
        foreach (int k in ks)
        {
            var r = TryDecode(L, recv, k >> 3, k & 7);
            if (dec == null || (r.Ok && (!dec.Ok || r.Errors < dec.Errors)) || (!r.Ok && !dec.Ok && r.FailedBlocks < dec.FailedBlocks)) dec = r;
        }
        a.Decoded = dec.Ok;
        if (dec.Ok || !a.FormatBrute) { a.Ecc = dec.Ecc; a.Mask = dec.Mask; }

        if (v >= 7)
        {
            int vb = QrLayout.VersionBits(v), v0 = 0, v1 = 0;
            for (int i = 0; i < 18; i++)
            {
                var (x0, y0) = QrLayout.VersionXY(n, 0, i); if (recv[y0, x0]) v0 |= 1 << i;
                var (x1, y1) = QrLayout.VersionXY(n, 1, i); if (recv[y1, x1]) v1 |= 1 << i;
            }
            a.Ver1 = Pop(v0 ^ vb); a.Ver2 = Pop(v1 ^ vb);
        }

        // --- ideal symbol and damage map
        var ideal = L.BuildIdeal(dec.Ok ? dec.Stream : null, a.Ecc, a.Mask, recv);
        var diff = new bool[n, n];
        var dReg = new int[8]; var tReg = new int[8];
        int dmg = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                tReg[(int)L.Reg[y, x]]++;
                if (ideal[y, x] != recv[y, x]) { diff[y, x] = true; dReg[(int)L.Reg[y, x]]++; dmg++; }
            }
        int nbits = L.RawCodewords * 8, remDiff = 0, remTot = 0;
        for (int i = nbits; i < L.Order.Length / 2; i++) { remTot++; if (diff[L.Order[2 * i + 1], L.Order[2 * i]]) remDiff++; }
        a.Diff = diff; a.DamagedModules = dmg; a.CwErrors = dec.Errors;
        a.WorstPct = 0;
        foreach (var b in dec.Blocks) a.WorstPct = Math.Max(a.WorstPct, b.Ok ? b.Errors * 100 / Math.Max(1, b.Ec / 2) : 100 + 1);

        // --- quiet zone
        var qz = new int[4]; var lim = new bool[4];
        double lqSum = 0; int lqN = 0;
        for (int s = 0; s < 4; s++)
        {
            int k;
            for (k = 1; k <= 6; k++)
            {
                int dk = 0; bool outside = false; double sl = 0;
                for (int t = 0; t < n; t++)
                {
                    double u, vv;
                    switch (s)
                    {
                        case 0: u = t + .5; vv = -k + .5; break;
                        case 1: u = n + k - .5; vv = t + .5; break;
                        case 2: u = t + .5; vv = n + k - .5; break;
                        default: u = -k + .5; vv = t + .5; break;
                    }
                    var (px, py) = Apply(H, u, vv);
                    if (px < 0 || py < 0 || px >= w || py >= h) { outside = true; break; }
                    double l = Lum(g, w, h, px, py); sl += l;
                    var (thr, _) = Th(u, vv);
                    if (inv ? l > thr : l < thr) dk++;
                }
                if (outside) { lim[s] = true; break; }
                if (dk > Math.Max(1, n * 0.04)) break;
                if (k >= 2 && k <= 3) { lqSum += sl / n; lqN++; }
            }
            qz[s] = k - 1;
        }
        a.QuietMin = qz.Min();
        if (lqN > 0 && Math.Abs(lqSum / lqN - dCore) > 5)
            a.SharpPct = Math.Min(100, Math.Abs(mL - dRing) / Math.Abs(lqSum / lqN - dCore) * 100);

        // --- geometry
        var P00 = Apply(H, 0, 0); var P10 = Apply(H, n, 0); var P11 = Apply(H, n, n); var P01 = Apply(H, 0, n);
        a.Quad = new[] { MapPt(H, 0, 0), MapPt(H, n, 0), MapPt(H, n, n), MapPt(H, 0, n) };
        double Ds((double x, double y) p, (double x, double y) q) => Math.Sqrt((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y));
        double top = Ds(P00, P10), bot = Ds(P01, P11), lef = Ds(P00, P01), rig = Ds(P10, P11);
        a.ModulePx = (top + bot + lef + rig) / 4 / n;
        a.RotDeg = Math.Atan2(P10.y - P00.y, P10.x - P00.x) * 180 / Math.PI;
        double cc = n / 2.0;
        var pu1 = Apply(H, cc + .5, cc); var pu0 = Apply(H, cc - .5, cc); var pv1 = Apply(H, cc, cc + .5); var pv0 = Apply(H, cc, cc - .5);
        double jA = pu1.x - pu0.x, jB = pv1.x - pv0.x, jC = pu1.y - pu0.y, jD = pv1.y - pv0.y;
        double S2 = jA * jA + jB * jB + jC * jC + jD * jD, det = Math.Abs(jA * jD - jB * jC);
        double disc = Math.Sqrt(Math.Max(0, S2 * S2 - 4 * det * det));
        double s1 = Math.Sqrt((S2 + disc) / 2), s2 = Math.Sqrt(Math.Max(0, (S2 - disc) / 2));
        a.TiltDeg = Math.Acos(Math.Min(1, s2 / s1)) * 180 / Math.PI;
        double shearDeg = Math.Acos(Math.Clamp(((P10.x - P00.x) * (P01.x - P00.x) + (P10.y - P00.y) * (P01.y - P00.y)) / (top * lef), -1, 1)) * 180 / Math.PI;
        double illum = (fl.Max() - fl.Min()) / Math.Max(1, fl.Average()) * 100;
        double clipPct = lightN > 0 ? clipHi * 100.0 / lightN : 0;

        // --- content, penalties
        string text = "", segs = "", pad = "";
        int used = 0;
        var pen = new int[8]; int bestMask = -1;
        if (dec.Ok)
        {
            text = ParseData(v, dec.Data, out segs, out used, out pad);
            a.Text = text;
            for (int m = 0; m < 8; m++) pen[m] = Penalty(L.BuildIdeal(dec.Stream, dec.Ecc, m, null), n);
            bestMask = Array.IndexOf(pen, pen.Min());
        }

        // =============================================================== report (details + checklist)
        var sb = new StringBuilder();
        void P(string s = "") => sb.AppendLine(s);
        void K(string k, string val) => sb.AppendLine($"  {k,-18}: {val}");
        P("DETAILS");
        P();
        P("SYMBOL");
        K("Version", $"{v}  ({n} x {n} modules)");
        K("ECC level", a.Ecc >= 0 ? $"{QrLayout.EccName[a.Ecc]}  (recovers up to ~{QrLayout.EccPct[a.Ecc]}% of codewords)" : "unknown (format info unreadable, no combination decodes)");
        K("Mask pattern", a.Mask >= 0 ? $"{a.Mask}  [{MaskDesc[a.Mask]}]" : "unknown");
        K("Format info", $"copy 1: {a.Fmt1} bit error(s), copy 2: {a.Fmt2} bit error(s)" + (a.FormatBrute ? "  -> unreadable; ECC/mask found by brute force" : ""));
        if (v >= 7) K("Version info", $"copy 1: {a.Ver1} bit error(s), copy 2: {a.Ver2} bit error(s)");
        K("Orientation", mir ? "MIRRORED image (camera/preview flips the symbol)" : "normal");
        K("Polarity", inv ? "light modules on dark (inverted)" : "dark modules on light");
        K("Alignment locked", v < 2 ? "n/a (version 1)" : alignOk ? "yes (perspective-corrected)" : "no (affine estimate only)");

        if (dec.Ok)
        {
            P(); P("CONTENT");
            K("Length", $"{text.Length} chars");
            K("Segments", segs);
            K("Data capacity", $"{used} of {dec.Data.Length * 8} bits used ({used * 100 / (dec.Data.Length * 8)}%)");
            K("Padding", pad);
        }

        P(); P("ERROR CORRECTION");
        for (int j = 0; j < dec.Blocks.Length; j++)
        {
            var b = dec.Blocks[j];
            K($"Block {j + 1}", $"{b.Data + b.Ec} cw (data {b.Data} + EC {b.Ec}): " + (b.Ok ? $"{b.Errors} corrected of max {b.Ec / 2}  ({b.Errors * 100 / Math.Max(1, b.Ec / 2)}% of budget)" : "UNCORRECTABLE"));
        }
        K("Total", $"{dec.Errors} codeword(s) corrected, {dec.FailedBlocks} block(s) failed");

        P(); P(a.Decoded ? "DAMAGE MAP (received vs. ideal re-rendered symbol)" : "DAMAGE MAP (function patterns only; data not decodable)");
        for (int z = 1; z < 8; z++)
        {
            if (z == 5 && a.Ecc < 0) { K(ZoneName[z], "n/a (ECC/mask unknown)"); continue; }
            K(ZoneName[z], $"{dReg[z]} of {tReg[z]} modules wrong");
        }
        if (a.Decoded) K("data/EC area", $"{dReg[0] - remDiff} of {tReg[0] - remTot} modules wrong ({(dReg[0] - remDiff) * 100.0 / Math.Max(1, tReg[0] - remTot):F1}%)");
        else K("data/EC area", "n/a");
        if (a.Decoded) K("remainder bits", $"{remDiff} of {remTot} wrong");
        K("Total", $"{dmg} modules ({dmg * 100.0 / (n * n):F1}%)");
        K("Marginal modules", $"{marg} ({marg * 100.0 / (n * n):F1}%) have low contrast margin (yellow in map)");

        P(); P("QUIET ZONE (spec: >= 4 modules light border on every side)");
        for (int s = 0; s < 4; s++) K(Side[s], (qz[s] >= 6 ? ">= 6" : qz[s].ToString()) + " modules" + (lim[s] ? "  (cut off by frame edge - not verified)" : "") + (qz[s] < 4 && !lim[s] ? "  <-- violates" : ""));

        P(); P("GEOMETRY / OPTICS");
        K("Module size", $"{a.ModulePx:F1} px   (symbol ~{a.ModulePx * n:F0} px wide)");
        K("Rotation", $"{a.RotDeg:F1} deg");
        K("Tilt", $"~{a.TiltDeg:F0} deg  (foreshortening {s2 / s1:F2})");
        K("Keystone", $"top/bottom {top / bot:F2}, left/right {lef / rig:F2}");
        K("Axis shear", $"{Math.Abs(90 - shearDeg):F1} deg off square");

        P(); P("IMAGE QUALITY");
        K("Contrast", $"{a.ContrastPct:F0}%  (dark {mD:F0}, light {mL:F0} of 255)");
        K("Finder contrast", $"TL {Math.Abs(fl[0] - fd[0]):F0}, TR {Math.Abs(fl[1] - fd[1]):F0}, BL {Math.Abs(fl[2] - fd[2]):F0}");
        K("Illumination", $"{illum:F0}% light-level variation across symbol");
        K("Sharpness", a.SharpPct >= 0 ? $"{a.SharpPct:F0}%  (1-module features vs. solid areas; 100 = crisp)" : "n/a (no clean in-frame quiet zone to reference)");
        K("Clipped light", $"{clipPct:F0}% of light modules at 250+/255");

        if (dec.Ok)
        {
            P(); P("MASK PENALTY (lower = better; spec says encoder should pick the lowest)");
            P("  " + string.Join("  ", Enumerable.Range(0, 8).Select(m => $"{m}:{pen[m]}{(m == a.Mask ? "*" : "")}")));
        }
        a.Details = sb.ToString();

        // --- checklist (pass = green tick, fail = red cross)
        var ch = a.Checks;
        void C(bool ok, string t) => ch.Add((ok, t));
        double margPct = marg * 100.0 / (n * n);
        C(a.Decoded, a.Decoded ? "Reed-Solomon decode succeeded (all blocks)" : $"Reed-Solomon decode FAILED ({dec.FailedBlocks} of {dec.Blocks.Length} blocks uncorrectable)");
        C(a.Decoded && a.WorstPct < 50, a.Decoded ? $"ECC headroom: worst block uses {a.WorstPct}% of its correction budget (limit 50%)" : "ECC headroom unknown (decode failed)");
        C(!a.FormatBrute, !a.FormatBrute ? $"Format info readable (bit errors {a.Fmt1} / {a.Fmt2})" : "Format info unreadable (ECC/mask found by brute force)");
        if (v >= 7) C(a.Ver1 <= 3 || a.Ver2 <= 3, $"Version info readable (bit errors {a.Ver1} / {a.Ver2})");
        C(dReg[1] + dReg[2] == 0, $"Finder patterns and separators intact ({dReg[1] + dReg[2]} wrong modules)");
        C(dReg[3] == 0, $"Timing patterns intact ({dReg[3]} wrong modules)");
        if (v >= 2) C(dReg[4] == 0, $"Alignment pattern(s) intact ({dReg[4]} wrong modules)");
        C(qz.Min() >= 4, $"Quiet zone >= 4 modules on all sides (tightest {qz.Min()}{(lim.Any(x => x) ? ", frame edge cuts it off" : "")})");
        C(a.ModulePx >= 3, $"Module size {a.ModulePx:F1} px (min 3)");
        C(a.ContrastPct >= 40, $"Contrast {a.ContrastPct:F0}% (min 40%)");
        if (!inv) C(mD <= 110, $"Dark modules dark enough (measure {mD:F0}/255, max 110: glare/bleed)");
        C(illum <= 25, $"Even illumination ({illum:F0}% variation, max 25%)");
        if (a.SharpPct >= 0) C(a.SharpPct >= 60, $"In focus ({a.SharpPct:F0}% sharpness, min 60%)");
        C(a.TiltDeg <= 45, $"Viewing angle ~{a.TiltDeg:F0} deg tilt (max 45)");
        C(margPct <= 10, $"Few ambiguous modules ({margPct:F1}%, max 10%)");
        C(!mir, mir ? "Image is mirrored" : "Image not mirrored");
        if (dec.Ok)
        {
            C(pen[a.Mask] <= pen.Min(), pen[a.Mask] <= pen.Min() ? $"Encoder picked the best mask ({a.Mask}, penalty {pen[a.Mask]})" : $"Encoder picked mask {a.Mask} (penalty {pen[a.Mask]}), best is {bestMask} ({pen[bestMask]})");
            C(pad.StartsWith("OK") || pad.StartsWith("no term"), "Padding follows the spec" + (pad.StartsWith("OK") || pad.StartsWith("no term") ? "" : ": " + pad));
            C(remDiff == 0, remDiff == 0 ? "Remainder bits are zero" : $"Remainder bits not zero ({remDiff} modules)");
        }

        a.Report = Compose(a);
        return a;
    }
}
