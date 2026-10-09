using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Severino.Proxy.Certificates;

public enum LocalCaState
{
    Disabled,
    Active,
    /// <summary>The CA files exist but Windows no longer trusts the root (removed by hand).</summary>
    NotTrusted,
    /// <summary>The CA files cannot be read, e.g. another Windows user's DPAPI.</summary>
    Unreadable,
}

public sealed record LocalCaStatus(LocalCaState State, IReadOnlyList<string> Names, DateTimeOffset? NotAfter = null);

public enum ActivationResult
{
    Activated,
    /// <summary>Activated, but the user kept the previous root in the store.</summary>
    ActivatedOldRootKept,
    /// <summary>The user declined Windows' warning; nothing changed.</summary>
    Declined,
}

/// <summary>The local HTTPS authority: creates, trusts, reissues and removes the root, and picks the leaf for each handshake.</summary>
public sealed class LocalCa(CaStore store, ITrustStore trust, TimeProvider time, ILogger<LocalCa> logger)
{
    private readonly Lock _gate = new();
    private readonly LeafCache _leaves = new(store, time);
    private CertificateAuthority? _ca;

    public LocalCaStatus Status { get; private set; } = new(LocalCaState.Disabled, []);

    /// <summary>Raised on the thread that made the change.</summary>
    public event EventHandler<LocalCaStatus>? Changed;

    public void Load()
    {
        try
        {
            var ca = store.Load();
            lock (_gate) _ca = ca;
            SetStatus(ca is null ? new(LocalCaState.Disabled, [])
                : trust.Contains(ca.Thumbprint) ? new(LocalCaState.Active, ca.Names, ca.NotAfter)
                : new(LocalCaState.NotTrusted, ca.Names, ca.NotAfter));
        }
        catch (CaUnreadableException ex)
        {
            logger.LogWarning(ex, "Local CA unreadable");
            SetStatus(new(LocalCaState.Unreadable, []));
        }
    }

    public bool Covers(string domain)
    {
        lock (_gate) return _ca?.Covers(domain) ?? false;
    }

    /// <summary>
    /// Creates a root for <paramref name="names"/> and asks Windows to trust it, then retires the
    /// previous root. Blocks while Windows shows its warning, so call it off the UI thread.
    /// </summary>
    public ActivationResult Activate(IReadOnlyCollection<string> names)
    {
        var ca = CertificateAuthority.Create(names, time);
        using (var root = ca.PublicCertificate())
        {
            if (!trust.Add(root))
            {
                logger.LogInformation("User declined trusting the local CA");
                ca.Dispose();
                return ActivationResult.Declined;
            }
        }

        store.Save(ca);
        CertificateAuthority? previous;
        lock (_gate)
        {
            previous = _ca;
            _ca = ca;
        }

        var result = ActivationResult.Activated;
        if (previous is not null && previous.Thumbprint != ca.Thumbprint && trust.Contains(previous.Thumbprint)
            && !trust.Remove(previous.Thumbprint))
        {
            // Harmless: its key is gone, so it cannot sign anything new.
            result = ActivationResult.ActivatedOldRootKept;
        }

        logger.LogInformation("Local CA {Thumbprint} active for {Names}", ca.Thumbprint, string.Join(", ", ca.Names));
        SetStatus(new(LocalCaState.Active, ca.Names, ca.NotAfter));
        return result;
    }

    /// <summary>Takes the root out of the store and deletes every key. Returns false if the user kept the root.</summary>
    public bool Remove()
    {
        CertificateAuthority? ca;
        lock (_gate)
        {
            ca = _ca;
            _ca = null;
        }

        var removed = ca is null || !trust.Contains(ca.Thumbprint) || trust.Remove(ca.Thumbprint);
        store.Delete();
        SetStatus(new(LocalCaState.Disabled, []));
        return removed;
    }

    /// <summary>The certificate to present for <paramref name="domain"/>, or null when it is not covered.</summary>
    public X509Certificate2? CertificateFor(string domain)
    {
        CertificateAuthority? ca;
        lock (_gate) ca = _ca;
        if (ca is null || Status.State != LocalCaState.Active || !ca.Covers(domain))
            return null;
        return _leaves.Get(ca, domain);
    }

    public string? ExportPem()
    {
        lock (_gate) return _ca?.ExportPem();
    }

    private void SetStatus(LocalCaStatus status)
    {
        Status = status;
        Changed?.Invoke(this, status);
    }
}
