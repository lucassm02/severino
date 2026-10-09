using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Severino.Proxy.Certificates;

public interface ITrustStore
{
    bool Contains(string thumbprint);

    /// <summary>Adds a root. Windows asks the user first.</summary>
    /// <returns>False when the user declined.</returns>
    bool Add(X509Certificate2 root);

    /// <returns>False when the user declined or it was not there.</returns>
    bool Remove(string thumbprint);
}

/// <summary>
/// The current user's Trusted Root store. Adding a root there shows Windows' own security
/// warning, which is the user's consent; no admin rights needed.
/// </summary>
public sealed class WindowsTrustStore : ITrustStore
{
    public bool Contains(string thumbprint)
    {
        using var store = Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
    }

    public bool Add(X509Certificate2 root)
    {
        using var store = Open(OpenFlags.ReadWrite);
        try
        {
            store.Add(root);
            return true;
        }
        catch (CryptographicException)
        {
            return false; // the user clicked "No"
        }
    }

    public bool Remove(string thumbprint)
    {
        using var store = Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (found.Count == 0)
            return false;
        try
        {
            store.RemoveRange(found);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static X509Store Open(OpenFlags flags)
    {
        var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(flags);
        return store;
    }
}
