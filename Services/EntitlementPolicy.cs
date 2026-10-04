using PokeQuad.Models;

namespace PokeQuad.Services;

public static class EntitlementPolicy
{
    public static int NormalizeMaxSessions(int serverValue) => serverValue >= 4 ? 4 : 1;

    public static bool CanUseAccount(int accountNumber, int maxSessions) =>
        accountNumber >= 1 && accountNumber <= NormalizeMaxSessions(maxSessions);

    public static bool CanUseLayout(LayoutMode layout, int maxSessions) => layout switch
    {
        LayoutMode.Solo => true,
        LayoutMode.Duo => NormalizeMaxSessions(maxSessions) >= 2,
        LayoutMode.Three => NormalizeMaxSessions(maxSessions) >= 3,
        LayoutMode.Quad => NormalizeMaxSessions(maxSessions) >= 4,
        _ => false
    };

    public static LayoutMode EnforceLayout(LayoutMode preferred, int maxSessions) =>
        CanUseLayout(preferred, maxSessions) ? preferred : LayoutMode.Solo;
}
