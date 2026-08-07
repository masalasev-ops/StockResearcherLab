using StockResearcherLab.Core;

namespace StockResearcherLab.Data.Eodhd;

/// <summary>
/// The provider's 1,000-requests-a-minute limit, enforced here rather than
/// discovered as a 429 in the middle of a night's ingest.
///
/// A sliding window over the injected clock. It takes an <see cref="IClock"/>
/// rather than reading the machine, because nothing reads system time outside the
/// clock implementation [INVARIANT 11] and guards.ps1 greps for the alternative.
/// The wait itself is a delay rather than a spin.
/// </summary>
public sealed class EodhdRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly IClock _clock;
    private readonly int _perWindow;
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EodhdRateLimiter(IClock clock, int requestsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);

        _clock = clock;
        _perWindow = requestsPerMinute;
    }

    /// <summary>Requests recorded inside the current window. For tests and for the run log.</summary>
    public int InWindow
    {
        get
        {
            lock (_sent)
            {
                return _sent.Count;
            }
        }
    }

    /// <summary>
    /// Returns once another request may be sent, recording it. Blocks only when the
    /// window is full, which on a nightly run is essentially never: the whole
    /// universe is one bulk call and the per-ticker work is thousands of requests
    /// spread over minutes.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _clock.UtcNow;
                Evict(now);

                if (_sent.Count < _perWindow)
                {
                    _sent.Enqueue(now);
                    return;
                }

                // The oldest request in the window decides how long to wait.
                var oldest = _sent.Peek();
                var wait = oldest + Window - now;

                if (wait <= TimeSpan.Zero)
                {
                    continue;
                }

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Evict(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= Window)
        {
            _sent.Dequeue();
        }
    }
}
