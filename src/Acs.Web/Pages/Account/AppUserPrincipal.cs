using System.Security.Claims;
using Acs.Domain.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Acs.Web.Pages.Account;

/// <summary>Sestavení cookie identity uživatele ACS — společné pro přihlášení heslem i účtem Windows.</summary>
public static class AppUserPrincipal
{
    /// <summary>Claim s tím, jak se uživatel přihlásil: <c>password</c> nebo <c>windows</c>.</summary>
    public const string AuthMethodClaim = "auth_method";

    public static ClaimsPrincipal Create(AppUser user, string authMethod)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(AuthMethodClaim, authMethod),
        };
        if (user.DisplayName is not null)
            claims.Add(new Claim("display_name", user.DisplayName));
        if (user.IsLocal)
            claims.Add(new Claim("is_local", "1"));   // heslo lze měnit jen u lokálních účtů, AD spravuje doména
        if (user.MustChangePassword)
            claims.Add(new Claim("must_change_password", "1"));

        foreach (var role in Enum.GetValues<AppRole>())
        {
            if (role != AppRole.None && user.Roles.HasFlag(role))
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
