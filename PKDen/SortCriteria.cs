using System;
using System.Collections.Generic;
using PKHeX.Core;

namespace PKDen;

/// <summary>
/// Available sorting criteria for Home storage Pokémon.
/// </summary>
public enum DenSortMode
{
    Species,
    SpeciesReverse,
    Level,
    LevelReverse,
    Alphabetical,
    Type,
    NationalDex,
    Shiny,
    IsEgg,
    HeldItem,
    Nature,
    Ability,
    IVTotal,
    IVTotalReverse,
    OriginalTrainer,
    Random,
}

/// <summary>
/// Provides comparison functions for sorting PKM by various criteria.
/// </summary>
public static class DenSortComparers
{
    public static Comparison<PKM> GetComparison(DenSortMode mode) => mode switch
    {
        DenSortMode.Species => (a, b) => CompareSpecies(a, b),
        DenSortMode.SpeciesReverse => (a, b) => CompareSpecies(b, a),
        DenSortMode.Level => (a, b) => a.CurrentLevel.CompareTo(b.CurrentLevel),
        DenSortMode.LevelReverse => (a, b) => b.CurrentLevel.CompareTo(a.CurrentLevel),
        DenSortMode.Alphabetical => (a, b) => string.Compare(GetSpeciesName(a), GetSpeciesName(b), StringComparison.OrdinalIgnoreCase),
        DenSortMode.Type => (a, b) => CompareType(a, b),
        DenSortMode.NationalDex => (a, b) => a.Species.CompareTo(b.Species),
        DenSortMode.Shiny => (a, b) =>
        {
            int sa = a.IsShiny ? 0 : 1;
            int sb = b.IsShiny ? 0 : 1;
            int cmp = sa.CompareTo(sb);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.IsEgg => (a, b) =>
        {
            int ea = a.IsEgg ? 0 : 1;
            int eb = b.IsEgg ? 0 : 1;
            int cmp = ea.CompareTo(eb);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.HeldItem => (a, b) =>
        {
            int cmp = a.HeldItem.CompareTo(b.HeldItem);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.Nature => (a, b) =>
        {
            int cmp = a.Nature.CompareTo(b.Nature);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.Ability => (a, b) =>
        {
            int cmp = a.Ability.CompareTo(b.Ability);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.IVTotal => (a, b) =>
        {
            int ivA = GetIVTotal(a);
            int ivB = GetIVTotal(b);
            int cmp = ivA.CompareTo(ivB);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.IVTotalReverse => (a, b) =>
        {
            int ivA = GetIVTotal(a);
            int ivB = GetIVTotal(b);
            int cmp = ivB.CompareTo(ivA);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.OriginalTrainer => (a, b) =>
        {
            int cmp = string.Compare(a.OriginalTrainerName, b.OriginalTrainerName, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : CompareSpecies(a, b);
        },
        DenSortMode.Random => (_, _) => Random.Shared.Next(-1, 2),
        _ => (a, b) => CompareSpecies(a, b),
    };

    private static int CompareSpecies(PKM a, PKM b)
    {
        int cmp = a.Species.CompareTo(b.Species);
        if (cmp != 0)
            return cmp;
        cmp = a.Form.CompareTo(b.Form);
        if (cmp != 0)
            return cmp;
        return a.Gender.CompareTo(b.Gender);
    }

    private static int CompareType(PKM a, PKM b)
    {
        var piA = a.PersonalInfo;
        var piB = b.PersonalInfo;
        int cmp = piA.Type1.CompareTo(piB.Type1);
        if (cmp != 0)
            return cmp;
        cmp = piA.Type2.CompareTo(piB.Type2);
        return cmp != 0 ? cmp : CompareSpecies(a, b);
    }

    private static int GetIVTotal(PKM pk)
    {
        return pk.IV_HP + pk.IV_ATK + pk.IV_DEF + pk.IV_SPA + pk.IV_SPD + pk.IV_SPE;
    }

    private static string GetSpeciesName(PKM pk)
    {
        var strings = GameInfo.Strings;
        if (pk.Species < strings.specieslist.Length)
            return strings.specieslist[pk.Species];
        return pk.Species.ToString();
    }

    /// <summary>
    /// Returns user-friendly display names for each sort mode.
    /// </summary>
    public static IReadOnlyList<string> GetSortModeNames() =>
    [
        "Species (A→Z)",
        "Species (Z→A)",
        "Level (Low→High)",
        "Level (High→Low)",
        "Alphabetical",
        "Type",
        "National Dex #",
        "Shiny First",
        "Eggs First",
        "Held Item",
        "Nature",
        "Ability",
        "IV Total (Low→High)",
        "IV Total (High→Low)",
        "Original Trainer",
        "Random",
    ];
}
