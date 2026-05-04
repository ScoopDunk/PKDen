using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PKHeX.Core;

namespace PKDen;

/// <summary>
/// Manages an in-memory collection of PKM organized into virtual "Home" boxes.
/// Supports per-Pokémon notes, transfer timestamps, and persistent save/load.
/// </summary>
public sealed class DenStorageManager
{
    public const int SlotsPerBox = 30;
    public const int DefaultBoxCount = 50;

    private readonly List<PKM?[]> _boxes = [];
    private readonly List<string> _boxNames = [];
    private readonly Dictionary<string, string> _notes = new();
    private readonly Dictionary<string, DateTime> _timestamps = new();
    private readonly Dictionary<int, string> _boxBackgrounds = new();
    private readonly Dictionary<string, string> _slotLabels = new();  // text shown in empty slots, auto-cleared when a Pokémon takes the slot

    /// <summary>
    /// Per-Pokémon custom sprite override.  Keyed by a stable identity composite
    /// (<see cref="GetPkIdentityKey"/>) so the override travels with the Pokémon
    /// across slot moves, box transfers, and metadata edits.  Value is the raw
    /// PNG bytes the user picked from disk — decoded on demand by SpriteOverrides.
    ///
    /// We store PNG bytes (not a file path) so the den file is fully self-contained
    /// and portable: backing up just PKDen.den preserves every custom sprite.
    /// </summary>
    private readonly Dictionary<ulong, byte[]> _customSprites = new();

    /// <summary>
    /// Per-box override for the slot fill color (the color shown behind each Pokémon).
    /// Stored as ARGB integers.  When a box has no entry here it falls back to the
    /// global slot fill color set in the View menu.  Lets the user color-code boxes
    /// (e.g. all-shiny box red, competitive box blue) without re-applying globally.
    /// </summary>
    private readonly Dictionary<int, int> _boxSlotColors = new();

    /// <summary>
    /// Per-box solid background color (ARGB).  Treated as a SECOND background mode
    /// alongside <see cref="_boxBackgrounds"/> (image path).  Resolution order at
    /// render time: image background wins if set, otherwise solid color, otherwise
    /// the global default panel color.
    /// </summary>
    private readonly Dictionary<int, int> _boxBackgroundColors = new();

    private string? _globalBackground;
    private int? _globalBackgroundColor;  // ARGB; null = no global solid color

    /// <summary>Path to the user-selected background music file (mp3/wav/flac), or null if none.</summary>
    private string? _musicPath;
    /// <summary>Music volume 0–100; defaults to 50 on first run.</summary>
    private int _musicVolume = 50;

    public int BoxCount => _boxes.Count;
    public int TotalSlots => BoxCount * SlotsPerBox;

    public DenStorageManager() => EnsureBoxCount(DefaultBoxCount);

    // === BOX MANAGEMENT ===

    public void EnsureBoxCount(int count)
    {
        while (_boxes.Count < count)
        {
            _boxes.Add(new PKM?[SlotsPerBox]);
            _boxNames.Add($"Den {_boxes.Count}");
        }
    }

    public string GetBoxName(int box) => (uint)box < _boxNames.Count ? _boxNames[box] : $"Den {box + 1}";

    public void SetBoxName(int box, string name)
    {
        if ((uint)box < _boxNames.Count) { _boxNames[box] = name; MarkDirty(); }
    }

