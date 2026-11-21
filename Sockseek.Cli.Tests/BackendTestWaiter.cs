using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using System.Threading.Channels;

namespace Tests.Cli;

internal static class BackendTestWaiter
{
    public static async Task<T> UntilAsync<T>(
        ICliBackend backend,
        Func<CancellationToken, Task<T>> observe,
        Func<T, bool> isSatisfied,
        string failureMessage,
        Func<T, string>? describe = null,
        int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        void Signal() => signals.Writer.TryWrite(0);
        void OnStateUpdated(DaemonClientUpdate _) => Signal();
        void OnActivity(ActivityEventDto _) => Signal();
        void OnSnapshot(StateSnapshotDto _) => Signal();

        backend.StateUpdated += OnStateUpdated;
        backend.ActivityReceived += OnActivity;
        backend.LiveSnapshotApplied += OnSnapshot;
        try
        {
            while (true)
            {
                T value = await observe(timeout.Token).ConfigureAwait(false);
                if (isSatisfied(value))
                    return value;
                await signals.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            string finalState;
            try
            {
                using var diagnosticTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                T final = await observe(diagnosticTimeout.Token).ConfigureAwait(false);
                finalState = describe?.Invoke(final) ?? final?.ToString() ?? "<null>";
            }
            catch (Exception exception)
            {
                finalState = $"<final observation failed: {exception.Message}>";
            }
            throw new AssertFailedException($"{failureMessage} Final state: {finalState}.");
        }
        finally
        {
            backend.StateUpdated -= OnStateUpdated;
            backend.ActivityReceived -= OnActivity;
            backend.LiveSnapshotApplied -= OnSnapshot;
        }
    }

    public static async Task UntilAsync(
        ICliBackend backend,
        Func<CancellationToken, Task<bool>> condition,
        string failureMessage,
        int timeoutMs = 5_000)
    {
        await UntilAsync(
            backend,
            condition,
            static satisfied => satisfied,
            failureMessage,
            static satisfied => satisfied.ToString(),
            timeoutMs).ConfigureAwait(false);
    }
}
