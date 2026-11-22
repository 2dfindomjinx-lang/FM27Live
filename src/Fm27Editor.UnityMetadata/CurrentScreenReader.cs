using Fm27Editor.Core.Memory;

namespace Fm27Editor.UnityMetadata;

public enum ScreenEntityKind { Person, Club, Competition, Nation }
public sealed record CurrentScreenTarget(ScreenEntityKind Kind, int Id);

/// <summary>Reads the game's navigation selection. Never invokes game code or writes memory.</summary>
public sealed class CurrentScreenReader(ScreenMetadata layout, ulong moduleBase)
{
    ulong moduleObject;
    public bool IsPrepared => moduleObject != 0;

    /// <summary>Find the navigation module even on a non-entity page. Cache no selected entity.</summary>
    public void Prepare(IProcessMemory memory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong moduleClass = memory.ReadUInt64(moduleBase + layout.ModuleClassRva);
        if (moduleClass == 0) { moduleObject = 0; return; }
        if (moduleObject != 0 && IsModule(memory, moduleObject, moduleClass)) return;
        moduleObject = 0;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var candidates = HeapVtableScanner.Scan(memory, cancellationToken, moduleClass)
                    .Where(m => IsModule(memory, m.Address, moduleClass)).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                // Never accept an incomplete heap pass or arbitrarily pick one of several modules.
                if (candidates.Length == 1) moduleObject = candidates[0].Address;
                return;
            }
            catch (MemoryAccessException) when (attempt < 1)
            {
                // FM may free a page after region enumeration. Re-enumerate and repeat the
                // complete pass; never hide unreadable pages and assume a unique match.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    bool IsModule(IProcessMemory memory, ulong address, ulong moduleClass)
    {
        try { return memory.ReadUInt64(address) == moduleClass && memory.ReadUInt64(address + (ulong)layout.NativeObjectOffset) != 0; }
        catch (MemoryAccessException) { return false; }
    }

    public CurrentScreenTarget Read(IProcessMemory memory, CancellationToken cancellationToken = default)
    {
        Prepare(memory, cancellationToken);
        if (moduleObject == 0) return null;
        ulong moduleClass = memory.ReadUInt64(moduleBase + layout.ModuleClassRva);
        ulong referenceClass = memory.ReadUInt64(moduleBase + layout.ReferenceValueClassRva);
        if (moduleClass == 0 || referenceClass == 0) return null;
        // A live module on a non-entity page returns nothing, never its previous selection.
        return ReadTarget(moduleObject);

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
                if (kind is null || before != Snapshot(address) || !IsModule(memory, address, moduleClass)) return null;
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
