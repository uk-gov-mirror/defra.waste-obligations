using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Api.Services.AccountBackend;

public record PersonEmail
{
    [JsonPropertyName("firstName")]
    public required string FirstName { get; init; }

    [JsonPropertyName("lastName")]
    public required string LastName { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }
}
