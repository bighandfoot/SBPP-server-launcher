using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace server_launcher;

// Every value here is copied from index.html's stylesheet.
internal static class Theme
{
    public static readonly Color Page = Color.FromArgb(15, 15, 15);
    public static readonly Color Panel = Color.FromArgb(35, 35, 35);
    public static readonly Color PanelHover = Color.FromArgb(45, 45, 45);
    public static readonly Color PanelActive = Color.FromArgb(52, 52, 52);
    public static readonly Color Accent = Color.FromArgb(0xff, 0x57, 0x22);
    public static readonly Color AccentHover = Color.FromArgb(0xe6, 0x4a, 0x19);
    public static readonly Color Blue = Color.FromArgb(0x2f, 0x80, 0xed);
    public static readonly Color BlueHover = Color.FromArgb(0x1a, 0x6e, 0xd8);
    public static readonly Color Green = Color.FromArgb(0x27, 0xae, 0x60);
    public static readonly Color Secondary = Color.FromArgb(52, 52, 52);
    public static readonly Color SecondaryHover = Color.FromArgb(65, 65, 65);
    public static readonly Color TabBg = Color.FromArgb(28, 28, 28);
    public static readonly Color TabHover = Color.FromArgb(38, 38, 38);
    public static readonly Color TabText = Color.FromArgb(0xa0, 0xa0, 0xa0);
    public static readonly Color IconColor = Color.FromArgb(0x99, 0x99, 0x99);
    public static readonly Color Muted = Color.FromArgb(0x88, 0x88, 0x88);
    public static readonly Color NoticeBg = Color.FromArgb(28, 28, 28);
    public static readonly Color NoticeText = Color.FromArgb(0xbb, 0xbb, 0xbb);
    public static readonly Color Error = Color.FromArgb(0xff, 0x8a, 0x65);
    public static readonly Color CodeBg = Color.FromArgb(25, 25, 25);
    public static readonly Color MenuBg = Color.FromArgb(45, 45, 45);
    public static readonly Color MenuHover = Color.FromArgb(58, 58, 58);
    public static readonly Color MenuText = Color.FromArgb(0xdd, 0xdd, 0xdd);
    public static readonly Color ModalText = Color.FromArgb(0xcc, 0xcc, 0xcc);
    public static readonly Color BoxHoverBorder = Color.FromArgb(55, 55, 55);
    public static readonly Color ScrollThumb = Color.FromArgb(45, 45, 45);
    public static readonly Color ScrollThumbHover = Color.FromArgb(65, 65, 65);
    public static readonly Color SliderTrack = Color.FromArgb(239, 239, 239);
    public static readonly Color Danger = Color.FromArgb(0xe5, 0x39, 0x35);
    public static readonly Color DangerHover = Color.FromArgb(0xc6, 0x28, 0x28);
    public static readonly Color DangerText = Color.FromArgb(0xff, 0x6b, 0x6b);

    public static Color Mix(Color from, Color to, float t)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        return Color.FromArgb(
            (int)MathF.Round(from.A + (to.A - from.A) * t),
            (int)MathF.Round(from.R + (to.R - from.R) * t),
            (int)MathF.Round(from.G + (to.G - from.G) * t),
            (int)MathF.Round(from.B + (to.B - from.B) * t));
    }
}

