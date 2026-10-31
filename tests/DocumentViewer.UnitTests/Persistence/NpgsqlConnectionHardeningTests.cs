using DocumentViewer.Infrastructure.Persistence;
using Npgsql;
using Shouldly;

namespace DocumentViewer.UnitTests.Persistence;

public sealed class NpgsqlConnectionHardeningTests
{
    private const string Bare =
        "Host=aws-0-eu-west-1.pooler.supabase.com;Port=6543;Database=postgres;Username=u;Password=p;Ssl Mode=Require";

    [Fact]
    public void Keeps_one_connection_warm_so_no_request_pays_the_handshake()
    {
        var hardened = new NpgsqlConnectionStringBuilder(NpgsqlConnectionHardening.Harden(Bare));

        // MinPoolSize is the one setting here that changes Npgsql's behaviour: its
        // default is 0, so without this nothing keeps a connection warm.
        hardened.MinPoolSize.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Pins_every_setting_it_depends_on_instead_of_inheriting_an_Npgsql_default()
    {
        // Npgsql's own defaults already happen to be Timeout=15 and
        // ConnectionIdleLifetime=300, so asserting those *values* would pass even if
        // Harden returned its input untouched — the "test that cannot fail" trap. These
        // assertions are on what Harden EMITS, so they fail if it stops pinning them,
        // and the pinning is what stops a future Npgsql default change from silently
        // widening the handshake budget this service depends on.
        var hardened = NpgsqlConnectionHardening.Harden(Bare).ShouldNotBeNull();

        hardened.ShouldContain("Timeout=15", Case.Sensitive);
        hardened.ShouldContain("Minimum Pool Size=1", Case.Sensitive);
        hardened.ShouldContain("Connection Idle Lifetime=300", Case.Sensitive);
    }

    [Fact]
    public void Does_not_override_settings_the_operator_supplied()
    {
        var supplied = Bare + ";Timeout=42;Minimum Pool Size=7;Connection Idle Lifetime=99;Maximum Pool Size=11";

        var hardened = new NpgsqlConnectionStringBuilder(NpgsqlConnectionHardening.Harden(supplied));

        hardened.Timeout.ShouldBe(42);
        hardened.MinPoolSize.ShouldBe(7);
        hardened.ConnectionIdleLifetime.ShouldBe(99);
        hardened.MaxPoolSize.ShouldBe(11);
    }

    [Fact]
    public void Never_sets_a_minimum_pool_size_when_pooling_is_disabled()
    {
        // Npgsql rejects Minimum Pool Size together with Pooling=false; the integration
        // tests' unreachable-database connection string relies on that combination.
        var hardened = new NpgsqlConnectionStringBuilder(
            NpgsqlConnectionHardening.Harden(Bare + ";Pooling=false"));

        hardened.Pooling.ShouldBeFalse();
        hardened.MinPoolSize.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a connection string at all")]
    public void Returns_the_input_untouched_when_it_cannot_be_hardened(string? input)
    {
        NpgsqlConnectionHardening.Harden(input).ShouldBe(input);
    }
}
