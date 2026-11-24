using System.Text.RegularExpressions;

namespace TestWizz.Core.Running;

/// <summary>
/// Syntax check for container image references (<c>[registry[:port]/]name[:tag][@digest]</c>) from configuration or
/// the command line. The reference is passed to <c>docker run</c> as a separate argument; rejecting leading dashes,
/// whitespace and control characters keeps it from ever being read as an option.
/// </summary>
public static partial class ContainerImage
{
    public const int MaxLength = 255;

    /// <summary>Null when <paramref name="reference"/> is a plausible image reference, otherwise why it is not.</summary>
    public static string? Problem(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return "Must not be empty.";
        }

        if (reference.Length > MaxLength)
        {
            return $"Must be at most {MaxLength} characters.";
        }

        if (!Reference().IsMatch(reference))
        {
            return "Must be an image reference such as 'mcr.microsoft.com/dotnet/sdk:10.0' or 'registry.example.com/testwizz-sdk:10.0-corporate'.";
        }

        return null;
    }

    // registry host[:port]/ path components (lowercase), optional :tag, optional @algorithm:hex digest.
    [GeneratedRegex(
        @"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)*(?::[0-9]{1,5})?/)?" +
        @"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*" +
        @"(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?" +
        @"(?:@[a-z0-9]+(?:[.+_-][a-z0-9]+)*:[a-fA-F0-9]{32,})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
