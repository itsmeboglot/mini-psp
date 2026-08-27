using Payments.Api.Domain;

namespace Payments.Api.Tests;

/// <summary>
/// The lifecycle rules, exercised without a database: these are domain
/// decisions, and nothing about them depends on storage.
/// </summary>
public sealed class PaymentTransitionTests
{
    private static Payment NewPayment()
        => Payment.Create(Guid.NewGuid(), Money("USD", 1000));

    private static Money Money(string currency, long minor)
    {
        Assert.True(Domain.Money.TryCreate(minor, currency, out var money, out _));
        return money;
    }

    [Fact]
    public void A_new_payment_starts_as_created_at_version_one()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(1, payment.Version);
    }

    [Theory]
    [InlineData(PaymentStatus.Created, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Created, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Unknown)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Refunded)]
    public void Allowed_moves_are_permitted(PaymentStatus from, PaymentStatus to)
        => Assert.True(PaymentTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Authorized)]   // no going back
    [InlineData(PaymentStatus.Created, PaymentStatus.Captured)]      // no skipping authorisation
    [InlineData(PaymentStatus.Failed, PaymentStatus.Authorized)]     // terminal
    [InlineData(PaymentStatus.Refunded, PaymentStatus.Captured)]     // terminal
    [InlineData(PaymentStatus.Expired, PaymentStatus.Pending)]       // terminal
    public void Illegal_moves_are_refused(PaymentStatus from, PaymentStatus to)
        => Assert.False(PaymentTransitions.IsAllowed(from, to));

    [Fact]
    public void An_illegal_transition_throws_and_leaves_the_payment_untouched()
    {
        var payment = NewPayment();

        var error = Assert.Throws<InvalidPaymentTransitionException>(
            () => payment.TransitionTo(PaymentStatus.Captured));

        Assert.Equal(payment.Id, error.PaymentId);
        Assert.Equal(PaymentStatus.Created, error.From);
        Assert.Equal(PaymentStatus.Captured, error.To);

        // Records are immutable, so the original is necessarily unchanged.
        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(1, payment.Version);
    }

    [Fact]
    public void A_legal_transition_advances_the_status_and_the_version()
    {
        var authorized = NewPayment()
            .TransitionTo(PaymentStatus.Pending)
            .TransitionTo(PaymentStatus.Authorized);

        Assert.Equal(PaymentStatus.Authorized, authorized.Status);
        Assert.Equal(3, authorized.Version);
    }

    [Fact]
    public void Unknown_is_not_terminal_because_reconciliation_must_resolve_it()
    {
        Assert.False(PaymentTransitions.IsTerminal(PaymentStatus.Unknown));

        Assert.True(PaymentTransitions.IsAllowed(PaymentStatus.Unknown, PaymentStatus.Authorized));
        Assert.True(PaymentTransitions.IsAllowed(PaymentStatus.Unknown, PaymentStatus.Captured));
        Assert.True(PaymentTransitions.IsAllowed(PaymentStatus.Unknown, PaymentStatus.Failed));
    }

    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Refunded)]
    public void Terminal_states_allow_nothing_further(PaymentStatus terminal)
    {
        Assert.True(PaymentTransitions.IsTerminal(terminal));

        foreach (var target in Enum.GetValues<PaymentStatus>())
        {
            Assert.False(PaymentTransitions.IsAllowed(terminal, target));
        }
    }

    [Fact]
    public void Every_status_has_a_wire_value_that_round_trips()
    {
        foreach (var status in Enum.GetValues<PaymentStatus>())
        {
            Assert.Equal(status, PaymentStatuses.Parse(PaymentStatuses.ToWire(status)));
        }
    }
}
