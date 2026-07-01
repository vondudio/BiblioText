using BiblioText.Cloud.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiblioText.Cloud.Pages.Account;

public class LoginModel(
    MagicLinkService magicLinks,
    AuthOptions options,
    ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string? Email { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Message { get; set; }
    public bool IsError { get; set; }

    /// <summary>Populated only in dev mode so the operator can click through without email.</summary>
    public string? DevMagicLink { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            Message = "Enter your email address.";
            IsError = true;
            return Page();
        }

        if (!options.IsAllowed(Email))
        {
            Message = "That email isn't on the member list. Ask the librarian to add you.";
            IsError = true;
            return Page();
        }

        var token = magicLinks.IssueToken(Email);
        var link = Url.Page(
            "/Account/Verify",
            pageHandler: null,
            values: new { token, returnUrl = ReturnUrl },
            protocol: Request.Scheme)!;

        logger.LogInformation("Magic sign-in link issued for {Email}: {Link}", Email, link);

        if (options.DevMode)
        {
            DevMagicLink = link;
            Message = "Dev mode — email delivery isn't wired yet. Use the link below to sign in.";
        }
        else
        {
            Message = "Check your email for a sign-in link. It expires shortly.";
        }

        return Page();
    }
}
