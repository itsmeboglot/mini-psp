namespace Payments.Core.Persistence;

/// <param name="Attempts">
/// How many times the provider has now been asked, this one included. A provider
/// that has still never heard of the payment after several asks did not take the
/// money.
/// </param>
public sealed record UnresolvedPayment(Domain.Payment Payment, int Attempts);
