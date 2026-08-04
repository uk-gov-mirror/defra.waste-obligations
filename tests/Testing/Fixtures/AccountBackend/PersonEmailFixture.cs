using Defra.WasteObligations.Api.Services.AccountBackend;

namespace Defra.WasteObligations.Testing.Fixtures.AccountBackend;

public static class PersonEmailFixture
{
    public static PersonEmail Default() =>
        new()
        {
            FirstName = "First",
            LastName = "Last",
            Email = "first.last@example.com",
        };

    public static PersonEmail[] CancellationRecipients() =>
        [
            new()
            {
                FirstName = "Approved",
                LastName = "Person",
                Email = "approved-person@email.com",
            },
            new()
            {
                FirstName = "Primary",
                LastName = "Contact",
                Email = "primary.contact@email.com",
            },
        ];
}
