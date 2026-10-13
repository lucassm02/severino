using Severino.Core.Network;

namespace Severino.Tests.Network;

public sealed class InputCleanupTests
{
    [Theory]
    [InlineData("192.168.100.5", "192.168.100.5", null, null)]
    [InlineData("192.168.100.5:", "192.168.100.5", null, null)]
    [InlineData(" 192.168.100.5:8000 ", "192.168.100.5", null, 8000)]
    [InlineData("http://192.168.100.5", "192.168.100.5", "http", null)]
    [InlineData("HTTPS://pedidos.loja:8443/api/v1?x=1", "pedidos.loja", "https", 8443)]
    [InlineData("http://user:senha@banco.interno:5432/", "banco.interno", "http", 5432)]
    [InlineData("postgres://banco.interno:5432/app", "banco.interno", "postgres", 5432)]
    [InlineData("meuapp.sev.", "meuapp.sev", null, null)]
    [InlineData("[::1]:5432", "::1", null, 5432)]
    [InlineData("http://[fe80::1]/", "fe80::1", "http", null)]
    [InlineData("fe80::1", "fe80::1", null, null)]
    [InlineData("10.0.0.5:abc", "10.0.0.5", null, null)]
    [InlineData("10.0.0.5:99999", "10.0.0.5", null, null)]
    [InlineData("", "", null, null)]
    public void Host_takes_out_what_comes_with_a_copied_address(string text, string host, string? scheme, int? port)
    {
        var clean = InputCleanup.Host(text);

        Assert.Equal(host, clean.Host);
        Assert.Equal(scheme, clean.Scheme);
        Assert.Equal(port, clean.Port);
    }

    [Theory]
    [InlineData(":8080", "8080")]
    [InlineData("8080/", "8080")]
    [InlineData(" 8080 ", "8080")]
    [InlineData("http://localhost:3000/", "3000")]
    [InlineData("192.168.100.5:8000", "8000")]
    [InlineData("abc", "abc")]
    public void Port_keeps_the_number(string text, string port) => Assert.Equal(port, InputCleanup.Port(text));

    [Theory]
    [InlineData("meuapp.sev", "meuapp.sev")]
    [InlineData("https://meuapp.sev/", "meuapp.sev")]
    [InlineData("http://meuapp.sev:3000/api/?x=1#topo", "meuapp.sev/api")]
    [InlineData("meuapp.sev:", "meuapp.sev")]
    [InlineData("*.meuapp.sev", "*.meuapp.sev")]
    public void Address_keeps_the_path_and_drops_the_rest(string text, string address) => Assert.Equal(address, InputCleanup.Address(text));

    [Fact]
    public void Names_are_one_per_line_and_cleaned()
    {
        var names = InputCleanup.Names("http://pedidos:8080/, pedidos.loja.\n\n catalogoapi:");

        Assert.Equal(["pedidos", "pedidos.loja", "catalogoapi"], names.Split(Environment.NewLine));
    }
}
