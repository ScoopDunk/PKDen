using System;
using System.Collections.Generic;
using System.IO;
using PKHeX.Core;

namespace PKDen;

/// <summary>
/// Encapsulates pcdata.bin format detection, parsing, and writing.
///
/// pcdata.bin is a flat concatenation of <c>SIZE_BOXSLOT × SlotCount</c> bytes — the byte-exact
/// contents of <see cref="SaveFile.GetPCBinary"/>. There is no header, no magic bytes, and no
/// metadata — just encrypted PKM slots back-to-back. The file size alone tells us which
/// generation it came from in nearly all cases (only SwSh-vs-SV needs content inspection).
///
/// Implementation note: PKHeX's PKM constructors call <c>DecryptIfEncrypted*</c> internally,
/// so we can pass encrypted slot bytes directly to <c>new PK4(memory)</c>, etc. — the constructor
/// auto-decrypts. Same for write: <c>WriteEncryptedDataStored/Party</c> handles re-encryption.
/// All sizes verified against PKHeX source; all formats empirically validated by decrypting
/// uploaded sample files.
/// </summary>
public static class PCDataBinSupport
{
    /// <summary>
    /// One entry in the size lookup table. The <see cref="Construct"/> and <see cref="Encrypt"/>
    /// delegates wrap PKM construction/serialization so the call sites in <see cref="ReadEntities"/>
    /// and <see cref="WriteEntities"/> stay format-agnostic.
    /// </summary>
    public sealed class FormatSpec
    {
        public required int FileSize { get; init; }
        public required int SlotSize { get; init; }   // Bytes per slot in the file
        public required int SlotCount { get; init; }
        public required string Label { get; init; }    // Human-readable, shown in dialogs
        public required Type PKMType { get; init; }
        public required Func<byte[], PKM> Construct { get; init; }    // Builds a PKM from a slot's bytes (auto-decrypts)
        public required Action<PKM, byte[]> Encrypt { get; init; }    // Writes encrypted bytes into the destination buffer
        // For Gen 9 ZA: file slot is 408 bytes but only the first 344 are PA9 data.
        public int DecryptSliceSize { get; init; }
    }

    /// <summary>All retail save layouts known to produce a clean pcdata.bin.</summary>
    public static IReadOnlyList<FormatSpec> KnownFormats => _formats;

    private static readonly List<FormatSpec> _formats = BuildTable();

