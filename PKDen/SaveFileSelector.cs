using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PKHeX.Core;

namespace PKDen;

/// <summary>
/// Save File Selector — scans a user-chosen directory for save files and presents each
/// as a "card" with the game's box artwork on the left and trainer/save info on the
/// right.  Each card has an "Open Box" button that loads the save into the existing
/// save-panel UI (same code path as <c>File → Open Save File...</c>).
///
/// === Architecture ===
///   • <see cref="GameArtResolver"/> picks the right embedded artwork for a given
///     (GameVersion, LanguageID) per the language-fallback rules.
///   • <see cref="SaveInfoCache"/> scans the directory and parses each save once,
///     caching the result by (path, lastWriteTime, fileSize).  Re-scans are cheap.
///   • <see cref="SaveFileSelectorPanel"/> is the UI — owns a scrollable list of
///     cards, plus a "no directory set" message when the user hasn't picked one.
///
/// === Why a separate module? ===
/// The selector is self-contained (artwork lookup, save metadata extraction, scrolling
/// card layout) and pulling it into MainForm.cs would add ~500 lines to an already-huge
/// file.  Keeping it here also makes the artwork resolver unit-testable in isolation.
/// </summary>
public static class GameArtResolver
{
    /// <summary>
    /// Returns the embedded resource name (e.g. "PKDen.GameArt.en_RD.png") for the given
    /// version + language, or null if no artwork is available for that combination.
    ///
    /// Rules per spec:
    ///   • Japanese gens 1-7  → use Japanese artwork
    ///   • Japanese gens 8-9  → use English artwork (no Japanese art available)
    ///   • English any gen    → use English artwork
    ///   • Any other language → use English artwork (French/German/Spanish/etc. fall back)
    /// </summary>
    public static string? GetResourceName(GameVersion version, LanguageID language, byte generation)
        => GetResourceName(version, language, generation, savePath: null);

    /// <summary>
    /// Variant that consults the save file's path for hints when the version is an
    /// umbrella (e.g. <see cref="GameVersion.RB"/> covers both Red AND Blue — PKHeX has
    /// no way to distinguish them from the save data alone).  Filename hints fill the gap:
    /// "pokemonred.sav" gets Red artwork, "pokemonblue.sav" gets Blue, etc.
    /// </summary>
    public static string? GetResourceName(GameVersion version, LanguageID language, byte generation, string? savePath)
    {
        // First try the specific version directly; fall back to umbrella resolution
        // (which uses the filename) if no specific match.
        var key = GetVersionKey(version) ?? ResolveUmbrella(version, savePath);
        if (key is null) return null;

        bool useJapanese = language == LanguageID.Japanese && generation <= 7;
        var prefix = useJapanese ? "jp_" : "en_";

        // Resolve the actual extension by probing both candidates.  We stored most files
        // as .png and the Switch-era JPEGs as .jpg — rather than hard-coding which is
        // which (and risking drift), we just try .png first then .jpg.
        var pngName = $"PKDen.GameArt.{prefix}{key}.png";
        if (ResourceExists(pngName)) return pngName;
        var jpgName = $"PKDen.GameArt.{prefix}{key}.jpg";
        if (ResourceExists(jpgName)) return jpgName;

        // If a Japanese variant doesn't exist (e.g. Japanese Brilliant Diamond), fall back
        // to English.  This catches gaps in the Japanese set automatically.
        if (useJapanese)
        {
            var fallbackPng = $"PKDen.GameArt.en_{key}.png";
            if (ResourceExists(fallbackPng)) return fallbackPng;
            var fallbackJpg = $"PKDen.GameArt.en_{key}.jpg";
            if (ResourceExists(fallbackJpg)) return fallbackJpg;
        }
        return null;
    }

