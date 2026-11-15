using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public sealed class OwnedTransferDealTests
{
    const ulong Module=0x7FF000000000, Owner=0x100000, Primary=0x200000, Copies=0x210000,
        First=0x300000, Loan=0x310000, Copy=0x320000;
    sealed class Memory : IProcessMemory
    {
        public FakeMemory Inner = new();
        public Action<ulong,int>? Before;
        public bool TryRead(ulong at, Span<byte> bytes) { Before?.Invoke(at,bytes.Length); return Inner.TryRead(at,bytes); }
        public bool TryWrite(ulong at, ReadOnlySpan<byte> bytes) => throw new Exception("Read-only discovery wrote");
    }
    static (Memory Memory, TransferDealReader Reader, byte[] Owner, byte[] Copy) Fixture()
    {
        var memory=new Memory();
        BitConverter.GetBytes(Owner).CopyTo(memory.Inner.Map(Module+0x580EA58,8),0);
        var owner=memory.Inner.Map(Owner,0x7D8);
        BitConverter.GetBytes(Module+0x4D90898).CopyTo(owner,0);
        Vector(owner,0x20,Primary,2);
        // Deliberately displaced: the supplementary collection is discovered, not assumed to be at +0x38.
        Vector(owner,0x108,Copies,1);
        var primary=memory.Inner.Map(Primary,16);
        BitConverter.GetBytes(First).CopyTo(primary,0); BitConverter.GetBytes(Loan).CopyTo(primary,8);
        BitConverter.GetBytes(Copy).CopyTo(memory.Inner.Map(Copies,8),0);
        Record(memory.Inner.Map(First,0x90),0x5051CD8,0,0x400000);
        Record(memory.Inner.Map(Loan,0x90),0x4F26178,1,0x410000);
        var copy=memory.Inner.Map(Copy,0x90); Record(copy,0x5051CD8,0,0x400000);
        var profile=OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory,"offsets","27.0.1+2503222-steam.json"));
        return (memory,new(memory,profile,Module),owner,copy);
    }
    static void Record(byte[] raw,uint type,int id,ulong person)
    { BitConverter.GetBytes(Module+type).CopyTo(raw,0);BitConverter.GetBytes(id).CopyTo(raw,0x5C);BitConverter.GetBytes(person).CopyTo(raw,0x38); }
    static void Vector(byte[] raw,int at,ulong begin,int count)
    { BitConverter.GetBytes(begin).CopyTo(raw,at);BitConverter.GetBytes(begin+(ulong)count*8).CopyTo(raw,at+8);BitConverter.GetBytes(begin+(ulong)count*8).CopyTo(raw,at+16); }
    [Fact]
    public void Owned_index_includes_supplementary_deal_without_a_heap_provider()
    { var f=Fixture(); Assert.Equal(new ulong[]{First,Copy},f.Reader.TryReadOwnedCandidates()); }
    [Theory]
    [InlineData("type")][InlineData("person")][InlineData("id")][InlineData("primary_id")][InlineData("manager")][InlineData("empty")]
    public void Invalid_or_unidentified_collection_requires_fallback(string field)
    {
        var f=Fixture();
        switch(field)
        {
            case "type": Array.Clear(f.Copy,0,8);break;
            case "person": f.Copy[0x38]++;break;
            case "id": f.Copy[0x5C]=9;break;
            case "primary_id": f.Memory.Inner.TryWrite(First+0x5C,BitConverter.GetBytes(1));break;
            case "manager": f.Owner[0]++;break;
            case "empty": Vector(f.Owner,0x108,Copies,0);break;
        }
        Assert.Null(f.Reader.TryReadOwnedCandidates());
    }
    [Fact]
    public void Two_valid_supplementary_vectors_are_ambiguous()
    { var f=Fixture();Vector(f.Owner,0x180,Copies,1);Assert.Null(f.Reader.TryReadOwnedCandidates()); }
    [Fact]
    public void Unreadable_potential_collection_cannot_be_treated_as_absent()
    { var f=Fixture();Vector(f.Owner,0x180,0x500000,1);Assert.Null(f.Reader.TryReadOwnedCandidates()); }
    [Theory]
    [InlineData("root")][InlineData("header")][InlineData("record")][InlineData("pointers")]
    public void Mutation_before_publication_requires_fallback(string field)
    {
        var f=Fixture();int records=0,roots=0;
        f.Memory.Before=(at,size)=>
        {
            if(field=="root" && at==Module+0x580EA58 && ++roots==2) f.Memory.Inner.TryWrite(at,BitConverter.GetBytes(0UL));
            if(field=="header" && at==Owner && size==0x7D8 && ++roots==2) f.Owner[0x110]++;
            if(field=="record" && at==Copy && size==0x90 && ++records==2) f.Copy[0x58]++;
            if(field=="pointers" && at==Owner && size==0x7D8 && ++roots==2) f.Memory.Inner.TryWrite(Copies,BitConverter.GetBytes(First));
        };
        Assert.Null(f.Reader.TryReadOwnedCandidates());
    }
    [Fact]
    public void Profile_without_manager_metadata_keeps_existing_heap_fallback()
    {
        var p=OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory,"offsets","27.0.0+2493426-gamepass.json"));
        Assert.Null(new TransferDealReader(new Memory(),p,Module).TryReadOwnedCandidates());
    }
}
