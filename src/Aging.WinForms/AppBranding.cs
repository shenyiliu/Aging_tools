namespace Aging.WinForms;

internal static class AppBranding
{
    public static readonly Icon Icon = LoadIcon();
    public static readonly Image Wordmark = LoadWordmark();

    private static Stream Resource(string name) => typeof(AppBranding).Assembly.GetManifestResourceStream("Branding." + name)
        ?? throw new InvalidOperationException("品牌资源缺失：" + name);

    private static Icon LoadIcon()
    {
        using var stream = Resource("gmktec.ico");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }

    private static Image LoadWordmark()
    {
        using var stream = Resource("gmktec-wordmark.png");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public static PictureBox Logo(Control parent, Rectangle bounds)
    {
        var picture = new PictureBox { Name = "BrandLogo", Image = Wordmark, SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = bounds, BackColor = Color.White, AccessibleName = "GMKtec 官方 Logo", TabStop = false };
        parent.Controls.Add(picture);
        return picture;
    }
}