    /// <summary>
    /// Disambiguates an umbrella <see cref="GameVersion"/> (RB, GS, RS, FRLG, etc.) into
    /// a specific version key by inspecting the save file's filename for hints.
    ///
    /// Returns null if the version isn't an umbrella we know how to disambiguate.
    /// For known umbrellas without a filename hint, returns a reasonable default
    /// (typically the first/"version-1" game in the pair) so the user gets SOME
    /// artwork rather than null.
    /// </summary>
    /// <remarks>
    /// PKHeX's Gen 1/2 SAV classes (and SAV3RS, SAV3FRLG, SAV4HGSS, etc.) report a
    /// shared umbrella version because the save data alone doesn't distinguish two
    /// cartridges in the same pair (e.g. Red.sav and Blue.sav are byte-identical
    /// structurally — only the cartridge stamp differs).  We work around this by
    /// reading the user's filename, since most users name their saves by the game.
    ///
    /// Patterns are LOWERCASE substrings — "PokemonRed.sav", "Pokemon_Red.sav", and
    /// "pokemonr.sav" all match.  Order within each umbrella matters: more specific
    /// keywords come first ("leafgreen" before "green") to avoid collisions.
    /// </remarks>
    private static string? ResolveUmbrella(GameVersion version, string? savePath)
    {
        var fname = string.IsNullOrEmpty(savePath) ? "" : Path.GetFileNameWithoutExtension(savePath).ToLowerInvariant();

        // Helper: returns the first matching key from candidates whose hint substring
        // appears in the filename.  Falls back to defaultKey if no hint matches —
        // gives the user SOMETHING rather than null when a save lacks naming hints.
        static string PickByHint(string fname, (string Hint, string Key)[] candidates, string defaultKey)
        {
            foreach (var (hint, key) in candidates)
                if (fname.Contains(hint)) return key;
            return defaultKey;
        }

        return version switch
        {
            // Gen 1 — Red/Blue/Green umbrella.  Yellow has its own specific version.
            // Matches both umbrella RB and RBY (the latter is the SAV1 default).
            GameVersion.RB or GameVersion.RBY => PickByHint(fname, new[]
            {
                ("yellow",  "YW"),
                ("pikachu", "YW"),
                ("blue",    "BU"),
                ("green",   "GN"),     // Pocket Monsters Midori (JP-only)
                ("midori",  "GN"),
                ("red",     "RD"),
                // Single-letter abbreviation patterns — common for short filenames.
                // Checked LAST so a name like "pokemonred.sav" matches "red" first.
                ("monr",    "RD"),
                ("monb",    "BU"),
                ("mony",    "YW"),
            }, defaultKey: "RD"),

            // Gen 2 — Gold/Silver umbrella; Crystal is specific.
            GameVersion.GS or GameVersion.GSC => PickByHint(fname, new[]
            {
                ("crystal", "C"),
                ("silver",  "SI"),
                ("gold",    "GD"),
            }, defaultKey: "GD"),

            // Gen 3
            GameVersion.RS or GameVersion.RSE => PickByHint(fname, new[]
            {
                ("emerald",  "E"),
                ("sapphire", "S"),
                ("ruby",     "R"),
            }, defaultKey: "R"),
            GameVersion.FRLG => PickByHint(fname, new[]
            {
                ("leafgreen", "LG"),
                ("firered",   "FR"),
            }, defaultKey: "FR"),

            // Gen 4
            GameVersion.DP or GameVersion.DPPt => PickByHint(fname, new[]
            {
                ("platinum", "Pt"),
                ("pearl",    "P"),
                ("diamond",  "D"),
            }, defaultKey: "D"),
            GameVersion.HGSS => PickByHint(fname, new[]
            {
                ("soulsilver", "SS"),
                ("heartgold",  "HG"),
            }, defaultKey: "HG"),

            // Gen 5
            GameVersion.BW => PickByHint(fname, new[]
            {
                ("white", "W"),
                ("black", "B"),
            }, defaultKey: "B"),
            GameVersion.B2W2 => PickByHint(fname, new[]
            {
                // "white2"/"black2" before plain "white"/"black" so a B2W2 save named
                // "blackwhite2.sav" picks the right variant.
                ("white2",  "W2"),
                ("white_2", "W2"),
                ("black2",  "B2"),
                ("black_2", "B2"),
                ("white",   "W2"),
                ("black",   "B2"),
            }, defaultKey: "B2"),

            // Gen 6+ — saves usually report specific versions, but include umbrella
            // fallback as a safety net for any edge case.
            GameVersion.XY   => PickByHint(fname, new[] { ("y", "Y"), ("x", "X") }, defaultKey: "X"),
            GameVersion.ORAS => PickByHint(fname, new[] { ("alphasapphire", "AS"), ("omegaruby", "OR") }, defaultKey: "OR"),
            GameVersion.SM   => PickByHint(fname, new[] { ("moon", "MN"), ("sun", "SN") }, defaultKey: "SN"),
            GameVersion.USUM => PickByHint(fname, new[] { ("ultramoon", "UM"), ("ultrasun", "US") }, defaultKey: "US"),
            GameVersion.GG   => PickByHint(fname, new[] { ("eevee", "GE"), ("pikachu", "GP") }, defaultKey: "GP"),
            GameVersion.SWSH => PickByHint(fname, new[] { ("shield", "SH"), ("sword", "SW") }, defaultKey: "SW"),
            GameVersion.BDSP => PickByHint(fname, new[] { ("shiningpearl", "SP"), ("brilliantdiamond", "BD") }, defaultKey: "BD"),
            GameVersion.SV   => PickByHint(fname, new[] { ("violet", "VL"), ("scarlet", "SL") }, defaultKey: "SL"),

            _ => null,
        };
    }

