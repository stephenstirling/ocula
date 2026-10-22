using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The debounce behind "Share people with Porta": many changes in a row cause one refresh, and a long steady run still refreshes now and then.
/// Real timers with short intervals, so the assertions leave generous margins.
/// </summary>
public class RefreshDebouncerTests
{
    [Fact]
    public async Task A_burst_of_requests_runs_the_action_once_after_things_go_quiet()
    {
        var runs = 0;
        using var debounce = new RefreshDebouncer(() => Interlocked.Increment(ref runs), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));

        for (var i = 0; i < 20; i++) { debounce.Request(); await Task.Delay(10); }
        Assert.Equal(0, Volatile.Read(ref runs));          // still busy: nothing yet

        await Task.Delay(600);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task A_run_of_changes_that_never_goes_quiet_still_runs_within_the_maximum_wait()
    {
        var runs = 0;
        using var debounce = new RefreshDebouncer(() => Interlocked.Increment(ref runs), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(300));

        // Keep producing requests until two runs have actually happened. A fixed one-second loop
        // can end before a timer callback gets CPU time on a busy CI runner, then assert too early.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref runs) < 2 && DateTime.UtcNow < until)
        {
            debounce.Request();
            await Task.Delay(20);
        }

        Assert.True(Volatile.Read(ref runs) >= 2, $"ran {runs} times; the quiet time (400 ms) was never reached, so only the maximum wait can have caused these");
    }

    [Fact]
    public async Task Requests_after_a_run_start_a_new_burst()
    {
        var runs = 0;
        using var debounce = new RefreshDebouncer(() => Interlocked.Increment(ref runs), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(10));

        debounce.Request();
        await Task.Delay(400);
        debounce.Request();
        await Task.Delay(400);

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task A_cancelled_request_does_not_run()
    {
        var runs = 0;
        using var debounce = new RefreshDebouncer(() => Interlocked.Increment(ref runs), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(10));

        debounce.Request();
        debounce.Cancel();
        await Task.Delay(400);

        Assert.Equal(0, Volatile.Read(ref runs));
    }
}
