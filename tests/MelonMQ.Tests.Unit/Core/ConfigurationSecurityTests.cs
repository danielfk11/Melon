using FluentAssertions;
using MelonMQ.Broker.Core;

namespace MelonMQ.Tests.Unit.Core;

public class ConfigurationSecurityTests
{
    [Fact]
    public void ValidateConfiguration_ShouldRequireAuthInProduction()
    {
        var config = new MelonMQConfiguration
        {
            Security = new SecurityConfiguration
            {
                RequireAuth = false,
                RequireAdminApiKey = true,
                AdminApiKey = "api-key"
            }
        };

        Action act = () => config.ValidateConfiguration(isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RequireAuth=true*");
    }

    [Fact]
    public void ValidateConfiguration_ShouldRejectPlaintextPasswords_WhenHashingRequired()
    {
        var config = new MelonMQConfiguration
        {
            Security = new SecurityConfiguration
            {
                RequireAuth = true,
                RequireHashedPasswords = true,
                Users = new Dictionary<string, string>
                {
                    ["alice"] = "plaintext"
                }
            }
        };

        Action act = () => config.ValidateConfiguration();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not hashed*");
    }

    [Fact]
    public void PasswordHasher_ShouldVerifyHashedPassword()
    {
        var hash = PasswordHasher.HashPassword("StrongPass#123");

        PasswordHasher.VerifyPassword("StrongPass#123", hash).Should().BeTrue();
        PasswordHasher.VerifyPassword("wrong", hash).Should().BeFalse();
    }

}