// Font sizes are CSS pixels; LineHeight 1.33 is Chrome's "normal" for Segoe UI.
internal readonly record struct FontSpec(string Family, float Px, FontStyle Style, float LineHeight)
{
    private static readonly bool HasBlack = DetectBlack();

    public float Line => Px * LineHeight;

    public static FontSpec Body(float px, FontStyle style = FontStyle.Regular, float lineHeight = 1.33f) =>
        new FontSpec("Segoe UI", px, style, lineHeight);

    // font-weight: 900 -> Segoe UI Black
    public static FontSpec Heavy(float px) => HasBlack
        ? new FontSpec("Segoe UI Black", px, FontStyle.Regular, 1.33f)
        : new FontSpec("Segoe UI", px, FontStyle.Bold, 1.33f);

    public static FontSpec Mono(float px, float lineHeight = 1.17f) => new FontSpec("Consolas", px, FontStyle.Regular, lineHeight);

    private static bool DetectBlack()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            return installed.Families.Any(family => family.Name.Equals("Segoe UI Black", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

// Layout happens in CSS pixels; Ui converts to device pixels for the current DPI.
internal sealed class Ui : IDisposable
{
    public const TextFormatFlags BaseFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    private readonly Dictionary<FontSpec, System.Drawing.Font> fonts = new();
    private readonly Dictionary<(FontSpec Spec, string Text), float> widths = new();

    public float Scale { get; private set; } = 1f;

    public void SetScale(float scale)
    {
        if (scale <= 0f || Math.Abs(scale - Scale) < 0.001f) return;
        Scale = scale;
        ClearCaches();
    }

    public System.Drawing.Font GetFont(FontSpec spec)
    {
        if (!fonts.TryGetValue(spec, out var font))
        {
            font = new System.Drawing.Font(spec.Family, spec.Px * Scale, spec.Style, GraphicsUnit.Pixel);
            fonts[spec] = font;
        }
        return font;
    }

    public float Measure(string text, FontSpec spec)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var key = (spec, text);
        if (widths.TryGetValue(key, out var width)) return width;
        if (widths.Count > 20000) widths.Clear();
        width = TextRenderer.MeasureText(text, GetFont(spec), new Size(short.MaxValue, short.MaxValue), BaseFlags).Width / Scale;
        widths[key] = width;
        return width;
    }

    public float SpaceWidth(FontSpec spec) => Measure("x x", spec) - Measure("xx", spec);

    public List<string> Wrap(string text, FontSpec spec, float width, bool breakAll = false)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (width <= 0f)
            {
                lines.Add(paragraph);
                continue;
            }

            var line = string.Empty;
            if (breakAll)
            {
                foreach (var ch in paragraph)
                {
                    var candidate = line + ch;
                    if (line.Length > 0 && Measure(candidate, spec) > width + 0.5f)
                    {
                        lines.Add(line);
                        line = ch.ToString();
                    }
                    else
                    {
                        line = candidate;
                    }
                }
            }
            else
            {
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Measure(candidate, spec) > width + 0.5f)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    private void ClearCaches()
    {
        foreach (var font in fonts.Values) font.Dispose();
        fonts.Clear();
        widths.Clear();
    }

    public void Dispose() => ClearCaches();
}

internal sealed class Painter
{
    private readonly List<(float Alpha, Color Backdrop)> layers = new();

    public Painter(Graphics graphics, Ui ui)
    {
        G = graphics;
        Ui = ui;
    }

    public Graphics G { get; }
    public Ui Ui { get; }
    public float OffsetX;
    public float OffsetY;
    public float ViewTop;
    public float ViewBottom = float.MaxValue;

    private float S => Ui.Scale;

    // CSS opacity over a known solid backdrop (fade-in animation, disabled buttons).
    public void PushOpacity(float alpha, Color backdrop) => layers.Add((alpha, backdrop));
    public void PopOpacity() => layers.RemoveAt(layers.Count - 1);

    public float Opacity
    {
        get
        {
            var alpha = 1f;
            foreach (var layer in layers) alpha *= layer.Alpha;
            return alpha;
        }
    }

    public Color Resolve(Color color)
    {
        for (var i = layers.Count - 1; i >= 0; i--) color = Theme.Mix(layers[i].Backdrop, color, layers[i].Alpha);
        return color;
    }

    public bool IsVisible(RectangleF r) => r.Bottom + OffsetY >= ViewTop && r.Top + OffsetY <= ViewBottom;

    public RectangleF ToDevice(RectangleF r) => new((r.X + OffsetX) * S, (r.Y + OffsetY) * S, r.Width * S, r.Height * S);

    public RectangleF Snapped(RectangleF logical)
    {
        var d = ToDevice(logical);
        return RectangleF.FromLTRB(MathF.Round(d.Left), MathF.Round(d.Top), MathF.Round(d.Right), MathF.Round(d.Bottom));
    }

    public GraphicsState Clip(RectangleF logical)
    {
        var state = G.Save();
        G.SetClip(Snapped(logical), CombineMode.Intersect);
        return state;
    }

    public void Fill(RectangleF r, Color color, float radius = 0f) => FillCorners(r, color, radius, radius, radius, radius);

    public void FillCorners(RectangleF r, Color color, float tl, float tr, float br, float bl, float alpha = 1f)
    {
        var d = Snapped(r);
        if (d.Width <= 0f || d.Height <= 0f) return;
        var resolved = Resolve(color);
        if (alpha < 1f) resolved = Color.FromArgb((int)Math.Clamp(alpha * resolved.A, 0f, 255f), resolved);
        using var brush = new SolidBrush(resolved);
        if (tl <= 0f && tr <= 0f && br <= 0f && bl <= 0f)
        {
            G.FillRectangle(brush, d);
            return;
        }
        using var path = RoundPath(d, tl * S, tr * S, br * S, bl * S);
        G.FillPath(brush, path);
    }

    // box-shadow approximation: stacked translucent layers spanning roughly +-0.8 * blur.
    public void Shadow(RectangleF r, float radius, float offsetY, float blur, float alpha)
    {
        alpha *= Opacity;
        if (alpha <= 0.004f) return;
        const int steps = 8;
        var per = 1f - MathF.Pow(1f - alpha, 1f / steps);
        using var brush = new SolidBrush(Color.FromArgb((int)Math.Clamp(per * 255f, 1f, 255f), 0, 0, 0));
        for (var i = 0; i < steps; i++)
        {
            var spread = blur * (-0.8f + 1.6f * (i + 0.5f) / steps);
            var layer = new RectangleF(r.X - spread, r.Y + offsetY - spread, r.Width + spread * 2, r.Height + spread * 2);
            if (layer.Width <= 0f || layer.Height <= 0f) continue;
            var corner = Math.Max(0f, radius + spread) * S;
            using var path = RoundPath(ToDevice(layer), corner, corner, corner, corner);
            G.FillPath(brush, path);
        }
    }

    public void Text(string text, FontSpec spec, Color color, RectangleF r, TextFormatFlags align = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
    {
        if (string.IsNullOrEmpty(text)) return;
        var d = ToDevice(r);
        var rect = Rectangle.FromLTRB((int)MathF.Round(d.Left), (int)MathF.Round(d.Top), (int)MathF.Round(d.Right), (int)MathF.Round(d.Bottom));
        TextRenderer.DrawText(G, text, Ui.GetFont(spec), rect, Resolve(color),
            align | Ui.BaseFlags | TextFormatFlags.NoClipping | TextFormatFlags.PreserveGraphicsClipping);
    }

    public void Icon(Glyph glyph, RectangleF box, Color color)
    {
        if (glyph == Glyph.None) return;
        var (source, stroke) = Icons.Get(glyph);
        var d = ToDevice(box);
        using var path = (GraphicsPath)source.Clone();
        using var matrix = new Matrix(d.Width / 24f, 0f, 0f, d.Height / 24f, MathF.Round(d.X), MathF.Round(d.Y));
        path.Transform(matrix);
        var resolved = Resolve(color);
        if (stroke)
        {
            using var pen = new Pen(resolved, 2.5f * d.Width / 24f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            G.DrawPath(pen, path);
        }
        else
        {
            using var brush = new SolidBrush(resolved);
            G.FillPath(brush, path);
        }
    }

    public static GraphicsPath RoundPath(RectangleF r, float tl, float tr, float br, float bl)
    {
        var max = Math.Max(0.01f, Math.Min(r.Width, r.Height) / 2f);
        tl = Math.Clamp(tl, 0.01f, max);
        tr = Math.Clamp(tr, 0.01f, max);
        br = Math.Clamp(br, 0.01f, max);
        bl = Math.Clamp(bl, 0.01f, max);
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, tl * 2, tl * 2, 180, 90);
        path.AddArc(r.Right - tr * 2, r.Top, tr * 2, tr * 2, 270, 90);
        path.AddArc(r.Right - br * 2, r.Bottom - br * 2, br * 2, br * 2, 0, 90);
        path.AddArc(r.Left, r.Bottom - bl * 2, bl * 2, bl * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum Glyph { None, Versions, Installed, Settings, Download, Play, Kebab }

// The exact SVG paths from index.html.
internal static class Icons
{
    private static readonly Dictionary<Glyph, (GraphicsPath Path, bool Stroke)> Cache = new();

    public static (GraphicsPath Path, bool Stroke) Get(Glyph glyph)
    {
        if (!Cache.TryGetValue(glyph, out var entry))
        {
            entry = glyph switch
            {
                Glyph.Versions => (Svg.Parse("M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z"), false),
                Glyph.Installed or Glyph.Play => (Svg.Parse("M8 5v14l11-7z"), false),
                Glyph.Settings => (Svg.Parse("M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"), false),
                Glyph.Kebab => (Svg.Parse("M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z"), false),
                Glyph.Download => (Svg.Parse("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10L12 15L17 10M12 15V3"), true),
                _ => (new GraphicsPath(), false)
            };
            Cache[glyph] = entry;
        }
        return entry;
    }
}

// Minimal SVG path-data parser (M L H V C S A Z, absolute and relative).
internal static class Svg
{
    private static readonly Regex Tokens = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    public static GraphicsPath Parse(string data)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = Tokens.Matches(data).Select(match => match.Value).ToArray();
        var index = 0;
        var command = 'M';
        PointF current = default, start = default, lastControl = default;
        var lastWasCurve = false;

        float ReadNumber() => float.Parse(tokens[index++], CultureInfo.InvariantCulture);

        PointF ReadPoint(bool relative)
        {
            var x = ReadNumber();
            var y = ReadNumber();
            return relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
        }

        while (index < tokens.Length)
        {
            if (char.IsLetter(tokens[index][0])) command = tokens[index++][0];
            else if (char.ToUpperInvariant(command) == 'Z') break;

            var relative = char.IsLower(command);
            var curve = false;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = start = ReadPoint(relative);
                    path.StartFigure();
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    {
                        var p = ReadPoint(relative);
                        path.AddLine(current, p);
                        current = p;
                        break;
                    }
                case 'H':
                    {
                        var p = new PointF(ReadNumber() + (relative ? current.X : 0f), current.Y);
                        path.AddLine(current, p);
                        current = p;
                        break;
                    }
                case 'V':
                    {
                        var p = new PointF(current.X, ReadNumber() + (relative ? current.Y : 0f));
                        path.AddLine(current, p);
                        current = p;
                        break;
                    }
                case 'C':
                    {
                        var c1 = ReadPoint(relative);
                        var c2 = ReadPoint(relative);
                        var p = ReadPoint(relative);
                        path.AddBezier(current, c1, c2, p);
                        lastControl = c2;
                        current = p;
                        curve = true;
                        break;
                    }
                case 'S':
                    {
                        var c1 = lastWasCurve ? new PointF(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y) : current;
                        var c2 = ReadPoint(relative);
                        var p = ReadPoint(relative);
                        path.AddBezier(current, c1, c2, p);
                        lastControl = c2;
                        current = p;
                        curve = true;
                        break;
                    }
                case 'A':
                    {
                        var rx = ReadNumber();
                        var ry = ReadNumber();
                        var rotation = ReadNumber();
                        var large = ReadNumber() != 0f;
                        var sweep = ReadNumber() != 0f;
                        var p = ReadPoint(relative);
                        ArcTo(path, current, rx, ry, rotation, large, sweep, p);
                        current = p;
                        break;
                    }
                case 'Z':
                    path.CloseFigure();
                    current = start;
                    break;
                default:
                    index = tokens.Length;
                    break;
            }
            lastWasCurve = curve;
        }
        return path;
    }

    // SVG spec F.6.5: endpoint -> center parameterization, then cubic segments of <= 90 degrees.
    private static void ArcTo(GraphicsPath path, PointF p0, float rx, float ry, float angle, bool large, bool sweep, PointF p1)
    {
        if (p0 == p1) return;
        if (rx == 0f || ry == 0f)
        {
            path.AddLine(p0, p1);
            return;
        }

        var phi = angle * Math.PI / 180.0;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double rxs = Math.Abs(rx), rys = Math.Abs(ry);
        var lambda = x1 * x1 / (rxs * rxs) + y1 * y1 / (rys * rys);
        if (lambda > 1)
        {
            var root = Math.Sqrt(lambda);
            rxs *= root;
            rys *= root;
        }

        var numerator = rxs * rxs * rys * rys - rxs * rxs * y1 * y1 - rys * rys * x1 * x1;
        var denominator = rxs * rxs * y1 * y1 + rys * rys * x1 * x1;
        var coefficient = Math.Sqrt(Math.Max(0, numerator / denominator)) * (large == sweep ? -1 : 1);
        var cx1 = coefficient * rxs * y1 / rys;
        var cy1 = coefficient * -rys * x1 / rxs;
        var cx = cos * cx1 - sin * cy1 + (p0.X + p1.X) / 2.0;
        var cy = sin * cx1 + cos * cy1 + (p0.Y + p1.Y) / 2.0;

        var theta = VectorAngle(1, 0, (x1 - cx1) / rxs, (y1 - cy1) / rys);
        var delta = VectorAngle((x1 - cx1) / rxs, (y1 - cy1) / rys, (-x1 - cx1) / rxs, (-y1 - cy1) / rys);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
        var step = delta / segments;
        var t = 4.0 / 3.0 * Math.Tan(step / 4);

        PointF Map(double ux, double uy) => new(
            (float)(cx + rxs * ux * cos - rys * uy * sin),
            (float)(cy + rxs * ux * sin + rys * uy * cos));

        var from = p0;
        for (var s = 0; s < segments; s++)
        {
            var a = theta + step * s;
            var b = a + step;
            var c1 = Map(Math.Cos(a) - t * Math.Sin(a), Math.Sin(a) + t * Math.Cos(a));
            var c2 = Map(Math.Cos(b) + t * Math.Sin(b), Math.Sin(b) - t * Math.Cos(b));
            var to = s == segments - 1 ? p1 : Map(Math.Cos(b), Math.Sin(b));
            path.AddBezier(from, c1, c2, to);
            from = to;
        }
    }

    private static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        var dot = ux * vx + uy * vy;
        var length = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        var result = Math.Acos(Math.Clamp(dot / length, -1, 1));
        return ux * vy - uy * vx < 0 ? -result : result;
    }
}

// ---------------------------------------------------------------------------------------------
// Elements (the "DOM"). Bounds are CSS pixels; page elements live in scrolled content space,
// sidebar / menu / modal elements live in window space.
// ---------------------------------------------------------------------------------------------

internal abstract class El
{
    public RectangleF Bounds;
    public float MarginTop;
    public float MarginBottom;
    public bool Hot;
    public bool Pressed;
    public float HoverT;
    public float PressT;

    public virtual bool TracksHover => false;
    public virtual bool Clickable => false;
    public virtual bool Enabled => true;
    public virtual Cursor Cursor => Cursors.Hand;
    public virtual string? Tip => null;
    public virtual IEnumerable<El> Kids => Array.Empty<El>();
    public virtual float PreferredWidth(Ui ui) => 0f;

    public abstract float Arrange(Ui ui, float x, float y, float width);
    public abstract void Paint(Painter p);

    public virtual void Click() { }
    public virtual bool PointerDown(PointF point) => false;
    public virtual bool PointerDrag(PointF point) => false;
}

internal sealed class VStack : El
{
    public readonly List<El> Items = new();
    public float Gap;

    public override IEnumerable<El> Kids => Items;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var cy = y;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (i > 0) cy += Gap;
            cy += item.MarginTop;
            var height = item.Arrange(ui, x, cy, width);
            cy += height + item.MarginBottom;
        }
        Bounds = new RectangleF(x, y, width, cy - y);
        return cy - y;
    }

    public override void Paint(Painter p)
    {
        foreach (var item in Items)
            if (p.IsVisible(item.Bounds)) item.Paint(p);
    }
}

// .main-content: padding 40px 20px, child column centered with max-width 900px.
internal sealed class PageRoot : El
{
    public El? Content;

    public override IEnumerable<El> Kids => Content is null ? Array.Empty<El>() : new[] { Content };

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var inner = Math.Max(0f, width - 40f);
        var column = Math.Min(900f, inner);
        var height = Content?.Arrange(ui, x + 20f + (inner - column) / 2f, y + 40f, column) ?? 0f;
        Bounds = new RectangleF(x, y, width, height + 80f);
        return height + 80f;
    }

    public override void Paint(Painter p) => Content?.Paint(p);
}

