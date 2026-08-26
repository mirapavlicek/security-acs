using Acs.Domain.Entities;
using Acs.Infrastructure.Auth;
using Xunit;

namespace Acs.Tests;

public class GroupRoleMappingTests
{
    [Fact]
    public void ResolveRoles_MatchesCnFromDn()
    {
        var roles = UserAuthenticationService.ResolveRolesFromGroups(
            ["CN=ACS-Spravci,OU=Groups,DC=fnmh,DC=local"],
            "ACS-Spravci=Admin");
        Assert.True(roles.HasFlag(AppRole.Admin));
        Assert.True(roles.HasFlag(AppRole.Employee)); // Employee vždy
    }

    [Fact]
    public void ResolveRoles_MultipleRolesPerGroup_AndMultipleLines()
    {
        var roles = UserAuthenticationService.ResolveRolesFromGroups(
            ["CN=Karty,OU=G,DC=x", "CN=Schvalovatele,OU=G,DC=x"],
            "Karty=CardAdmin,CatalogManager\nSchvalovatele=Approver\nJina=Admin");
        Assert.True(roles.HasFlag(AppRole.CardAdmin));
        Assert.True(roles.HasFlag(AppRole.CatalogManager));
        Assert.True(roles.HasFlag(AppRole.Approver));
        Assert.False(roles.HasFlag(AppRole.Admin));
    }

    [Fact]
    public void ResolveRoles_NoMatch_ReturnsEmployeeOnly()
    {
        var roles = UserAuthenticationService.ResolveRolesFromGroups(
            ["CN=Ucetni,OU=G,DC=x"],
            "ACS-Spravci=Admin");
        Assert.Equal(AppRole.Employee, roles);
    }

    [Fact]
    public void ResolveRoles_IsCaseInsensitive()
    {
        var roles = UserAuthenticationService.ResolveRolesFromGroups(
            ["cn=acs-spravci,ou=g,dc=x"],
            "ACS-SPRAVCI=admin");
        Assert.True(roles.HasFlag(AppRole.Admin));
    }
}
