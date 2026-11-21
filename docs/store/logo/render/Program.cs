using SkiaSharp;
using Svg.Skia;

// Renders every MSIX logo size and scale from the two SVG sources in docs/store/logo.
// The large source is used from 48 px up; the small one (thicker ring, bigger spark) below that.
var repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var logoDir = Path.Combine(repo, "docs", "store", "logo");
var assets = Path.Combine(repo, "src", "PortalAIHost.App", "Assets");
Directory.CreateDirectory(assets);

using var big = Load(Path.Combine(logoDir, "portal-ai-host-icon.svg"));
using var small = Load(Path.Combine(logoDir, "portal-ai-host-icon-small.svg"));

int[] scales = [100, 125, 150, 200, 400];
static int S(int px, int scale) => (int)Math.Round(px * scale / 100.0);

foreach (var sc in scales)
{
    var px = S(44, sc);
    Save(Render(px, px, px), Path.Combine(assets, $"Square44x44Logo.scale-{sc}.png"));
    Save(Render(S(150, sc), S(150, sc), S(150, sc) * 0.62), Path.Combine(assets, $"Square150x150Logo.scale-{sc}.png"));
    Save(Render(S(310, sc), S(150, sc), S(150, sc) * 0.62), Path.Combine(assets, $"Wide310x150Logo.scale-{sc}.png"));
    Save(Render(S(310, sc), S(310, sc), S(310, sc) * 0.55), Path.Combine(assets, $"LargeTile.scale-{sc}.png"));
    Save(Render(S(71, sc), S(71, sc), S(71, sc) * 0.78), Path.Combine(assets, $"SmallTile.scale-{sc}.png"));
    Save(Render(S(50, sc), S(50, sc), S(50, sc)), Path.Combine(assets, $"StoreLogo.scale-{sc}.png"));
    Save(Render(S(620, sc), S(300, sc), S(300, sc) * 0.6), Path.Combine(assets, $"SplashScreen.scale-{sc}.png"));
}
foreach (var t in new[] { 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 })
{
    var img = Render(t, t, t);
    Save(img, Path.Combine(assets, $"Square44x44Logo.targetsize-{t}.png"));
    Save(Render(t, t, t), Path.Combine(assets, $"Square44x44Logo.targetsize-{t}_altform-unplated.png"));
    Save(Render(t, t, t), Path.Combine(assets, $"Square44x44Logo.targetsize-{t}_altform-lightunplated.png"));
}
// Store listing logo and a preview sheet for review.
Save(Render(300, 300, 300), Path.Combine(logoDir, "store-logo-300.png"));
Save(Preview(), Path.Combine(logoDir, "preview.png"));
Console.WriteLine($"Assets: {Directory.GetFiles(assets, "*.png").Length} PNG files in {assets}");

SKBitmap Render(int w, int h, double iconPx)
{
    var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.Transparent);
    Draw(c, w / 2f, h / 2f, (float)iconPx);
    return bmp;
}

void Draw(SKCanvas c, float cx, float cy, float size)
{
    var src = size < 48 ? small : big;
    var pic = src.Picture!;
    var scale = size / pic.CullRect.Width;
    c.Save();
    c.Translate(cx - size / 2, cy - size / 2);
    c.Scale(scale);
    c.DrawPicture(pic);
    c.Restore();
}

SKBitmap Preview()
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    var w = sizes.Sum() + 40 * (sizes.Length + 1);
    var h = 2 * (256 + 60);
    var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
    using var c = new SKCanvas(bmp);
    var bgs = new[] { new SKColor(0xF3, 0xF3, 0xF3), new SKColor(0x20, 0x20, 0x20) };
    for (var row = 0; row < 2; row++)
    {
        using var p = new SKPaint { Color = bgs[row] };
        c.DrawRect(0, row * (h / 2), w, h / 2, p);
        var x = 40f;
        foreach (var s in sizes)
        {
            using var icon = Render(s, s, s);
            c.DrawBitmap(icon, x, row * (h / 2) + (h / 2 - s) / 2f);
            x += s + 40;
        }
    }
    return bmp;
}

static SKSvg Load(string path)
{
    var svg = new SKSvg();
    svg.Load(path);
    return svg;
}

static void Save(SKBitmap bmp, string path)
{
    using (bmp)
    using (var img = SKImage.FromBitmap(bmp))
    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
    using (var fs = File.Create(path))
    {
        data.SaveTo(fs);
    }
}
