namespace Sldl.Core;

﻿public class RateLimitedSemaphore
{
    private readonly int maxCount;
    private readonly TimeSpan resetTimeSpan;
    private readonly SemaphoreSlim semaphore;
    private long nextResetTimeTicks;
    private readonly object resetTimeLock = new object();
    private int _limitedNotified = 0; // 0 = not yet notified for current window

    public RateLimitedSemaphore(int maxCount, TimeSpan resetTimeSpan)
    {
        this.maxCount = maxCount;
        this.resetTimeSpan = resetTimeSpan;
        this.semaphore = new SemaphoreSlim(maxCount, maxCount);
        this.nextResetTimeTicks = (DateTimeOffset.UtcNow + this.resetTimeSpan).UtcTicks;
    }

    private void TryResetSemaphore()
    {
        if (!(DateTimeOffset.UtcNow.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks)))
            return;

        lock (this.resetTimeLock)
        {
            var currentTime = DateTimeOffset.UtcNow;
            if (currentTime.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks))
            {
                int releaseCount = this.maxCount - this.semaphore.CurrentCount;
                if (releaseCount > 0)
                    this.semaphore.Release(releaseCount);

                var newResetTimeTicks = (currentTime + this.resetTimeSpan).UtcTicks;
                Interlocked.Exchange(ref this.nextResetTimeTicks, newResetTimeTicks);
                Interlocked.Exchange(ref _limitedNotified, 0);
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default, Action? onWaiting = null)
    {
        TryResetSemaphore();
        var semaphoreTask = this.semaphore.WaitAsync(cancellationToken);

        if (!semaphoreTask.IsCompleted && onWaiting != null && Interlocked.Exchange(ref _limitedNotified, 1) == 0)
            onWaiting();

        while (!semaphoreTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticks = Interlocked.Read(ref this.nextResetTimeTicks);
            var nextResetTime = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
            var delayTime = nextResetTime - DateTimeOffset.UtcNow;

            Task delayTask;
            if (delayTime > TimeSpan.Zero)
                delayTask = Task.Delay(delayTime, cancellationToken);
            else
                delayTask = Task.CompletedTask;

            try
            {
                await Task.WhenAny(semaphoreTask, delayTask);
            }
            catch (OperationCanceledException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            TryResetSemaphore();
        }

        await semaphoreTask;
    }
}
