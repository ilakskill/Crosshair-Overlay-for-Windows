// Render test harness for Crosshair Overlay.
//
// Compiles together with ..\Crosshair.cs and calls the project's Renderer
// directly, without creating a window, tray icon or hotkey. It checks that
// every style is centered on the bitmap's center pixel and that Opacity is
// not baked into the bitmap (it is applied at push time through
// BLENDFUNCTION.SourceConstantAlpha in OverlayForm.PushToScreen).
//
// Build (from the project folder, flags exactly as build.cmd plus /main):
//   csc /nologo /target:exe /main:RenderTest /out:%TEMP%\crosshair-check\RenderTest.exe
//       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//       Crosshair.cs verify\RenderTest.cs
//
// Exit code 0 when every check passes, 1 otherwise.
//
// How the anti-aliased shapes are judged. GDI+ coverage sampling is not
// perfectly symmetric: a pixel that is ideally 50% covered comes out anywhere
// from about 40% to 65%, and the bias leans slightly right/down. When a shape
// edge falls exactly on a pixel boundary (even Thickness with the shape
// centered on a pixel) those 50% pixels sit right on the alpha > 128
// threshold, so a raw threshold set can differ from its mirror by a few
// knife-edge pixels even though the geometry is exactly centered. The checks
// therefore use three measures together:
//   1. alpha-weighted centroid within 0.1 px of the center pixel (the real
//      test of centering, independent of any threshold),
//   2. every pixel's alpha within 80 (of 255) of its mirror pixel's alpha,
//   3. the alpha > 128 set mirror-symmetric, or differing only at knife-edge
//      pixels whose alpha is within 48 of 128.
// The cross arms are hard-edged rectangles and are checked exactly.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CrosshairOverlay;

public static class RenderTest
{
    private const int Threshold = 128;
    private const int KnifeEdge = 48;          // |alpha - 128| tolerance for pixels allowed to differ at the threshold
    private const int MirrorAlphaTolerance = 80;
    private const double CentroidTolerance = 0.1;

    private static int passCount = 0;
    private static int failCount = 0;

    // A rendered bitmap reduced to its alpha channel plus geometry.
    private class Raster
    {
        public int Side;
        public int Center;
        public byte[] Alpha; // Side * Side, row-major

        public byte A(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Side || y >= Side) return 0;
            return Alpha[y * Side + x];
        }

