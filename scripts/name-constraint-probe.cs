#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:project ../src/Severino.Proxy/Severino.Proxy.csproj

// Manual check for the local CA's Name Constraints (Phase 2, criterion 6).
//
// Signs a leaf for any name with the real local CA key, skipping the coverage check the app
// enforces, and serves it on https://127.0.0.1:<port>. A browser that honours the constraints must
// refuse a name outside the CA (banco.com.br) and accept one inside it (probe.sev).
//
//   dotnet run scripts/name-constraint-probe.cs -- banco.com.br
//   dotnet run scripts/name-constraint-probe.cs -- probe.sev
//
// The leaf lives only in memory and the server stops with Ctrl+C. Nothing is installed.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Severino.Proxy.Certificates;

var domain = args.Length > 0 ? args[0] : "banco.com.br";
var port = args.Length > 1 ? int.Parse(args[1]) : 9443;

using var ca = new CaStore(CaStore.DefaultDirectory).Load();
if (ca is null)
{
    Console.Error.WriteLine("Não há CA local. Ative o HTTPS no Severino primeiro.");
    return 1;
}

using var leaf = IssueAnyName(ca.Certificate, domain);
var inside = ca.Covers(domain);
Console.WriteLine($"CA: {ca.Certificate.Subject}");
Console.WriteLine($"Cobre: {string.Join(", ", ca.Names)}");
Console.WriteLine($"Folha para {domain}: {(inside ? "DENTRO" : "FORA")} da cobertura. Esperado no navegador: {(inside ? "abre com cadeado" : "erro de certificado")}.");
Console.WriteLine($"Servindo em https://{domain}:{port}/ (127.0.0.1). Ctrl+C para parar.");

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        using (client)
        await using (var tls = new SslStream(client.GetStream()))
        {
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = leaf,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                });
                var buffer = new byte[4096];
                await tls.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false); // the request line; its content does not matter
                var body = inside
                    ? $"<h1>Controle ok</h1><p>{domain} está dentro da CA, então abrir esta página é o esperado.</p>"
                    : $"<h1>FALHOU</h1><p>{domain} está fora da CA. Se esta página abriu, a Name Constraint não foi aplicada.</p>";
                var bytes = Encoding.UTF8.GetBytes(body);
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                await tls.WriteAsync(bytes);
                Console.WriteLine($"{DateTime.Now:T} handshake aceito pelo cliente");
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException)
            {
                Console.WriteLine($"{DateTime.Now:T} handshake recusado pelo cliente");
            }
        }
    });
}

// Same shape as CertificateAuthority.IssueLeaf, minus its coverage check.
static X509Certificate2 IssueAnyName(X509Certificate2 issuer, string domain)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName(domain);
    request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
    var now = DateTimeOffset.UtcNow;
    using var signed = request.Create(issuer, now.AddMinutes(-5), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
    using var withKey = signed.CopyWithPrivateKey(key);
    // Schannel needs a key that went through PKCS#12, not one created in memory.
    return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
}