    /// <summary>Deletes a box and shifts all subsequent boxes down.</summary>
    public void DeleteBox(int box)
    {
        if ((uint)box >= _boxes.Count) return;

        // Clear metadata for this box
        foreach (var k in _notes.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _notes.Remove(k);
        foreach (var k in _timestamps.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _timestamps.Remove(k);
        foreach (var k in _slotLabels.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _slotLabels.Remove(k);

        // Shift metadata for all boxes above this one down by 1
        for (int b = box + 1; b < _boxes.Count; b++)
        {
            for (int s = 0; s < SlotsPerBox; s++)
            {
                var noteKey = SlotKey(b, s);
                var newKey = SlotKey(b - 1, s);
                if (_notes.TryGetValue(noteKey, out var note)) { _notes[newKey] = note; _notes.Remove(noteKey); }
                if (_timestamps.TryGetValue(noteKey, out var ts)) { _timestamps[newKey] = ts; _timestamps.Remove(noteKey); }
                if (_slotLabels.TryGetValue(noteKey, out var lbl)) { _slotLabels[newKey] = lbl; _slotLabels.Remove(noteKey); }
            }
        }

        // Shift background mapping
        _boxBackgrounds.Remove(box);
        var bgKeys = _boxBackgrounds.Keys.Where(k => k > box).OrderBy(k => k).ToList();
        foreach (var k in bgKeys)
        {
            _boxBackgrounds[k - 1] = _boxBackgrounds[k];
            _boxBackgrounds.Remove(k);
        }

        _boxes.RemoveAt(box);
        _boxNames.RemoveAt(box);
        MarkDirty();
    }

    /// <summary>Renumbers any boxes that have default "Den N" names to match their actual position.</summary>
    public void RenumberDefaultBoxNames()
    {
        for (int i = 0; i < _boxNames.Count; i++)
        {
            var name = _boxNames[i];
            // If name matches any "Den #", "Home #", or is empty, renumber it
            if (string.IsNullOrWhiteSpace(name) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(Den|Home)\s+\d+$"))
                _boxNames[i] = $"Den {i + 1}";
        }
    }

    // === SLOT ACCESS ===

    public PKM? GetSlot(int box, int slot)
    {
        if ((uint)box >= _boxes.Count || (uint)slot >= SlotsPerBox) return null;
        return _boxes[box][slot];
    }

    public void SetSlot(int box, int slot, PKM? pk)
    {
        if ((uint)box >= _boxes.Count || (uint)slot >= SlotsPerBox) return;
        _boxes[box][slot] = pk;
        // When a Pokémon takes a labeled empty slot, the label is consumed (per spec).
        if (pk is { Species: > 0 })
            _slotLabels.Remove(SlotKey(box, slot));
        MarkDirty();
    }

    public void ClearSlot(int box, int slot)
    {
        SetSlot(box, slot, null);
        RemoveNote(box, slot);
        RemoveTimestamp(box, slot);
        // Don't remove the label here — clearing a slot back to empty preserves any label that was there.
    }

    // === SLOT LABELS ===
    // Per-slot text shown in empty slots. Cleared automatically when a Pokémon takes the slot.

    public string? GetSlotLabel(int box, int slot)
        => _slotLabels.TryGetValue(SlotKey(box, slot), out var label) ? label : null;

    public void SetSlotLabel(int box, int slot, string? label)
    {
        if ((uint)box >= _boxes.Count || (uint)slot >= SlotsPerBox) return;
        var key = SlotKey(box, slot);
        if (string.IsNullOrEmpty(label))
            _slotLabels.Remove(key);
        else
            _slotLabels[key] = label;
        MarkDirty();
    }

    public void RemoveSlotLabel(int box, int slot)
        => SetSlotLabel(box, slot, null);

    public void SwapSlots(int box1, int slot1, int box2, int slot2)
    {
        var a = GetSlot(box1, slot1);
        var b = GetSlot(box2, slot2);
        var noteA = GetNote(box1, slot1);
        var noteB = GetNote(box2, slot2);
        var tsA = GetTimestamp(box1, slot1);
        var tsB = GetTimestamp(box2, slot2);

        SetSlot(box1, slot1, b);
        SetSlot(box2, slot2, a);
        SetNote(box1, slot1, noteB);
        SetNote(box2, slot2, noteA);
        SetTimestampInternal(box1, slot1, tsB);
        SetTimestampInternal(box2, slot2, tsA);
    }

    // === NOTES ===

    private static string SlotKey(int box, int slot) => $"{box}:{slot}";

    public string? GetNote(int box, int slot) =>
        _notes.TryGetValue(SlotKey(box, slot), out var n) ? n : null;

    public void SetNote(int box, int slot, string? note)
    {
        var key = SlotKey(box, slot);
        if (string.IsNullOrWhiteSpace(note)) _notes.Remove(key);
        else _notes[key] = note;
        MarkDirty();
    }

    public void RemoveNote(int box, int slot) => _notes.Remove(SlotKey(box, slot));

    // === TIMESTAMPS ===

    public DateTime? GetTimestamp(int box, int slot) =>
        _timestamps.TryGetValue(SlotKey(box, slot), out var ts) ? ts : null;

    public void SetTimestamp(int box, int slot, DateTime ts)
    {
        _timestamps[SlotKey(box, slot)] = ts;
        MarkDirty();
    }

    private void SetTimestampInternal(int box, int slot, DateTime? ts)
    {
        var key = SlotKey(box, slot);
        if (ts.HasValue) _timestamps[key] = ts.Value;
        else _timestamps.Remove(key);
    }

    public void RemoveTimestamp(int box, int slot) => _timestamps.Remove(SlotKey(box, slot));

    // === BACKGROUNDS ===

    public string? GetGlobalBackground() => _globalBackground;
    public void SetGlobalBackground(string? path) { _globalBackground = path; MarkDirty(); }

    public string? GetBoxBackground(int box) =>
        _boxBackgrounds.TryGetValue(box, out var p) ? p : null;

    public void SetBoxBackground(int box, string? path)
    {
        if (string.IsNullOrEmpty(path)) _boxBackgrounds.Remove(box);
        else _boxBackgrounds[box] = path;
        MarkDirty();
    }

    /// <summary>Returns box-specific background if set, otherwise global, otherwise null.</summary>
    public string? GetEffectiveBackground(int box) =>
        GetBoxBackground(box) ?? _globalBackground;

    // === SOLID BACKGROUND COLORS (alternative to wallpapers) ===

    public int? GetGlobalBackgroundColor() => _globalBackgroundColor;
    public void SetGlobalBackgroundColor(int? argb) { _globalBackgroundColor = argb; MarkDirty(); }

    public int? GetBoxBackgroundColor(int box) =>
        _boxBackgroundColors.TryGetValue(box, out var c) ? c : null;

    public void SetBoxBackgroundColor(int box, int? argb)
    {
        if (argb is null) _boxBackgroundColors.Remove(box);
        else _boxBackgroundColors[box] = argb.Value;
        MarkDirty();
    }

    /// <summary>
    /// Returns the effective solid background color for the box.  Resolution order:
    /// box-specific solid color → global solid color → null (use panel default).
    /// Image backgrounds take precedence over solid colors at the render layer
    /// (handled in MainForm), so this method only needs to walk the color chain.
    /// </summary>
    public int? GetEffectiveBackgroundColor(int box) =>
        GetBoxBackgroundColor(box) ?? _globalBackgroundColor;

    // === PER-BOX SLOT FILL COLOR ===

    /// <summary>
    /// Returns the per-box slot fill color override (ARGB), or null if this box
    /// uses the global slot fill color.
    /// </summary>
    public int? GetBoxSlotColor(int box) =>
        _boxSlotColors.TryGetValue(box, out var c) ? c : null;

    /// <summary>Sets a per-box slot fill color override (pass null to remove).</summary>
    public void SetBoxSlotColor(int box, int? argb)
    {
        if (argb is null) _boxSlotColors.Remove(box);
        else _boxSlotColors[box] = argb.Value;
        MarkDirty();
    }

    // === BACKGROUND MUSIC ===

    public string? GetMusicPath() => _musicPath;
    public void SetMusicPath(string? path) { _musicPath = path; MarkDirty(); }

    public int GetMusicVolume() => _musicVolume;
    public void SetMusicVolume(int volume)
    {
        // Clamp to 0–100 here so callers don't have to.  Volume is a tiny field; saving it
        // as part of the den file means the user's preferred listening level travels with
        // the den (rather than being a separate per-machine setting).
        _musicVolume = Math.Clamp(volume, 0, 100);
        MarkDirty();
    }

    // === CUSTOM SPRITES (per-Pokémon override) ===

    /// <summary>
    /// Builds a stable identity key for a Pokémon — combines EncryptionConstant
    /// (high 32 bits) and PID (low 32 bits) into a single 64-bit value.  This pair
    /// uniquely identifies a Pokémon and survives slot moves, box transfers, and
    /// edits to non-essential fields like nickname or held item.
    /// </summary>
    /// <remarks>
    /// Trade-off: if the user clones a Pokémon (same PID/EC), both clones share
    /// the same custom sprite.  That's intentional — they ARE the same Pokémon
    /// from the game's perspective.
    /// </remarks>
    public static ulong GetPkIdentityKey(PKM pk) =>
        ((ulong)pk.EncryptionConstant << 32) | pk.PID;

    /// <summary>Returns the raw PNG bytes of the custom sprite for this Pokémon, or null if none set.</summary>
    public byte[]? GetCustomSpriteBytes(PKM pk) =>
        _customSprites.TryGetValue(GetPkIdentityKey(pk), out var bytes) ? bytes : null;

    /// <summary>Returns true if a custom sprite is set for this Pokémon.</summary>
    public bool HasCustomSprite(PKM pk) =>
        _customSprites.ContainsKey(GetPkIdentityKey(pk));

    /// <summary>
    /// Sets a custom sprite for this Pokémon from raw PNG bytes.  Pass null/empty
    /// to remove the override.  Caller is responsible for validating the bytes are
    /// a decodable image (see <see cref="ValidateImageBytes"/>) before calling.
    /// </summary>
    public void SetCustomSprite(PKM pk, byte[]? pngBytes)
    {
        var key = GetPkIdentityKey(pk);
        if (pngBytes is null || pngBytes.Length == 0)
        {
            if (_customSprites.Remove(key)) MarkDirty();
        }
        else
        {
            _customSprites[key] = pngBytes;
            MarkDirty();
        }
    }

    /// <summary>Removes the custom sprite for this Pokémon, if any.</summary>
    public void RemoveCustomSprite(PKM pk)
    {
        if (_customSprites.Remove(GetPkIdentityKey(pk))) MarkDirty();
    }

    /// <summary>
    /// Verifies that <paramref name="bytes"/> can be decoded as an image without
    /// throwing.  Used by the file-picker flow to reject corrupt files before they
    /// hit the den storage.  Note: this fully decodes the image into memory once,
    /// so it's not free — only call once at import time.
    /// </summary>
    public static bool ValidateImageBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return false;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            return img.Width > 0 && img.Height > 0;
        }
        catch { return false; }
    }

    // === QUERIES ===

    public IReadOnlyList<PKM> GetAllPokemon()
    {
        var result = new List<PKM>();
        foreach (var box in _boxes)
            foreach (var pk in box)
                if (pk is { Species: > 0 }) result.Add(pk);
        return result;
    }

    public IReadOnlyList<PKM> GetBoxPokemon(int box)
    {
        if ((uint)box >= _boxes.Count) return [];
        return _boxes[box].Where(p => p is { Species: > 0 }).ToList()!;
    }

    public (int Box, int Slot) FindNextEmpty(int startBox = 0)
    {
        for (int b = startBox; b < _boxes.Count; b++)
            for (int s = 0; s < SlotsPerBox; s++)
                if (_boxes[b][s] is null or { Species: 0 }) return (b, s);
        return (-1, -1);
    }

    public int GetTotalCount()
    {
        int c = 0;
        foreach (var box in _boxes)
            foreach (var pk in box)
                if (pk is { Species: > 0 }) c++;
        return c;
    }

    public int GetBoxCount(int box)
    {
        if ((uint)box >= _boxes.Count) return 0;
        return _boxes[box].Count(p => p is { Species: > 0 });
    }

    // === IMPORT ===

    public int ImportFromFolder(string path, SaveFile sav, int startBox = 0, bool clearFirst = false, bool subfolders = false)
    {
        if (!Directory.Exists(path)) return 0;
        if (clearFirst) ClearAllBoxes();
        var option = subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return ImportFromFiles(Directory.EnumerateFiles(path, "*.*", option), sav, startBox);
    }

    public int ImportFromFiles(IEnumerable<string> files, SaveFile sav, int startBox = 0)
    {
        int count = 0;
        int box = Math.Max(0, startBox);
        int slot = 0;
        foreach (var file in files)
        {
            var pk = TryLoadPKM(file, sav);
            if (pk is null) continue;
            if (!AdvanceToNextFreeSlot(ref box, ref slot)) break;
            SetSlot(box, slot, pk);
            SetTimestamp(box, slot, DateTime.Now);
            slot++;
            count++;
        }
        return count;
    }

    public int ImportPKMs(IEnumerable<PKM> pks, int startBox = 0)
    {
        int count = 0;
        int box = Math.Max(0, startBox);
        int slot = 0;
        foreach (var pk in pks)
        {
            if (pk.Species == 0) continue;
            if (!AdvanceToNextFreeSlot(ref box, ref slot)) break;
            SetSlot(box, slot, pk);
            SetTimestamp(box, slot, DateTime.Now);
            slot++;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Advances (box, slot) forward to the next empty position, creating boxes if needed.
    /// Used to fill imports sequentially starting from a chosen box.
    /// </summary>
    private bool AdvanceToNextFreeSlot(ref int box, ref int slot)
    {
        int safetyLimit = 10000;
        while (safetyLimit-- > 0)
        {
            if (box >= _boxes.Count) EnsureBoxCount(box + 1);
            if (slot >= SlotsPerBox) { slot = 0; box++; continue; }
            var existing = _boxes[box][slot];
            if (existing is null or { Species: 0 }) return true;
            slot++;
        }
        return false;
    }

    private static PKM? TryLoadPKM(string file, SaveFile sav)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            if (EntityDetection.IsSizePlausible(fi.Length))
            {
                var data = File.ReadAllBytes(file);
                var prefer = EntityFileExtension.GetContextFromExtension(fi.Extension, sav.Context);
                var pk = EntityFormat.GetFromBytes(data, prefer);
                if (pk is { Species: > 0 }) return pk;
            }
            var obj = FileUtil.GetSupportedFile(file, sav);
            return obj switch
            {
                PKM pk when pk.Species > 0 => pk,
                MysteryGift { IsEntity: true } mg => mg.ConvertToPKM(sav),
                IEncounterInfo enc when enc.Species != 0 => enc.ConvertToPKM(sav),
                IPokeGroup g => g.Contents.FirstOrDefault(p => p.Species > 0),
                _ => null,
            };
        }
        catch { return null; }
    }

    // === MUTATIONS ===

    public void ClearAllBoxes()
    {
        foreach (var box in _boxes) Array.Clear(box);
        _notes.Clear();
        _timestamps.Clear();
        // Clearing every box also orphans every custom sprite (no Pokémon left to map to).
        // Drop them rather than letting them accumulate forever in the .den file.
        _customSprites.Clear();
        MarkDirty();
    }

    public void ClearBox(int box)
    {
        if ((uint)box >= _boxes.Count) return;
        Array.Clear(_boxes[box]);
        foreach (var k in _notes.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _notes.Remove(k);
        foreach (var k in _timestamps.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _timestamps.Remove(k);
        MarkDirty();
    }

    /// <summary>Collects a slot's PKM + metadata for sorting/compacting.</summary>
    private readonly record struct SlotData(PKM Pk, string? Note, DateTime? Timestamp);

    private List<SlotData> CollectSlots(int box)
    {
        var items = new List<SlotData>();
        for (int s = 0; s < SlotsPerBox; s++)
        {
            var pk = GetSlot(box, s);
            if (pk is { Species: > 0 })
                items.Add(new(pk, GetNote(box, s), GetTimestamp(box, s)));
        }
        return items;
    }

    private List<SlotData> CollectAllSlots()
    {
        var items = new List<SlotData>();
        for (int b = 0; b < BoxCount; b++)
            for (int s = 0; s < SlotsPerBox; s++)
            {
                var pk = GetSlot(b, s);
                if (pk is { Species: > 0 })
                    items.Add(new(pk, GetNote(b, s), GetTimestamp(b, s)));
            }
        return items;
    }

    private void ClearMetadata()
    {
        foreach (var box in _boxes) Array.Clear(box);
        _notes.Clear();
        _timestamps.Clear();
    }

    private void PlaceItems(IList<SlotData> items, int startFlat = 0)
    {
        for (int i = 0; i < items.Count; i++)
        {
            int flat = startFlat + i;
            int bx = flat / SlotsPerBox, sl = flat % SlotsPerBox;
            EnsureBoxCount(bx + 1);
            _boxes[bx][sl] = items[i].Pk;
            if (!string.IsNullOrEmpty(items[i].Note)) _notes[SlotKey(bx, sl)] = items[i].Note!;
            if (items[i].Timestamp is { } ts) _timestamps[SlotKey(bx, sl)] = ts;
        }
    }

    private void ClearBoxMetadata(int box)
    {
        Array.Clear(_boxes[box]);
        foreach (var k in _notes.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _notes.Remove(k);
        foreach (var k in _timestamps.Keys.Where(k => k.StartsWith($"{box}:")).ToList()) _timestamps.Remove(k);
    }

    private void PlaceItemsInBox(int box, IList<SlotData> items)
    {
        for (int i = 0; i < items.Count && i < SlotsPerBox; i++)
        {
            _boxes[box][i] = items[i].Pk;
            if (!string.IsNullOrEmpty(items[i].Note)) _notes[SlotKey(box, i)] = items[i].Note!;
            if (items[i].Timestamp is { } ts) _timestamps[SlotKey(box, i)] = ts;
        }
    }

    public void SortAll(Comparison<PKM> comparison)
    {
        var items = CollectAllSlots();
        items.Sort((a, b) => comparison(a.Pk, b.Pk));
        ClearMetadata();
        PlaceItems(items);
        MarkDirty();
    }

    public void SortBox(int box, Comparison<PKM> comparison)
    {
        if ((uint)box >= _boxes.Count) return;
        var items = CollectSlots(box);
        items.Sort((a, b) => comparison(a.Pk, b.Pk));
        ClearBoxMetadata(box);
        PlaceItemsInBox(box, items);
        MarkDirty();
    }

    public void CompactBox(int box)
    {
        if ((uint)box >= _boxes.Count) return;
        var items = CollectSlots(box);
        ClearBoxMetadata(box);
        PlaceItemsInBox(box, items);
        MarkDirty();
    }

    // === SAVE / LOAD — Format v3 (notes + timestamps) ===
    //
    // Header: "PHOM" (4) + version (4) + boxCount (4) + slotsPerBox (4)
    // Box names: [ushort len][utf8 bytes] × boxCount
    // Per slot:
    //   [int pkDataLen][bytes pkData]      — 0 = empty
    //   [ushort noteLen][utf8 noteBytes]   — 0 = no note  (v2+)
    //   [long ticks]                       — 0 = no timestamp (v3+)
    // Backgrounds (v4+): [globalBgString][int count]([int box][string path]) × count
    // Slot labels (v5+): [int count]([int box][int slot][string text]) × count
    // Custom sprites (v6+): [int count]([ulong identityKey][int byteLen][byte[] pngBytes]) × count
    // V7 additions (per-box colors, solid backgrounds, music):
    //   [int hasGlobalBgColor (0/1)][int argb if 1]
    //   [int boxBgColorCount]([int box][int argb]) × count
    //   [int boxSlotColorCount]([int box][int argb]) × count
    //   [string musicPath][int musicVolume]

    private static readonly byte[] Magic = "PHOM"u8.ToArray();
    private const int FormatVersion = 7;

    /// <summary>
    /// Returns the path where the Den file should be saved (PKDen.den next to the exe).
    /// If a legacy PokemonDen.den file exists but the new PKDen.den does not,
    /// the legacy path is returned so the user's existing data loads correctly.
    /// The next save will then write to PKDen.den, completing the migration.
    /// </summary>
    public static string GetDefaultSavePath()
    {
        string newPath = Path.Combine(AppContext.BaseDirectory, "PKDen.den");
        if (File.Exists(newPath)) return newPath;

        string legacyPath = Path.Combine(AppContext.BaseDirectory, "PokemonDen.den");
        if (File.Exists(legacyPath)) return legacyPath;

        return newPath;
    }

    /// <summary>Always returns the new-name path for saving — migration writes here.</summary>
    public static string GetSavePathForWriting() =>
        Path.Combine(AppContext.BaseDirectory, "PKDen.den");

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(Magic);
        bw.Write(FormatVersion);
        bw.Write(BoxCount);
        bw.Write(SlotsPerBox);

        for (int b = 0; b < BoxCount; b++)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(GetBoxName(b));
            bw.Write((ushort)nameBytes.Length);
            bw.Write(nameBytes);
        }

        for (int b = 0; b < BoxCount; b++)
        {
            for (int s = 0; s < SlotsPerBox; s++)
            {
                var pk = GetSlot(b, s);
                if (pk is not { Species: > 0 })
                {
                    bw.Write(0);          // no PKM
                    bw.Write((ushort)0);   // no note
                    bw.Write(0L);          // no timestamp
                    continue;
                }

                pk.ForcePartyData();
                var data = new byte[pk.SIZE_PARTY];
                pk.WriteDecryptedDataParty(data);
                bw.Write(data.Length);
                bw.Write(data);

                var note = GetNote(b, s);
                if (string.IsNullOrEmpty(note)) { bw.Write((ushort)0); }
                else { var nb = System.Text.Encoding.UTF8.GetBytes(note); bw.Write((ushort)nb.Length); bw.Write(nb); }

                var ts = GetTimestamp(b, s);
                bw.Write(ts?.Ticks ?? 0L);
            }
        }

        // v4: Backgrounds
        WriteString(bw, _globalBackground ?? "");
        bw.Write(_boxBackgrounds.Count);
        foreach (var (box, bgPath) in _boxBackgrounds)
        {
            bw.Write(box);
            WriteString(bw, bgPath);
        }

        // v5: Slot labels (text shown in empty slots)
        bw.Write(_slotLabels.Count);
        foreach (var (key, text) in _slotLabels)
        {
            // SlotKey is "box:slot" — split, validate, write
            var parts = key.Split(':');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], out int b)) continue;
            if (!int.TryParse(parts[1], out int s)) continue;
            bw.Write(b);
            bw.Write(s);
            WriteString(bw, text);
        }

        // v6: Custom per-Pokémon sprites (raw PNG bytes keyed by PID+EC composite).
        // Stored last so older readers that bail at end-of-stream still load the rest.
        bw.Write(_customSprites.Count);
        foreach (var (identityKey, pngBytes) in _customSprites)
        {
            bw.Write(identityKey);
            bw.Write(pngBytes.Length);
            bw.Write(pngBytes);
        }

        // v7: Per-box colors + solid backgrounds + background music.
        // Colors are stored as 32-bit ARGB ints — same wire format as Color.ToArgb().
        bw.Write(_globalBackgroundColor.HasValue ? 1 : 0);
        if (_globalBackgroundColor.HasValue) bw.Write(_globalBackgroundColor.Value);

        bw.Write(_boxBackgroundColors.Count);
        foreach (var (box, argb) in _boxBackgroundColors) { bw.Write(box); bw.Write(argb); }

        bw.Write(_boxSlotColors.Count);
        foreach (var (box, argb) in _boxSlotColors) { bw.Write(box); bw.Write(argb); }

        WriteString(bw, _musicPath ?? "");
        bw.Write(_musicVolume);
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        int len = br.ReadUInt16();
        return len > 0 ? System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)) : "";
    }

    public bool LoadFromFile(string path, SaveFile sav)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != 'P' || magic[1] != 'H' || magic[2] != 'O' || magic[3] != 'M')
                return false;

            int version = br.ReadInt32();
            if (version > FormatVersion) return false;

            int boxCount = br.ReadInt32();
            int fileSlotsPerBox = br.ReadInt32();
            if (boxCount <= 0 || boxCount > 5000 || fileSlotsPerBox <= 0 || fileSlotsPerBox > 1000)
                return false;

            ClearMetadata();

            // Clear existing boxes/names/backgrounds — critical for proper undo/restore
            _boxes.Clear();
            _boxNames.Clear();
            _boxBackgrounds.Clear();
            _globalBackground = null;

            // Box names
            for (int b = 0; b < boxCount; b++)
            {
                int nameLen = br.ReadUInt16();
                var nameBytes = br.ReadBytes(nameLen);
                int destBox = (b * fileSlotsPerBox) / SlotsPerBox;
                EnsureBoxCount(destBox + 1);
                if ((b * fileSlotsPerBox) % SlotsPerBox == 0)
                    SetBoxName(destBox, System.Text.Encoding.UTF8.GetString(nameBytes));
            }

            // Slots
            for (int b = 0; b < boxCount; b++)
            {
                for (int s = 0; s < fileSlotsPerBox; s++)
                {
                    int flat = b * fileSlotsPerBox + s;
                    int destBox = flat / SlotsPerBox;
                    int destSlot = flat % SlotsPerBox;
                    EnsureBoxCount(destBox + 1);

                    int dataLen = br.ReadInt32();
                    PKM? pk = null;
                    if (dataLen > 0)
                    {
                        var data = br.ReadBytes(dataLen);
                        pk = EntityFormat.GetFromBytes(data);
                    }

                    // Note (v2+)
                    string? note = null;
                    if (version >= 2)
                    {
                        int noteLen = br.ReadUInt16();
                        if (noteLen > 0)
                            note = System.Text.Encoding.UTF8.GetString(br.ReadBytes(noteLen));
                    }

                    // Timestamp (v3+)
                    DateTime? ts = null;
                    if (version >= 3)
                    {
                        long ticks = br.ReadInt64();
                        if (ticks > 0) ts = new DateTime(ticks);
                    }

                    if (pk is { Species: > 0 } && destSlot < SlotsPerBox)
                    {
                        _boxes[destBox][destSlot] = pk;
                        if (!string.IsNullOrEmpty(note)) _notes[SlotKey(destBox, destSlot)] = note;
                        if (ts.HasValue) _timestamps[SlotKey(destBox, destSlot)] = ts.Value;
                    }
                }
            }

            // Backgrounds (v4+)
            _boxBackgrounds.Clear();
            _globalBackground = null;
            if (version >= 4)
            {
                var globalBg = ReadString(br);
                if (!string.IsNullOrEmpty(globalBg)) _globalBackground = globalBg;

                int bgCount = br.ReadInt32();
                for (int i = 0; i < bgCount; i++)
                {
                    int box = br.ReadInt32();
                    var bgPath = ReadString(br);
                    if (!string.IsNullOrEmpty(bgPath))
                        _boxBackgrounds[box] = bgPath;
                }
            }

            // Slot labels (v5+) — text on empty slots
            _slotLabels.Clear();
            if (version >= 5)
            {
                int labelCount = br.ReadInt32();
                if (labelCount < 0 || labelCount > 100000) return false;  // sanity guard
                for (int i = 0; i < labelCount; i++)
                {
                    int b = br.ReadInt32();
                    int s = br.ReadInt32();
                    var text = ReadString(br);
                    if (!string.IsNullOrEmpty(text))
                        _slotLabels[SlotKey(b, s)] = text;
                }
            }

            // Custom per-Pokémon sprites (v6+) — raw PNG bytes keyed by PID+EC composite.
            _customSprites.Clear();
            if (version >= 6)
            {
                int spriteCount = br.ReadInt32();
                if (spriteCount < 0 || spriteCount > 100000) return false;
                for (int i = 0; i < spriteCount; i++)
                {
                    ulong identityKey = br.ReadUInt64();
                    int byteLen = br.ReadInt32();
                    if (byteLen < 0 || byteLen > 16 * 1024 * 1024) return false;
                    var pngBytes = br.ReadBytes(byteLen);
                    if (pngBytes.Length == byteLen && byteLen > 0)
                        _customSprites[identityKey] = pngBytes;
                }
            }

            // Per-box colors + solid backgrounds + music settings (v7+).
            _globalBackgroundColor = null;
            _boxBackgroundColors.Clear();
            _boxSlotColors.Clear();
            _musicPath = null;
            _musicVolume = 50;
            if (version >= 7)
            {
                int hasGlobalBgColor = br.ReadInt32();
                if (hasGlobalBgColor != 0)
                    _globalBackgroundColor = br.ReadInt32();

                int boxBgColorCount = br.ReadInt32();
                if (boxBgColorCount < 0 || boxBgColorCount > 10000) return false;
                for (int i = 0; i < boxBgColorCount; i++)
                {
                    int box = br.ReadInt32();
                    int argb = br.ReadInt32();
                    _boxBackgroundColors[box] = argb;
                }

                int boxSlotColorCount = br.ReadInt32();
                if (boxSlotColorCount < 0 || boxSlotColorCount > 10000) return false;
                for (int i = 0; i < boxSlotColorCount; i++)
                {
                    int box = br.ReadInt32();
                    int argb = br.ReadInt32();
                    _boxSlotColors[box] = argb;
                }

                var musicPath = ReadString(br);
                if (!string.IsNullOrEmpty(musicPath)) _musicPath = musicPath;
                _musicVolume = Math.Clamp(br.ReadInt32(), 0, 100);
            }

            RenumberDefaultBoxNames();
            return true;
        }
        catch { return false; }
    }

    public static bool SaveFileExists(string path) => File.Exists(path);

    /// <summary>Saves current state to a byte array for undo snapshots.</summary>
    public byte[] SaveToBytes()
    {
        var tmp = Path.GetTempFileName();
        try { SaveToFile(tmp); return File.ReadAllBytes(tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Restores state from a byte array (undo).</summary>
    public bool LoadFromBytes(byte[] data, SaveFile sav)
    {
        var tmp = Path.GetTempFileName();
        try { File.WriteAllBytes(tmp, data); return LoadFromFile(tmp, sav); }
        finally { try { File.Delete(tmp); } catch { } }
    }
    public bool IsDirty { get; set; }
    public void MarkDirty() => IsDirty = true;
    public void MarkClean() => IsDirty = false;
}
