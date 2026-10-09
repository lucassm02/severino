using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Severino.Proxy.Certificates;

/// <summary>The CA file is there but cannot be read: corrupted, or protected for another user.</summary>
public sealed class CaUnreadableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Keeps the CA and the issued leaves on disk, private keys always encrypted with DPAPI for the
/// current user. Layout: ca.crt (DER), ca.key (PKCS#12 with the key, DPAPI), certs/&lt;domain&gt;.pfx (DPAPI).
/// </summary>
public sealed class CaStore(string directory)
{
    // Ties the blobs to Severino: another program running as the same user must at least know
    // this to call ProtectedData on them.
    private static readonly byte[] Entropy = "Severino.LocalCA.v1"u8.ToArray();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Severino", "ca");

    public string Directory { get; } = directory;
    private string CertificatePath => Path.Combine(Directory, "ca.crt");
    private string KeyPath => Path.Combine(Directory, "ca.key");
    private string LeafDirectory => Path.Combine(Directory, "certs");

    public bool Exists => File.Exists(KeyPath);

    /// <returns>The CA, or null when there is none.</returns>
    /// <exception cref="CaUnreadableException">The files exist but cannot be used.</exception>
    public CertificateAuthority? Load()
    {
        if (!Exists)
            return null;
        try
        {
            var pkcs12 = Unprotect(File.ReadAllBytes(KeyPath));
            return new CertificateAuthority(X509CertificateLoader.LoadPkcs12(pkcs12, null));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new CaUnreadableException("Não foi possível ler a CA local.", ex);
        }
    }

    public void Save(CertificateAuthority ca)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomic(KeyPath, Protect(ca.Certificate.Export(X509ContentType.Pkcs12)));
        WriteAtomic(CertificatePath, ca.Certificate.RawData);
        DeleteLeaves();
    }

    /// <summary>Removes the CA and every leaf.</summary>
    public void Delete()
    {
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }

    public X509Certificate2? LoadLeaf(string domain)
    {
        var path = LeafPath(domain);
        if (!File.Exists(path))
            return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12(Unprotect(File.ReadAllBytes(path)), null);
        }
        catch (CryptographicException)
        {
            File.Delete(path); // reissued on demand
            return null;
        }
    }

    public void SaveLeaf(string domain, X509Certificate2 leaf)
    {
        System.IO.Directory.CreateDirectory(LeafDirectory);
        WriteAtomic(LeafPath(domain), Protect(leaf.Export(X509ContentType.Pkcs12)));
    }

    public void DeleteLeaves()
    {
        if (System.IO.Directory.Exists(LeafDirectory))
            System.IO.Directory.Delete(LeafDirectory, recursive: true);
    }

    // Domains are validated hostnames ([a-z0-9.-]), safe as file names.
    private string LeafPath(string domain) => Path.Combine(LeafDirectory, domain + ".pfx");

    private static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }
}
