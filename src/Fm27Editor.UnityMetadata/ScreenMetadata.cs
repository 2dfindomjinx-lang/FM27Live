using Il2CppDumper;

namespace Fm27Editor.UnityMetadata;

public sealed record ScreenMetadata(ulong ModuleClassRva, ulong ReferenceValueClassRva,
    int EntityOffset, int EntityTypeOffset, int UniqueIdOffset, int ReferenceIdOffset,
    int PersonType, int ClubType, int CompetitionType, int NationType, int NativeObjectOffset);

public static class ScreenMetadataReader
{
    public static ScreenMetadata Read(string assemblyPath, string metadataPath)
    {
        using var metadata = new Metadata(new MemoryStream(File.ReadAllBytes(metadataPath)));
        // Unknown runtime layouts must be reviewed before reading a process.
        if (metadata.Version != 39) throw new NotSupportedException("FM UI metadata version is not supported.");
        using var binary = new PE(new MemoryStream(File.ReadAllBytes(assemblyPath)));
        if (binary.Is32Bit) throw new NotSupportedException("FM UI requires a 64-bit game.");
        binary.SetProperties(metadata.Version, metadata.metadataUsagesCount);
        int methods = metadata.methodDefs.Count(m => m.methodIndex >= 0);
        if (!binary.PlusSearch(methods, metadata.typeDefs.Length, metadata.imageDefs.Length))
            throw new NotSupportedException("FM UI metadata registration was not found.");
        int module = Type("GameWorldTabsAndBookmarksModule", "");
        int reference = Type("ReferenceTypedValue", "SI.Core");
        int enumType = Enumerable.Range(0, metadata.typeDefs.Length).Single(i =>
            metadata.GetStringFromIndex(metadata.typeDefs[i].nameIndex) == "ViewedEntityTypes" &&
            metadata.typeDefs[i].declaringTypeIndex == metadata.typeDefs[module].byvalTypeIndex);
        var targets = new Dictionary<ulong, int>
        {
            [Token(module)] = module,
            [Token(reference)] = reference
        };
        var slots = new Dictionary<int, List<ulong>> { [module] = [], [reference] = [] };
        foreach (var section in binary.GetSectionHelper(methods, metadata.typeDefs.Length, metadata.imageDefs.Length).Data)
        {
            for (ulong pos = section.offset; pos + 8 <= Math.Min(section.offsetEnd, binary.Length); pos += 8)
            {
                binary.Position = pos;
                if (targets.TryGetValue(binary.ReadUInt64(), out int type))
                    slots[type].Add(binary.GetRVA(binary.MapRTVA(pos)));
            }
        }
        return new(slots[module].Distinct().Single(), slots[reference].Distinct().Single(),
            Offset(module, "m_currentViewedEntity"), Offset(module, "m_currentViewedEntityType"),
            Offset(module, "m_currentSecondaryTabObjectUniqueID"), Offset(module, "m_currentViewedEntityReferenceID"),
            Constant(enumType,"Person"), Constant(enumType,"Club"), Constant(enumType,"Competition"), Constant(enumType,"Nation"),
            Offset(Type("Object", "UnityEngine"), "m_CachedPtr"));

        int Type(string name, string ns) => Enumerable.Range(0, metadata.typeDefs.Length).Single(i =>
            metadata.GetStringFromIndex(metadata.typeDefs[i].nameIndex) == name &&
            metadata.GetStringFromIndex(metadata.typeDefs[i].namespaceIndex) == ns);
        ulong Token(int index) => (1ul << 29 | (ulong)metadata.typeDefs[index].byvalTypeIndex << 1) + 1;
        int Field(int type, string name) => Enumerable.Range(metadata.typeDefs[type].fieldStart, metadata.typeDefs[type].field_count)
            .Single(i => metadata.GetStringFromIndex(metadata.fieldDefs[i].nameIndex) == name);
        int Offset(int type, string name)
        {
            int index = Field(type, name);
            int offset = binary.GetFieldOffsetFromIndex(type, index - metadata.typeDefs[type].fieldStart, index, false, false);
            if (offset < 16 || offset > 8192) throw new InvalidDataException("FM UI member offset is invalid.");
            return offset;
        }
        int Constant(int type, string name)
        {
            if (!metadata.GetFieldDefaultValueFromIndex(Field(type,name), out var value) || value.dataIndex < 0)
                throw new InvalidDataException("FM UI enum value is missing.");
            metadata.Position = metadata.GetDefaultValueFromIndex(value.dataIndex);
            return metadata.ReadCompressedInt32();
        }
    }
}
