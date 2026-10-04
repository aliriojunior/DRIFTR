using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed record PanelPlacement(bool Visible, int Row, int Column, int RowSpan, int ColumnSpan);

public static class WorkspaceLayoutPolicy
{
    public static ThreeSessionLayout ParseThreeSessionLayout(string? value) =>
        Enum.TryParse(value, true, out ThreeSessionLayout parsed) && Enum.IsDefined(parsed)
            ? parsed
            : ThreeSessionLayout.ThreeLargeLeft;

    public static IReadOnlyList<PanelPlacement> Solo(int selectedAccount) =>
        Enumerable.Range(1, 4)
            .Select(account => new PanelPlacement(account == selectedAccount, 0, 0, 2, 2))
            .ToArray();

    public static IReadOnlyList<PanelPlacement> Duo(
        int firstAccount,
        int secondAccount,
        DuoOrientation orientation)
    {
        return Enumerable.Range(1, 4).Select(account =>
        {
            if (account == firstAccount)
            {
                return orientation == DuoOrientation.Horizontal
                    ? new PanelPlacement(true, 0, 0, 2, 1)
                    : new PanelPlacement(true, 0, 0, 1, 2);
            }

            if (account == secondAccount)
            {
                return orientation == DuoOrientation.Horizontal
                    ? new PanelPlacement(true, 0, 1, 2, 1)
                    : new PanelPlacement(true, 1, 0, 1, 2);
            }

            return new PanelPlacement(false, 0, 0, 1, 1);
        }).ToArray();
    }

    public static IReadOnlyList<PanelPlacement> Quad() =>
        Enumerable.Range(0, 4)
            .Select(index => new PanelPlacement(true, index / 2, index % 2, 1, 1))
            .ToArray();

    public static IReadOnlyList<PanelPlacement> Three(
        IReadOnlyList<int> dockedAccounts,
        int largeAccount,
        ThreeSessionLayout layout)
    {
        int[] accounts = dockedAccounts.Distinct().Where(account => account is >= 1 and <= 4).Take(3).ToArray();
        if (accounts.Length != 3) throw new ArgumentException("Exactly three distinct docked accounts are required.", nameof(dockedAccounts));
        if (!accounts.Contains(largeAccount)) largeAccount = accounts[0];
        if (!Enum.IsDefined(layout)) layout = ThreeSessionLayout.ThreeLargeLeft;

        int[] others = accounts.Where(account => account != largeAccount).ToArray();
        var placements = Enumerable.Range(0, 4)
            .Select(_ => new PanelPlacement(false, 0, 0, 1, 1))
            .ToArray();

        switch (layout)
        {
            case ThreeSessionLayout.ThreeLargeLeft:
                placements[largeAccount - 1] = new PanelPlacement(true, 0, 0, 2, 1);
                placements[others[0] - 1] = new PanelPlacement(true, 0, 1, 1, 1);
                placements[others[1] - 1] = new PanelPlacement(true, 1, 1, 1, 1);
                break;
            case ThreeSessionLayout.ThreeLargeRight:
                placements[others[0] - 1] = new PanelPlacement(true, 0, 0, 1, 1);
                placements[others[1] - 1] = new PanelPlacement(true, 1, 0, 1, 1);
                placements[largeAccount - 1] = new PanelPlacement(true, 0, 1, 2, 1);
                break;
            case ThreeSessionLayout.ThreeLargeTop:
                placements[largeAccount - 1] = new PanelPlacement(true, 0, 0, 1, 2);
                placements[others[0] - 1] = new PanelPlacement(true, 1, 0, 1, 1);
                placements[others[1] - 1] = new PanelPlacement(true, 1, 1, 1, 1);
                break;
            case ThreeSessionLayout.ThreeLargeBottom:
                placements[others[0] - 1] = new PanelPlacement(true, 0, 0, 1, 1);
                placements[others[1] - 1] = new PanelPlacement(true, 0, 1, 1, 1);
                placements[largeAccount - 1] = new PanelPlacement(true, 1, 0, 1, 2);
                break;
            case ThreeSessionLayout.ThreeColumns:
                for (int index = 0; index < accounts.Length; index++)
                    placements[accounts[index] - 1] = new PanelPlacement(true, 0, index, 1, 1);
                break;
            case ThreeSessionLayout.ThreeRows:
                for (int index = 0; index < accounts.Length; index++)
                    placements[accounts[index] - 1] = new PanelPlacement(true, index, 0, 1, 1);
                break;
        }

        return placements;
    }

    public static IReadOnlyList<PanelPlacement> Docked(
        IReadOnlyList<int> dockedAccounts,
        DuoOrientation duoOrientation,
        ThreeSessionLayout threeLayout = ThreeSessionLayout.ThreeLargeLeft,
        int threeLargeAccount = 0)
    {
        var placements = Enumerable.Range(0, 4)
            .Select(_ => new PanelPlacement(false, 0, 0, 1, 1))
            .ToArray();
        switch (dockedAccounts.Count)
        {
            case 1:
                placements[dockedAccounts[0] - 1] = new PanelPlacement(true, 0, 0, 2, 2);
                break;
            case 2:
                IReadOnlyList<PanelPlacement> duo = Duo(dockedAccounts[0], dockedAccounts[1], duoOrientation);
                for (int index = 0; index < placements.Length; index++) placements[index] = duo[index];
                break;
            case 3:
                return Three(dockedAccounts, threeLargeAccount, threeLayout);
            case >= 4:
                return Quad();
        }
        return placements;
    }
}