    private static List<FormatSpec> BuildTable()
    {
        var table = new List<FormatSpec>();

        // Gen 1 — plaintext PK1 list-format slots. Use PokeList1.ReadFromSingle / WrapSingle
        // (the standard PK1(Memory<byte>) constructor expects raw stored data, not list-format).
        table.Add(new FormatSpec
        {
            FileSize = 16560, SlotSize = 69, SlotCount = 240,
            Label = "Gen 1 International (R/B/Y)", PKMType = typeof(PK1),
            Construct = data => PokeList1.ReadFromSingle(data),
            Encrypt = (pk, dest) => { PokeList1.WrapSingle((PK1)pk, dest); },
        });
        table.Add(new FormatSpec
        {
            FileSize = 14160, SlotSize = 59, SlotCount = 240,
            Label = "Gen 1 Japanese (R/G/B/Y/Pi)", PKMType = typeof(PK1),
            Construct = data => PokeList1.ReadFromSingle(data),
            Encrypt = (pk, dest) => { PokeList1.WrapSingle((PK1)pk, dest); },
        });

        // Gen 2 — plaintext PK2 list-format slots.
        table.Add(new FormatSpec
        {
            FileSize = 20440, SlotSize = 73, SlotCount = 280,
            Label = "Gen 2 International (G/S/C)", PKMType = typeof(PK2),
            Construct = data => PokeList2.ReadFromSingle(data),
            Encrypt = (pk, dest) => { PokeList2.WrapSingle((PK2)pk, dest); },
        });
        table.Add(new FormatSpec
        {
            FileSize = 17010, SlotSize = 63, SlotCount = 270,
            Label = "Gen 2 Japanese (G/S/C)", PKMType = typeof(PK2),
            Construct = data => PokeList2.ReadFromSingle(data),
            Encrypt = (pk, dest) => { PokeList2.WrapSingle((PK2)pk, dest); },
        });

        // Gen 3 — encrypted PK3, 80 bytes/slot. Constructor auto-decrypts.
        table.Add(new FormatSpec
        {
            FileSize = 33600, SlotSize = 80, SlotCount = 420,
            Label = "Gen 3 (RSE/FRLG/E)", PKMType = typeof(PK3),
            Construct = data => new PK3(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 4 (DPP/HGSS): 18×30=540, 136 bytes/slot.
        table.Add(new FormatSpec
        {
            FileSize = 73440, SlotSize = 136, SlotCount = 540,
            Label = "Gen 4 (DPP/HGSS)", PKMType = typeof(PK4),
            Construct = data => new PK4(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 5 (BW/BW2): 24×30=720, 136 bytes/slot. Same per-slot as Gen 4 but different total.
        table.Add(new FormatSpec
        {
            FileSize = 97920, SlotSize = 136, SlotCount = 720,
            Label = "Gen 5 (BW/BW2)", PKMType = typeof(PK5),
            Construct = data => new PK5(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 6 (XY/ORAS): 31×30=930, 232 bytes/slot.
        table.Add(new FormatSpec
        {
            FileSize = 215760, SlotSize = 232, SlotCount = 930,
            Label = "Gen 6 (XY/ORAS)", PKMType = typeof(PK6),
            Construct = data => new PK6(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 7 (SuMo/USUM): 32×30=960, 232 bytes/slot.
        table.Add(new FormatSpec
        {
            FileSize = 222720, SlotSize = 232, SlotCount = 960,
            Label = "Gen 7 (SuMo/USUM)", PKMType = typeof(PK7),
            Construct = data => new PK7(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 7b LGPE: 40×25=1000, 260 bytes/slot (party-sized).
        table.Add(new FormatSpec
        {
            FileSize = 260000, SlotSize = 260, SlotCount = 1000,
            Label = "Gen 7b (Let's Go P/E)", PKMType = typeof(PB7),
            Construct = data => new PB7(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataParty(dest),
        });

        // Gen 8 SwSh: 32×30=960, 344 bytes/slot. Disambiguated from Gen 9 SV by byte 0x11F.
        table.Add(new FormatSpec
        {
            FileSize = 330240, SlotSize = 344, SlotCount = 960,
            Label = "Gen 8 SwSh", PKMType = typeof(PK8),
            Construct = data => new PK8(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataParty(dest),
        });

        // Gen 8 BDSP: 40×30=1200, 344 bytes/slot.
        table.Add(new FormatSpec
        {
            FileSize = 412800, SlotSize = 344, SlotCount = 1200,
            Label = "Gen 8 BDSP", PKMType = typeof(PB8),
            Construct = data => new PB8(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataParty(dest),
        });

        // Gen 8 LA: 32×30=960, 360 bytes/slot stored.
        table.Add(new FormatSpec
        {
            FileSize = 345600, SlotSize = 360, SlotCount = 960,
            Label = "Gen 8 Legends Arceus", PKMType = typeof(PA8),
            Construct = data => new PA8(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataStored(dest),
        });

        // Gen 9 SV: 32×30=960, 344 bytes/slot. Same total as SwSh — content disambiguator picks PK9.
        table.Add(new FormatSpec
        {
            FileSize = 330240, SlotSize = 344, SlotCount = 960,
            Label = "Gen 9 SV", PKMType = typeof(PK9),
            Construct = data => new PK9(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataParty(dest),
        });

        // Gen 9 ZA: 32×30=960, 408 bytes/slot. The 408 = 344 PA9 + 1 presence flag + 63 zero gap.
        // Decrypt only the first 344 bytes (the PKHeX gap is layout filler).
        table.Add(new FormatSpec
        {
            FileSize = 391680, SlotSize = 408, SlotCount = 960,
            Label = "Gen 9 Legends Z-A", PKMType = typeof(PA9),
            Construct = data => new PA9(data),
            Encrypt = (pk, dest) => pk.WriteEncryptedDataParty(dest),
            DecryptSliceSize = 344,
        });

        return table;
    }

    /// <summary>
    /// Identifies the format of a pcdata.bin file by size alone, then resolves SwSh/SV ambiguity by content inspection.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="error">Set to a human-readable error message if detection fails.</param>
    /// <returns>The matching <see cref="FormatSpec"/>, or null if no known format fits.</returns>
    public static FormatSpec? DetectFormat(string filePath, out string? error)
    {
        error = null;
        long fileSize;
        try { fileSize = new FileInfo(filePath).Length; }
        catch (Exception ex) { error = $"Could not read file size: {ex.Message}"; return null; }

        // Gather all formats matching the file size
        var candidates = new List<FormatSpec>();
        foreach (var fmt in _formats)
            if (fmt.FileSize == fileSize)
                candidates.Add(fmt);

        if (candidates.Count == 0)
        {
            error = $"File size {fileSize:N0} bytes does not match any known pcdata.bin format.\n\n" +
                    "Known sizes include 16,560 (Gen 1 INT), 33,600 (Gen 3), 73,440 (Gen 4), " +
                    "97,920 (Gen 5), 215,760 (Gen 6), 222,720 (Gen 7), 330,240 (Gen 8 SwSh / Gen 9 SV), " +
                    "412,800 (BDSP), 345,600 (Legends Arceus), and 391,680 (Legends Z-A).";
            return null;
        }

        if (candidates.Count == 1)
            return candidates[0];

        // Size collision case (currently only 330,240 = SwSh OR SV).
        // Disambiguate by scanning up to 20 non-empty slots — if ANY has the Obedience Level
        // byte (0x11F) set non-zero, it's PK9 (SV) since PK8 (SwSh) and PB8 (BDSP) never set it.
        // Empirically verified across 4080+ slots in the uploaded HOME living dex samples.
        try
        {
            byte[] data = File.ReadAllBytes(filePath);
            FormatSpec? swshSpec = null;
            FormatSpec? svSpec = null;
            foreach (var c in candidates)
            {
                if (c.PKMType == typeof(PK8)) swshSpec = c;
                else if (c.PKMType == typeof(PK9)) svSpec = c;
            }
            if (swshSpec is null || svSpec is null) return candidates[0];

            int slotSize = swshSpec.SlotSize;
            int scanLimit = 20;
            int scanned = 0;
            for (int i = 0; i + slotSize <= data.Length && scanned < scanLimit; i += slotSize)
            {
                bool empty = true;
                for (int b = 0; b < 8; b++) if (data[i + b] != 0) { empty = false; break; }
                if (empty) continue;

                byte[] copy = new byte[slotSize];
                Array.Copy(data, i, copy, 0, slotSize);
                try { PokeCrypto.Decrypt8(copy); }
                catch { continue; }

                if (copy[0x11F] != 0)
                    return svSpec;
                scanned++;
            }
            return swshSpec;
        }
        catch
        {
            return candidates[0];
        }
    }

    /// <summary>
    /// Reads a pcdata.bin and yields each non-empty Pokémon as a constructed PKM.
    /// </summary>
    public static IEnumerable<PKM> ReadEntities(string filePath, FormatSpec format)
    {
        byte[] data = File.ReadAllBytes(filePath);
        int slotSize = format.SlotSize;
        // For ZA, only the first DecryptSliceSize bytes of each slot are PA9 data
        int constructLen = format.DecryptSliceSize > 0 ? format.DecryptSliceSize : slotSize;

        for (int i = 0; i + slotSize <= data.Length; i += slotSize)
        {
            if (IsSlotEmpty(data, i, format))
                continue;

            byte[] copy = new byte[constructLen];
            Array.Copy(data, i, copy, 0, constructLen);
            PKM? pk = null;
            try
            {
                pk = format.Construct(copy);
            }
            catch
            {
                continue;
            }

            if (pk is null || pk.Species == 0) continue;
            if (pk.Species > 1100) continue;

            yield return pk;
        }
    }

    private static bool IsSlotEmpty(byte[] data, int offset, FormatSpec format)
    {
        if (format.PKMType == typeof(PK1) || format.PKMType == typeof(PK2))
            return data[offset] == 0;
        for (int b = 0; b < 8; b++)
            if (data[offset + b] != 0) return false;
        return true;
    }

    /// <summary>
    /// Writes a list of Pokémon to a pcdata.bin file in the chosen target format.
    /// Returns counts of (written, skipped) — skipped entries couldn't be converted to the target type.
    /// </summary>
    public static (int Written, int Skipped) WriteEntities(string filePath, IEnumerable<PKM> source, FormatSpec format)
    {
        // Allocate the full buffer (zero-initialised → empty slots are zeros, matching real saves)
        int totalBytes = format.SlotSize * format.SlotCount;
        byte[] buffer = new byte[totalBytes];

        int written = 0, skipped = 0;
        bool isZA = format.PKMType == typeof(PA9) && format.SlotSize == 408;
        int writeLen = isZA ? 344 : format.SlotSize;

        foreach (var pk in source)
        {
            if (written >= format.SlotCount) break;
            if (pk.Species == 0) continue;

            // Convert to target type if needed
            PKM? target = pk;
            if (pk.GetType() != format.PKMType)
            {
                target = EntityConverter.ConvertToType(pk, format.PKMType, out _);
                if (target is null) { skipped++; continue; }
            }

            try
            {
                int offset = written * format.SlotSize;
                byte[] tmp = new byte[writeLen];
                format.Encrypt(target, tmp);
                Array.Copy(tmp, 0, buffer, offset, writeLen);

                // ZA: presence flag at byte 344, gap (345-407) stays zero
                if (isZA)
                    buffer[offset + 344] = 0x01;

                written++;
            }
            catch
            {
                skipped++;
            }
        }

        File.WriteAllBytes(filePath, buffer);
        return (written, skipped);
    }
}
