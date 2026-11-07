namespace MacroSuite.Core.Vision;

/// <summary>
/// Cached CIE Lab color. Converted once from sRGB when a visual condition is created or loaded.
/// </summary>
public readonly struct LabColor
{
    public readonly float L;
    public readonly float A;
    public readonly float B;

    public LabColor(float l, float a, float b)
    {
        L = l;
        A = a;
        B = b;
    }
}

/// <summary>
/// sRGB → CIE Lab (D65) conversion with a 256-entry gamma LUT.
/// Reference colors must be converted once and reused (do not reconvert every poll).
/// </summary>
public static class SrgbLabConvert
{
    private static readonly float[] s_linearLut = CreateLinearLut();

    /// <summary>
    /// Maps user-facing similarity percent (70–99, higher = stricter) to a CIE76 ΔE threshold.
    /// Formula: <c>ΔE76 = (100 - percent) / 3.0</c>
    /// <list type="bullet">
    /// <item>99% → ΔE ≤ 0.333 (near-exact)</item>
    /// <item>90% → ΔE ≤ 3.333 (~1.5× just-noticeable difference of ~2.3)</item>
    /// <item>70% → ΔE ≤ 10.0 (clearly different colors still allowed)</item>
    /// </list>
    /// Mapping is deterministic and monotonic.
    /// </summary>
    public static double SimilarityPercentToDeltaE76(int similarityPercent)
    {
        int pct = Math.Clamp(similarityPercent, 70, 99);
        return (100.0 - pct) / 3.0;
    }

    public static LabColor ToLab(byte r, byte g, byte b)
    {
        float rl = s_linearLut[r];
        float gl = s_linearLut[g];
        float bl = s_linearLut[b];

        // sRGB D65
        float x = (0.4124564f * rl) + (0.3575761f * gl) + (0.1804375f * bl);
        float y = (0.2126729f * rl) + (0.7151522f * gl) + (0.0721750f * bl);
        float z = (0.0193339f * rl) + (0.1191920f * gl) + (0.9503041f * bl);

        float fx = Pivot(x / 0.95047f);
        float fy = Pivot(y);
        float fz = Pivot(z / 1.08883f);

        return new LabColor(
            (116f * fy) - 16f,
            500f * (fx - fy),
            200f * (fy - fz));
    }

    /// <summary>CIE76 ΔE — fast path used for Balanced matching.</summary>
    public static double DeltaE76(in LabColor a, in LabColor b)
    {
        double dl = a.L - b.L;
        double da = a.A - b.A;
        double db = a.B - b.B;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    /// <summary>
    /// CIEDE2000 ΔE — used for Maximum Accuracy pixel matching only.
    /// Still operates on cached Lab values; no extra RGB→XYZ work.
    /// </summary>
    public static double DeltaE2000(in LabColor lab1, in LabColor lab2)
    {
        double l1 = lab1.L, a1 = lab1.A, b1 = lab1.B;
        double l2 = lab2.L, a2 = lab2.A, b2 = lab2.B;

        double c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        double c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        double cBar = (c1 + c2) / 2.0;
        double cBar7 = Math.Pow(cBar, 7);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + 6103515625.0))); // 25^7

        double a1p = (1.0 + g) * a1;
        double a2p = (1.0 + g) * a2;
        double c1p = Math.Sqrt((a1p * a1p) + (b1 * b1));
        double c2p = Math.Sqrt((a2p * a2p) + (b2 * b2));

        double h1p = Atan2Deg(b1, a1p);
        double h2p = Atan2Deg(b2, a2p);

        double dLp = l2 - l1;
        double dCp = c2p - c1p;

        double dhp;
        if (c1p * c2p == 0)
            dhp = 0;
        else if (Math.Abs(h2p - h1p) <= 180)
            dhp = h2p - h1p;
        else
            dhp = h2p - h1p > 180 ? h2p - h1p - 360 : h2p - h1p + 360;

        double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp * Math.PI / 360.0);

        double lBarp = (l1 + l2) / 2.0;
        double cBarp = (c1p + c2p) / 2.0;

        double hBarp;
        if (c1p * c2p == 0)
            hBarp = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180)
            hBarp = (h1p + h2p) / 2.0;
        else
            hBarp = (h1p + h2p < 360) ? (h1p + h2p + 360) / 2.0 : (h1p + h2p - 360) / 2.0;

        double t = 1.0
            - (0.17 * Math.Cos(Rad(hBarp - 30)))
            + (0.24 * Math.Cos(Rad(2 * hBarp)))
            + (0.32 * Math.Cos(Rad(3 * hBarp + 6)))
            - (0.20 * Math.Cos(Rad(4 * hBarp - 63)));

        double sl = 1.0 + ((0.015 * (lBarp - 50) * (lBarp - 50)) / Math.Sqrt(20 + ((lBarp - 50) * (lBarp - 50))));
        double sc = 1.0 + (0.045 * cBarp);
        double sh = 1.0 + (0.015 * cBarp * t);

        double dTheta = 30.0 * Math.Exp(-Math.Pow((hBarp - 275) / 25.0, 2));
        double cBarp7 = Math.Pow(cBarp, 7);
        double rc = 2.0 * Math.Sqrt(cBarp7 / (cBarp7 + 6103515625.0));
        double rt = -Math.Sin(Rad(2.0 * dTheta)) * rc;

        double termL = dLp / sl;
        double termC = dCp / sc;
        double termH = dHp / sh;
        return Math.Sqrt((termL * termL) + (termC * termC) + (termH * termH) + (rt * termC * termH));
    }

    private static float[] CreateLinearLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            lut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return lut;
    }

    private static float Pivot(float t)
    {
        const float Epsilon = 0.008856f;
        const float Kappa = 7.787f;
        return t > Epsilon ? MathF.Cbrt(t) : (Kappa * t) + (16f / 116f);
    }

    private static double Atan2Deg(double y, double x)
    {
        double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
        return deg < 0 ? deg + 360.0 : deg;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
}
