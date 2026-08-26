using Acs.Infrastructure.Auth;
using Xunit;

namespace Acs.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_And_Verify_Roundtrip()
    {
        var hash = PasswordHasher.Hash("Tajn0eHeslo!123");
        Assert.True(PasswordHasher.Verify("Tajn0eHeslo!123", hash));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hash = PasswordHasher.Hash("spravne-heslo");
        Assert.False(PasswordHasher.Verify("spatne-heslo", hash));
    }

    [Fact]
    public void Hash_IsSalted_DifferentEveryTime()
    {
        Assert.NotEqual(PasswordHasher.Hash("heslo"), PasswordHasher.Hash("heslo"));
    }

    [Fact]
    public void Verify_MalformedHash_Fails()
    {
        Assert.False(PasswordHasher.Verify("heslo", "neplatny-format"));
    }
}
