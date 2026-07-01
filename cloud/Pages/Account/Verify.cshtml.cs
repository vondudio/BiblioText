using System.Security.Claims;
using BiblioText.Cloud.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiblioText.Cloud.Pages.Account;

public class VerifyModel(MagicLinkService magicLinks) : PageModel
{
    public bool IsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? token, string? returnUrl)
    {
        var email = magicLinks.ValidateToken(token);
        if (email is null)
        {
            IsError = true;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage("/Index");
    }
}