    /// <summary>
    /// Loads the artwork bitmap for this save's metadata record.  Caller owns the
    /// returned bitmap and should dispose it.  Returns null if no matching artwork
    /// is embedded.
    ///
    /// Resolution priority:
    ///   1. <see cref="ThumbnailOverrides"/> — user-picked thumbnail for this save's path
    ///   2. <see cref="GetResourceName"/> — auto-detected from version/language/gen
    /// </summary>
    public static Bitmap? LoadArtwork(SaveInfo info)
    {
        // Per-save user override takes priority.  When set, we use that exact resource
        // regardless of the save's version/language — the user has explicitly chosen
        // what they want to see for this file.
        var overrideName = ThumbnailOverrides.Get(info.Path);
        var resourceName = overrideName ?? GetResourceName(info.Version, info.Language, info.Generation, info.Path);
        if (resourceName is null) return null;
        // If the user-set override resource doesn't actually exist (e.g. file edited
        // by hand, or referencing an old build), fall back to the auto-detected name.
        if (!ResourceExists(resourceName))
        {
            resourceName = GetResourceName(info.Version, info.Language, info.Generation, info.Path);
            if (resourceName is null) return null;
        }
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var src = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    /// <summary>Returns every embedded artwork resource name — used by the thumbnail picker dialog.</summary>
    public static IEnumerable<string> GetAllResourceNames() => _resourceCache;

    /// <summary>
    /// Returns a friendly label for an embedded resource name (e.g. "PKDen.GameArt.en_RD.png" → "Red (English)").
    /// Used by the thumbnail picker so the user sees something meaningful when hovering options.
    /// </summary>
    public static string GetFriendlyLabel(string resourceName)
    {
        // Strip prefix and extension to isolate the "lang_KEY" part (e.g. "en_RD", "jp_X").
        const string prefix = "PKDen.GameArt.";
        if (!resourceName.StartsWith(prefix)) return resourceName;
        var stem = resourceName[prefix.Length..];
        var dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];

        var underscore = stem.IndexOf('_');
        if (underscore <= 0) return stem;
        var lang = stem[..underscore];
        var key = stem[(underscore + 1)..];
        var langLabel = lang switch { "en" => "English", "jp" => "Japanese", _ => lang };
        var keyLabel = key switch
        {
            "RD" => "Red", "GN" => "Green (Midori)", "BU" => "Blue", "YW" => "Yellow",
            "GD" => "Gold", "SI" => "Silver", "C" => "Crystal",
            "R" => "Ruby", "S" => "Sapphire", "E" => "Emerald", "FR" => "FireRed", "LG" => "LeafGreen",
            "D" => "Diamond", "P" => "Pearl", "Pt" => "Platinum", "HG" => "HeartGold", "SS" => "SoulSilver",
            "B" => "Black", "W" => "White", "B2" => "Black 2", "W2" => "White 2",
            "X" => "X", "Y" => "Y", "OR" => "Omega Ruby", "AS" => "Alpha Sapphire",
            "SN" => "Sun", "MN" => "Moon", "US" => "Ultra Sun", "UM" => "Ultra Moon",
            "GP" => "Let's Go Pikachu", "GE" => "Let's Go Eevee",
            "SW" => "Sword", "SH" => "Shield",
            "BD" => "Brilliant Diamond", "SP" => "Shining Pearl",
            "PLA" => "Legends Arceus", "SL" => "Scarlet", "VL" => "Violet",
            "ZA" => "Legends Z-A",
            _ => key,
        };
        return $"{keyLabel} ({langLabel})";
    }