// .title-text
internal sealed class TitleEl : El
{
    private static readonly FontSpec Font = FontSpec.Heavy(36);
    private readonly string text;

    public TitleEl(string text)
    {
        this.text = text;
        MarginBottom = 24;
    }

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, width, Font.Line);
        return Font.Line;
    }

    public override void Paint(Painter p) =>
        p.Text(text, Font, Color.White, Bounds, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
}

// .tab-btn
internal sealed class TabEl : El
{
    public static readonly FontSpec Font = FontSpec.Body(15, FontStyle.Bold);
    private readonly Action onClick;
    public bool Active;

    public TabEl(string text, Action onClick)
    {
        Text = text;
        this.onClick = onClick;
    }

    public string Text { get; }
    public override bool Clickable => true;
    public override float PreferredWidth(Ui ui) => ui.Measure(Text, Font) + 48f;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, PreferredWidth(ui), 40f);
        return 40f;
    }

    public override void Paint(Painter p)
    {
        var background = Active ? Theme.Panel : Theme.Mix(Theme.TabBg, Theme.TabHover, HoverT);
        var foreground = Active ? Color.White : Theme.Mix(Theme.TabText, Color.White, HoverT);
        p.FillCorners(Bounds, background, 6, 6, 0, 0);
        p.Text(Text, Font, foreground, Bounds, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Active) p.FillCorners(new RectangleF(Bounds.X, Bounds.Bottom - 3, Bounds.Width, 3), Theme.Accent, 2, 2, 0, 0);
    }

    public override void Click() => onClick();
}

// .category-tabs (centered, wrapping, 16px gap, 1px bottom border)
internal sealed class TabsEl : El
{
    public readonly List<TabEl> Tabs = new();

    public TabsEl() => MarginBottom = 24;

    public override IEnumerable<El> Kids => Tabs;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var rows = new List<List<TabEl>>();
        var row = new List<TabEl>();
        var rowWidth = 0f;
        foreach (var tab in Tabs)
        {
            var w = tab.PreferredWidth(ui);
            if (row.Count > 0 && rowWidth + 16f + w > width)
            {
                rows.Add(row);
                row = new List<TabEl>();
                rowWidth = 0f;
            }
            rowWidth += (row.Count > 0 ? 16f : 0f) + w;
            row.Add(tab);
        }
        if (row.Count > 0) rows.Add(row);

        var cy = y;
        foreach (var r in rows)
        {
            var total = r.Sum(tab => tab.PreferredWidth(ui)) + 16f * (r.Count - 1);
            var cx = x + (width - total) / 2f;
            foreach (var tab in r)
            {
                tab.Arrange(ui, cx, cy, 0f);
                cx += tab.Bounds.Width + 16f;
            }
            cy += 56f;
        }

        var height = rows.Count == 0 ? 1f : rows.Count * 40f + (rows.Count - 1) * 16f + 1f;
        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        p.Fill(new RectangleF(Bounds.X, Bounds.Bottom - 1, Bounds.Width, 1), Theme.Panel);
        foreach (var tab in Tabs) tab.Paint(p);
    }
}

// .loading-spinner
internal sealed class LoadingEl : El
{
    private static readonly FontSpec Font = FontSpec.Body(16);
    private readonly string text;
    private List<string> lines = new();

