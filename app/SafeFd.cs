namespace EvDev;

using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

sealed class SafeFd: IDisposable {
    public int Value { get; private set; }
    readonly ILogger logger;
    readonly string devicePath;

    private SafeFd(int value, string devicePath, ILogger logger) {
        this.Value = value;
        this.devicePath = devicePath;
        this.logger = logger;
    }

    public static SafeFd OpenReadNonBlocking(string path, ILogger logger) {
        int fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to open {path}");
        return new SafeFd(fd, path, logger);
    }

    public int ReadSome(byte[] buffer, CancellationToken cancel) {
        // Poll in short intervals so shutdown doesn't hang on blocked reads.
        while (!cancel.IsCancellationRequested) {
            var pfd = new PollFd { Fd = this.Value, Events = POLLIN };
            int prc = poll(ref pfd, 1, 500);
            if (prc < 0) {
                int err = Marshal.GetLastWin32Error();
                if (err == EINTR)
                    continue;
                throw new Win32Exception(err, "poll failed");
            }

            if (prc == 0)
                continue;

            if ((pfd.Revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
                this.logger.LogInformation(
                    "Device poll ended (revents=0x{Revents:x}); stopping recorder for {DevicePath}",
                    pfd.Revents, this.devicePath);
                return 0;
            }

            if ((pfd.Revents & POLLIN) == 0)
                continue;

            unsafe {
                fixed (byte* ptr = buffer) {
                    nint rc = read(this.Value, ptr, (nuint)buffer.Length);
                    if (rc == 0)
                        return 0;

                    if (rc < 0) {
                        int err = Marshal.GetLastWin32Error();
                        if (err is EAGAIN or EWOULDBLOCK or EINTR)
                            continue;

                        // When unplugging, evdev reads commonly fail with ENODEV.
                        this.logger.LogWarning(
                            "Device read failed (errno {Err}); stopping recorder for {DevicePath}",
                            err, this.devicePath);
                        return 0;
                    }

                    return (int)rc;
                }
            }
        }

        return 0;
    }

    public void Dispose() {
        int fd = this.Value;
        if (fd >= 0) {
            this.Value = -1;
            _ = close(fd);
        }
    }

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    /// <summary>
    /// close descriptor on execXX calls
    /// </summary>
    private const int O_CLOEXEC = 0x80000;

    private const short POLLIN = 0x0001;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;

    private const int EAGAIN = 11;
    private const int EINTR = 4;
    private const int EWOULDBLOCK = 11;
    internal const int EACCES = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
                           int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern unsafe nint read(int fd, void* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern int poll(ref PollFd fds, nuint nfds, int timeout);
}