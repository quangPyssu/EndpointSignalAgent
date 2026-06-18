// src/Bootstrap/Backend/BearerTokenHandler.cs
using EndpointSignalAgent.Bootstrap.Identity;

namespace EndpointSignalAgent.Bootstrap.Backend;

public sealed class BearerTokenHandler(DeviceTokenStore tokenStore) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = tokenStore.Get();
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
