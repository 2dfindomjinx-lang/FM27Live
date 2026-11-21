using Fm27Editor.Core.Memory;

namespace Fm27Editor.UnityMetadata;

public enum ScreenEntityKind { Person, Club, Competition, Nation }
public sealed record CurrentScreenTarget(ScreenEntityKind Kind, int Id);

/// <summary>Reads the game's navigation selection. Never invokes game code or writes memory.</summary>
public sealed class CurrentScreenReader(ScreenMetadata layout, ulong moduleBase)
{
    ulong moduleObject;

    public CurrentScreenTarget Read(IProcessMemory memory)
    {
        ulong moduleClass = memory.ReadUInt64(moduleBase + layout.ModuleClassRva);
        ulong referenceClass = memory.ReadUInt64(moduleBase + layout.ReferenceValueClassRva);
        if (moduleClass == 0 || referenceClass == 0) return null;
        // A cached, live module on a non-entity screen returns nothing, never a previous selection.
        if (moduleObject != 0 && IsModule(moduleObject)) return ReadTarget(moduleObject);
        moduleObject = 0;
        var candidates = HeapVtableScanner.Scan(memory, moduleClass)
            .Where(m => IsModule(m.Address))
            .Select(m => (m.Address, Target: ReadTarget(m.Address)))
            .Where(m => m.Target is not null).ToArray();
        // Multiple live-looking modules are ambiguous; do not choose an arbitrary address.
        if (candidates.Length != 1) return null;
        moduleObject = candidates[0].Address;
        return ReadTarget(moduleObject);

        bool IsModule(ulong address)
        {
            try { return memory.ReadUInt64(address) == moduleClass && memory.ReadUInt64(address + (ulong)layout.NativeObjectOffset) != 0; }
            catch (MemoryAccessException) { return false; }
        }
        CurrentScreenTarget ReadTarget(ulong address)
        {
            try
            {
                var before = Snapshot(address);
                if (before.Entity == 0 || memory.ReadUInt64(before.Entity) != referenceClass || before.Id <= 0 || before.ReferenceId == 0) return null;
                ScreenEntityKind? kind = before.Type == layout.PersonType ? ScreenEntityKind.Person :
                    before.Type == layout.ClubType ? ScreenEntityKind.Club :
                    before.Type == layout.CompetitionType ? ScreenEntityKind.Competition :
                    before.Type == layout.NationType ? ScreenEntityKind.Nation : null;
                if (kind is null || before != Snapshot(address) || !IsModule(address)) return null;
                return new(kind.Value, before.Id);
            }
            catch (MemoryAccessException) { return null; }
        }
        (ulong Entity, int Type, int Id, uint ReferenceId) Snapshot(ulong address) =>
            (memory.ReadUInt64(address + (ulong)layout.EntityOffset),
             BitConverter.ToInt32(memory.Read(address + (ulong)layout.EntityTypeOffset, 4)),
             BitConverter.ToInt32(memory.Read(address + (ulong)layout.UniqueIdOffset, 4)),
             BitConverter.ToUInt32(memory.Read(address + (ulong)layout.ReferenceIdOffset, 4)));
    }
}
