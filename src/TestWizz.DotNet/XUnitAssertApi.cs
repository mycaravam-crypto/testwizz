namespace TestWizz.DotNet;

/// <summary>
/// The public static methods of <c>Xunit.Assert</c>, taken from the real assemblies: <c>xunit.assert</c> 2.9.3 (the
/// last v2) and <c>xunit.v3.assert</c> 4.0.3. Packages are not resolved offline, so generated <c>Assert.X(...)</c> calls
/// are checked by name against the detected major version; <c>run</c> still verifies them by compiling (#93).
/// </summary>
public static class XUnitAssertApi
{
    private static readonly string[] Common =
    [
        "All", "AllAsync", "Collection", "CollectionAsync", "Contains", "Distinct", "DoesNotContain", "DoesNotMatch",
        "Empty", "EndsWith", "Equal", "Equivalent", "Fail", "False", "InRange", "IsAssignableFrom", "IsNotAssignableFrom",
        "IsNotType", "IsType", "Matches", "Multiple", "NotEmpty", "NotEqual", "NotInRange", "NotNull", "NotSame",
        "NotStrictEqual", "Null", "ProperSubset", "ProperSuperset", "PropertyChanged", "PropertyChangedAsync", "Raises",
        "RaisesAny", "RaisesAnyAsync", "RaisesAsync", "Same", "Single", "StartsWith", "StrictEqual", "Subset", "Superset",
        "Throws", "ThrowsAny", "ThrowsAnyAsync", "ThrowsAsync", "True",
    ];

    private static readonly string[] V3Only =
    [
        "EquivalentWithExclusions", "MultipleAsync", "NotRaisedAny", "NotRaisedAnyAsync", "OverrideMaxEnumerableLength",
        "OverrideMaxObjectDepth", "OverrideMaxObjectMemberCount", "OverrideMaxStringLength", "Skip", "SkipUnless", "SkipWhen",
    ];

    public static readonly IReadOnlySet<string> V2 = new HashSet<string>(Common, StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> V3 = new HashSet<string>([.. Common, .. V3Only], StringComparer.Ordinal);

    /// <summary>Common assertions from other frameworks that xUnit does not have, with what to write instead.</summary>
    private static readonly Dictionary<string, string> Alternatives = new(StringComparer.Ordinal)
    {
        ["DoesNotThrow"] = "call the code directly (an exception fails the test), or use Record.Exception(...) with Assert.Null",
        ["DoesNotThrowAsync"] = "await the call directly (an exception fails the test), or use Record.ExceptionAsync(...) with Assert.Null",
        ["Equals"] = "Assert.Equals is object.Equals and always throws; use Assert.Equal",
        ["ReferenceEquals"] = "Assert.ReferenceEquals always throws; use Assert.Same",
        ["AreEqual"] = "use Assert.Equal",
        ["AreNotEqual"] = "use Assert.NotEqual",
        ["IsTrue"] = "use Assert.True",
        ["IsFalse"] = "use Assert.False",
        ["IsNull"] = "use Assert.Null",
        ["IsNotNull"] = "use Assert.NotNull",
        ["That"] = "use a specific xUnit assertion such as Assert.Equal or Assert.True",
        ["IsInstanceOf"] = "use Assert.IsType or Assert.IsAssignableFrom",
        ["IsEmpty"] = "use Assert.Empty",
        ["IsNotEmpty"] = "use Assert.NotEmpty",
        ["Greater"] = "use Assert.True(a > b) or Assert.InRange",
        ["Less"] = "use Assert.True(a < b) or Assert.InRange",
        ["Pass"] = "remove it; a test passes when no assertion fails",
        ["Inconclusive"] = "remove it, or use Assert.Skip in xUnit v3",
    };

    /// <summary>Major version from a detected package (<c>xunit 2.9.2</c>, <c>xunit.v3 1.0.0</c>); null when unknown.</summary>
    public static int? MajorVersion(string? xunitPackage) => xunitPackage switch
    {
        null => null,
        _ when xunitPackage.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase) => 3,
        _ when xunitPackage.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) => 2,
        _ => null,
    };

    /// <summary>Null when <c>Assert.<paramref name="name"/></c> exists in that xUnit version, otherwise why it does not.</summary>
    public static string? Problem(string name, int major)
    {
        var api = major >= 3 ? V3 : V2;
        if (api.Contains(name) && name is not ("Equals" or "ReferenceEquals"))
        {
            return null;
        }

        var version = major >= 3 ? "xUnit v3" : "xUnit v2";
        if (major < 3 && V3.Contains(name))
        {
            return $"Assert.{name} exists only in xUnit v3, but the test project uses {version}.";
        }

        return Alternatives.TryGetValue(name, out var instead)
            ? $"Assert.{name} is not part of the {version} Assert API; {instead}."
            : $"Assert.{name} is not part of the {version} Assert API.";
    }
}
