namespace PokeQuad.Models;

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }
    public string LastView { get; set; } = "Quad";
    public string PreferredLayout { get; set; } = "Quad";
    public int SelectedAccount { get; set; } = 1;
    public int DuoAccount1 { get; set; } = 1;
    public int DuoAccount2 { get; set; } = 2;
    public string DuoOrientation { get; set; } = nameof(PokeQuad.Models.DuoOrientation.Horizontal);
    public string ThreeSessionLayout { get; set; } = nameof(PokeQuad.Models.ThreeSessionLayout.ThreeLargeLeft);
    public int ThreeLargeAccount { get; set; } = 1;
    public int[] DetachedAccounts { get; set; } = [];
    public DetachedWindowSettings[] DetachedWindows { get; set; } = [];
    public bool ToolbarVisible { get; set; } = true;
    public string[] AccountNames { get; set; } = ["Account 1", "Account 2", "Account 3", "Account 4"];
    public double[] AccountZoomLevels { get; set; } = [1.0, 1.0, 1.0, 1.0];
    public int[] AccountAccentIndexes { get; set; } = [0, 1, 2, 3];
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string? LastNotifiedVersion { get; set; }
}

public sealed class DetachedWindowSettings
{
    public int AccountNumber { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 700;
    public bool IsMaximized { get; set; }
}