    public LoadingEl(string text) => this.text = text;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        lines = ui.Wrap(text, Font, width - 80f);
        var height = 80f + lines.Count * Font.Line;
        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        for (var i = 0; i < lines.Count; i++)
            p.Text(lines[i], Font, Theme.Muted, new RectangleF(Bounds.X + 40, Bounds.Y + 40 + i * Font.Line, Bounds.Width - 80, Font.Line),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// .error-box
internal sealed class MessageEl : El
{
    private static readonly FontSpec Font = FontSpec.Body(16);
    private readonly string text;
    private readonly Color color;
    private readonly ActionButton? button;
    private List<string> lines = new();

    public MessageEl(string text, Color color, ActionButton? button = null)
    {
        this.text = text;
        this.color = color;
        this.button = button;
    }

    public override IEnumerable<El> Kids => button is null ? Array.Empty<El>() : new El[] { button };

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        lines = ui.Wrap(text, Font, width - 60f);
        var height = 30f + lines.Count * Font.Line;
        if (button is not null)
        {
            height += 14f;
            var buttonWidth = button.PreferredWidth(ui);
            button.Arrange(ui, x + (width - buttonWidth) / 2f, y + height, 0f);
            height += 40f;
        }
        height += 30f;
        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        for (var i = 0; i < lines.Count; i++)
            p.Text(lines[i], Font, color, new RectangleF(Bounds.X + 30, Bounds.Y + 30 + i * Font.Line, Bounds.Width - 60, Font.Line),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        button?.Paint(p);
    }
}

// .download-action-btn (+ .secondary-btn, .downloading, :disabled)
internal sealed class ActionButton : El
{
    public static readonly FontSpec Font = FontSpec.Body(15, FontStyle.Bold);

    public ActionButton(string label, Glyph icon = Glyph.None, bool secondary = false)
    {
        Label = label;
        IconGlyph = icon;
        Secondary = secondary;
    }

    public string Label;
    public Glyph IconGlyph;
    public bool Secondary;
    public bool Downloading;
    public bool Disabled;
    public bool Danger;
    public Color Backdrop = Theme.Panel;
    public Action? OnClick;

    public override bool Clickable => true;
    public override bool Enabled => !Disabled;
    public override Cursor Cursor => Downloading ? Cursors.WaitCursor : Disabled ? Cursors.No : Cursors.Hand;

    public override float PreferredWidth(Ui ui) =>
        Math.Max(140f, 36f + (IconGlyph == Glyph.None ? 0f : 28f) + ui.Measure(Label, Font));

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, PreferredWidth(ui), 40f);
        return 40f;
    }

    public override void Paint(Painter p)
    {
        var faded = Disabled && !Downloading;
        if (faded) p.PushOpacity(0.45f, Backdrop);

        var k = 1f - 0.03f * PressT; // :active { transform: scale(.97) }
        var r = new RectangleF(
            Bounds.X + Bounds.Width * (1 - k) / 2, Bounds.Y + Bounds.Height * (1 - k) / 2,
            Bounds.Width * k, Bounds.Height * k);

        if (!Secondary) p.Shadow(r, 6, 2, 8, 0.2f);
        var background = Downloading ? Theme.Green
            : Danger ? Theme.Mix(Theme.Danger, Theme.DangerHover, HoverT)
            : Secondary ? Theme.Mix(Theme.Secondary, Theme.SecondaryHover, HoverT)
            : Theme.Mix(Theme.Blue, Theme.BlueHover, HoverT);
        p.Fill(r, background, 6);

        var textWidth = p.Ui.Measure(Label, Font);
        var iconWidth = IconGlyph == Glyph.None ? 0f : 28f;
        var cx = r.X + (r.Width - iconWidth - textWidth) / 2f;
        if (IconGlyph != Glyph.None) p.Icon(IconGlyph, new RectangleF(cx, r.Y + (r.Height - 18) / 2, 18, 18), Color.White);
        p.Text(Label, Font, Color.White, new RectangleF(cx + iconWidth, r.Y, textWidth + 4, r.Height));

        if (faded) p.PopOpacity();
    }

    public override void Click() => OnClick?.Invoke();
}

// .icon-btn (3-dot menu button)
internal sealed class KebabButton : El
{
    public Func<bool>? IsExpanded;
    public Action? OnClick;

    public override bool Clickable => true;
    public override string? Tip => "More options";
    public override float PreferredWidth(Ui ui) => 40f;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, 40f, 40f);
        return 40f;
    }

    public override void Paint(Painter p)
    {
        var t = Math.Max(HoverT, IsExpanded?.Invoke() == true ? 1f : 0f);
        p.Fill(Bounds, Theme.Mix(Theme.Panel, Theme.PanelActive, t), 6);
        p.Icon(Glyph.Kebab, new RectangleF(Bounds.X + 10, Bounds.Y + 10, 20, 20), Theme.Mix(Theme.IconColor, Color.White, t));
    }

    public override void Click() => OnClick?.Invoke();
}

// .version-box (also used for installed servers)
internal sealed class VersionBox : El
{
    private static readonly FontSpec TitleFont = FontSpec.Body(16, FontStyle.Bold);
    private static readonly FontSpec SubtitleFont = FontSpec.Body(13);
    private readonly string title;
    private readonly string subtitle;
    private readonly long born = Stopwatch.GetTimestamp();
    private List<string> titleLines = new();
    private List<string> subtitleLines = new();
    private float infoX;
    private float infoY;

    public readonly List<El> Actions = new();

    public VersionBox(string title, string subtitle)
    {
        this.title = title;
        this.subtitle = subtitle;
    }

    public override bool TracksHover => true;
    public override IEnumerable<El> Kids => Actions;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var contentX = x + 21f;
        var contentWidth = width - 42f;
        var actionsWidth = Actions.Sum(action => action.PreferredWidth(ui)) + Math.Max(0, Actions.Count - 1) * 10f;
        var infoWidth = Math.Max(40f, contentWidth - actionsWidth - (Actions.Count > 0 ? 16f : 0f));

        titleLines = ui.Wrap(title, TitleFont, infoWidth);
        subtitleLines = string.IsNullOrEmpty(subtitle) ? new List<string>() : ui.Wrap(subtitle, SubtitleFont, infoWidth);
        var infoHeight = titleLines.Count * TitleFont.Line + (subtitleLines.Count > 0 ? 4f + subtitleLines.Count * SubtitleFont.Line : 0f);
        var height = Math.Max(70f, Math.Max(infoHeight, Actions.Count > 0 ? 40f : 0f) + 30f);

        infoX = contentX;
        infoY = y + (height - infoHeight) / 2f;
        var ax = contentX + contentWidth - actionsWidth;
        foreach (var action in Actions)
        {
            action.Arrange(ui, ax, y + (height - 40f) / 2f, 0f);
            ax += action.Bounds.Width + 10f;
        }

        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        // @keyframes fadeInBox: opacity 0 -> 1, translateY(12px) -> 0, .25s ease-out
        var t = (float)Math.Min(1.0, Stopwatch.GetElapsedTime(born).TotalSeconds / 0.25);
        var eased = 1f - (1f - t) * (1f - t) * (1f - t);
        var fading = eased < 0.999f;
        var savedOffset = p.OffsetY;
        if (fading)
        {
            p.PushOpacity(eased, Theme.Page);
            p.OffsetY += (1f - eased) * 12f;
        }

        p.Fill(Bounds, Theme.Mix(Theme.Panel, Theme.BoxHoverBorder, HoverT), 6);
        p.Fill(RectangleF.Inflate(Bounds, -1, -1), Theme.Panel, 5);

        var ty = infoY;
        foreach (var line in titleLines)
        {
            p.Text(line, TitleFont, Color.White, new RectangleF(infoX, ty, Bounds.Right - infoX, TitleFont.Line));
            ty += TitleFont.Line;
        }
        if (subtitleLines.Count > 0) ty += 4f;
        foreach (var line in subtitleLines)
        {
            p.Text(line, SubtitleFont, Theme.Muted, new RectangleF(infoX, ty, Bounds.Right - infoX, SubtitleFont.Line));
            ty += SubtitleFont.Line;
        }

        foreach (var action in Actions) action.Paint(p);

        if (fading)
        {
            p.PopOpacity();
            p.OffsetY = savedOffset;
        }
    }
}

// .notice
internal sealed class NoticeEl : El
{
    private static readonly FontSpec Font = FontSpec.Body(14, FontStyle.Regular, 1.5f);
    private readonly string text;
    private readonly ActionButton? button;
    private List<string> lines = new();
    private float textY;

    public NoticeEl(string text, ActionButton? button = null)
    {
        this.text = text;
        this.button = button;
        MarginBottom = 16;
    }

