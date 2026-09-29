using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The definitions' <c>certificates</c> list (#986): SHA-1 fingerprints of the
/// one certificate a site is known to serve when no authority signed it (or
/// it has expired). Everything else is checked as usual.
/// </summary>
public static class TrackerCertificates
{
    public static bool Accepts(IReadOnlyList<string> fingerprints, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // A name that does not match is still refused: the fingerprint says
        // which certificate to trust, not that any host may present it.
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }

        var actual = certificate.GetCertHashString();
        return fingerprints.Any(fingerprint => string.Equals(fingerprint.Replace(":", string.Empty).Trim(), actual, StringComparison.OrdinalIgnoreCase));
    }
}
