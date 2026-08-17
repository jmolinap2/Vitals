using System.Runtime.InteropServices;

namespace Vitals;

[StructLayout(LayoutKind.Sequential)]
internal struct PointF(float x, float y)
{
    public float X = x;
    public float Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RectF(float x, float y, float w, float h)
{
    public float X = x;
    public float Y = y;
    public float Width = w;
    public float Height = h;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput
{
    public uint GdiplusVersion;
    public nint DebugEventCallback;
    public int SuppressBackgroundThread;
    public int SuppressExternalCodecs;
}

/// <summary>
/// GDI+ flat API. GDI plano no hace antialiasing: las líneas diagonales de las
/// sparklines y los íconos quedarían dentados. GDI+ resuelve eso sin salir de
/// Win32 nativo ni romper la compilación NativeAOT.
/// </summary>
internal static class Gdip
{
    public const int SmoothingModeAntiAlias = 4;
    public const int TextRenderingHintAntiAliasGridFit = 3;
    public const int UnitPixel = 2;
    public const int LineCapRound = 2;
    public const int LineJoinRound = 2;
    public const int FillModeAlternate = 0;
    public const int FontStyleRegular = 0;
    public const int FontStyleBold = 1;
    public const int StringAlignNear = 0;
    public const int StringAlignCenter = 1;

    [DllImport("gdiplus.dll")]
    public static extern int GdiplusStartup(out nint token, ref GdiplusStartupInput input, nint output);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateFromHDC(nint hdc, out nint graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteGraphics(nint graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetSmoothingMode(nint graphics, int mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetTextRenderingHint(nint graphics, int mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGraphicsClear(nint graphics, uint argb);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateSolidFill(uint argb, out nint brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteBrush(nint brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePen(nint pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPenStartCap(nint pen, int startCap);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPenEndCap(nint pen, int endCap);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPenLineJoin(nint pen, int lineJoin);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawLines(nint graphics, nint pen, [In] PointF[] points, int count);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawRectangle(nint graphics, nint pen, float x, float y, float w, float h);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePath(int brushMode, out nint path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePath(nint path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathArc(nint path, float x, float y, float w, float h, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathPolygon(nint path, [In] PointF[] points, int count);

    [DllImport("gdiplus.dll")]
    public static extern int GdipClosePathFigure(nint path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillPath(nint graphics, nint brush, nint path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawPath(nint graphics, nint pen, nint path);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipCreateFontFamilyFromName(string name, nint fontCollection, out nint family);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateFont(nint family, float emSize, int style, int unit, out nint font);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateStringFormat(int formatAttributes, int language, out nint format);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatAlign(nint format, int align);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatLineAlign(nint format, int align);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipDrawString(nint graphics, string str, int length, nint font,
        ref RectF layoutRect, nint stringFormat, nint brush);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipMeasureString(nint graphics, string str, int length, nint font,
        ref RectF layoutRect, nint stringFormat, out RectF boundingBox, out int codepointsFitted, out int linesFilled);

    public static uint Argb(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    public static void Startup()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out _, ref input, 0);
    }

    public static nint RoundRectPath(float x, float y, float w, float h, float r)
    {
        GdipCreatePath(FillModeAlternate, out nint path);
        float d = r * 2;
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(path);
        return path;
    }
}
