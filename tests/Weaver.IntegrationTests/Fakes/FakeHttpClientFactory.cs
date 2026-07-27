namespace Weaver.IntegrationTests.Fakes;

/// <summary>
/// Returns an HttpClient backed by a single fake handler for every client name —
/// AgentController only ever asks for "llama", but there's no reason to special-case it.
/// </summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
