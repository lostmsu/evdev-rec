namespace EvDev;

using System.Collections.Concurrent;
using System.ComponentModel;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

sealed class EvdevCaptureService(
    EvdevCaptureOptions options,
    SyncWriter syncWriter,
    LibInputMetadataProvider metadataProvider,
    ILogger<EvdevCaptureService> logger): BackgroundService {
    readonly ConcurrentDictionary<string, EvdevDeviceRecorder> recorders =
        new(StringComparer.Ordinal);

    private int loggedInitialDeviceList;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        logger.LogInformation("output: {OutputDirectory}/{Timestamp}",
                              options.OutputDirectory, options.SessionStamp);
        Directory.CreateDirectory(options.OutputDirectory);

        using var watcher = new FileSystemWatcher(options.DevicesDirectory) {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            Filter = "event*",
            EnableRaisingEvents = true,
        };

        watcher.Created += async (_, e) => {
            logger.LogInformation("Device node appeared: {DevicePath}", e.FullPath);
            await this.TryStartRecorderAsync(e.FullPath, stoppingToken).ConfigureAwait(false);
        };
        watcher.Renamed += async (_, e) => {
            logger.LogInformation("Device node appeared (rename): {DevicePath}", e.FullPath);
            await this.TryStartRecorderAsync(e.FullPath, stoppingToken).ConfigureAwait(false);
        };
        watcher.Deleted += async (_, e) => {
            logger.LogDebug("Device node removed: {DevicePath}", e.FullPath);
            bool wasStopped = await this.StopRecorder(e.FullPath).ConfigureAwait(false);
            logger.LogInformation("Stopped recorder for removed {DevicePath}: {Stopped}",
                                  e.FullPath, wasStopped);
        };
        watcher.Error += async (_, args) => {
            logger.LogError(args.GetException(), "FileSystemWatcher error; forcing rescan.");
            await Task.Delay(TimeSpan.FromMicroseconds(1)).ConfigureAwait(false);
            await this.RescanAsync(stoppingToken).ConfigureAwait(false);
        };

        await this.RescanAsync(stoppingToken).ConfigureAwait(false);

        using var rescanTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try {
            while (await rescanTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await this.RescanAsync(stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // normal shutdown
        } finally {
            await Task.WhenAll(this.recorders.Values.Select(async r => {
                logger.LogDebug("Stopping recorder for {DevicePath}", r.DevicePath);
                await r.DisposeAsync().ConfigureAwait(false);
                logger.LogInformation("Stopped recorder for {DevicePath}", r.DevicePath);
            }));
            this.recorders.Clear();
        }
    }

    async Task RescanAsync(CancellationToken stoppingToken) {
        string[] candidates = Directory.GetFiles(options.DevicesDirectory, "event*");
        Array.Sort(candidates, StringComparer.Ordinal);

        if (Interlocked.Exchange(ref this.loggedInitialDeviceList, 1) == 0) {
            logger.LogInformation("Found {Count} evdev nodes: {Devices}", candidates.Length,
                                  string.Join(", ", candidates));
        }

        await Task
              .WhenAll(candidates.Select(path => this.TryStartRecorderAsync(path, stoppingToken)))
              .ConfigureAwait(false);

        // Stop recorders for devices that vanished (udev removes nodes on unplug).
        await Task.WhenAll(this.recorders.Keys.Where(path => !File.Exists(path))
                               .Select(async path => {
                                   logger.LogInformation(
                                       "Device node missing on rescan: {DevicePath}", path);
                                   await this.StopRecorder(path).ConfigureAwait(false);
                               })).ConfigureAwait(false);
    }

    async Task TryStartRecorderAsync(string devicePath, CancellationToken stoppingToken) {
        if (!devicePath.StartsWith(options.DevicesDirectory, StringComparison.Ordinal)) {
            logger.LogWarning("Ignoring device path outside of devices directory: {DevicePath}",
                              devicePath);
            return;
        }

        if (!Path.GetFileName(devicePath).StartsWith("event", StringComparison.Ordinal))
            return;

        if (this.recorders.ContainsKey(devicePath)) {
            logger.LogDebug("Recorder already running for {DevicePath}", devicePath);
            return;
        }

        // Try opening early to avoid racing on Created events for non-openable nodes.
        EvdevDeviceRecorder recorder;
        try {
            LogLevel accessErrorLogLevel = LogLevel.Warning;
            retry:
            try {
                recorder = new EvdevDeviceRecorder(
                    devicePath: devicePath,
                    options,
                    metadataProvider,
                    logger);
            } catch (Win32Exception ex) when (ex.NativeErrorCode == SafeFd.EACCES) {
                if (stoppingToken.IsCancellationRequested) return;

                logger.Log(
                    accessErrorLogLevel,
                    ex,
                    "Insufficient permissions to open device node {DevicePath}; " +
                    "ensure the capture service has access to evdev nodes.",
                    devicePath);
                accessErrorLogLevel = LogLevel.Debug;
                await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);

                goto retry;
            }

            recorder.OnEvents += (_, events) => { syncWriter.NotifyEvent(events[^1].time); };
        } catch (Exception ex) {
            logger.LogWarning(ex, "Unable to initialize recorder for {DevicePath}", devicePath);
            return;
        }

        if (!this.recorders.TryAdd(devicePath, recorder)) {
            await recorder.DisposeAsync().ConfigureAwait(false);
        } else {
            logger.LogDebug("Added recorder for {DevicePath}", devicePath);
        }
    }

    async Task<bool> StopRecorder(string devicePath) {
        bool wasRemoved = this.recorders.TryRemove(devicePath, out var recorder);
        if (wasRemoved)
            await recorder.DisposeAsync().ConfigureAwait(false);
        return wasRemoved;
    }
}