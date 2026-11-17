using System.Net;
using System.Text;

namespace ClockManager.Tests.Fakes;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate. No network.</summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }

    public static StubHttpMessageHandler Returning(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(Response(status, body, mediaType)));

    /// <summary>Never answers; completes only when the token passed by the client is cancelled.</summary>
    public static StubHttpMessageHandler Hanging() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    public static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}
