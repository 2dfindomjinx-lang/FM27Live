using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Staff;

/// <summary>
/// The address check every staff reader and writer shares. A record stores its person part (<see cref="StaffRecord.Address"/>)
/// and its staff part (<see cref="StaffRecord.StaffAddress"/>); both must still belong together and the staff part must still
/// carry its class vtable. On the builds whose staff vector holds person parts the person part carries the person vtable too;
/// on the builds whose vector holds staff objects it does not, because the staff vtable is the object's identity.
/// </summary>
public static class StaffIdentity
{
    public static bool IsCurrent(StaffRecord staff, IProcessMemory memory, ulong moduleBase, StaffOffsets offsets)
    {
        var k = staff.Kind;
        ulong personDelta = offsets.DirectStaffVector ? (ulong)k.DirectPersonDelta : (ulong)k.PersonVectorPersonDelta;
        if (staff.StaffAddress == 0 || staff.Address != staff.StaffAddress + personDelta) return false;
        var head = new byte[8];
        if (!memory.TryRead(staff.StaffAddress, head) || BitConverter.ToUInt64(head) != moduleBase + k.StaffVtableRva) return false;
        if (offsets.DirectStaffVector) return true;
        return memory.TryRead(staff.Address, head) && BitConverter.ToUInt64(head) == moduleBase + k.PersonVtableRva;
    }

    /// <summary>True when <paramref name="vtable"/> is the vtable of one of the profile's staff classes.</summary>
    public static bool IsStaffVtable(ulong vtable, ulong moduleBase, StaffOffsets offsets) =>
        offsets.Kinds.Any(k => vtable == moduleBase + k.StaffVtableRva);

    /// <summary>True when <paramref name="vtable"/> is the person vtable of one of the profile's staff classes.</summary>
    public static bool IsPersonVtable(ulong vtable, ulong moduleBase, StaffOffsets offsets) =>
        offsets.Kinds.Any(k => vtable == moduleBase + k.PersonVtableRva);
}