    public override IEnumerable<El> Kids => button is null ? Array.Empty<El>() : new El[] { button };

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var innerWidth = width - 40f;
        var buttonWidth = button?.PreferredWidth(ui) ?? 0f;
        var textWidth = innerWidth - (button is null ? 0f : buttonWidth + 16f);
        lines = ui.Wrap(text, Font, textWidth);
        var textHeight = lines.Count * Font.Line;
        var height = Math.Max(textHeight, button is null ? 0f : 40f) + 28f;
        textY = y + (height - textHeight) / 2f;
        button?.Arrange(ui, x + width - 18f - buttonWidth, y + (height - 40f) / 2f, 0f);
        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        p.Fill(Bounds, Theme.Accent, 6);
        p.FillCorners(new RectangleF(Bounds.X + 4, Bounds.Y, Bounds.Width - 4, Bounds.Height), Theme.NoticeBg, 2, 6, 6, 2);
        for (var i = 0; i < lines.Count; i++)
            p.Text(lines[i], Font, Theme.NoticeText, new RectangleF(Bounds.X + 22, textY + i * Font.Line, Bounds.Width - 40, Font.Line));
        button?.Paint(p);
    }
}

// .settings-section
internal sealed class SettingsSection : El
{
    private static readonly FontSpec NameFont = FontSpec.Body(16, FontStyle.Bold);
    private static readonly FontSpec ValueFont = FontSpec.Mono(14);
    private static readonly FontSpec HintFont = FontSpec.Body(13, FontStyle.Regular, 1.5f);
    private readonly string name;
    private readonly string? value;
    private readonly string hint;
    private readonly ActionButton[] buttons;
    private List<string> valueLines = new();
    private List<string> hintLines = new();
    private float textX;
    private float textY;
    private float textWidth;

    public SettingsSection(string name, string? value, string hint, params ActionButton[] buttons)
    {
        this.name = name;
        this.value = value;
        this.hint = hint;
        this.buttons = buttons;
    }

    public override IEnumerable<El> Kids => buttons;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var innerWidth = width - 40f;
        var actionsWidth = buttons.Sum(button => button.PreferredWidth(ui)) + Math.Max(0, buttons.Length - 1) * 10f;
        var available = innerWidth - (buttons.Length > 0 ? actionsWidth + 20f : 0f);
        var maxContent = Math.Max(ui.Measure(name, NameFont), ui.Measure(hint, HintFont));
        if (value is not null) maxContent = Math.Max(maxContent, ui.Measure(value, ValueFont) + 20f);
        textWidth = Math.Max(60f, Math.Min(maxContent, available));

        valueLines = value is null ? new List<string>() : ui.Wrap(value, ValueFont, textWidth - 20f, breakAll: true);
        hintLines = ui.Wrap(hint, HintFont, textWidth);
        var textHeight = NameFont.Line
            + (value is null ? 0f : 6f + valueLines.Count * ValueFont.Line + 12f)
            + 6f + hintLines.Count * HintFont.Line;
        var height = Math.Max(textHeight, buttons.Length > 0 ? 40f : 0f) + 40f;

        textX = x + 20f;
        textY = y + (height - textHeight) / 2f;
        var bx = x + width - 20f - actionsWidth;
        foreach (var button in buttons)
        {
            button.Arrange(ui, bx, y + (height - 40f) / 2f, 0f);
            bx += button.Bounds.Width + 10f;
        }

        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        p.Fill(Bounds, Theme.Panel, 6);
        var ty = textY;
        p.Text(name, NameFont, Color.White, new RectangleF(textX, ty, textWidth + 4, NameFont.Line));
        ty += NameFont.Line + 6f;

        if (value is not null)
        {
            var boxHeight = valueLines.Count * ValueFont.Line + 12f;
            p.Fill(new RectangleF(textX, ty, textWidth, boxHeight), Theme.CodeBg, 4);
            for (var i = 0; i < valueLines.Count; i++)
                p.Text(valueLines[i], ValueFont, Color.White, new RectangleF(textX + 10, ty + 6 + i * ValueFont.Line, textWidth - 16, ValueFont.Line));
            ty += boxHeight + 6f;
        }

        for (var i = 0; i < hintLines.Count; i++)
            p.Text(hintLines[i], HintFont, Theme.Muted, new RectangleF(textX, ty + i * HintFont.Line, textWidth + 4, HintFont.Line));

        foreach (var button in buttons) button.Paint(p);
    }
}

// .modal-card h2
internal sealed class HeadingEl : El
{
    private static readonly FontSpec Font = FontSpec.Body(20, FontStyle.Bold);
    private readonly string text;
    private List<string> lines = new();

    public HeadingEl(string text)
    {
        this.text = text;
        MarginBottom = 10;
    }

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        lines = ui.Wrap(text, Font, width);
        Bounds = new RectangleF(x, y, width, lines.Count * Font.Line);
        return Bounds.Height;
    }

    public override void Paint(Painter p)
    {
        for (var i = 0; i < lines.Count; i++)
            p.Text(lines[i], Font, Color.White, new RectangleF(Bounds.X, Bounds.Y + i * Font.Line, Bounds.Width, Font.Line));
    }
}

// Paragraph text with inline <code> chips (modal body copy).
internal sealed class RichTextEl : El
{
    private static readonly FontSpec CodeFont = FontSpec.Mono(11.5f);
    private readonly (string Text, bool Code)[] runs;
    private readonly FontSpec font;
    private readonly Color color;
    private readonly List<(string Text, bool Code, float X, int Line, float Width)> placed = new();

    public RichTextEl(string text, FontSpec? font = null, Color? color = null)
        : this(new (string, bool)[] { (text, false) }, font, color)
    {
    }

    public RichTextEl((string Text, bool Code)[] runs, FontSpec? font = null, Color? color = null)
    {
        this.runs = runs;
        this.font = font ?? FontSpec.Body(14, FontStyle.Regular, 1.6f);
        this.color = color ?? Theme.ModalText;
    }

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        placed.Clear();
        var space = ui.SpaceWidth(font);
        var cx = 0f;
        var line = 0;
        var pendingSpace = false;

        void Place(string token, bool isCode)
        {
            var w = isCode ? ui.Measure(token, CodeFont) + 12f : ui.Measure(token, font);
            var gap = cx > 0f && pendingSpace ? space : 0f;
            if (cx > 0f && cx + gap + w > width)
            {
                line++;
                cx = 0f;
                gap = 0f;
            }
            placed.Add((token, isCode, cx + gap, line, w));
            cx += gap + w;
            pendingSpace = false;
        }

        foreach (var (runText, runCode) in runs)
        {
            if (runCode)
            {
                Place(runText, true);
                continue;
            }
            var parts = runText.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) pendingSpace = true;
                if (parts[i].Length > 0) Place(parts[i], false);
            }
        }

        var lines = placed.Count == 0 ? 1 : line + 1;
        Bounds = new RectangleF(x, y, width, lines * font.Line);
        return Bounds.Height;
    }

    public override void Paint(Painter p)
    {
        foreach (var (text, code, px, line, w) in placed)
        {
            var top = Bounds.Y + line * font.Line;
            if (code)
            {
                var chipHeight = CodeFont.Line + 4f;
                var chip = new RectangleF(Bounds.X + px, top + (font.Line - chipHeight) / 2f, w, chipHeight);
                p.Fill(chip, Theme.CodeBg, 4);
                p.Text(text, CodeFont, Color.White, chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                p.Text(text, font, color, new RectangleF(Bounds.X + px, top, w + 2, font.Line));
            }
        }
    }
}

// <input type="range"> with accent-color: #ff5722
internal sealed class SliderEl : El
{
    public SliderEl(int min, int max, int value)
    {
        Min = min;
        Max = max;
        Value = Math.Clamp(value, min, max);
    }

    public int Min { get; }
    public int Max { get; }
    public int Value { get; private set; }
    public Action? Changed;