    /// <summary>
    /// Loads artwork for a live <see cref="SaveFile"/>.  Used by future call sites that
    /// have a SaveFile but no SaveInfo; currently unused but kept for symmetry.
    /// </summary>
    public static Bitmap? LoadArtwork(SaveFile sav)
    {
        var lang = sav.Language >= 0 ? (LanguageID)sav.Language : LanguageID.None;
        var resourceName = GetResourceName(sav.Version, lang, sav.Generation);
        if (resourceName is null) return null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var src = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    /// <summary>
    /// Maps a <see cref="GameVersion"/> enum value to the short two-letter (or three)
    /// key we use in the embedded filenames.  Returns null for versions that don't
    /// have artwork (e.g. virtual versions like CXD, Stadium, etc).
    /// </summary>
    /// <remarks>
    /// Order matters slightly here: cases at the top are the "main" releases.  Edge
    /// cases like ZA (Legends Z-A — no <see cref="GameVersion"/> enum member yet) are
    /// handled by string comparison so future PKHeX updates with the new enum value
    /// pick up the artwork without code changes.
    /// </remarks>
    private static string? GetVersionKey(GameVersion v) => v switch
    {
        GameVersion.RD => "RD",
        GameVersion.GN => "GN",   // Pocket Monsters Midori (Japan-only release)
        GameVersion.BU => "BU",
        GameVersion.YW => "YW",
        GameVersion.GD => "GD",
        GameVersion.SI => "SI",   // Silver (Gen 2).  GameVersion.SV is the Scarlet/Violet
                                  // umbrella version, NOT Silver — handled in ResolveUmbrella above.
        GameVersion.C  => "C",
        GameVersion.R  => "R",
        GameVersion.S  => "S",
        GameVersion.E  => "E",
        GameVersion.FR => "FR",
        GameVersion.LG => "LG",
        GameVersion.D  => "D",
        GameVersion.P  => "P",
        GameVersion.Pt => "Pt",
        GameVersion.HG => "HG",
        GameVersion.SS => "SS",
        GameVersion.B  => "B",
        GameVersion.W  => "W",
        GameVersion.B2 => "B2",
        GameVersion.W2 => "W2",
        GameVersion.X  => "X",
        GameVersion.Y  => "Y",
        GameVersion.OR => "OR",
        GameVersion.AS => "AS",
        GameVersion.SN => "SN",
        GameVersion.MN => "MN",
        GameVersion.US => "US",
        GameVersion.UM => "UM",
        GameVersion.GP => "GP",
        GameVersion.GE => "GE",
        GameVersion.SW => "SW",
        GameVersion.SH => "SH",
        GameVersion.PLA=> "PLA",
        GameVersion.BD => "BD",
        GameVersion.SP => "SP",
        GameVersion.SL => "SL",
        GameVersion.VL => "VL",
        // Future-proof: Legends Z-A may not have an enum value yet.  When it does, add
        // its enum case here and the existing en_ZA.jpg artwork picks up automatically.
        _ => null,
    };

    private static readonly HashSet<string> _resourceCache = BuildResourceCache();
    private static HashSet<string> BuildResourceCache()
    {
        var asm = Assembly.GetExecutingAssembly();
        return new HashSet<string>(asm.GetManifestResourceNames().Where(n => n.StartsWith("PKDen.GameArt.")));
    }
    private static bool ResourceExists(string name) => _resourceCache.Contains(name);
}

/// <summary>
/// One row of save metadata as displayed on a selector card.
/// </summary>
public sealed record SaveInfo(
    string Path,
    string OT,
    string TID,
    string GameDisplay,
    GameVersion Version,
    LanguageID Language,
    byte Generation,
    int BoxCount,
    int FilledCount,
    int PartyCount,
    string PlayTime
);

/// <summary>
/// Scans a directory for save files and caches metadata so subsequent re-renders
/// don't re-parse the (potentially expensive) full save file each time.
/// Cache key is (path, lastWriteTime, fileSize) — any change to the file invalidates.
/// </summary>
public static class SaveInfoCache
{
    /// <summary>File extensions recognized as save files.  Same set the Open Save File dialog accepts.</summary>
    public static readonly string[] SaveExtensions = { ".sav", ".dsv", ".dat", ".gci", ".bin", ".sa1", ".sa2", ".main", ".bak", ".fla", ".raw", ".srm", ".saveram" };

    private readonly record struct CacheKey(string Path, long Ticks, long Size);
    private static readonly Dictionary<CacheKey, SaveInfo?> _cache = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Walks <paramref name="directory"/> recursively, returning a SaveInfo for every
    /// recognized save file.  Files that fail to parse are skipped silently — typically
    /// non-save .bin/.dat files in the same folder.
    /// </summary>
    public static List<SaveInfo> Scan(string directory)
    {
        var results = new List<SaveInfo>();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return results;

        IEnumerable<string> files;
        try
        {
            // Use EnumerateFiles (lazy) rather than GetFiles (eager) so giant directories
            // don't allocate a huge string[] up front.
            files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);
        }
        catch { return results; }

        foreach (var path in files)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(SaveExtensions, ext) < 0) continue;

            var info = TryGetInfo(path);
            if (info is not null) results.Add(info);
        }
        return results;
    }

    private static SaveInfo? TryGetInfo(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return null; }

        var key = new CacheKey(path, fi.LastWriteTimeUtc.Ticks, fi.Length);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        SaveInfo? info = null;
        try
        {
            // SaveUtil.GetSaveFile returns null for non-save files (which is most common
            // failure mode here since save folders often contain rom backups too).
            var sav = SaveUtil.GetSaveFile(path);
            if (sav is not null)
            {
                int filled = 0;
                try
                {
                    // Walk every box and count Species != 0.  This is one full pass over the
                    // save's pokemon storage but only happens once per file due to caching.
                    for (int b = 0; b < sav.BoxCount; b++)
                    {
                        var boxData = sav.GetBoxData(b);
                        foreach (var pk in boxData)
                            if (pk is not null && pk.Species != 0) filled++;
                    }
                }
                catch { /* some saves throw on partial box reads; treat as 0 */ }

                var gameName = GameInfo.GetVersionName(sav.Version);
                if (string.IsNullOrEmpty(gameName)) gameName = sav.Version.ToString();

                info = new SaveInfo(
                    Path: path,
                    OT: string.IsNullOrEmpty(sav.OT) ? "(unknown)" : sav.OT,
                    TID: sav.DisplayTID.ToString(),
                    GameDisplay: gameName,
                    Version: sav.Version,
                    Language: sav.Language >= 0 ? (LanguageID)sav.Language : LanguageID.None,
                    Generation: sav.Generation,
                    BoxCount: sav.BoxCount,
                    FilledCount: filled,
                    PartyCount: sav.PartyCount,
                    PlayTime: $"{sav.PlayedHours}h {sav.PlayedMinutes}m"
                );
            }
        }
        catch { /* corrupt or unsupported — info stays null */ }

        lock (_gate) { _cache[key] = info; }
        return info;
    }

    /// <summary>Drops the entire cache.  Call when the user changes directories.</summary>
    public static void Clear()
    {
        lock (_gate) { _cache.Clear(); }
    }
}

