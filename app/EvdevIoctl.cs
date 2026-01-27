namespace EvDev;

using System.Runtime.InteropServices;
using System.Text;

sealed record EvdevMetadata
{
    public string? Name { get; init; }
    public string? Phys { get; init; }
    public string? Uniq { get; init; }
    public int? DriverVersion { get; init; }
    public EvdevInputId? Id { get; init; }
}

sealed record EvdevInputId
{
    public ushort BusType { get; init; }
    public ushort Vendor { get; init; }
    public ushort Product { get; init; }
    public ushort Version { get; init; }
}

public enum Clock: int {
    CLOCK_REALTIME = 0,
    CLOCK_MONOTONIC = 1,
    CLOCK_MONOTONIC_RAW = 4,
    BLOCK_BOOTTIME = 7,
    CLOCK_TAI = 11,
}

static class EvdevIoctl
{
    // Based on linux/input.h and asm-generic/ioctl.h

    private const int IOC_NRBITS = 8;
    private const int IOC_TYPEBITS = 8;
    private const int IOC_SIZEBITS = 14;
    private const int IOC_DIRBITS = 2;

    private const int IOC_NRSHIFT = 0;
    private const int IOC_TYPESHIFT = IOC_NRSHIFT + IOC_NRBITS;
    private const int IOC_SIZESHIFT = IOC_TYPESHIFT + IOC_TYPEBITS;
    private const int IOC_DIRSHIFT = IOC_SIZESHIFT + IOC_SIZEBITS;

    private const int IOC_NONE = 0;
    private const int IOC_WRITE = 1;
    private const int IOC_READ = 2;

    static ulong IOC(int dir, int type, int nr, int size)
        => ((ulong)dir << IOC_DIRSHIFT)
         | ((ulong)type << IOC_TYPESHIFT)
         | ((ulong)nr << IOC_NRSHIFT)
         | ((ulong)size << IOC_SIZESHIFT);

    static ulong IoctlRead(int type, int nr, int size) => IOC(IOC_READ, type, nr, size);
    static ulong IoctlWrite(int type, int nr, int size) => IOC(IOC_WRITE, type, nr, size);

    private const int EVIOCGVERSION = 0x01;
    private const int EVIOCGID = 0x02;
    private const int EVIOCGNAME = 0x06;
    private const int EVIOCGPHYS = 0x07;
    private const int EVIOCGUNIQ = 0x08;
    private const int EVIOCSCLOCKID = 0xA0;

    public static EvdevMetadata TryGetMetadata(int fd)
    {
        var metadata = new EvdevMetadata
        {
            DriverVersion = TryGetInt(fd, IoctlRead('E', EVIOCGVERSION, sizeof(int))),
            Name = TryGetString(fd, IoctlRead('E', EVIOCGNAME, 256), 256),
            Phys = TryGetString(fd, IoctlRead('E', EVIOCGPHYS, 256), 256),
            Uniq = TryGetString(fd, IoctlRead('E', EVIOCGUNIQ, 256), 256),
            Id = TryGetInputId(fd),
        };

        return metadata;
    }

    public static bool TrySetClockId(int fd, Clock clock, out int errno) {
        unsafe {
            int value = (int)clock;
            int rc = ioctl(fd, IoctlWrite('E', EVIOCSCLOCKID, sizeof(int)), new IntPtr(&value));
            if (rc == 0) {
                errno = 0;
                return true;
            }

            errno = Marshal.GetLastWin32Error();
            return false;
        }
    }

    static int? TryGetInt(int fd, ulong request)
    {
        unsafe
        {
            int value = 0;
            int rc = ioctl(fd, request, new IntPtr(&value));
            return rc == 0 ? value : null;
        }
    }

    static string? TryGetString(int fd, ulong request, int size)
    {
        byte[] buffer = new byte[size];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                int rc = ioctl(fd, request, new IntPtr(ptr));
                if (rc != 0)
                    return null;
            }
        }

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
            end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    static EvdevInputId? TryGetInputId(int fd)
    {
        const int size = 8;
        byte[] buffer = new byte[size];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                int rc = ioctl(fd, IoctlRead('E', EVIOCGID, size), new IntPtr(ptr));
                if (rc != 0)
                    return null;
            }
        }

        return new EvdevInputId
        {
            BusType = BitConverter.ToUInt16(buffer, 0),
            Vendor = BitConverter.ToUInt16(buffer, 2),
            Product = BitConverter.ToUInt16(buffer, 4),
            Version = BitConverter.ToUInt16(buffer, 6),
        };
    }

    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(int fd, ulong request, IntPtr argp);
}
