using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace BiblioText.Cloud.Auth;

/// <summary>
/// Issues and verifies passwordless "magic link" tokens. Tokens are stateless:
/// the member's email is encrypted with a time-limited Data Protection payload,
/// so no server-side token store is needed and expiry is enforced cryptographically.
/// A tampered or expired token fails to unprotect and yields null.
/// </summary>
public sealed class MagicLinkService
{
    private const string Purpose = "BiblioText.Cloud.MagicLink.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly AuthOptions _options;
    private readonly ILogger<MagicLinkService> _logger;

    public MagicLinkService(
        IDataProtectionProvider provider,
        AuthOptions options,
        ILogger<MagicLinkService> logger)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _options = options;
        _logger = logger;
    }

    /// <summary>Creates a signed, expiring token that encodes the given email.</summary>
    public string IssueToken(string email) =>
        _protector.Protect(email.Trim(), _options.MagicLinkLifetime);

    /// <summary>
    /// Validates a token and returns the email it encodes, or null if the token is
    /// expired, tampered, or belongs to an email no longer on the allowlist.
    /// </summary>
    public string? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string email;
        try
        {
            email = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            // Expired, tampered, or issued under a rotated key.
            return null;
        }

        // Re-check the allowlist at redemption time so removing an email revokes
        // any links already in flight.
        if (!_options.IsAllowed(email))
        {
            _logger.LogWarning("Magic link redeemed for non-allowlisted email {Email}", email);
            return null;
        }

        return email;
    }
}
