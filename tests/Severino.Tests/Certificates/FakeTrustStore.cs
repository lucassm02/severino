using System.Security.Cryptography.X509Certificates;
using Severino.Proxy.Certificates;

namespace Severino.Tests.Certificates;

/// <summary>Stands in for the Windows root store, so tests never touch the real one.</summary>
internal sealed class FakeTrustStore : ITrustStore
{
    public List<string> Roots { get; } = [];
    public bool Decline { get; set; }
    public bool DeclineRemoval { get; set; }

    public bool Contains(string thumbprint) => Roots.Contains(thumbprint);

    public bool Add(X509Certificate2 root)
    {
        if (Decline)
            return false;
        Assert.False(root.HasPrivateKey);
        Roots.Add(root.Thumbprint);
        return true;
    }

    public bool Remove(string thumbprint) => !DeclineRemoval && Roots.Remove(thumbprint);
}
