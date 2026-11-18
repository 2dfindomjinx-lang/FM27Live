namespace Fm27Editor.Core.Transfer;

/// <summary>
/// A new contract made as a copy of another must not share what the other owns. Game Pass 27.1.0 (18/11/2026): after the
/// editor signed a free agent on a byte copy of a squad mate's contract, loading another save in the same FM session crashed
/// in ntdll's heap free, as FM destroyed both contracts and freed their lists twice. The owned parts, as FMLibreEditor clears
/// them when it signs a free agent (layout.toml [contract], docs/recon/fm27-object-pools.md): the unhappiness record +0x30,
/// the clauses list +0x68 and competition bonuses list +0x80 (begin, end, capacity), and +0x28, +0x98, +0xA0, which hold
/// pointers in some contracts.
/// </summary>
public static class ContractCopy
{
    public const int Unhappiness = 0x30, Clauses = 0x68, CompetitionBonuses = 0x80;
    static readonly int[] Pointers = [0x28, Unhappiness, 0x98, 0xA0];

    /// <summary>Clears every list and pointer the template's own contract owns.</summary>
    public static void ClearOwned(byte[] contract)
    {
        foreach (int at in Pointers) Array.Clear(contract, at, 8);
        Array.Clear(contract, Clauses, 24);
        Array.Clear(contract, CompetitionBonuses, 24);
    }
}
