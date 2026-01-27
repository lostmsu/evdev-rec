namespace EvDev;

using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

sealed class SyncWriter(EvdevCaptureOptions options, ILogger<SyncWriter> logger)
    : BackgroundService {
    readonly EvdevCaptureOptions options =
        options ?? throw new ArgumentNullException(nameof(options));

    readonly ILogger<SyncWriter> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    long newDelta;
    long lastTimestampTicks;

    public void NotifyEvent(TimeSpan lastTimestamp) {
        var now = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref this.lastTimestampTicks, lastTimestamp.Ticks);
        Interlocked.Exchange(ref this.newDelta, now.Ticks - lastTimestamp.Ticks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        string syncPath = GetSyncPath(this.options);

        var delta = TimeSpan.FromMinutes(-13); // force initial write

        await using var writer = new StreamWriter(syncPath, Utf8NoBom, new() {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            Share = FileShare.Read,
        });
        await writer.WriteLineAsync("event_ts_microsec\tunix_time_microsec").ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                var newTimestamp = TimeSpan.FromTicks(Interlocked.Read(ref this.lastTimestampTicks));
                var updatedDelta = TimeSpan.FromTicks(Interlocked.Read(ref this.newDelta));
                if (Math.Abs((delta - updatedDelta).TotalMilliseconds) < 20)
                    continue;

                var time = new DateTimeOffset(ticks: newTimestamp.Ticks + updatedDelta.Ticks,
                                              offset: TimeSpan.Zero);
                var sinceUnixEpoch = time - DateTimeOffset.UnixEpoch;
                long tsMicrosec = newTimestamp.Ticks / TimeSpan.TicksPerMicrosecond;
                long unixMicrosec = sinceUnixEpoch.Ticks / TimeSpan.TicksPerMicrosecond;
                string line = Invariant($"{tsMicrosec}\t{unixMicrosec}");
                await writer.WriteLineAsync(line.AsMemory(), stoppingToken).ConfigureAwait(false);
                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { } catch
            (Exception ex) {
            this.logger.LogError(ex, "SyncWriter background loop failed.");
        }
    }

    static readonly Encoding Utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static string GetSyncPath(EvdevCaptureOptions options)
        => Path.Combine(options.OutputDirectory, Invariant($"{options.SessionStamp}-evdev.sync"));
}