/// <summary>
/// Scrollable card-list panel.  Owned by MainForm; placed inside savePanel and toggled
/// visible when no save is loaded AND a saves directory is configured.
/// </summary>
public sealed class SaveFileSelectorPanel : Panel
{
    private readonly Action<string> _onOpenSave;
    private readonly Action _onPickDirectory;
    private readonly FlowLayoutPanel _list;
    private readonly Label _emptyMessage;
    private readonly Button _setDirBtn;
    private readonly Panel _toolbar;
    private string? _savesDirectory;

    /// <summary>
    /// <paramref name="onOpenSave"/> is invoked when the user clicks "Open Box" on a card —
    /// it should be wired to MainForm's <c>LoadSaveFile(path)</c>.
    /// <paramref name="onPickDirectory"/> opens the directory chooser.
    /// <paramref name="onBrowseForSave"/> opens a file picker for arbitrary save files outside the directory.
    /// </summary>
    public SaveFileSelectorPanel(Action<string> onOpenSave, Action onPickDirectory, Action onBrowseForSave)
    {
        _onOpenSave = onOpenSave;
        _onPickDirectory = onPickDirectory;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(40, 40, 48);
        AutoScroll = false;  // children handle their own scrolling

        // === Toolbar (top) ===
        // Always-visible row with "Load Save (browse...)" + "Change Directory" + "Refresh" buttons.
        // Lets the user load a save from anywhere on disk even when a directory is configured.
        _toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(50, 55, 70),
        };
        var browseBtn = new Button
        {
            Text = "📂 Load Save (Browse...)",
            Width = 200, Height = 32,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 130, 180),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(8, 6),
        };
        browseBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(95, 155, 205);
        browseBtn.Click += (_, _) => onBrowseForSave();

        var changeDirBtn = new Button
        {
            Text = "Change Directory...",
            Width = 150, Height = 32,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(80, 90, 110),
            Font = new Font("Segoe UI", 9),
            Location = new Point(216, 6),
        };
        changeDirBtn.Click += (_, _) => onPickDirectory();

        var refreshBtn = new Button
        {
            Text = "↻ Refresh",
            Width = 90, Height = 32,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(80, 90, 110),
            Font = new Font("Segoe UI", 9),
            Location = new Point(374, 6),
        };
        refreshBtn.Click += (_, _) =>
        {
            // Force re-scan: clear the cache so any saves modified outside PKDen pick up new metadata.
            SaveInfoCache.Clear();
            Refresh_();
        };

        _toolbar.Controls.AddRange([browseBtn, changeDirBtn, refreshBtn]);

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            // LeftToRight + WrapContents=true gives a 3-per-row grid (or however many fit
            // at the current panel width).  Cards have a fixed width so the count per row
            // is predictable: ~250px card + 16px margin = 266px → at savePanel width 800
            // that's exactly 3 columns with comfortable padding.
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            BackColor = Color.FromArgb(40, 40, 48),
            // Padding biased larger on the left/right so the row of cards visually centers
            // within the panel even when fewer than 3 fit on the last row.
            Padding = new Padding(20, 16, 20, 16),
        };

        _emptyMessage = new Label
        {
            Text = "No saves directory set.\n\nUse File → Set Saves Directory… to choose a folder.\nSave files in that folder will appear here as cards.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 11),
            BackColor = Color.FromArgb(40, 40, 48),
        };

        _setDirBtn = new Button
        {
            Text = "Set Saves Directory…",
            Width = 220, Height = 36,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 130, 180),
            Anchor = AnchorStyles.None,
        };
        _setDirBtn.Click += (_, _) => onPickDirectory();

        // Order matters with Dock: Fill is added first, then Top docks above it.
        // _list is Dock=Fill so it takes whatever's left after the toolbar.
        Controls.Add(_list);
        Controls.Add(_emptyMessage);
        Controls.Add(_setDirBtn);
        Controls.Add(_toolbar);
        Resize += (_, _) => CenterEmptyState();

        // Refresh on first show as a defensive measure: if the panel was constructed
        // before the form had a proper layout, the initial Refresh_ would have run with
        // a zero-size client area.  By re-rendering the first time the panel actually
        // becomes visible we guarantee the cards exist when the user can see them.
        // Use a bool flag rather than unsubscribing because VisibleChanged can fire
        // multiple times during normal operation and we only want this one extra render.
        bool initialized = false;
        VisibleChanged += (_, _) =>
        {
            if (Visible && !initialized && !string.IsNullOrEmpty(_savesDirectory))
            {
                initialized = true;
                Refresh_();
            }
        };
    }

    /// <summary>
    /// Sets the saves directory and rebuilds the card list.  Pass null to clear.
    /// Re-uses the existing cache, so a directory the user picks again is instant.
    /// </summary>
    public void SetDirectory(string? path)
    {
        _savesDirectory = path;
        Refresh_();
    }

    /// <summary>Rebuilds the card list from disk.  Called after directory change or manual refresh.</summary>
    public void Refresh_()
    {
        _list.SuspendLayout();
        // Dispose previous card controls (and any artwork bitmaps they own) to avoid GDI leaks
        // when the user navigates back and forth.
        foreach (Control c in _list.Controls)
        {
            if (c is SaveCard card) card.DisposeArtwork();
            c.Dispose();
        }
        _list.Controls.Clear();

        bool hasDir = !string.IsNullOrEmpty(_savesDirectory) && Directory.Exists(_savesDirectory);
        _emptyMessage.Visible = !hasDir;
        _setDirBtn.Visible    = !hasDir;
        _list.Visible         = hasDir;
        CenterEmptyState();

        if (hasDir)
        {
            var saves = SaveInfoCache.Scan(_savesDirectory!);
            // Sort by game generation then version then OT name so the list reads in a
            // sensible order regardless of file/folder ordering on disk.
            saves.Sort((a, b) =>
            {
                int g = a.Generation.CompareTo(b.Generation);
                if (g != 0) return g;
                int v = string.CompareOrdinal(a.GameDisplay, b.GameDisplay);
                if (v != 0) return v;
                return string.CompareOrdinal(a.OT, b.OT);
            });

            foreach (var info in saves)
            {
                // Fixed-width card; SaveCard's constructor sets Width internally.
                // No dependence on _list.ClientSize — that was zero at startup before the
                // panel had laid out, leading to zero-width cards (the "empty until you
                // re-pick the directory" bug).
                var card = new SaveCard(info, _onOpenSave);
                _list.Controls.Add(card);
            }

            if (saves.Count == 0)
            {
                _emptyMessage.Text = $"No save files found in:\n{_savesDirectory}";
                _emptyMessage.Visible = true;
                _list.Visible = false;
                CenterEmptyState();
            }
            else
            {
                _emptyMessage.Text = ""; // reset for next time
            }
        }
        _list.ResumeLayout();
    }

    public string? Directory_ => _savesDirectory;

    /// <summary>
    /// Re-centers the "no directory set" message + button when the panel is resized.
    /// FlowLayoutPanel auto-handles cards, but the empty-state controls need manual layout.
    /// </summary>
    private void CenterEmptyState()
    {
        // Account for the toolbar (44px) docked at top — the empty message sits below it.
        const int toolbarHeight = 44;
        int avail = Math.Max(0, ClientSize.Height - toolbarHeight);
        _emptyMessage.Bounds = new Rectangle(0, toolbarHeight, ClientSize.Width, avail - 60);
        _setDirBtn.Location = new Point(
            (ClientSize.Width - _setDirBtn.Width) / 2,
            ClientSize.Height - 80);
    }

    /// <summary>
    /// Single save-file card.  Vertical layout: artwork on top, breakdown info below.
    /// The whole card is clickable — clicking anywhere on it (artwork, info text, or
    /// the surrounding border) opens that save file.  A hover effect (brighter border +
    /// slightly lighter background) tells the user the card is interactive.
    /// </summary>
    /// <remarks>
    /// We deliberately don't put a separate "Open Box" button on the card — the entire
    /// card surface IS the button.  This was a UX simplification: with three cards per
    /// row, an extra button per card created visual noise that the click-anywhere
    /// affordance avoids.  Border + hover state replace the textual "this is clickable"
    /// signal a button would have provided.
    ///
    /// Click delegation: every child control (PictureBox, Label) gets the same click
    /// handler attached so the user can click "into" the artwork or info text without
    /// the click being swallowed by the child.  WinForms doesn't bubble Click events
    /// from children to parents by default — hence the explicit attach-everywhere.
    /// </remarks>
    private sealed class SaveCard : Panel
    {
        private const int CardWidth = 240;
        private const int CardHeight = 320;
        private const int ArtHeight = 168;
        private const int BorderThickness = 2;

        private static readonly Color NormalBack = Color.FromArgb(50, 55, 70);
        private static readonly Color HoverBack  = Color.FromArgb(64, 72, 92);
        private static readonly Color NormalBorder = Color.FromArgb(80, 90, 110);
        private static readonly Color HoverBorder  = Color.FromArgb(120, 180, 240);

        private Bitmap? _artwork;
        private bool _hovered;
        private readonly PictureBox _pic;
        private readonly SaveInfo _info;

        public SaveCard(SaveInfo info, Action<string> onOpen)
        {
            _info = info;
            Width = CardWidth;
            Height = CardHeight;
            Margin = new Padding(8);
            BackColor = NormalBack;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            // === Artwork (top) ===
            _pic = new PictureBox
            {
                Bounds = new Rectangle(BorderThickness, BorderThickness,
                                       CardWidth - BorderThickness * 2, ArtHeight),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(30, 30, 38),
                Cursor = Cursors.Hand,
            };
            _artwork = GameArtResolver.LoadArtwork(info);
            _pic.Image = _artwork;
            Controls.Add(_pic);

            // === Info text (bottom) ===
            var infoLabel = new Label
            {
                Bounds = new Rectangle(BorderThickness + 8, ArtHeight + 8,
                                       CardWidth - BorderThickness * 2 - 16,
                                       CardHeight - ArtHeight - BorderThickness * 2 - 12),
                AutoSize = false,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f),
                Text = BuildInfoText(info),
                TextAlign = ContentAlignment.TopLeft,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
            };
            Controls.Add(infoLabel);

            // === Click → open this save ===
            void OpenHandler(object? _, EventArgs __) => onOpen(info.Path);
            Click       += OpenHandler;
            _pic.Click  += OpenHandler;
            infoLabel.Click += OpenHandler;

            // === Right-click → change thumbnail ===
            // Attached to the card itself AND children (same reason as Click handler:
            // events don't bubble through PictureBox/Label by default).  The menu offers
            // a "Change Thumbnail…" item that opens the picker dialog, plus "Reset to
            // Default" when the user has previously set a custom one.
            var menu = new ContextMenuStrip();
            var changeItem = new ToolStripMenuItem("Change Thumbnail…", null, (_, _) => ShowThumbnailPicker());
            var resetItem  = new ToolStripMenuItem("Reset to Default", null, (_, _) => ResetThumbnail());
            menu.Items.Add(changeItem);
            menu.Items.Add(resetItem);
            // Update menu state each time it opens so the Reset item only shows when relevant.
            menu.Opening += (_, _) =>
            {
                resetItem.Visible = ThumbnailOverrides.Get(info.Path) is not null;
            };
            ContextMenuStrip = menu;
            _pic.ContextMenuStrip = menu;
            infoLabel.ContextMenuStrip = menu;

            // === Hover effect ===
            void EnterHandler(object? _, EventArgs __) => SetHovered(true);
            void LeaveHandler(object? _, EventArgs __)
            {
                if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    SetHovered(false);
            }
            MouseEnter      += EnterHandler;
            MouseLeave      += LeaveHandler;
            _pic.MouseEnter += EnterHandler;
            _pic.MouseLeave += LeaveHandler;
            infoLabel.MouseEnter += EnterHandler;
            infoLabel.MouseLeave += LeaveHandler;
        }

        /// <summary>
        /// Opens the modal thumbnail-picker dialog.  When the user picks an image, persists
        /// the override and reloads this card's artwork so the change shows immediately.
        /// </summary>
        private void ShowThumbnailPicker()
        {
            using var dlg = new ThumbnailPickerDialog(_info);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            // Picker dialog persists the choice itself via ThumbnailOverrides.Set;
            // we just need to reload the local artwork to reflect it.
            ReloadArtwork();
        }

        /// <summary>Removes the user's custom thumbnail and reverts to auto-detected artwork.</summary>
        private void ResetThumbnail()
        {
            ThumbnailOverrides.Set(_info.Path, null);
            ReloadArtwork();
        }

        /// <summary>Reloads the artwork bitmap from the resolver and updates the picture box.</summary>
        private void ReloadArtwork()
        {
            _pic.Image = null;
            _artwork?.Dispose();
            _artwork = GameArtResolver.LoadArtwork(_info);
            _pic.Image = _artwork;
        }

        private void SetHovered(bool hovered)
        {
            if (_hovered == hovered) return;
            _hovered = hovered;
            BackColor = hovered ? HoverBack : NormalBack;
            Invalidate(); // redraw border with new color
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Border around the card so the click target is visually obvious — the user
            // shouldn't have to guess "is this clickable?" — it should look like a button-
            // shaped card from the start, and brighter on hover.
            using var pen = new Pen(_hovered ? HoverBorder : NormalBorder, BorderThickness);
            var rect = ClientRectangle;
            // DrawRectangle needs an inset so the line stays fully inside the bitmap.
            int inset = BorderThickness / 2;
            e.Graphics.DrawRectangle(pen, inset, inset,
                rect.Width - BorderThickness, rect.Height - BorderThickness);
        }

        public void DisposeArtwork()
        {
            _artwork?.Dispose();
            _artwork = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeArtwork();
            base.Dispose(disposing);
        }

        private static string BuildInfoText(SaveInfo info)
        {
            // Compact info layout — fewer rows per card since cards are now narrower
            // (240px vs the old 800+).  Game name + gen on one line, trainer/TID stacked,
            // box/party stats on a single line.  Filename is the visual "tag" at the bottom.
            return $"{info.GameDisplay}  •  Gen {info.Generation}\n" +
                   $"\n" +
                   $"Trainer: {info.OT}\n" +
                   $"TID: {info.TID}    Lang: {info.Language}\n" +
                   $"\n" +
                   $"Boxes: {info.BoxCount} ({info.FilledCount} stored)\n" +
                   $"Party: {info.PartyCount}    Played: {info.PlayTime}\n" +
                   $"\n" +
                   $"📁 {Path.GetFileName(info.Path)}";
        }
    }
}

