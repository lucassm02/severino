using Severino.Contracts;

namespace Severino.Tests.Contracts;

public sealed class DomainNameTests
{
    [Theory]
    [InlineData("meuapp.sev", "meuapp.sev")]
    [InlineData("  MeuApp.SEV  ", "meuapp.sev")]
    [InlineData("meuapp.sev.", "meuapp.sev")]
    [InlineData("api.meuapp.sev", "api.meuapp.sev")]
    [InlineData("café.sev", "xn--caf-dma.sev")]
    [InlineData("my-app.test", "my-app.test")]
    [InlineData("app.localhost", "app.localhost")]
    [InlineData("api.empresa.com", "api.empresa.com")]
    [InlineData("1password.sev", "1password.sev")]
    public void Accepts_and_normalizes(string input, string expected)
    {
        Assert.True(DomainName.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("meuapp")]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    [InlineData("app.123")]
    [InlineData("-app.sev")]
    [InlineData("app-.sev")]
    [InlineData("a..sev")]
    [InlineData("under_score.sev")]
    [InlineData("call fred.sev")]
    [InlineData("meuapp.sev\r\n127.0.0.1 banco.com.br")]
    [InlineData("meuapp.sev\n")]
    [InlineData("meuapp.sev#x")]
    [InlineData("meuapp.sev\t")]
    [InlineData("call\0fred.sev")]
    [InlineData("meuapp.sev/path")]
    [InlineData("meuapp.sev:3000")]
    public void Rejects(string? input)
    {
        Assert.False(DomainName.TryNormalize(input, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Enforces_label_length()
    {
        Assert.True(DomainName.IsValid(new string('a', 63) + ".sev"));
        Assert.False(DomainName.IsValid(new string('a', 64) + ".sev"));
    }

    [Fact]
    public void Enforces_total_length()
    {
        // 4 labels of 63 + ".sev" = 4*63 + 3 dots + 4 = 259; trim the last label to land on the limit.
        var atLimit = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 57)) + ".sev";
        Assert.Equal(DomainName.MaxLength, atLimit.Length);
        Assert.True(DomainName.IsValid(atLimit));
        Assert.False(DomainName.IsValid("x" + atLimit));
    }

    [Fact]
    public void One_label_names_only_when_asked()
    {
        Assert.False(DomainName.TryNormalize("Redis", out _, out var error));
        Assert.Contains("duas partes", error);

        Assert.True(DomainName.TryNormalize("Redis", out var name, out _, allowSingleLabel: true));
        Assert.Equal("redis", name);
        // Still not a way around the other rules.
        Assert.False(DomainName.TryNormalize("localhost", out _, out _, allowSingleLabel: true));
        Assert.False(DomainName.TryNormalize("8080", out _, out _, allowSingleLabel: true));
        Assert.False(DomainName.TryNormalize("-redis", out _, out _, allowSingleLabel: true));
    }
}
