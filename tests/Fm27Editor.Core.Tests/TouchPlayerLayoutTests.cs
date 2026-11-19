using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class TouchPlayerLayoutTests
{
    [Theory]
    [InlineData(0x57B2850u, 0x2F8, 0x2E4, 0x1F0, 0x240, 0x209)]
    [InlineData(0x57B88A0u, 0x3F0, 0x3DC, 0x2E8, 0x338, 0x301)]
    public void Reads_identity_and_contract_and_freezes_the_correct_byte(uint vt, int size, int idAt,
        int lastNameAt, int contractAt, int ambitionAt)
    {
        // The larger record has the same playing data but its person and contract fields are later.
        const ulong address = 0x100000, entry = 0x200000, text = 0x300000, contract = 0x400000;
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2503222-touch-mac.json"));
        var memory = new FakeMemory();
        var bytes = memory.Map(address, size);
        TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + vt);
        TestWorld.Set64(bytes, lastNameAt, entry);
        memory.MapNameEntry(entry, text, "Test surname");
        TestWorld.Set64(bytes, contractAt, contract);
        var job = memory.Map(contract, 0x60);
        TestWorld.Set64(job, 0, TestWorld.ModuleBase + 0x57D56D0);
        BitConverter.GetBytes(3200).CopyTo(job, 0x20);
        const ulong city = 0x500000, cityName = 0x510000, motives = 0x600000, motive = 0x610000;
        int cityAt = vt == 0x57B88A0 ? 0x310 : 0x218;
        TestWorld.Set64(bytes, cityAt, city);
        var cityBytes = memory.Map(city, 0x58);
        TestWorld.Set64(cityBytes, 0, TestWorld.ModuleBase + 0x57C5EC0);
        BitConverter.GetBytes(102580).CopyTo(cityBytes, 0xC);
        TestWorld.Set64(cityBytes, 0x18, cityName);
        memory.MapString(cityName, "Glasgow");
        TestWorld.Set64(job, 0x30, motives);
        var list = memory.Map(motives, 16);
        BitConverter.GetBytes(1).CopyTo(list, 0);
        TestWorld.Set64(list, 8, motive);
        TestWorld.Set16(memory.Map(motive, 0x1C), 0x16, 17);
        const ulong offerHeader = 0x620000, offerIds = 0x630000;
        int offersAt = vt == 0x57B88A0 ? 0x328 : 0x230;
        TestWorld.Set64(bytes, offersAt, offerHeader);
        var offerVector = memory.Map(offerHeader, 16);
        TestWorld.Set64(offerVector, 0, offerIds);
        TestWorld.Set64(offerVector, 8, offerIds + 4);
        BitConverter.GetBytes(123456).CopyTo(bytes, idAt);
        int personIndexAt = vt == 0x57B88A0 ? 0x3D8 : 0x2E0;
        BitConverter.GetBytes(8092).CopyTo(bytes, personIndexAt);
        bytes[0xAE] = 76; // displayed as 15; Freeze must keep the raw precision.
        bytes[0xBE] = 85; // a wrong Passing write would change this unrelated attribute.
        int capsAt = vt == 0x57B88A0 ? 0x3C4 : 0x2CC;
        TestWorld.Set16(bytes, capsAt, 95);
        // Mainoo's Touch bytes and the separately recorded FMRTE position ratings.
        new byte[] { 1, 1, 1, 1, 10, 17, 1, 20, 10, 1, 15, 1, 1, 1, 10 }.CopyTo(bytes, 0x98);
        bytes[ambitionAt] = 11;
        bytes[ambitionAt + 3] = 16;
        TestWorld.Set16(bytes, 0x1AC, 164);
        TestWorld.Set16(bytes, 0x1AE, 172);

        var repo = new PlayerRepository(memory, profile, TestWorld.ModuleBase);
        var writer = new PlayerWriter(memory, profile, TestWorld.ModuleBase);
        var player = Assert.IsType<PlayerRecord>(repo.Reload(address));
        Assert.Equal(123456, player.Id);
        Assert.Equal("Test surname", player.DisplayName);
        Assert.Equal(3200, player.Contract?.Wage);
        Assert.Equal("Glasgow", new CityRepository(memory, profile, TestWorld.ModuleBase).CityOf(player)?.Name);
        Assert.Equal(17, Assert.Single(new UnhappinessReader(memory, profile, TestWorld.ModuleBase).Read(player)).Motive);
        Assert.True(new TransferProposalReader(memory, profile, TestWorld.ModuleBase).HasOfferIds(player));
        Assert.Equal(8092u, new PlannedRetirements(memory, profile, TestWorld.ModuleBase).PersonId(player));
        const ulong nation = 0x700000, permitHeader = 0x710000, permitPointers = 0x720000, otherPermit = 0x730000;
        TestWorld.Set64(memory.Map(nation, 8), 0, TestWorld.ModuleBase + profile.Nation!.VtableRva);
        var permitVector = memory.Map(permitHeader, 24);
        TestWorld.Set64(permitVector, 0, permitPointers);
        TestWorld.Set64(permitVector, 8, permitPointers + 8);
        TestWorld.Set64(permitVector, 16, permitPointers + 8);
        TestWorld.Set64(memory.Map(permitPointers, 8), 0, otherPermit);
        var permitRecord = memory.Map(otherPermit, 0x20);
        TestWorld.Set64(permitRecord, 0, nation);
        BitConverter.GetBytes(17102).CopyTo(permitRecord, 8);
        var permitPlan = WorkPermitBatch.Plan(memory, profile, TestWorld.ModuleBase, [player], nation, 2026, _ => "England", permitHeader);
        Assert.Single(permitPlan.Targets);
        Assert.Equal(8092, permitPlan.ExpectedPersons![address]);
        var generation = new GoldenGenerationWriter(memory, profile, TestWorld.ModuleBase).Plan(player);
        Assert.Equal(new GoldenGenerationValues(172, 164, 16, 11), generation.Before);
        Assert.Equal(15, player.Attribute(PlayerAttribute.Passing));
        Assert.Equal(new[] { 1, 1, 1, 10, 17, 1, 20, 10, 1, 15, 1, 1, 1, 10 },
            Enum.GetValues<PlayerPosition>().Select(player.Position));
        var owner = PersonOwner.Of(player, profile);
        Assert.True(owner.IsValid(memory, TestWorld.ModuleBase));
        Assert.Equal(address + (vt == 0x57B88A0 ? 0x308UL : 0x210UL), owner.At(0x210));
        Assert.Equal(address + (vt == 0x57B88A0 ? 0x3D8UL : 0x2E0UL), owner.At(0x2E0));
        var fields = new PlayerFieldAccess(memory, profile, TestWorld.ModuleBase);
        Assert.Equal(95L, fields.Read(player, PlayerField.Caps));
        var frozen = FrozenPlayer.Capture(player, [(PlayerAttribute.Passing, FreezeMode.Fixed)]);
        var expected = bytes.ToArray();
        bytes[0xAE] = 63;
        Assert.Equal(1, frozen.Reconcile(repo, writer));
        Assert.Equal(expected, bytes);

        writer.SetAttribute(player, PlayerAttribute.Passing, 16);
        expected[0xAE] = 0x50; // One-byte FMRTE write captured on Mainoo, from raw 0x4C.
        Assert.Equal(expected, bytes);

        fields.Set(player, PlayerField.Caps, 95, 96);
        TestWorld.Set16(expected, capsAt, 96);
        Assert.Equal(expected, bytes);

        const ulong playerPointers = 0x740000;
        var playerList = memory.Map(TestWorld.ModuleBase + profile.PlayerListRootRva, profile.PlayerListEndOffset + 8);
        TestWorld.Set64(playerList, profile.PlayerListBeginOffset, playerPointers);
        TestWorld.Set64(playerList, profile.PlayerListEndOffset, playerPointers + 8);
        TestWorld.Set64(memory.Map(playerPointers, 8), 0, address);
        using var limited = new VerifiedWriteMemory(memory, profile, TestWorld.ModuleBase);
        var limitedWriter = new PlayerWriter(limited, profile, TestWorld.ModuleBase);
        var current = repo.Reload(address)!;
        limitedWriter.SetAttribute(current, PlayerAttribute.Passing, 17);
        expected[0xAE] = 85;
        var limitedFields = new PlayerFieldAccess(limited, profile, TestWorld.ModuleBase);
        limitedFields.Set(current, PlayerField.Caps, 96, 95);
        TestWorld.Set16(expected, capsAt, 95);
        Assert.Throws<NotSupportedException>(() => limitedWriter.SetPersonality(current, PersonalityAttribute.Ambition, 13));
        Assert.Throws<NotSupportedException>(() => limited.TryWrite(address + 0xAE, [80, 0]));
        Assert.Equal(expected, bytes);

        TestWorld.Set64(playerList, profile.PlayerListEndOffset, playerPointers);
        Assert.Throws<StaleObjectException>(() => limitedFields.Set(current, PlayerField.Caps, 95, 96));
        TestWorld.Set64(playerList, profile.PlayerListEndOffset, playerPointers + 8);

        writer.SetPersonality(player, PersonalityAttribute.Ambition, 13);
        expected[ambitionAt] = 13;
        Assert.Equal(expected, bytes);
        writer.SetPosition(player, PlayerPosition.MidfielderCentral, 12);
        expected[0x9F] = 12;
        Assert.Equal(expected, bytes);
        BitConverter.GetBytes(123457).CopyTo(bytes, idAt);
        int writes = memory.Writes;
        Assert.Throws<StaleObjectException>(() => writer.SetAttribute(player, PlayerAttribute.Passing, 10));
        Assert.Throws<StaleObjectException>(() => frozen.Reconcile(repo, writer));
        Assert.Equal(writes, memory.Writes);
    }
}
