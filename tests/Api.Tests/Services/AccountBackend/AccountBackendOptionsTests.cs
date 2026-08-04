using System.Net;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.AccountBackend;

namespace Defra.WasteObligations.Api.Tests.Services.AccountBackend;

public class AccountBackendOptionsTests
{
    [Theory]
    [InlineData("http", 1)]
    [InlineData("https", 2)]
    public void WhenScheme_ShouldBeExpectedVersion(string scheme, int expectedVersion)
    {
        var subject = new AccountBackendOptions
        {
            BaseAddress = $"{scheme}://account-backend",
            TokenEndpoint = "http://oauth2/token",
            ClientId = "client_id",
            ClientSecret = "client_secret",
        };

        var client = new HttpClient();
        subject.Configure(client);

        switch (expectedVersion)
        {
            case 1:
                client.DefaultRequestVersion.Should().Be(HttpVersion.Version11);
                break;
            case 2:
                client.DefaultRequestVersion.Should().Be(HttpVersion.Version20);
                break;
        }
    }
}
