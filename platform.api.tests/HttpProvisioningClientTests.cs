using System.Net;
using System.Text;
using AppPlatform.Platform.Services;

namespace AppPlatform.Platform.Tests;

public class HttpProvisioningClientTests
{
    /// <summary>Answers every request with one fixed response, as core would.</summary>
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static Task<ProvisionResult> Provision(HttpStatusCode status, string body)
        => new HttpProvisioningClient(new HttpClient(new Answer(status, body)) { BaseAddress = new("http://core") }, "key")
            .ProvisionAsync(new("Acme", "a@b.c", "key-1", "acme"));

    [Theory]
    [InlineData(HttpStatusCode.Conflict, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task A_refusal_carries_cores_code_and_message(HttpStatusCode status, bool conflict)
    {
        var result = await Provision(status, """{"problemCode":"subdomain_taken","message":"Taken."}""");

        Assert.False(result.Succeeded);
        Assert.Equal(new ProvisionRefusal(conflict, "subdomain_taken", "Taken."), result.Refusal);
    }

    [Theory]
    // A wrong internal key, core failing, and a proxy's error page: failures, not refusals.
    [InlineData(HttpStatusCode.Unauthorized, """{"problemCode":"not_signed_in","message":"No."}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"problemCode":"internal_error","message":"Ref 1."}""")]
    [InlineData(HttpStatusCode.BadRequest, "<html>Bad gateway</html>")]
    public async Task Anything_else_is_a_failure_with_no_refusal(HttpStatusCode status, string body)
    {
        var result = await Provision(status, body);

        Assert.False(result.Succeeded);
        Assert.Null(result.Refusal);
    }
}
