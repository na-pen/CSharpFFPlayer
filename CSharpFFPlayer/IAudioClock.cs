using System.Runtime.InteropServices;

[Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClock
{
    int GetFrequency(out ulong pu64Frequency);
    int GetPosition(out ulong pu64Position, out long pu64QPCPosition);
    int GetCharacteristics(out uint pdwCharacteristics);
}
