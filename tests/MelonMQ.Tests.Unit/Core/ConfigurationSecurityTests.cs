using FluentAssertions;
using MelonMQ.Broker.Core;

namespace MelonMQ.Tests.Unit.Core;

public class ConfigurationSecurityTests
{
    [Fact]
    public void LocalObservabilityStack_ShouldDefaultOffInCode()
    {
        var config = new ObservabilityConfiguration();

        config.LocalStack.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ValidateConfiguration_ShouldRejectLocalObservabilityStackInProduction()
    {
        var config = new MelonMQConfiguration
        {
            Observability = new ObservabilityConfiguration
            {
                LocalStack = new LocalObservabilityStackConfiguration
                {
                    Enabled = true
                }
            }
        };

        Action act = () => config.ValidateConfiguration(isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must not auto-start*");
    }

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

    [Fact]
    public void ValidateConfiguration_ShouldRejectInvalidApiKeyRole()
    {
        var config = new MelonMQConfiguration
        {
            Security = new SecurityConfiguration
            {
                RequireAdminApiKey = true,
                ApiKeys =
                [
                    new ApiKeyEntryConfiguration
                    {
                        Name = "bad-role",
                        Key = "k1",
                        Role = "super-admin"
                    }
                ]
            }
        };

        Action act = () => config.ValidateConfiguration();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid API key role*");
    }

    [Fact]
    public void ValidateConfiguration_ShouldEnforceProductionClusterGuardrails()
    {
        var config = new MelonMQConfiguration
        {
            TcpTls = new TcpTlsConfiguration
            {
                Enabled = true,
                CertificatePath = typeof(ConfigurationSecurityTests).Assembly.Location
            },
            Security = new SecurityConfiguration
            {
                RequireAuth = true,
                RequireHashedPasswords = false,
                RequireAdminApiKey = true,
                AllowedOrigins = ["https://example.test"],
                ApiKeys =
                [
                    new ApiKeyEntryConfiguration
                    {
                        Name = "admin",
                        Key = "admin-key",
                        Role = SecurityConfiguration.RoleAdmin
                    }
                ],
                Users = new Dictionary<string, string>
                {
                    ["admin"] = "plaintext"
                }
            },
            Cluster = new ClusterConfiguration
            {
                Enabled = true,
                NodeId = "node-1",
                NodeAddress = "https://node-1.example",
                SharedKey = "cluster-key",
                Consistency = "leader",
                RequireQuorumForWrites = true,
                SeedNodes = ["https://node-1.example", "https://node-2.example", "https://node-3.example"]
            }
        };

        Action act = () => config.ValidateConfiguration(isProduction: true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Consistency=quorum*");
    }
}
