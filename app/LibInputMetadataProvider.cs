namespace EvDev;

using System.ComponentModel;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

sealed class LibInputMetadataProvider(ILogger<LibInputMetadataProvider> logger) {
    readonly ILogger<LibInputMetadataProvider> logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    readonly Lock cacheGate = new();
    private DateTimeOffset cachedAtUtc;
    private string? cachedListDevices;
    private Task<string?>? inFlight;
    private bool warnedMissing;

    public async Task<string?> TryGetDeviceBlockAsync(string devicePath, CancellationToken cancel) {
        string? output = await this.TryGetListDevicesAsync(cancel).ConfigureAwait(false);
        if (output is null)
            return null;

        string normalized = devicePath.Trim();

        // libinput list-devices output is block-oriented; keep the matching block verbatim.
        string[] lines = output.Split('\n');
        var block = new List<string>(64);
        var current = new List<string>(64);
        bool anyInBlock = false;

        foreach (string rawLine in lines) {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0) {
                if (anyInBlock) {
                    block = [.. current];
                    break;
                }

                current.Clear();
                anyInBlock = false;
                continue;
            }

            current.Add(line);
            if (line.Contains(normalized, StringComparison.Ordinal))
                anyInBlock = true;
        }

        if (block.Count == 0 && anyInBlock)
            block = [.. current];

        if (block.Count == 0)
            return null;

        return string.Join('\n', block);
    }

    async Task<string?> TryGetListDevicesAsync(CancellationToken cancel) {
        Task<string?> task;
        lock (this.cacheGate) {
            if (this.cachedListDevices is not null && (DateTimeOffset.UtcNow - this.cachedAtUtc) <
                TimeSpan.FromSeconds(10))
                return this.cachedListDevices;

            this.inFlight ??= Task.Run(() => this.FetchListDevicesAsync(cancel), cancel);
            task = this.inFlight;
        }

        try {
            string? stdout = await task.WaitAsync(cancel).ConfigureAwait(false);
            lock (this.cacheGate) {
                if (ReferenceEquals(this.inFlight, task)) this.inFlight = null;

                if (stdout is not null) {
                    this.cachedAtUtc = DateTimeOffset.UtcNow;
                    this.cachedListDevices = stdout;
                }
            }

            return stdout;
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
            return null;
        }
    }

    async Task<string?> FetchListDevicesAsync(CancellationToken cancel) {
        if (cancel.IsCancellationRequested)
            return null;

        var psi = new ProcessStartInfo("libinput", "list-devices") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try {
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            // libinput should be fast; on shutdown we prefer to stop quietly rather than log noisy stack traces.
            string stdout = await proc.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync(cancel).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancel).ConfigureAwait(false);

            if (proc.ExitCode != 0) {
                this.logger.LogWarning("libinput list-devices failed (exit {ExitCode}): {Stderr}",
                                       proc.ExitCode, stderr.Trim());
                return null;
            }

            return stdout;
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 2) {
            if (!this.warnedMissing) {
                this.warnedMissing = true;
                this.logger.LogWarning("libinput not found on PATH; skipping libinput metadata.");
            }

            return null;
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
            return null;
        } catch (Exception ex) {
            this.logger.LogWarning(ex, "Unable to run libinput; skipping libinput metadata.");
            return null;
        } finally {
            lock (this.cacheGate) {
                this.inFlight = null;
            }
        }
    }
}