    public override bool Clickable => true;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, Math.Max(20f, width), 16f);
        return 16f;
    }

    public bool Set(int value)
    {
        value = Math.Clamp(value, Min, Max);
        if (value == Value) return false;
        Value = value;
        Changed?.Invoke();
        return true;
    }

    public override bool PointerDown(PointF point) => Set(ValueAt(point.X));
    public override bool PointerDrag(PointF point) => Set(ValueAt(point.X));

    private int ValueAt(float x)
    {
        var left = Bounds.X + 8f;
        var width = Math.Max(1f, Bounds.Width - 16f);
        var fraction = Math.Clamp((x - left) / width, 0f, 1f);
        return Min + (int)MathF.Round(fraction * (Max - Min));
    }

    public override void Paint(Painter p)
    {
        var cy = Bounds.Y + Bounds.Height / 2f;
        var fraction = Max == Min ? 0f : (Value - Min) / (float)(Max - Min);
        var thumbX = Bounds.X + 8f + (Bounds.Width - 16f) * fraction;
        var accent = Theme.Mix(Theme.Accent, Theme.AccentHover, Math.Max(HoverT, PressT));
        var track = new RectangleF(Bounds.X, cy - 2.5f, Bounds.Width, 5f);
        p.Fill(track, Theme.SliderTrack, 2.5f);
        p.FillCorners(new RectangleF(track.X, track.Y, thumbX - track.X, track.Height), accent, 2.5f, 0, 0, 2.5f);
        p.Fill(new RectangleF(thumbX - 8f, cy - 8f, 16f, 16f), accent, 8f);
    }
}

// .ram-row
internal sealed class SliderRow : El
{
    private static readonly FontSpec ValueFont = FontSpec.Heavy(22);
    private RectangleF valueRect;

    public SliderRow(SliderEl slider) => Slider = slider;

    public SliderEl Slider { get; }
    public override IEnumerable<El> Kids => new El[] { Slider };

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var height = ValueFont.Line;
        var valueWidth = Math.Max(70f, ui.Measure($"{Slider.Max} GB", ValueFont));
        Slider.Arrange(ui, x + 2f, y + (height - 16f) / 2f, width - 16f - valueWidth - 4f);
        valueRect = new RectangleF(x + width - valueWidth, y, valueWidth, height);
        Bounds = new RectangleF(x, y, width, height);
        return height;
    }

    public override void Paint(Painter p)
    {
        Slider.Paint(p);
        p.Text($"{Slider.Value} GB", ValueFont, Color.White, valueRect, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

// .modal-actions
internal sealed class ButtonRow : El
{
    private readonly ActionButton[] buttons;

    public ButtonRow(params ActionButton[] buttons)
    {
        this.buttons = buttons;
        MarginTop = 20;
    }

    public override IEnumerable<El> Kids => buttons;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var total = buttons.Sum(button => button.PreferredWidth(ui)) + Math.Max(0, buttons.Length - 1) * 10f;
        var cx = x + width - total;
        foreach (var button in buttons)
        {
            button.Arrange(ui, cx, y, 0f);
            cx += button.Bounds.Width + 10f;
        }
        Bounds = new RectangleF(x, y, width, 40f);
        return 40f;
    }

    public override void Paint(Painter p)
    {
        foreach (var button in buttons) button.Paint(p);
    }
}

// .modal-card
internal sealed class ModalCard : El
{
    private readonly El[] blocks;

    public ModalCard(float maxWidth, params El[] blocks)
    {
        MaxWidth = maxWidth;
        this.blocks = blocks;
    }

    public float MaxWidth { get; }
    public override IEnumerable<El> Kids => blocks;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var cy = y + 4f + 24f;
        foreach (var block in blocks)
        {
            cy += block.MarginTop;
            var height = block.Arrange(ui, x + 24f, cy, width - 48f);
            cy += height + block.MarginBottom;
        }
        var total = cy - y + 24f;
        Bounds = new RectangleF(x, y, width, total);
        return total;
    }

    public override void Paint(Painter p)
    {
        p.Fill(Bounds, Theme.Accent, 8);
        p.FillCorners(new RectangleF(Bounds.X, Bounds.Y + 4, Bounds.Width, Bounds.Height - 4), Theme.Panel, 6, 6, 8, 8);
        foreach (var block in blocks) block.Paint(p);
    }
}

internal sealed record MenuItemSpec(string Label, Action Action, bool Danger = false);

// .context-menu button
internal sealed class MenuItemEl : El
{
    public static readonly FontSpec Font = FontSpec.Body(14);
    private readonly Action action;
    private readonly bool danger;

    public MenuItemEl(string label, Action action, bool danger = false)
    {
        Label = label;
        this.action = action;
        this.danger = danger;
    }

    public string Label { get; }
    public override bool Clickable => true;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, width, 20f + Font.Line);
        return Bounds.Height;
    }

    public override void Paint(Painter p)
    {
        if (Hot) p.Fill(Bounds, Theme.MenuHover);
        var color = danger ? Theme.DangerText : Hot ? Color.White : Theme.MenuText;
        p.Text(Label, Font, color, new RectangleF(Bounds.X + 16, Bounds.Y, Bounds.Width - 32, Bounds.Height));
    }

    public override void Click() => action();
}

// .context-menu
internal sealed class MenuEl : El
{
    private readonly MenuItemEl[] items;

    public MenuEl(El owner, MenuItemEl[] items)
    {
        Owner = owner;
        this.items = items;
    }

    public El Owner { get; }
    public override IEnumerable<El> Kids => items;
    public float TotalHeight => 12f + items.Length * (20f + MenuItemEl.Font.Line);

    public override float PreferredWidth(Ui ui) =>
        Math.Max(170f, items.Length == 0 ? 0f : items.Max(item => ui.Measure(item.Label, MenuItemEl.Font)) + 32f);

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        var cy = y + 6f;
        foreach (var item in items) cy += item.Arrange(ui, x, cy, width);
        Bounds = new RectangleF(x, y, width, cy - y + 6f);
        return Bounds.Height;
    }

    public override void Paint(Painter p)
    {
        p.Shadow(Bounds, 6, 6, 20, 0.5f);
        p.Fill(Bounds, Theme.MenuBg, 6);
        foreach (var item in items) item.Paint(p);
    }
}

// .panel-btn
internal sealed class SidebarButton : El
{
    private readonly Glyph glyph;
    private readonly string label;
    private readonly Action onClick;
    public bool Active;

    public SidebarButton(Glyph glyph, string label, Action onClick)
    {
        this.glyph = glyph;
        this.label = label;
        this.onClick = onClick;
    }

    public override bool Clickable => true;
    public override string? Tip => label;

    public override float Arrange(Ui ui, float x, float y, float width)
    {
        Bounds = new RectangleF(x, y, Surface.SidebarWidth, 55f);
        return 55f;
    }

    public override void Paint(Painter p)
    {
        var background = Active ? Theme.PanelActive : Theme.Mix(Theme.Panel, Theme.PanelHover, HoverT);
        var foreground = Active ? Color.White : Theme.Mix(Theme.IconColor, Color.White, HoverT);
        p.Fill(Bounds, background);
        if (Active) p.Fill(new RectangleF(Bounds.X, Bounds.Y, 4, Bounds.Height), Theme.Accent);
        p.Icon(glyph, new RectangleF(Bounds.X + (Bounds.Width - 24) / 2, Bounds.Y + (Bounds.Height - 24) / 2, 24, 24), foreground);
    }

    public override void Click() => onClick();
}

// ---------------------------------------------------------------------------------------------
// Surface: one double-buffered control that paints the whole window like the browser does.
// ---------------------------------------------------------------------------------------------
internal sealed class Surface : Control
{
    public const float SidebarWidth = 75f;
    private const float ScrollbarWidth = 8f;
    private static readonly FontSpec ToastFont = FontSpec.Body(16);

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ToolTip toolTip = new();
    private readonly HashSet<El> hot = new();
    private readonly HashSet<El> animating = new();

    private double lastTick;
    private double appearUntil;
    private PointF? mouse;
    private El? hoverTarget;
    private El? pressed;
    private El? tipOwner;
    private bool hoverInContent;
    private bool pressedInContent;
    private float scroll;
    private float scrollTarget;
    private float contentHeight;
    private bool hasScrollbar;
    private bool thumbHot;
    private bool draggingThumb;
    private bool backdropDown;
    private float dragOffset;
    private MenuEl? menu;
    private ModalState? modal;

    private string? toastText;
    private bool toastVisible;
    private double toastHideAt;
    private float toastT;
    private Bitmap? toastBitmap;
    private string? toastBitmapKey;

