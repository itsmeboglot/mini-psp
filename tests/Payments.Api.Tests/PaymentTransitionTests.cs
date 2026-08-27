using Microsoft.Extensions.Time.Testing;
using Payments.Api.Domain;

namespace Payments.Api.Tests;

/// <summary>
/// The lifecycle rules, exercised without a database: these are domain
/// decisions, and nothing about them depends on storage.
/// </summary>
public sealed class PaymentTransitionTests
{
    private static Payment NewPayment(TimeProvider? clock = null)
        => Payment.Create(Guid.NewGuid(), Amount("USD", 1000), clock ?? TimeProvider.System);

    private static Money Amount(string currency, long minor)
    {
        Assert.True(Money.TryCreate(minor, currency, out var money, out _));
        return money;
    }

    [Fact]
    public void A_new_payment_starts_as_created_at_version_one()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(1, payment.Version);
    }

    [Fact]
    public void A_new_payment_is_stamped_by_the_injected_clock()
    {
        var instant = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);
        var clock = new FakeTimeProvider(instant);

        var payment = NewPayment(clock);

        Assert.Equal(instant, payment.CreatedAt);

        // The same instant seeds the version 7 id, so an id and its timestamp can
        // never tell different stories about when the payment was created.
        Assert.Equal(7, payment.Id.Version);
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

    /// <summary>
    /// Guards the gap that a table of rules leaves open: add a status to the enum,
    /// forget the rules, and every query about it would quietly answer "no".
    /// </summary>
    [Fact]
    public void Every_status_has_transition_rules()
    {
        foreach (var from in Enum.GetValues<PaymentStatus>())
        {
            foreach (var to in Enum.GetValues<PaymentStatus>())
            {
                // Throws if `from` is missing from the rules, which is the point.
                PaymentTransitions.IsAllowed(from, to);
            }

            PaymentTransitions.IsTerminal(from);
        }
    }

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

    [Fact]
    public void Money_accepts_any_sign_and_normalises_the_currency()
    {
        // Sign is the caller's rule, not money's: a ledger needs both directions
        // and a zero amount authorisation is a real operation.
        Assert.True(Money.TryCreate(0, "usd", out var zero, out _));
        Assert.True(Money.TryCreate(-250, "EUR", out var credit, out _));

        Assert.Equal("USD", zero.Currency);
        Assert.Equal(-250, credit.MinorUnits);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("USDD")]
    [InlineData("")]
    [InlineData(null)]
    public void Money_refuses_anything_that_is_not_a_three_letter_code(string? currency)
        => Assert.False(Money.TryCreate(100, currency, out _, out var error));
}
