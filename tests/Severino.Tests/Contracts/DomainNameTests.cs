using Severino.Contracts;

namespace Severino.Tests.Contracts;

public sealed class DomainNameTests
{
    [Theory]
    [InlineData("callfred.sev", "callfred.sev")]
    [InlineData("  CallFred.SEV  ", "callfred.sev")]
    [InlineData("callfred.sev.", "callfred.sev")]
    [InlineData("api.callfred.sev", "api.callfred.sev")]
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
    [InlineData("callfred")]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    [InlineData("app.123")]
    [InlineData("-app.sev")]
    [InlineData("app-.sev")]
    [InlineData("a..sev")]
    [InlineData("under_score.sev")]
    [InlineData("call fred.sev")]
    [InlineData("callfred.sev\r\n127.0.0.1 banco.com.br")]
    [InlineData("callfred.sev\n")]
    [InlineData("callfred.sev#x")]
    [InlineData("callfred.sev\t")]
    [InlineData("call\0fred.sev")]
    [InlineData("callfred.sev/path")]
    [InlineData("callfred.sev:3000")]
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
}
