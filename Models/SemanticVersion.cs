using System.Globalization;
using System.Text.RegularExpressions;

namespace PokeQuad.Models;

public sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        Match match = VersionPattern().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;
        string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        for (int index = 0; index < Math.Max(Prerelease.Count, other.Prerelease.Count); index++)
        {
            if (index >= Prerelease.Count) return -1;
            if (index >= other.Prerelease.Count) return 1;
            string left = Prerelease[index];
            string right = other.Prerelease[index];
            bool leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);
            int comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1 : rightNumeric ? 1 : StringComparer.Ordinal.Compare(left, right);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(IsPrerelease ? $"-{string.Join('.', Prerelease)}" : string.Empty)}";

    [GeneratedRegex(@"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
