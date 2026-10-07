using System.Numerics;
using System.Text.RegularExpressions;

namespace Quartermaster.SelfUpdate;

internal sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly BigInteger[] numbers;
    private readonly string[] prerelease;
    private ReleaseVersion(BigInteger[] numbers, string[] prerelease) { this.numbers = numbers; this.prerelease = prerelease; }
    public static ReleaseVersion Parse(string value)
    {
        if (value.Length > 256) throw new FormatException("Invalid application version.");
        var match = Pattern().Match(value.StartsWith('v') ? value[1..] : value);
        if (!match.Success) throw new FormatException($"Invalid application version: {value}");
        var identifiers = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (identifiers.Any(identifier => Numeric(identifier) && identifier.Length > 1 && identifier[0] == '0'))
            throw new FormatException("Numeric prerelease identifiers cannot have leading zeroes.");
        return new([BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value)], identifiers);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var index = 0; index < numbers.Length; index++)
        { var result = numbers[index].CompareTo(other.numbers[index]); if (result != 0) return result; }
        if (prerelease.Length == 0 || other.prerelease.Length == 0) return (prerelease.Length == 0).CompareTo(other.prerelease.Length == 0);
        for (var index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
        {
            var left = prerelease[index]; var right = other.prerelease[index];
            var result = Numeric(left) && Numeric(right) ? BigInteger.Parse(left).CompareTo(BigInteger.Parse(right)) :
                Numeric(left) != Numeric(right) ? (Numeric(left) ? -1 : 1) : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
    private static bool Numeric(string value) => value.All(character => character is >= '0' and <= '9');
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex Pattern();
}
