// ReSharper disable InconsistentNaming

namespace EvDev;

public struct input_event {
    public timeval time;
    public input_even_type type;
    public ushort code;
    public uint value;
}

public struct timeval {
    public long tv_sec;
    public long tv_usec;

    public static implicit operator TimeSpan(timeval value)
        => new(checked(value.tv_sec * TimeSpan.TicksPerSecond
                     + value.tv_usec * (TimeSpan.TicksPerMillisecond / 1000)));

    public override string ToString() => ((TimeSpan)this).ToString();
}

public enum input_even_type: ushort {
    EV_SYN = 0x00,
    EV_KEY = 0x01,
    EV_REL = 0x02,
    EV_ABS = 0x03,
    EV_MSC = 0x04,
    EV_SW = 0x05,
    EV_LED = 0x11,
    EV_SND = 0x12,
    EV_REP = 0x14,
    EV_FF = 0x15,
    EV_PWR = 0x16,
    EV_FF_STATUS = 0x17,
}