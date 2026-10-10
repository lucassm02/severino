using System.Security.Principal;
using System.Text.Json;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>
/// <c>Severino.Helper.exe --approve-dns &lt;argument&gt;</c> (see <see cref="HelperProtocol.EncodeApproval"/>): records approvals for public
/// addresses. The app starts it with "run as administrator", so Windows' UAC prompt is the
/// consent; without elevation it refuses. Validates the pairs as the pipe would.
/// </summary>
public static class ApproveDns
{
    public static int Run(string encoded, IDnsApprovals? store = null)
    {
        if (store is null && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return 5; // ERROR_ACCESS_DENIED

        HelperRequest request;
        try
        {
            request = HelperProtocol.DeserializeRequest(Convert.FromBase64String(encoded));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return 2;
        }
        if (!HelperProtocol.TryNormalizeDnsEntries(request.Entries, out var entries, out _))
            return 3;

        var publicOnes = entries.Where(e => DnsAddress.TryClassify(e.Address, out _, out var scope) && scope == AddressScope.Public).ToList();
        (store ?? new DnsApprovals()).Approve(publicOnes);
        return 0;
    }
}
