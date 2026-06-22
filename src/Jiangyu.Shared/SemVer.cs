using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jiangyu.Shared;

/// <summary>
/// A semantic version (major.minor.patch with an optional pre-release tag). Build
/// metadata after <c>+</c> is parsed away and ignored, matching the SemVer rule that
/// it does not participate in ordering. A leading <c>v</c> is tolerated, and a
/// hand-written constraint may omit the minor or patch component (missing parts read
/// as zero), so <c>1</c> and <c>1.0.0</c> compare equal.
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The pre-release identifiers after <c>-</c>, or null for a release build.
    /// A pre-release version is ordered below the same major.minor.patch release.</summary>
    public string? PreRelease { get; }

    public SemVer(int major, int minor, int patch, string? preRelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrEmpty(preRelease) ? null : preRelease;
    }

    public static SemVer Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a valid version.");

    /// <summary>Parse a version string. Returns false for null, empty, or a string whose
    /// leading component is not a number, so callers can degrade to presence-only checks
    /// rather than throwing on an unparseable constraint.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text!.Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span.Substring(1);

        // Build metadata never affects ordering; drop it before anything else.
        var plus = span.IndexOf('+');
        if (plus >= 0)
            span = span.Substring(0, plus);

        string? preRelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = span.Substring(dash + 1);
            span = span.Substring(0, dash);
        }

        var parts = span.Split('.');
        if (parts.Length is 0 or > 3)
            return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return false;
            numbers[i] = value;
        }

        version = new SemVer(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release outranks any pre-release of the same core version.
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    // Dot-separated identifiers: numeric ones compare numerically and rank below
    // alphanumeric ones; a larger set of identifiers outranks a prefix-equal smaller set.
    private static int ComparePreRelease(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');
        var count = Math.Min(l.Length, r.Length);
        for (var i = 0; i < count; i++)
        {
            var lNumeric = int.TryParse(l[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ln);
            var rNumeric = int.TryParse(r[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rn);

            int c;
            if (lNumeric && rNumeric) c = ln.CompareTo(rn);
            else if (lNumeric) c = -1;
            else if (rNumeric) c = 1;
            else c = string.CompareOrdinal(l[i], r[i]);

            if (c != 0) return c;
        }

        return l.Length.CompareTo(r.Length);
    }

    /// <summary>Evaluate a manifest dependency constraint such as
    /// <c>actual &gt;= required</c>. An unrecognised operator yields false.</summary>
    public static bool Satisfies(SemVer actual, string @operator, SemVer required)
    {
        var c = actual.CompareTo(required);
        return @operator switch
        {
            ">=" => c >= 0,
            "<=" => c <= 0,
            ">" => c > 0,
            "<" => c < 0,
            "!=" => c != 0,
            "==" or "=" => c == 0,
            _ => false,
        };
    }

    public bool Equals(SemVer other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return PreRelease is null ? core : $"{core}-{PreRelease}";
    }

    public static bool operator ==(SemVer left, SemVer right) => left.Equals(right);
    public static bool operator !=(SemVer left, SemVer right) => !left.Equals(right);
    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
}
