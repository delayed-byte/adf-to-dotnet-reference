namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>
/// The test's notion of "now", shared by the orchestration runner (<c>CurrentUtcDateTime</c>, durable
/// timers) and the batch's injected <see cref="TimeProvider"/>. Durable timers advance it instantly,
/// so retry intervals copied from ADF (30 s and more) cost no wall-clock time. Only the wall clock is
/// virtual: timers created through <see cref="TimeProvider.CreateTimer"/> and timestamps still run in
/// real time, so HTTP and database timeouts behave normally.
/// </summary>
public sealed class VirtualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private DateTimeOffset now = start.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    /// <summary>Sets the clock (between runs only; it may move backwards, e.g. for a rerun of an earlier date).</summary>
    public void Set(DateTimeOffset value)
    {
        lock (gate)
        {
            now = value.ToUniversalTime();
        }
    }

    /// <summary>Moves the clock forward to <paramref name="value"/>; an earlier value is ignored.</summary>
    public void AdvanceTo(DateTimeOffset value)
    {
        lock (gate)
        {
            if (value > now)
            {
                now = value.ToUniversalTime();
            }
        }
    }
}
