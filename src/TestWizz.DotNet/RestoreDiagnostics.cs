using TestWizz.Core.Running;

namespace TestWizz.DotNet;

/// <summary>
/// Recognizes common causes of a failed restore phase from its log: the SDK image could not be pulled, the feed's TLS
/// certificate is not trusted inside the container (typical behind a TLS-inspecting corporate proxy), the feed
/// rejected the credentials, the feed could not be reached, or a package does not exist. The returned message is
/// fixed text with a remedy and never quotes the log, so feed URLs with tokens or other secrets are not repeated.
/// </summary>
public static class RestoreDiagnostics
{
    public const string CorporateGuide = "docs/corporate-environments.md";

    private static readonly string[] ImageMarkers =
    [
        "Unable to find image", "pull access denied", "manifest unknown", "repository does not exist",
        "Error response from daemon: manifest", "failed to resolve reference", "No such image",
    ];

    private static readonly string[] TlsMarkers =
    [
        "The SSL connection could not be established", "RemoteCertificateChainErrors", "UntrustedRoot",
        "PartialChain", "certificate verify failed", "unable to get local issuer certificate",
        "self signed certificate in certificate chain", "self-signed certificate in certificate chain",
        "The remote certificate is invalid", "RemoteCertificateNameMismatch",
    ];

    private static readonly string[] AuthMarkers =
    [
        "401 (Unauthorized)", "403 (Forbidden)", "status code does not indicate success: 401",
        "status code does not indicate success: 403",
    ];

    private static readonly string[] UnreachableMarkers =
    [
        "Name or service not known", "No such host is known", "nodename nor servname provided",
        "Temporary failure in name resolution", "Resource temporarily unavailable", "Connection refused",
        "Network is unreachable", "No route to host", "A connection attempt failed",
    ];

    /// <summary>Null when the log shows none of the recognized causes.</summary>
    public static RunDiagnosis? Diagnose(string? log)
    {
        if (string.IsNullOrEmpty(log))
        {
            return null;
        }

        if (Has(log, ImageMarkers))
        {
            return new RunDiagnosis("restore.image_unavailable",
                "The SDK image could not be pulled or found. Check the image name (run.image in the configuration or --image), " +
                "that you are logged in to its registry, or build the image locally first. " +
                $"See {CorporateGuide} for building an SDK image.");
        }

        if (Has(log, TlsMarkers))
        {
            return new RunDiagnosis("restore.tls_untrusted",
                "NuGet could not verify the package feed's TLS certificate inside the SDK container. Containers do not use " +
                "the host's or Docker Desktop's certificate store, so a CA trusted on the host (for example a corporate " +
                "TLS-inspecting proxy) is not trusted in the container. Use an SDK image that trusts your organization's " +
                $"approved CA via run.image or --image; see {CorporateGuide}. TestWizz never disables TLS validation.");
        }

        if (Has(log, AuthMarkers))
        {
            return new RunDiagnosis("restore.feed_auth",
                "A package feed rejected the request (401/403). Restore runs in a fresh container without the host's " +
                "credential providers; configure feed access in the repository's nuget.config through environment-based " +
                "credentials or an SDK image prepared for your feed. Credentials are never copied into proposals or logs by TestWizz.");
        }

        if (Has(log, UnreachableMarkers))
        {
            return new RunDiagnosis("restore.feed_unreachable",
                "A package feed could not be reached from the restore container (DNS, proxy or firewall). Check that the " +
                "container runtime has network access to the feeds in nuget.config, and any proxy settings of the runtime.");
        }

        if (log.Contains("NU1101", StringComparison.Ordinal) || log.Contains("NU1102", StringComparison.Ordinal))
        {
            return new RunDiagnosis("restore.package_not_found",
                "A package or package version was not found on the configured feeds (NU1101/NU1102). Check nuget.config " +
                "and that private feeds are reachable from the restore container.");
        }

        return null;
    }

    private static bool Has(string log, IEnumerable<string> markers) =>
        markers.Any(m => log.Contains(m, StringComparison.OrdinalIgnoreCase));
}
