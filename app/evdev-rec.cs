using EvDev;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var parsed = Cli.Parse(args);
if (parsed.Kind is CliResultKind.SHOW_HELP) {
    Console.WriteLine(Cli.HelpText);
    return;
}

string outputDirectory = parsed.OutputDirectory!;
string sessionStamp = parsed.SessionStamp!;

using var host = Host.CreateDefaultBuilder(args)
                     .UseSystemd()
                     .ConfigureLogging(logging => {
                         logging.ClearProviders();
                         logging.AddSystemdConsole();
                         logging.AddSimpleConsole(options => {
                             options.SingleLine = true;
                             options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                         });
                     })
                     .ConfigureServices(services => {
                         services.AddSingleton(
                             new EvdevCaptureOptions(outputDirectory, sessionStamp));
                         services.AddSingleton<LibInputMetadataProvider>();
                         services.AddSingleton<SyncWriter>();
                         services.AddHostedService(sp => sp.GetRequiredService<SyncWriter>());
                         services.AddHostedService<EvdevCaptureService>();
                     })
                     .Build();

await host.RunAsync();

namespace EvDev {
    static class Cli {
        public static string HelpText =>
            """
            Usage:
              evdev-rec --output <output_directory>

            Options:
              --dir, --output, -o   Output directory (required)
              --help, -h           Show help
            """;

        public static CliResult Parse(string[] args) {
            if (args.Any(a => a is "--help" or "-h"))
                return new CliResult { Kind = CliResultKind.SHOW_HELP };

            string? outputDirectory = null;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] is "--dir" or "--output" or "-o") {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Missing value for --dir / --output / -o.");
                    outputDirectory = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory required. Use --dir <dir> (or --help).");

            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            return new CliResult {
                Kind = CliResultKind.RUN,
                OutputDirectory = outputDirectory,
                SessionStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss.fffZ"),
            };
        }
    }

    enum CliResultKind {
        RUN,
        SHOW_HELP,
    }

    sealed class CliResult {
        public CliResultKind Kind { get; init; }
        public string? OutputDirectory { get; init; }
        public string? SessionStamp { get; init; }
    }

    sealed record EvdevCaptureOptions(string OutputDirectory, string SessionStamp) {
        public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromMinutes(15);
        public int ZstdCompressionLevel { get; init; } = 3;
        public string DevicesDirectory { get; init; } = "/dev/input";
    }
}