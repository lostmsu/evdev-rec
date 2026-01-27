namespace EvDev;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ZstdSharp;

sealed class EvdevDeviceRecorder: IAsyncDisposable {
    static long collisionSuffixCounter;

    public string DevicePath { get; }

    readonly EvdevCaptureOptions options;

    readonly LibInputMetadataProvider metadataProvider;

    readonly ILogger logger;
    readonly SafeFd fd;
    readonly EvdevMetadata metadata;
    readonly CancellationTokenSource stop = new();
    readonly int inputIndex;
    readonly Task running;

    public EvdevDeviceRecorder(string devicePath,
                               EvdevCaptureOptions options,
                               LibInputMetadataProvider metadataProvider,
                               ILogger logger) {
        this.DevicePath = devicePath ?? throw new ArgumentNullException(nameof(devicePath));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.metadataProvider = metadataProvider ??
                                throw new ArgumentNullException(nameof(metadataProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        this.inputIndex = ParseInputIndex(this.DevicePath);
        this.fd = SafeFd.OpenReadNonBlocking(this.DevicePath, this.logger);
        try {
            this.metadata = EvdevIoctl.TryGetMetadata(this.fd.Value);
        } catch {
            this.fd.Dispose();
            throw;
        }

        this.running = this.RunAsync();
    }

    public event Action<EvdevDeviceRecorder, ReadOnlySpan<input_event>>? OnEvents;

    async Task RunAsync() {
        if (nint.Size != 8)
            throw new PlatformNotSupportedException(
                "This recorder currently assumes linux-x64 (struct input_event is 24 bytes).");

        string? libinputText;
        try {
            libinputText = await this.metadataProvider
                                     .TryGetDeviceBlockAsync(this.DevicePath, this.stop.Token)
                                     .ConfigureAwait(false);
        } catch (OperationCanceledException) when (this.stop.Token.IsCancellationRequested) {
            return;
        }

        await Task.Factory.StartNew(() => this.Run(libinputText), TaskCreationOptions.LongRunning)
                  .ConfigureAwait(false);
    }

    void Run(string? libinputText) {
        int eventSize = Marshal.SizeOf<input_event>();
        byte[] buffer = new byte[eventSize * 1024];

        DateTimeOffset segmentStart = default;
        CompressionStream? segment = null;

        try {
            while (!this.stop.IsCancellationRequested) {
                int read = this.fd.ReadSome(buffer, this.stop.Token);
                if (read <= 0)
                    break;

                var bytes = buffer.AsSpan(0, read);
                var events = MemoryMarshal.Cast<byte, input_event>(bytes);
                this.OnEvents?.Invoke(this, events);

                if (read % eventSize != 0)
                    throw new InvalidProgramException(
                        "Partial input_event read from evdev device.");

                if (segment is not null
                 && DateTimeOffset.UtcNow - segmentStart >= this.options.SegmentDuration) {
                    this.logger.LogInformation("Committing segment for {DevicePath}", this.DevicePath);
                    CloseSegment(segment);
                    segment = null;
                }

                if (segment is null) {
                    segmentStart = DateTimeOffset.UtcNow;
                    segment = this.OpenNewSegment(segmentStart, libinputText);
                }

                segment.Write(bytes);
            }
        } finally {
            if (segment is not null)
                CloseSegment(segment);
        }
    }

    CompressionStream OpenNewSegment(DateTimeOffset segmentStart, string? libinputText) {
        string stamp = segmentStart.ToUniversalTime()
                                   .ToString("yyyyMMdd-HHmmss.fffZ", CultureInfo.InvariantCulture);
        string baseName = Invariant($"{stamp}-input{this.inputIndex}");

        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            // Collisions are handled by FileMode.CreateNew; this suffix makes retries deterministic.
            long suffixCounter = Interlocked.Increment(ref collisionSuffixCounter);
            string suffix = attempt == 0
                ? ""
                : Invariant($"-{Environment.ProcessId:x}-{suffixCounter:x}");
            string dataPath = Path.Combine(this.options.OutputDirectory, baseName + suffix + ".zst");
            string metaPath =
                Path.Combine(this.options.OutputDirectory, baseName + suffix + ".meta.json");

            try {
                this.WriteMetadata(metaPath, segmentStart, libinputText);
            } catch (Exception ex) {
                this.logger.LogWarning(ex, "Unable to write metadata for {DevicePath} ({MetaPath})",
                                       this.DevicePath, metaPath);
            }

            try {
                var file = new FileStream(
                    dataPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1024 * 256,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                this.logger.LogInformation("Recording {DevicePath} -> {DataPath}", this.DevicePath,
                                           dataPath);
                return new(file, level: this.options.ZstdCompressionLevel,
                           leaveOpen: false);
            } catch (IOException) when (File.Exists(dataPath) && attempt < maxAttempts - 1) {
                // try another suffix
            }
        }

        throw new InvalidProgramException($"End of {nameof(this.OpenNewSegment)} reached.");
    }

    static int ParseInputIndex(string devicePath) {
        string name = Path.GetFileName(devicePath);
        if (!name.StartsWith("event", StringComparison.Ordinal))
            throw new ArgumentException($"Unexpected device node name: {name}", nameof(devicePath));

        if (!int.TryParse(name.AsSpan("event".Length), CultureInfo.InvariantCulture,
                          out int index) || index < 0)
            throw new ArgumentException($"Unable to parse evdev index from: {name}",
                                        nameof(devicePath));

        return index;
    }

    static void CloseSegment(CompressionStream compressor) {
        compressor.Flush();
        compressor.Dispose();
    }

    void WriteMetadata(string metaPath, DateTimeOffset segmentStart, string? libinputMeta) {
        var captureMetadata = new CaptureMetadata {
            SegmentStartUtc = segmentStart.UtcDateTime,
            DevicePath = this.DevicePath,
            Evdev = this.metadata,
            Libinput = libinputMeta is null
                ? new LibinputMetadata
                    { Available = false, Warning = "libinput not available or failed to run." }
                : new LibinputMetadata { Available = true, DeviceBlock = libinputMeta },
        };

        using var meta =
            new FileStream(metaPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(meta, captureMetadata,
                                 new JsonSerializerOptions { WriteIndented = true });
        meta.Close();
    }

    public async ValueTask DisposeAsync() {
        await this.stop.CancelAsync().ConfigureAwait(false);
        try {
            await this.running.ConfigureAwait(false);
        } catch (OperationCanceledException) when (this.stop.IsCancellationRequested) {
            // expected on shutdown
        } finally {
            this.fd.Dispose();
        }
    }

    sealed class CaptureMetadata {
        public DateTime SegmentStartUtc { get; init; }
        public string DevicePath { get; init; } = "";
        public EvdevMetadata Evdev { get; init; } = new();
        public LibinputMetadata Libinput { get; init; } = new();
    }

    sealed class LibinputMetadata {
        public bool Available { get; init; }
        public string? Warning { get; init; }
        public string? DeviceBlock { get; init; }

        // TODO: Parse libinput fields into structured JSON and/or include udev properties.
    }
}