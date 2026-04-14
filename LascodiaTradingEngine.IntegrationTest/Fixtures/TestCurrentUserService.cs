using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models.AccessModel;

namespace LascodiaTradingEngine.IntegrationTest.Fixtures;

public sealed class TestCurrentUserService : ICurrentUserService
{
    private static readonly UserVM IntegrationUser = new()
    {
        Id = "integration-user",
        FirstName = "Integration",
        LastName = "User",
        Email = "integration@lascodia.test",
        PhoneNumber = "0000000000"
    };

    public string UserId => IntegrationUser.Id ?? string.Empty;

    public UserVM? User => IntegrationUser;

    public string? BearerToken => "integration-test-token";
}
