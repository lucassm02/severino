using Severino.Contracts;
using Severino.Core.Helper;

namespace Severino.Tests.Routes;

/// <summary>Records what the app asks the Helper, and can play unavailable or failing.</summary>
internal sealed class FakeHelper : IHelperClient
{
    private readonly Lock _gate = new();
    private readonly List<HelperRequest> _requests = [];

    public bool Unavailable { get; set; }
    public string? Error { get; set; }
    public int ProtocolVersion { get; set; } = HelperProtocol.Version;

    public List<HelperRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    public Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw new HelperUnavailableException("fora", new TimeoutException());
        lock (_gate) _requests.Add(request);
        return Task.FromResult(new HelperResponse(Error is null, Error, ProtocolVersion, "test"));
    }
}