        public HashSet<int> Set(int threshold)
        {
            HashSet<int> s = new HashSet<int>();
            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                {
                    if (Alpha[y * Side + x] > threshold) s.Add(y * Side + x);
                }
            }
            return s;
        }

        public int X(int key) { return key % Side; }
        public int Y(int key) { return key / Side; }
        public int Key(int x, int y) { return y * Side + x; }

        public int MaxAlpha()
        {
            int m = 0;
            for (int i = 0; i < Alpha.Length; i++) if (Alpha[i] > m) m = Alpha[i];
            return m;
        }

        public string Dump(int threshold)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                {
                    byte a = Alpha[y * Side + x];
                    char ch;
                    if (x == Center && y == Center) ch = a > threshold ? '@' : '+';
                    else if (a > threshold) ch = '#';
                    else if (a > 0) ch = '.';
                    else ch = ' ';
                    sb.Append(ch);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    private static Config MakeConfig(CrosshairStyle style, int size, int thickness, int gap, bool outline, int dotSize, int opacity)
    {
        Config c = new Config();
        c.Style = style;
        c.Size = size;
        c.Thickness = thickness;
        c.Gap = gap;
        c.Outline = outline;
        c.DotSize = dotSize;
        c.Opacity = opacity;
        return c;
    }

    private static Raster RenderRaster(Config c)
    {
        int extent;
        using (Bitmap bmp = Renderer.Render(c, out extent))
        {
            Raster r = new Raster();
            r.Side = bmp.Width;
            r.Center = extent;
            if (bmp.Height != bmp.Width || bmp.Width != extent * 2 + 1)
            {
                throw new InvalidOperationException("Bitmap is not a square of side 2*extent+1");
            }
            r.Alpha = new byte[r.Side * r.Side];
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] row = new byte[stride];
                for (int y = 0; y < bmp.Height; y++)
                {
                    IntPtr p = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(p, row, 0, stride);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        r.Alpha[y * r.Side + x] = row[x * 4 + 3]; // BGRA in memory: alpha is byte 3
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return r;
        }
    }

    private static HashSet<int> Mirror(Raster r, HashSet<int> set, bool mirrorX, bool mirrorY)
    {
        HashSet<int> m = new HashSet<int>();
        foreach (int key in set)
        {
            int x = r.X(key);
            int y = r.Y(key);
            if (mirrorX) x = 2 * r.Center - x;
            if (mirrorY) y = 2 * r.Center - y;
            m.Add(r.Key(x, y));
        }
        return m;
    }

    private static bool SameSet(HashSet<int> a, HashSet<int> b)
    {
        return a.Count == b.Count && a.SetEquals(b);
    }

    private static void Report(string name, bool ok, string detail)
    {
        if (ok)
        {
            passCount++;
            Console.WriteLine("PASS  " + name + (detail != null ? "  (" + detail + ")" : ""));
        }
        else
        {
            failCount++;
            Console.WriteLine("FAIL  " + name + (detail != null ? "  (" + detail + ")" : ""));
        }
    }

    private static string Fmt(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // Alpha-weighted centroid of the whole raster.
    private static void Centroid(Raster r, out double cx, out double cy)
    {
        double sx = 0, sy = 0, sw = 0;
        for (int y = 0; y < r.Side; y++)
        {
            for (int x = 0; x < r.Side; x++)
            {
                int a = r.Alpha[y * r.Side + x];
                if (a == 0) continue;
                sx += (double)x * a;
                sy += (double)y * a;
                sw += a;
            }
        }
        cx = sw > 0 ? sx / sw : double.NaN;
        cy = sw > 0 ? sy / sw : double.NaN;
    }

    // Largest |alpha(p) - alpha(mirror p)| over both mirrors.
    private static int MaxMirrorAlphaDiff(Raster r)
    {
        int max = 0;
        for (int y = 0; y < r.Side; y++)
        {
            for (int x = 0; x < r.Side; x++)
            {
                int a = r.A(x, y);
                int dx = Math.Abs(a - r.A(2 * r.Center - x, y));
                int dy = Math.Abs(a - r.A(x, 2 * r.Center - y));
                if (dx > max) max = dx;
                if (dy > max) max = dy;
            }
        }
        return max;
    }

    // Threshold-set symmetry. Returns true when exact. When not exact, reports
    // how many pixels differ and how far the worst of them is from 128; only
    // knife-edge pixels (within KnifeEdge of 128) are tolerated by the caller.
    private static bool ThresholdSymmetry(Raster r, out int differing, out int worstDeviation)
    {
        HashSet<int> set = r.Set(Threshold);
        HashSet<int> mx = Mirror(r, set, true, false);
        HashSet<int> my = Mirror(r, set, false, true);
        HashSet<int> diff = new HashSet<int>();
        foreach (int k in set) { if (!mx.Contains(k) || !my.Contains(k)) diff.Add(k); }
        foreach (int k in mx) { if (!set.Contains(k)) diff.Add(k); }
        foreach (int k in my) { if (!set.Contains(k)) diff.Add(k); }
        differing = diff.Count;
        worstDeviation = 0;
        foreach (int k in diff)
        {
            int dev = Math.Abs(r.Alpha[k] - Threshold);
            if (dev > worstDeviation) worstDeviation = dev;
        }
        return differing == 0;
    }

    // The combined centering check for anti-aliased shapes (see file header).
    private static bool CheckCentered(string label, Raster r)
    {
        double cx, cy;
        Centroid(r, out cx, out cy);
        bool centroidOk = !double.IsNaN(cx) && Math.Abs(cx - r.Center) <= CentroidTolerance && Math.Abs(cy - r.Center) <= CentroidTolerance;
        Report(label + " alpha-weighted centroid is the center pixel (within " + Fmt(CentroidTolerance) + " px)", centroidOk,
            string.Format(CultureInfo.InvariantCulture, "centroid=({0},{1}) center=({2},{2})", Fmt(cx), Fmt(cy), r.Center));

        int maxDiff = MaxMirrorAlphaDiff(r);
        bool alphaOk = maxDiff <= MirrorAlphaTolerance;
        Report(label + " every pixel's alpha matches its mirror pixel within " + MirrorAlphaTolerance, alphaOk, "max difference=" + maxDiff);

        int differing, worst;
        bool exact = ThresholdSymmetry(r, out differing, out worst);
        bool thresholdOk = exact || worst <= KnifeEdge;
        string detail = exact
            ? "exact, " + r.Set(Threshold).Count + " px"
            : string.Format(CultureInfo.InvariantCulture, "{0} knife-edge pixels differ, all within {1} of 128 (limit {2})", differing, worst, KnifeEdge);
        Report(label + " alpha>128 set mirror-symmetric in both axes (knife-edge pixels tolerated)", thresholdOk, detail);

        bool ok = centroidOk && alphaOk && thresholdOk;
        if (!ok)
        {
            Console.WriteLine(r.Dump(Threshold));
        }
        return ok;
    }

    // ---------------------------------------------------------------------

    private static void TestDot()
    {
        Config c = MakeConfig(CrosshairStyle.Dot, 12, 2, 4, false, 3, 255);
        Raster r = RenderRaster(c);
        CheckCentered("a. Dot (DotSize=3, Outline=0)", r);
        int differing, worst;
        bool exact = ThresholdSymmetry(r, out differing, out worst);
        Report("a. Dot (DotSize=3, Outline=0) alpha>128 set exactly symmetric about center pixel", exact, r.Set(Threshold).Count + " px");
        Report("a. Dot center pixel is fully opaque", r.A(r.Center, r.Center) == 255, "alpha=" + r.A(r.Center, r.Center));
    }

    private static void TestCircle()
    {
        Config c = MakeConfig(CrosshairStyle.Circle, 12, 2, 4, false, 3, 255);
        Raster r = RenderRaster(c);
        CheckCentered("b. Circle (Size=12, Thickness=2, Outline=0)", r);
        Report("b. Circle center pixel is transparent", r.A(r.Center, r.Center) == 0, "alpha=" + r.A(r.Center, r.Center));

        // Odd thickness as well. GDI+ flattens ellipses to curve segments and
        // never rasterizes them exactly mirror-symmetric, so the same
        // tolerant check applies rather than an exact set match.
        Config c3 = MakeConfig(CrosshairStyle.Circle, 12, 3, 4, false, 3, 255);
        Raster r3 = RenderRaster(c3);
        CheckCentered("b. Circle (Size=12, Thickness=3, Outline=0)", r3);
    }

    // Splits a Cross raster into its four arms and checks that they agree on
    // the center pixel. For odd thickness the whole set must be exactly mirror
    // symmetric in both axes. For even thickness a hard-edged arm cannot be
    // mirror symmetric about a single pixel (a 2 px wide arm has no middle
    // pixel), so the check is: all four arms straddle the center pixel the
    // same way, every arm contains the center row/column, and the arm extents
    // (gap and length) mirror exactly.
    private static void TestCross(int thickness)
    {
        Config c = MakeConfig(CrosshairStyle.Cross, 12, thickness, 4, false, 3, 255);
        Raster r = RenderRaster(c);
        HashSet<int> set = r.Set(Threshold);
        int cx = r.Center, cy = r.Center;

        SortedSet<int> colsUp = new SortedSet<int>(), colsDown = new SortedSet<int>();
        SortedSet<int> rowsLeft = new SortedSet<int>(), rowsRight = new SortedSet<int>();
        SortedSet<int> rowsUp = new SortedSet<int>(), rowsDown = new SortedSet<int>();
        SortedSet<int> colsLeft = new SortedSet<int>(), colsRight = new SortedSet<int>();
        int unclassified = 0;
        int partial = 0; // pixels with 0 < alpha <= 128: a crisp cross must have none

        for (int i = 0; i < r.Alpha.Length; i++)
        {
            if (r.Alpha[i] > 0 && r.Alpha[i] <= Threshold) partial++;
        }

        foreach (int key in set)
        {
            int x = r.X(key), y = r.Y(key);
            int dx = x - cx, dy = y - cy;
            bool vertical = Math.Abs(dx) <= thickness && Math.Abs(dy) > c.Gap;
            bool horizontal = Math.Abs(dy) <= thickness && Math.Abs(dx) > c.Gap;
            if (vertical && !horizontal)
            {
                if (dy < 0) { colsUp.Add(dx); rowsUp.Add(dy); }
                else { colsDown.Add(dx); rowsDown.Add(dy); }
            }
            else if (horizontal && !vertical)
            {
                if (dx < 0) { rowsLeft.Add(dy); colsLeft.Add(dx); }
                else { rowsRight.Add(dy); colsRight.Add(dx); }
            }
            else
            {
                unclassified++;
            }
        }

        string label = string.Format(CultureInfo.InvariantCulture, "c. Cross (Thickness={0}, Gap=4, Size=12, Outline=0)", thickness);

        bool fourArms = colsUp.Count > 0 && colsDown.Count > 0 && rowsLeft.Count > 0 && rowsRight.Count > 0 && unclassified == 0 && partial == 0;
        Report(label + " has exactly four crisp arms and nothing else", fourArms,
            string.Format(CultureInfo.InvariantCulture, "{0} px, unclassified={1}, partial-alpha={2}", set.Count, unclassified, partial));

        // Same width and same straddle on every arm.
        bool sameStraddle = colsUp.SetEquals(colsDown) && rowsLeft.SetEquals(rowsRight) && colsUp.SetEquals(rowsLeft);
        bool widthOk = colsUp.Count == thickness;
        bool containsCenter = colsUp.Contains(0) && rowsLeft.Contains(0);
        Report(label + " arms straddle the center pixel identically and contain it", sameStraddle && widthOk && containsCenter,
            "vertical arm column offsets " + Join(colsUp) + ", horizontal arm row offsets " + Join(rowsLeft));

        // Arm extents mirror: up rows -> down rows, left columns -> right columns.
        SortedSet<int> rowsUpMirrored = new SortedSet<int>();
        foreach (int dy in rowsUp) rowsUpMirrored.Add(-dy);
        SortedSet<int> colsLeftMirrored = new SortedSet<int>();
        foreach (int dx in colsLeft) colsLeftMirrored.Add(-dx);
        bool extentsMirror = rowsUpMirrored.SetEquals(rowsDown) && colsLeftMirrored.SetEquals(colsRight) && rowsUpMirrored.SetEquals(colsRight);
        Report(label + " arm lengths and gaps mirror about the center pixel", extentsMirror,
            "up rows " + Range(rowsUp) + ", down rows " + Range(rowsDown) + ", left cols " + Range(colsLeft) + ", right cols " + Range(colsRight));

        // Whole-set mirror symmetry.
        bool lr = SameSet(set, Mirror(r, set, true, false));
        bool ud = SameSet(set, Mirror(r, set, false, true));
        if (thickness % 2 == 1)
        {
            Report(label + " whole pixel set mirror-symmetric in both axes", lr && ud,
                string.Format(CultureInfo.InvariantCulture, "mirrorX={0}, mirrorY={1}", lr, ud));
        }
        else
        {
            // Not achievable with hard-edged even-width arms; the straddle and
            // extent checks above are the meaningful test here.
            Console.WriteLine("INFO  " + label + " whole-set mirror symmetry is impossible for an even width (mirrorX="
                + lr.ToString() + ", mirrorY=" + ud.ToString() + "); consistency verified by the straddle and extent checks above");
        }

        if (!(fourArms && sameStraddle && widthOk && containsCenter && extentsMirror))
        {
            Console.WriteLine(r.Dump(Threshold));
        }
    }

    private static string Join(SortedSet<int> s)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (int v in s)
        {
            if (!first) sb.Append(",");
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
            first = false;
        }
        sb.Append("}");
        return sb.ToString();
    }

    private static string Range(SortedSet<int> s)
    {
        if (s.Count == 0) return "(none)";
        return string.Format(CultureInfo.InvariantCulture, "[{0}..{1}]", s.Min, s.Max);
    }

    private static void TestCrossDot()
    {
        // Default config values: Thickness 2, Gap 4, Size 12, DotSize 3.
        Config c = MakeConfig(CrosshairStyle.CrossDot, 12, 2, 4, false, 3, 255);
        Raster r = RenderRaster(c);
        HashSet<int> set = r.Set(Threshold);
        int cx = r.Center, cy = r.Center;

        double dotSx = 0, dotSy = 0; int dotN = 0;
        double armSx = 0, armSy = 0; int armN = 0;
        foreach (int key in set)
        {
            int x = r.X(key), y = r.Y(key);
            int dx = x - cx, dy = y - cy;
            if (Math.Abs(dx) <= c.Gap && Math.Abs(dy) <= c.Gap)
            {
                dotSx += x; dotSy += y; dotN++;
            }
            else
            {
                armSx += x; armSy += y; armN++;
            }
        }

        bool have = dotN > 0 && armN > 0;
        double dotCx = have ? dotSx / dotN : double.NaN;
        double dotCy = have ? dotSy / dotN : double.NaN;
        double armCx = have ? armSx / armN : double.NaN;
        double armCy = have ? armSy / armN : double.NaN;
        bool ok = have && Math.Abs(dotCx - armCx) <= 0.5 && Math.Abs(dotCy - armCy) <= 0.5;
        Report("d. CrossDot dot centroid and arm centroid are the same pixel (within 0.5 px)", ok,
            string.Format(CultureInfo.InvariantCulture, "dot=({0},{1}) n={2}; arms=({3},{4}) n={5}; center=({6},{6})",
                Fmt(dotCx), Fmt(dotCy), dotN, Fmt(armCx), Fmt(armCy), armN, cx));

        // The dot must be centered on the exact center pixel, and that pixel must be opaque.
        bool dotCentered = have && Math.Abs(dotCx - cx) < 0.01 && Math.Abs(dotCy - cy) < 0.01;
        Report("d. CrossDot dot is centered exactly on the center pixel, which is opaque", dotCentered && r.A(cx, cy) == 255,
            "dot centroid=(" + Fmt(dotCx) + "," + Fmt(dotCy) + "), alpha(center)=" + r.A(cx, cy));
        if (!ok || !dotCentered)
        {
            Console.WriteLine(r.Dump(Threshold));
        }
    }

    private static void TestX()
    {
        Config c = MakeConfig(CrosshairStyle.X, 12, 2, 4, false, 3, 255);
        Raster r = RenderRaster(c);
        CheckCentered("e. X (Size=12, Thickness=2, Outline=0)", r);
    }

    private static void TestOpacityNotBaked()
    {
        // Outline on (the default) so both passes are exercised.
        Config c = MakeConfig(CrosshairStyle.CrossDot, 12, 2, 4, true, 3, 102);
        Raster r = RenderRaster(c);
        int max = r.MaxAlpha();
        Report("f. Opacity=102 bitmap is still fully opaque where drawn (max alpha 255)", max == 255, "max alpha=" + max);

        // Every style at 40% must render a 255 alpha somewhere.
        bool allStyles = true;
        foreach (CrosshairStyle s in Enum.GetValues(typeof(CrosshairStyle)))
        {
            Config cs = MakeConfig(s, 12, 2, 4, true, 3, 102);
            Raster rs = RenderRaster(cs);
            if (rs.MaxAlpha() != 255) { allStyles = false; Console.WriteLine("      style " + s + " max alpha " + rs.MaxAlpha()); }
        }
        Report("f. Every style at Opacity=102 renders with max alpha 255", allStyles, null);

        // Opacity 255 and 102 must produce identical bitmaps (opacity is not in the pixels).
        Config full = MakeConfig(CrosshairStyle.CrossDot, 12, 2, 4, true, 3, 255);
        Raster rf = RenderRaster(full);
        bool identical = rf.Alpha.Length == r.Alpha.Length;
        if (identical)
        {
            for (int i = 0; i < rf.Alpha.Length; i++) if (rf.Alpha[i] != r.Alpha[i]) { identical = false; break; }
        }
        Report("f. Opacity=255 and Opacity=102 produce identical alpha channels", identical, null);
    }

    private static void TestOutlinedShapesStayCentered()
    {
        // With the outline pass on, the anti-aliased shapes must remain centered.
        CrosshairStyle[] styles = new CrosshairStyle[] { CrosshairStyle.Dot, CrosshairStyle.Circle, CrosshairStyle.X, CrosshairStyle.CircleDot };
        foreach (CrosshairStyle s in styles)
        {
            Config c = MakeConfig(s, 12, 3, 4, true, 3, 255);
            Raster r = RenderRaster(c);
            CheckCentered("g. " + s.ToString() + " (Thickness=3, Outline=1)", r);
        }
    }

    public static int Main()
    {
        Console.WriteLine("Crosshair Overlay render test harness");
        Console.WriteLine();
        try
        {
            TestDot();
            TestCircle();
            TestCross(2);
            TestCross(3);
            TestCrossDot();
            TestX();
            TestOpacityNotBaked();
            TestOutlinedShapesStayCentered();
        }
        catch (Exception ex)
        {
            failCount++;
            Console.WriteLine("FAIL  harness exception: " + ex.ToString());
        }
        Console.WriteLine();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} passed, {1} failed", passCount, failCount));
        return failCount == 0 ? 0 : 1;
    }
}