/// <summary>
/// Modal dialog that displays every embedded artwork option as a clickable thumbnail
/// grid.  User picks one to set it as the override for the save passed in to the ctor.
/// "Reset to Default" entry at the top reverts to auto-detected artwork.
/// </summary>
/// <remarks>
/// Implementation is intentionally lo-fi — a FlowLayoutPanel of PictureBoxes with
/// click handlers.  Each picture is loaded at thumbnail resolution (~110×140) and
/// disposed when the dialog closes.  The dialog persists the chosen override via
/// <see cref="ThumbnailOverrides.Set"/> before returning DialogResult.OK, so the
/// caller just needs to reload the artwork.
/// </remarks>
public sealed class ThumbnailPickerDialog : Form
{
    private readonly SaveInfo _info;
    private readonly List<Bitmap> _thumbCache = new();

    public ThumbnailPickerDialog(SaveInfo info)
    {
        _info = info;
        Text = $"Choose Thumbnail — {Path.GetFileName(info.Path)}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 540);
        MinimumSize = new Size(500, 400);
        BackColor = Color.FromArgb(40, 40, 48);
        ForeColor = Color.White;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Text = "Click a thumbnail to set it for this save file. The choice is saved with PKDen and persists across sessions.",
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 9),
            Padding = new Padding(12, 8, 12, 0),
        };

        var resetBtn = new Button
        {
            Text = "Reset to Default (Auto-Detect)",
            Dock = DockStyle.Bottom,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 90, 110),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        resetBtn.Click += (_, _) =>
        {
            ThumbnailOverrides.Set(_info.Path, null);
            DialogResult = DialogResult.OK;
            Close();
        };

        var grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            BackColor = Color.FromArgb(40, 40, 48),
            Padding = new Padding(8),
        };

        // Build a tile per resource.  Sorted alphabetically by friendly label so similar
        // games cluster together (English variants near Japanese variants).
        var allResources = GameArtResolver.GetAllResourceNames()
            .OrderBy(n => GameArtResolver.GetFriendlyLabel(n), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var resourceName in allResources)
        {
            var tile = BuildTile(resourceName);
            if (tile is not null) grid.Controls.Add(tile);
        }

        // Order: header on top, reset on bottom, grid fills the middle.
        Controls.Add(grid);
        Controls.Add(resetBtn);
        Controls.Add(header);

        FormClosed += (_, _) =>
        {
            // Dispose the thumbnail bitmap cache so the dialog closes cleanly without
            // leaking GDI handles.  Each tile holds a reference but the form owns them.
            foreach (var bmp in _thumbCache)
            {
                try { bmp.Dispose(); } catch { }
            }
            _thumbCache.Clear();
        };
    }

    private Panel? BuildTile(string resourceName)
    {
        // Load the artwork at full resolution then let the PictureBox zoom-fit it.
        // Cache the loaded bitmap so the form's Dispose can release it.
        Bitmap? bmp = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var src = Image.FromStream(stream, false, false);
            bmp = new Bitmap(src);
        }
        catch { return null; }
        _thumbCache.Add(bmp);

        var tile = new Panel
        {
            Width = 130, Height = 170,
            Margin = new Padding(6),
            BackColor = Color.FromArgb(50, 55, 70),
            Cursor = Cursors.Hand,
        };
        var pic = new PictureBox
        {
            Bounds = new Rectangle(4, 4, 122, 130),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(30, 30, 38),
            Image = bmp,
            Cursor = Cursors.Hand,
        };
        var lbl = new Label
        {
            Bounds = new Rectangle(4, 136, 122, 30),
            Text = GameArtResolver.GetFriendlyLabel(resourceName),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 7.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        tile.Controls.Add(pic);
        tile.Controls.Add(lbl);

        // Hover highlight + click-to-select.  Same delegated-handler pattern as SaveCard
        // so a click on the picture or label does the same as a click on the tile panel.
        void Hover(bool on) { tile.BackColor = on ? Color.FromArgb(70, 90, 130) : Color.FromArgb(50, 55, 70); }
        void OnClick(object? _, EventArgs __)
        {
            ThumbnailOverrides.Set(_info.Path, resourceName);
            DialogResult = DialogResult.OK;
            Close();
        }
        tile.MouseEnter += (_, _) => Hover(true);
        tile.MouseLeave += (_, _) => Hover(false);
        pic.MouseEnter  += (_, _) => Hover(true);
        pic.MouseLeave  += (_, _) => Hover(false);
        lbl.MouseEnter  += (_, _) => Hover(true);
        lbl.MouseLeave  += (_, _) => Hover(false);
        tile.Click += OnClick;
        pic.Click  += OnClick;
        lbl.Click  += OnClick;
        return tile;
    }
}

// (End of file)
