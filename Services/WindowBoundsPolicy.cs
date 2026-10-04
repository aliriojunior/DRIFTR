using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed record DisplayBounds(double Left, double Top, double Width, double Height);

public static class WindowBoundsPolicy
{
    private const double MinimumVisibleArea = 80;

    public static DetachedWindowSettings Normalize(
        DetachedWindowSettings? saved,
        int accountNumber,
        IReadOnlyList<DisplayBounds> displays)
    {
        double width = saved is { Width: >= 480 } && double.IsFinite(saved.Width) ? saved.Width : 1000;
        double height = saved is { Height: >= 360 } && double.IsFinite(saved.Height) ? saved.Height : 700;
        DisplayBounds display = displays.FirstOrDefault() ?? new DisplayBounds(0, 0, 1920, 1080);
        double left = saved is not null && double.IsFinite(saved.Left) ? saved.Left : display.Left + 60;
        double top = saved is not null && double.IsFinite(saved.Top) ? saved.Top : display.Top + 60;

        bool visible = displays.Any(screen =>
            left < screen.Left + screen.Width - MinimumVisibleArea &&
            top < screen.Top + screen.Height - MinimumVisibleArea &&
            left + MinimumVisibleArea > screen.Left &&
            top + MinimumVisibleArea > screen.Top);
        if (!visible)
        {
            left = display.Left + 60;
            top = display.Top + 60;
        }

        return new DetachedWindowSettings
        {
            AccountNumber = accountNumber,
            Left = left,
            Top = top,
            Width = Math.Min(width, Math.Max(480, display.Width)),
            Height = Math.Min(height, Math.Max(360, display.Height)),
            IsMaximized = saved?.IsMaximized == true
        };
    }
}
