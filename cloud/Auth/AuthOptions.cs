namespace BiblioText.Cloud.Auth;

/// <summary>
/// Bound from the "Auth" configuration section. Controls the members-only gate:
/// a short <see cref="AllowedEmails"/> allowlist (this is a small, invite-only
/// family/friends catalog — no self-service signup), magic-link lifetime, and a
/// <see cref="DevMode"/> switch that surfaces the login link on-screen instead of
/// emailing it (real email delivery lands in Phase 5).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Emails permitted to sign in. Case-insensitive.</summary>
    public List<string> AllowedEmails { get; set; } = [];

    /// <summary>How long an issued magic link stays valid.</summary>
    public TimeSpan MagicLinkLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a signed-in session cookie lasts.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// When true, the login flow shows the magic link on the page (and logs it)
    /// instead of sending an email. Intended for local dev until Phase 5 wires a
    /// real email sender.
    /// </summary>
    public bool DevMode { get; set; }

    public bool IsAllowed(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && AllowedEmails.Any(e => string.Equals(e.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}
