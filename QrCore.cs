namespace QrScope;

/// <summary>Module classification of a QR symbol.</summary>
public enum Zone : byte { Data, Finder, Separator, Timing, Alignment, Format, Version, DarkModule }

public static class Gf
{
    public static readonly int[] Exp = new int[512];
    public static readonly int[] Log = new int[256];

    static Gf()
    {
        int x = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = x; Log[x] = i; x <<= 1; if (x >= 256) x ^= 0x11D; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static int Mul(int a, int b) => (a == 0 || b == 0) ? 0 : Exp[Log[a] + Log[b]];
    public static int Div(int a, int b) => a == 0 ? 0 : Exp[Log[a] + 255 - Log[b]];
}

/// <summary>Reed-Solomon decoder (QR flavour: roots alpha^0 .. alpha^(ec-1)).</summary>
public static class Rs
{
    static int[] Syndromes(int[] cw, int ec, out bool clean)
    {
        var s = new int[ec];
        clean = true;
        for (int j = 0; j < ec; j++)
        {
            int a = Gf.Exp[j], v = 0;
            foreach (int c in cw) v = Gf.Mul(v, a) ^ c;
            s[j] = v;
            if (v != 0) clean = false;
        }
        return s;
    }

    /// <summary>Returns number of corrected codewords, or -1 if uncorrectable. cw is only modified on success.</summary>
    public static int Decode(int[] cw, int ec, List<int> errIdx)
    {
        var r = (int[])cw.Clone();
        int n = r.Length;
        var S = Syndromes(r, ec, out bool clean);
        if (clean) return 0;

        // Berlekamp-Massey
        var C = new int[ec + 1]; var B = new int[ec + 1];
        C[0] = 1; B[0] = 1;
        int L = 0, m = 1, b = 1;
        for (int i = 0; i < ec; i++)
        {
            int d = S[i];
            for (int k = 1; k <= L; k++) d ^= Gf.Mul(C[k], S[i - k]);
            if (d == 0) { m++; continue; }
            var T = (int[])C.Clone();
            int coef = Gf.Div(d, b);
            for (int k = 0; k + m <= ec; k++) C[k + m] ^= Gf.Mul(coef, B[k]);
            if (2 * L <= i) { L = i + 1 - L; B = T; b = d; m = 1; } else m++;
        }
        if (L == 0 || L > ec / 2) return -1;

        // Chien search: error at index idx has power e = n-1-idx; root of C at alpha^-e
        var pos = new List<int>();
        for (int e = 0; e < n; e++)
        {
            int x = Gf.Exp[(255 - e) % 255], v = 0;
            for (int k = L; k >= 0; k--) v = Gf.Mul(v, x) ^ C[k];
            if (v == 0) pos.Add(n - 1 - e);
        }
        if (pos.Count != L) return -1;

        // Forney
        var O = new int[ec];
        for (int i = 0; i < ec; i++)
        {
            int v = 0;
            for (int k = 0; k <= i && k <= L; k++) v ^= Gf.Mul(C[k], S[i - k]);
            O[i] = v;
        }
        foreach (int idx in pos)
        {
            int e = n - 1 - idx;
            int X = Gf.Exp[e % 255];
            int xi = (255 - e) % 255;
            int Xi = Gf.Exp[xi];
            int num = 0;
            for (int k = ec - 1; k >= 0; k--) num = Gf.Mul(num, Xi) ^ O[k];
            int den = 0;
            for (int k = 1; k <= L; k += 2) den ^= Gf.Mul(C[k], Gf.Exp[((k - 1) * xi) % 255]);
            if (den == 0) return -1;
            r[idx] ^= Gf.Mul(X, Gf.Div(num, den));
        }
        Syndromes(r, ec, out bool ok);
        if (!ok) return -1;
        Array.Copy(r, cw, n);
        errIdx.AddRange(pos);
        return pos.Count;
    }
}

/// <summary>Function-pattern template, data placement order and block structure of one QR version.</summary>
public sealed class QrLayout
{
    static readonly Dictionary<int, QrLayout> cache = new();
    public static QrLayout Get(int v)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(v, out var l)) cache[v] = l = new QrLayout(v);
            return l;
        }
    }

    // Index 0 of each row is a dummy; index = version. Rows: L, M, Q, H.
    static readonly int[][] EccPerBlock =
    {
        new[]{-1,7,10,15,20,26,18,20,24,30,18,20,24,26,30,22,24,28,30,28,28,28,28,30,30,26,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
        new[]{-1,10,16,26,18,24,16,18,22,22,26,30,22,22,24,24,28,28,26,26,26,26,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28},
        new[]{-1,13,22,18,26,18,24,18,22,20,24,28,26,24,20,30,24,28,28,26,30,28,30,30,30,30,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
        new[]{-1,17,28,22,16,22,28,26,26,24,28,24,28,22,24,24,30,28,28,26,28,30,24,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
    };
    static readonly int[][] NumBlocks =
    {
        new[]{-1,1,1,1,1,1,2,2,2,2,4,4,4,4,4,6,6,6,6,7,8,8,9,9,10,12,12,12,13,14,15,16,17,18,19,19,20,21,22,24,25},
        new[]{-1,1,1,1,2,2,4,4,4,5,5,5,8,9,9,10,10,11,13,14,16,17,17,18,20,21,23,25,26,28,29,31,33,35,37,38,40,43,45,47,49},
        new[]{-1,1,1,2,2,4,4,6,6,8,8,8,10,12,16,12,17,16,18,21,20,23,23,25,27,29,34,34,35,38,40,43,45,48,51,53,56,59,62,65,68},
        new[]{-1,1,1,2,4,4,4,5,6,8,8,11,11,16,16,18,16,19,21,25,25,25,34,30,32,35,37,40,42,45,48,51,54,57,60,63,66,70,74,77,81},
    };

    public static readonly string[] EccName = { "L", "M", "Q", "H" };
    public static readonly int[] EccPct = { 7, 15, 25, 30 };
    public static readonly int[] EccBits = { 1, 0, 3, 2 };   // index (L,M,Q,H) -> 2-bit code in format info

    public static int EcPerBlock(int v, int ecc) => EccPerBlock[ecc][v];

    public readonly int Version, Size, RawCodewords;
    public readonly Zone[,] Reg;       // [y,x]
    public readonly bool[,] Base;      // ideal function modules (true = dark); data modules false
    public readonly int[] Order;       // x,y pairs of data modules in codeword placement order

    QrLayout(int v)
    {
        Version = v; Size = 17 + 4 * v;
        int n = Size;
        Reg = new Zone[n, n]; Base = new bool[n, n];

        for (int i = 0; i < n; i++) { Put(6, i, i % 2 == 0, Zone.Timing); Put(i, 6, i % 2 == 0, Zone.Timing); }
        Finder(3, 3); Finder(n - 4, 3); Finder(3, n - 4);

        var ap = AlignPositions(v, n);
        int na = ap.Length;
        for (int i = 0; i < na; i++)
            for (int j = 0; j < na; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == na - 1) || (i == na - 1 && j == 0)))
                    Align(ap[i], ap[j]);

        for (int k = 0; k < 15; k++)
        {
            var (x, y) = FormatXY(n, 0, k); Put(x, y, false, Zone.Format);
            (x, y) = FormatXY(n, 1, k); Put(x, y, false, Zone.Format);
        }
        Put(8, n - 8, true, Zone.DarkModule);
        if (v >= 7)
            for (int k = 0; k < 18; k++)
            {
                var (x, y) = VersionXY(n, 0, k); Put(x, y, false, Zone.Version);
                (x, y) = VersionXY(n, 1, k); Put(x, y, false, Zone.Version);
            }

        int raw = (16 * v + 128) * v + 64;
        if (v >= 2) { int na2 = v / 7 + 2; raw -= (25 * na2 - 10) * na2 - 55; if (v >= 7) raw -= 36; }
        RawCodewords = raw / 8;

        var list = new List<int>();
        for (int right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < n; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool up = ((right + 1) & 2) == 0;
                    int y = up ? n - 1 - vert : vert;
                    if (Reg[y, x] == Zone.Data) { list.Add(x); list.Add(y); }
                }
        }
        Order = list.ToArray();
    }

    void Put(int x, int y, bool dark, Zone z) { Base[y, x] = dark; Reg[y, x] = z; }

    void Finder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Put(x, y, d != 2 && d != 4, d == 4 ? Zone.Separator : Zone.Finder);
            }
    }

    void Align(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Put(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1, Zone.Alignment);
    }

    public static int[] AlignPositions(int v, int n)
    {
        if (v == 1) return Array.Empty<int>();
        int na = v / 7 + 2;
        int step = v == 32 ? 26 : (v * 4 + na * 2 + 1) / (na * 2 - 2) * 2;
        var r = new int[na];
        r[0] = 6;
        for (int i = na - 1, pos = n - 7; i >= 1; i--, pos -= step) r[i] = pos;
        return r;
    }

    public static (int x, int y) FormatXY(int n, int copy, int i)
    {
        if (copy == 0)
        {
            if (i < 6) return (8, i);
            if (i == 6) return (8, 7);
            if (i == 7) return (8, 8);
            if (i == 8) return (7, 8);
            return (14 - i, 8);
        }
        return i < 8 ? (n - 1 - i, 8) : (8, n - 15 + i);
    }

    public static (int x, int y) VersionXY(int n, int copy, int i)
    {
        int a = n - 11 + i % 3, b = i / 3;
        return copy == 0 ? (a, b) : (b, a);
    }

    public static int FormatBits(int ecc, int mask)
    {
        int data = EccBits[ecc] << 3 | mask, rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return (data << 10 | rem) ^ 0x5412;
    }

    public static int VersionBits(int v)
    {
        int rem = v;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return v << 12 | rem;
    }

    public static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    /// <summary>For each RS block, the stream (placement-order) codeword index of every codeword (data first, then EC).</summary>
    public int[][] BlockMap(int ecc)
    {
        int nb = NumBlocks[ecc][Version], ec = EccPerBlock[ecc][Version];
        int nShort = nb - RawCodewords % nb, shortLen = RawCodewords / nb, shortData = shortLen - ec;
        var map = new int[nb][];
        for (int j = 0; j < nb; j++) map[j] = new int[shortLen + (j >= nShort ? 1 : 0)];
        int k = 0;
        for (int i = 0; i <= shortData; i++)
            for (int j = 0; j < nb; j++)
            {
                if (i == shortData && j < nShort) continue;
                map[j][i] = k++;
            }
        for (int i = 0; i < ec; i++)
            for (int j = 0; j < nb; j++)
                map[j][shortData + (j >= nShort ? 1 : 0) + i] = k++;
        return map;
    }

    /// <summary>Build the ideal symbol. stream==null / ecc or mask &lt; 0: those parts are copied from fb instead.</summary>
    public bool[,] BuildIdeal(int[] stream, int ecc, int mask, bool[,] fb)
    {
        var m = (bool[,])Base.Clone();
        int n = Size;
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < 15; i++)
            {
                var (x, y) = FormatXY(n, c, i);
                m[y, x] = (ecc >= 0 && mask >= 0) ? ((FormatBits(ecc, mask) >> i) & 1) != 0 : fb[y, x];
            }
        if (Version >= 7)
        {
            int vb = VersionBits(Version);
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < 18; i++) { var (x, y) = VersionXY(n, c, i); m[y, x] = ((vb >> i) & 1) != 0; }
        }
        int nbits = RawCodewords * 8;
        for (int i = 0; i < Order.Length / 2; i++)
        {
            int x = Order[2 * i], y = Order[2 * i + 1];
            if (stream == null) m[y, x] = fb[y, x];
            else
            {
                bool bit = i < nbits && ((stream[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                m[y, x] = bit ^ MaskBit(mask, x, y);
            }
        }
        return m;
    }
}