    private sealed record ModalState(ModalCard Card, bool CloseOnBackdrop, Action OnEscape, Action<Keys>? OnKey);

    public Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Page;
        TabStop = true;
        timer.Tick += (_, _) => Tick();
    }

    public Ui Ui { get; } = new();
    public PageRoot Root { get; } = new();
    public List<SidebarButton> NavTop { get; } = new();
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SidebarButton? NavBottom { get; set; }
    public El? MenuOwner => menu?.Owner;

    private float LW => ClientSize.Width / Ui.Scale;
    private float LH => ClientSize.Height / Ui.Scale;
    private float MaxScroll => Math.Max(0f, contentHeight - LH);
    private double Now => clock.Elapsed.TotalSeconds;

    // ----- public API -----

    public void Relayout()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var mainWidth = LW - SidebarWidth;
        contentHeight = Root.Arrange(Ui, SidebarWidth, 0f, mainWidth);
        hasScrollbar = contentHeight > LH + 0.5f;
        if (hasScrollbar) contentHeight = Root.Arrange(Ui, SidebarWidth, 0f, mainWidth - ScrollbarWidth);
        scroll = Math.Clamp(scroll, 0f, MaxScroll);
        scrollTarget = Math.Clamp(scrollTarget, 0f, MaxScroll);

        for (var i = 0; i < NavTop.Count; i++) NavTop[i].Arrange(Ui, 0f, 12f + i * 55f, SidebarWidth);
        NavBottom?.Arrange(Ui, 0f, LH - 12f - 55f, SidebarWidth);

        if (modal is not null) ArrangeModal();
        UpdateHover();
        Invalidate();
    }

    public void Touch()
    {
        UpdateHover();
        Invalidate();
    }

    public void StartAppear()
    {
        appearUntil = Now + 0.32;
        EnsureTimer();
    }

    public void ShowToast(string message)
    {
        toastText = message;
        toastVisible = true;
        toastHideAt = Now + 3.5;
        EnsureTimer();
        Invalidate();
    }

    public void ShowModal(ModalCard card, bool closeOnBackdrop, Action onEscape, Action<Keys>? onKey = null)
    {
        CloseMenu();
        modal = new ModalState(card, closeOnBackdrop, onEscape, onKey);
        ArrangeModal();
        UpdateHover();
        Invalidate();
    }

    public void CloseModal()
    {
        if (modal is null) return;
        modal = null;
        backdropDown = false;
        UpdateHover();
        Invalidate();
    }

    public void OpenMenu(El owner, IReadOnlyList<MenuItemSpec> items)
    {
        CloseMenu();
        var created = new MenuEl(owner, items.Select(spec => new MenuItemEl(spec.Label, () =>
        {
            CloseMenu();
            spec.Action();
        }, spec.Danger)).ToArray());

        var width = created.PreferredWidth(Ui);
        var height = created.TotalHeight;
        var anchor = owner.Bounds;
        anchor.Y -= scroll;
        var top = anchor.Bottom + 6f;
        if (top + height > LH - 8f) top = anchor.Top - height - 6f;
        var left = Math.Max(8f, anchor.Right - width);
        created.Arrange(Ui, left, top, width);
        menu = created;
        UpdateHover();
        Invalidate();
    }

    public void CloseMenu()
    {
        if (menu is null) return;
        menu = null;
        UpdateHover();
        Invalidate();
    }

    // ----- lifecycle -----

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.SetScale(DeviceDpi / 96f);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Ui.SetScale(DeviceDpi / 96f);
        toastBitmapKey = null;
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CloseMenu();
        Relayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            toolTip.Dispose();
            toastBitmap?.Dispose();
            Ui.Dispose();
        }
        base.Dispose(disposing);
    }

    // ----- painting -----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.Clear(Theme.Page);

        var p = new Painter(g, Ui) { ViewTop = 0f, ViewBottom = LH };

        var mainRight = LW - (hasScrollbar ? ScrollbarWidth : 0f);
        var state = p.Clip(RectangleF.FromLTRB(SidebarWidth, 0f, mainRight, LH));
        p.OffsetY = -scroll;
        Root.Paint(p);
        p.OffsetY = 0f;
        g.Restore(state);

        p.Fill(new RectangleF(0f, 0f, SidebarWidth, LH), Theme.Panel);
        foreach (var nav in Navs()) nav.Paint(p);

        if (hasScrollbar)
        {
            var (thumbY, thumbHeight) = Thumb();
            p.Fill(new RectangleF(LW - ScrollbarWidth, thumbY, ScrollbarWidth, thumbHeight),
                thumbHot || draggingThumb ? Theme.ScrollThumbHover : Theme.ScrollThumb, 4f);
        }

        PaintToast(g, p);
        menu?.Paint(p);

        if (modal is not null)
        {
            using var shade = new SolidBrush(Color.FromArgb(153, 0, 0, 0));
            g.FillRectangle(shade, 0, 0, ClientSize.Width, ClientSize.Height);
            modal.Card.Paint(p);
        }
    }

    // .toast-notification (fades + slides, text kept crisp via a cached ClearType bitmap)
    private void PaintToast(Graphics g, Painter p)
    {
        if (toastText is null || toastT <= 0.001f) return;
        var eased = Ease(toastT);
        var s = Ui.Scale;

        var lines = Ui.Wrap(toastText, ToastFont, Math.Max(80f, LW - 40f - 44f));
        var textWidth = lines.Max(line => Ui.Measure(line, ToastFont));
        var width = textWidth + 44f;
        var height = lines.Count * ToastFont.Line + 24f;
        var box = new RectangleF(LW - 20f - width, LH - 20f - height + (1f - eased) * 20f, width, height);
        var textRect = new Rectangle(
            (int)MathF.Round((box.X + 24f) * s),
            (int)MathF.Round((box.Y + 12f) * s),
            (int)MathF.Ceiling(textWidth * s) + 2,
            Math.Max(1, (int)MathF.Round(lines.Count * ToastFont.Line * s)));

        var key = $"{toastText}|{s}|{textRect.Width}|{textRect.Height}";
        if (toastBitmap is null || toastBitmapKey != key)
        {
            toastBitmap?.Dispose();
            toastBitmap = new Bitmap(textRect.Width, textRect.Height, PixelFormat.Format32bppRgb);
            using (var bg = Graphics.FromImage(toastBitmap))
            {
                bg.Clear(Theme.MenuBg);
                var font = Ui.GetFont(ToastFont);
                for (var i = 0; i < lines.Count; i++)
                {
                    var lineRect = new Rectangle(0, (int)MathF.Round(i * ToastFont.Line * s), textRect.Width, (int)MathF.Round(ToastFont.Line * s));
                    TextRenderer.DrawText(bg, lines[i], font, lineRect, Color.White, Ui.BaseFlags | TextFormatFlags.VerticalCenter);
                }
            }
            toastBitmapKey = key;
        }

        p.Shadow(box, 6f, 4f, 12f, 0.4f * eased);

        var outer = g.Save();
        g.SetClip(textRect, CombineMode.Exclude);
        var strip = g.Save();
        g.SetClip(p.Snapped(new RectangleF(box.X, box.Y, 4f, box.Height)), CombineMode.Intersect);
        p.FillCorners(box, Theme.Accent, 6, 6, 6, 6, eased);
        g.Restore(strip);
        p.FillCorners(new RectangleF(box.X + 4f, box.Y, box.Width - 4f, box.Height), Theme.MenuBg, 2, 6, 6, 2, eased);
        g.Restore(outer);

        if (eased >= 0.999f)
        {
            g.DrawImage(toastBitmap, textRect);
        }
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = eased });
            g.DrawImage(toastBitmap, textRect, 0, 0, toastBitmap.Width, toastBitmap.Height, GraphicsUnit.Pixel, attributes);
        }
    }

    // ----- input -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var w = ToLogical(e.Location);
        mouse = w;

        if (draggingThumb)
        {
            var (_, thumbHeight) = Thumb();
            var range = LH - thumbHeight;
            var fraction = range <= 0f ? 0f : (w.Y - dragOffset) / range;
            scroll = scrollTarget = Math.Clamp(fraction, 0f, 1f) * MaxScroll;
            CloseMenu();
            UpdateHover();
            Invalidate();
            return;
        }

        if (pressed is not null && pressed.PointerDrag(ToSpace(w, pressedInContent))) Invalidate();
        UpdateHover();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouse = null;
        UpdateHover();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused) Focus();
        if (e.Button != MouseButtons.Left) return;

        var w = ToLogical(e.Location);
        mouse = w;

        if (modal is not null)
        {
            backdropDown = !modal.Card.Bounds.Contains(w);
        }
        else
        {
            if (menu is not null && !menu.Bounds.Contains(w) && !OwnerWindowRect(menu).Contains(w)) CloseMenu();

            if (hasScrollbar && w.X >= LW - ScrollbarWidth && (menu is null || !menu.Bounds.Contains(w)))
            {
                var (thumbY, thumbHeight) = Thumb();
                if (w.Y >= thumbY && w.Y < thumbY + thumbHeight)
                {
                    draggingThumb = true;
                    dragOffset = w.Y - thumbY;
                }
                else
                {
                    ScrollBy(w.Y < thumbY ? -LH * 0.875f : LH * 0.875f);
                }
                Invalidate();
                return;
            }
        }

        UpdateHover();
        if (hoverTarget is { Enabled: true } target)
        {
            pressed = target;
            pressedInContent = hoverInContent;
            target.Pressed = true;
            animating.Add(target);
            EnsureTimer();
            if (target.PointerDown(ToSpace(w, pressedInContent))) Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        var w = ToLogical(e.Location);
        mouse = w;

        if (draggingThumb)
        {
            draggingThumb = false;
            UpdateHover();
            Invalidate();
            return;
        }

        var released = pressed;
        pressed = null;
        if (released is not null)
        {
            released.Pressed = false;
            animating.Add(released);
            EnsureTimer();
        }
        UpdateHover();

        if (modal is not null && backdropDown && released is null && !modal.Card.Bounds.Contains(w) && modal.CloseOnBackdrop)
        {
            backdropDown = false;
            modal.OnEscape();
            return;
        }
        backdropDown = false;

        if (released is not null && ReferenceEquals(released, hoverTarget)) released.Click();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (modal is not null) return;
        if (ToLogical(e.Location).X < SidebarWidth) return;
        ScrollBy(-e.Delta / 120f * 100f);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (menu is not null) CloseMenu();
            else modal?.OnEscape.Invoke();
            e.Handled = true;
            return;
        }

        if (modal is not null)
        {
            modal.OnKey?.Invoke(e.KeyCode);
            Invalidate();
            return;
        }

        float? delta = e.KeyCode switch
        {
            Keys.Up => -40f,
            Keys.Down => 40f,
            Keys.PageUp => -LH * 0.875f,
            Keys.PageDown or Keys.Space => LH * 0.875f,
            Keys.Home => -contentHeight,
            Keys.End => contentHeight,
            _ => null
        };
        if (delta is float d) ScrollBy(d);
    }

    // ----- internals -----

    private IEnumerable<SidebarButton> Navs()
    {
        foreach (var nav in NavTop) yield return nav;
        if (NavBottom is not null) yield return NavBottom;
    }

    private PointF ToLogical(Point point) => new(point.X / Ui.Scale, point.Y / Ui.Scale);

    private PointF ToSpace(PointF window, bool content) => content ? new PointF(window.X, window.Y + scroll) : window;

    private RectangleF OwnerWindowRect(MenuEl m)
    {
        var rect = m.Owner.Bounds;
        rect.Y -= scroll;
        return rect;
    }

    private void ArrangeModal()
    {
        if (modal is null) return;
        var width = Math.Min(modal.Card.MaxWidth, LW - 40f);
        var height = modal.Card.Arrange(Ui, 0f, 0f, width);
        modal.Card.Arrange(Ui, (LW - width) / 2f, Math.Max(20f, (LH - height) / 2f), width);
    }

    private (float Y, float Height) Thumb()
    {
        var height = Math.Min(LH, Math.Max(24f, LH * LH / Math.Max(contentHeight, 1f)));
        var y = MaxScroll <= 0f ? 0f : scroll / MaxScroll * (LH - height);
        return (y, height);
    }

    private void ScrollBy(float delta)
    {
        if (!hasScrollbar) return;
        scrollTarget = Math.Clamp(scrollTarget + delta, 0f, MaxScroll);
        CloseMenu();
        EnsureTimer();
    }

    private static void Collect(El element, PointF point, List<El> chain)
    {
        if (!element.Bounds.Contains(point)) return;
        if (element.TracksHover || element.Clickable) chain.Add(element);
        foreach (var kid in element.Kids) Collect(kid, point, chain);
    }

    private void UpdateHover()
    {
        var chain = new List<El>();
        var inContent = false;
        var wasThumbHot = thumbHot;
        thumbHot = false;

        if (mouse is PointF w && !draggingThumb)
        {
            if (modal is not null)
            {
                Collect(modal.Card, w, chain);
            }
            else if (menu is not null && menu.Bounds.Contains(w))
            {
                Collect(menu, w, chain);
            }
            else if (w.X < SidebarWidth)
            {
                foreach (var nav in Navs()) Collect(nav, w, chain);
            }
            else if (hasScrollbar && w.X >= LW - ScrollbarWidth)
            {
                var (thumbY, thumbHeight) = Thumb();
                thumbHot = w.Y >= thumbY && w.Y < thumbY + thumbHeight;
            }
            else
            {
                inContent = true;
                Collect(Root, new PointF(w.X, w.Y + scroll), chain);
            }
        }

        foreach (var element in hot)
        {
            if (chain.Contains(element)) continue;
            element.Hot = false;
            animating.Add(element);
        }
        hot.RemoveWhere(element => !element.Hot);
        foreach (var element in chain)
        {
            if (!hot.Add(element)) continue;
            element.Hot = true;
            animating.Add(element);
        }

        hoverTarget = chain.LastOrDefault(element => element.Clickable);
        hoverInContent = inContent;

        var cursor = hoverTarget?.Cursor ?? Cursors.Default;
        if (Cursor != cursor) Cursor = cursor;

        if (!ReferenceEquals(tipOwner, hoverTarget))
        {
            tipOwner = hoverTarget;
            toolTip.SetToolTip(this, hoverTarget?.Tip);
        }

        if (wasThumbHot != thumbHot) Invalidate();
        if (animating.Count > 0) EnsureTimer();
    }

    private void EnsureTimer()
    {
        if (timer.Enabled) return;
        lastTick = Now;
        timer.Start();
    }

    private void Tick()
    {
        var now = Now;
        var dt = (float)Math.Clamp(now - lastTick, 0.0, 0.05);
        lastTick = now;
        var dirty = false;
        var busy = false;

        if (animating.Count > 0)
        {
            foreach (var element in animating.ToArray())
            {
                var hoverGoal = element.Hot ? 1f : 0f;
                var pressGoal = element.Pressed ? 1f : 0f;
                element.HoverT = Approach(element.HoverT, hoverGoal, dt / 0.15f);
                element.PressT = Approach(element.PressT, pressGoal, dt / 0.1f);
                if (element.HoverT == hoverGoal && element.PressT == pressGoal) animating.Remove(element);
                else busy = true;
            }
            dirty = true;
        }

        if (scroll != scrollTarget)
        {
            scroll += (scrollTarget - scroll) * (1f - MathF.Exp(-dt * 20f));
            if (Math.Abs(scrollTarget - scroll) < 0.5f) scroll = scrollTarget;
            UpdateHover();
            dirty = true;
            busy |= scroll != scrollTarget;
        }

        if (toastVisible && now >= toastHideAt) toastVisible = false;
        var toastGoal = toastVisible ? 1f : 0f;
        if (toastT != toastGoal)
        {
            toastT = Approach(toastT, toastGoal, dt / 0.3f);
            dirty = true;
        }
        busy |= toastVisible || toastT != toastGoal;

        if (now < appearUntil)
        {
            dirty = true;
            busy = true;
        }

        if (dirty) Invalidate();
        if (!busy) timer.Stop();
    }

    private static float Approach(float value, float goal, float step) =>
        value < goal ? Math.Min(goal, value + step) : Math.Max(goal, value - step);

    private static float Ease(float t) => 1f - (1f - t) * (1f - t) * (1f - t);
}