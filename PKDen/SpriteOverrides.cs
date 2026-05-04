using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using PKHeX.Drawing.PokeSprite;

namespace PKDen;

/// <summary>
/// Indexes sprite-override PNGs that are <b>embedded as resources inside the PKDen
/// assembly itself</b> — no external folders, no shipping a sprite pack alongside
/// the EXE.  Files included via the &lt;EmbeddedResource&gt; glob in PKDen.csproj are
/// given LogicalNames of the form <c>PKDen.Sprites.&lt;filename&gt;</c>, which we
/// enumerate and parse at startup.
///
/// Currently supports the Pokémon HOME naming convention (e.g.
/// <c>poke_capture_0001_000_mf_n_00000000_f_n.png</c>):
///
///   poke_capture_{species:NNNN}_{form:FFF}_{gender}_{n|g}_{formarg:00000000}_f_{n|r}.png
///
/// Where:
///   • gender = mf|fd|md|fo|mo|uk
///       mf = unisex,  fd/md = gendered "different" sprite,
///       fo/mo = gender-only species,  uk = genderless
///   • n|g = "n" normal,  "g" gigantamax variant of this form
///   • _f_n = front normal,  _f_r = front shiny
///
/// === Caching strategy ===
/// Resources are indexed (key → resource name) at startup, but the actual PNG
/// decode + scale is deferred until first request.  Once decoded, sprites are
/// stored in a "master" cache at <see cref="CacheW"/>×<see cref="CacheH"/>
/// (4× the active SpriteBuilder canvas, e.g. 272×224 for SB8a Artwork mode).
/// Callers request the sprite at any target size via <see cref="TryGet"/>;
/// each call returns a freshly allocated bitmap downscaled (or upscaled) from
/// the master with bicubic interpolation, preserving aspect with transparent
/// padding around the centered image.
///
/// Why 4× canvas as the master?  Three reasons:
///   1. It exactly matches PKDen's typical max display zoom (4×), so the most
///      common request is a 1:1 blit with no resampling at all.
///   2. Going from a 512×512 source down to 272×224 retains far more detail
///      than the previous 68×56 master, which threw away ~98% of pixel data.
///   3. Memory cost is bounded: ~244KB per cached sprite × ~472 sprites worst
///      case = ~115MB, vs. ~2GB if we cached at native 512×512 source res.
///
/// At zoom levels above 4× the master gets bicubic-upscaled, which is softer
/// but still strictly better than the prior nearest-neighbor pipeline.
/// </summary>
public static class SpriteOverrides
{
    // -- Cache resolution constants -------------------------------------------

    /// <summary>Master cache width — 4× the active SpriteBuilder canvas width.</summary>
    private static readonly int CacheW = SpriteUtil.Spriter.Width  * 4;
    /// <summary>Master cache height — 4× the active SpriteBuilder canvas height.</summary>
    private static readonly int CacheH = SpriteUtil.Spriter.Height * 4;

    // -- Composite key -------------------------------------------------------

    /// <summary>Internal lookup key for the override map.</summary>
    private readonly record struct Key(ushort Species, byte Form, byte GenderCode, bool IsGmax, bool IsShiny);

    // Gender code constants used in our internal key (NOT the same as PKM.Gender values)
    private const byte G_Unisex   = 0;  // mf
    private const byte G_Male     = 1;  // md or mo
    private const byte G_Female   = 2;  // fd or fo
    private const byte G_Unknown  = 3;  // uk

    // -- Storage -------------------------------------------------------------

    /// <summary>Maps lookup key → manifest resource name (full LogicalName).</summary>
    private static readonly Dictionary<Key, string> _resources = new();
    /// <summary>Decoded master cache.  Null entry = "tried, not present" (negative cache).</summary>
    private static readonly Dictionary<Key, Bitmap?> _master = new();
    private static readonly object _gate = new();
    private static Assembly? _spriteAssembly;

    // -- Filename pattern ----------------------------------------------------

    /// <summary>
    /// Matches both bare filenames and our LogicalName prefix
    /// (<c>PKDen.Sprites.poke_capture_...</c>) — anchored at the
    /// <c>poke_capture_</c> token so either form parses cleanly.
    /// </summary>
    private static readonly Regex Pattern = new(
        @"poke_capture_(?<sp>\d{4})_(?<form>\d{3})_(?<gen>mf|fd|md|fo|mo|uk)_(?<gmax>n|g)_(?<arg>\d{8})_f_(?<sh>n|r)\.png$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // -- Public API ----------------------------------------------------------

    /// <summary>Number of override entries currently indexed.</summary>
    public static int OverrideCount { get { lock (_gate) return _resources.Count; } }

    /// <summary>
    /// Scans the running PKDen assembly's embedded resources for sprite files
    /// matching the HOME naming convention and builds the override index.
    /// Idempotent — clears prior state on each call.
    /// </summary>
    /// <returns>Count of resources successfully indexed.</returns>
    public static int Initialize()
    {
        lock (_gate)
        {
            _resources.Clear();
            DisposeMaster();

            _spriteAssembly = typeof(SpriteOverrides).Assembly;

            try
            {
                foreach (var name in _spriteAssembly.GetManifestResourceNames())
                {
                    var m = Pattern.Match(name);
                    if (!m.Success) continue;

                    if (!ushort.TryParse(m.Groups["sp"].Value, out var species)) continue;
                    if (!byte.TryParse(m.Groups["form"].Value, out var form)) continue;

                    byte gender = m.Groups["gen"].Value.ToLowerInvariant() switch
                    {
                        "mf"        => G_Unisex,
                        "md" or "mo" => G_Male,
                        "fd" or "fo" => G_Female,
                        "uk"        => G_Unknown,
                        _           => G_Unisex,
                    };
                    bool isGmax  = string.Equals(m.Groups["gmax"].Value, "g", StringComparison.OrdinalIgnoreCase);
                    bool isShiny = string.Equals(m.Groups["sh"].Value,   "r", StringComparison.OrdinalIgnoreCase);

                    var key = new Key(species, form, gender, isGmax, isShiny);
                    _resources[key] = name;
                }
            }
            catch
            {
                // Swallow — sprite overrides are best-effort. PKHeX's built-in sprites
                // remain available as fallback if manifest enumeration fails for any reason.
            }
            return _resources.Count;
        }
    }

    /// <summary>
    /// Resolves an override sprite for the given Pokémon parameters at the
    /// requested target size.  See class docs for fallback chain details.
    /// </summary>
    /// <param name="species">National Dex species ID.</param>
    /// <param name="form">PKHeX form index.</param>
    /// <param name="gender">PKM gender (0 = male, 1 = female, 2 = genderless).</param>
    /// <param name="isShiny">True when shiny variant requested.</param>
    /// <param name="isGmax">True when caller wants the Gigantamax variant.</param>
    /// <param name="targetW">Desired output bitmap width (e.g. 68 for canvas, 272 for 4× zoom).</param>
    /// <param name="targetH">Desired output bitmap height.</param>
    /// <returns>
    /// A freshly allocated <see cref="Bitmap"/> at exactly (targetW × targetH),
    /// or null if no override matches.  Caller owns the returned bitmap and is
    /// responsible for disposing it.
    /// </returns>
    public static Bitmap? TryGet(ushort species, byte form, byte gender, bool isShiny, bool isGmax,
                                 int targetW, int targetH)
    {
        // Map PKM.Gender → our preferred lookup gender code, then walk the fallback chain
        byte preferred = gender switch
        {
            0 => G_Male,
            1 => G_Female,
            2 => G_Unknown,
            _ => G_Unisex,
        };

        var master = TryGetMaster(species, form, preferred, isShiny, isGmax)
                  ?? (preferred != G_Unisex  ? TryGetMaster(species, form, G_Unisex,  isShiny, isGmax) : null)
                  ?? (preferred != G_Unknown ? TryGetMaster(species, form, G_Unknown, isShiny, isGmax) : null);

        // Shiny fallback to non-shiny base if no shiny override exists
        if (master is null && isShiny)
        {
            master = TryGetMaster(species, form, preferred, false, isGmax)
                  ?? (preferred != G_Unisex  ? TryGetMaster(species, form, G_Unisex,  false, isGmax) : null)
                  ?? (preferred != G_Unknown ? TryGetMaster(species, form, G_Unknown, false, isGmax) : null);
        }

        if (master is null) return null;
        return ResampleToTarget(master, targetW, targetH);
    }

    /// <summary>
    /// Convenience overload that returns the canvas-sized (68×56 for SB8a) version.
    /// Equivalent to calling <see cref="TryGet(ushort, byte, byte, bool, bool, int, int)"/>
    /// with target = active SpriteBuilder canvas dimensions.
    /// </summary>
    public static Bitmap? TryGet(ushort species, byte form, byte gender, bool isShiny, bool isGmax)
        => TryGet(species, form, gender, isShiny, isGmax, SpriteUtil.Spriter.Width, SpriteUtil.Spriter.Height);

    /// <summary>
    /// Cheap "does an override exist?" check — walks the same fallback chain as
    /// <see cref="TryGet"/> but stops at the index lookup, never decoding a bitmap.
    /// Useful for callers that need to make a behavioral decision (e.g. "should
    /// we add the dyna marker?") based on whether an override is present without
    /// paying for an actual sprite render.
    /// </summary>
    public static bool HasOverride(ushort species, byte form, byte gender, bool isShiny, bool isGmax)
    {
        byte preferred = gender switch
        {
            0 => G_Male,
            1 => G_Female,
            2 => G_Unknown,
            _ => G_Unisex,
        };

        lock (_gate)
        {
            if (_resources.ContainsKey(new Key(species, form, preferred, isGmax, isShiny))) return true;
            if (preferred != G_Unisex  && _resources.ContainsKey(new Key(species, form, G_Unisex,  isGmax, isShiny))) return true;
            if (preferred != G_Unknown && _resources.ContainsKey(new Key(species, form, G_Unknown, isGmax, isShiny))) return true;

            if (isShiny)
            {
                if (_resources.ContainsKey(new Key(species, form, preferred, isGmax, false))) return true;
                if (preferred != G_Unisex  && _resources.ContainsKey(new Key(species, form, G_Unisex,  isGmax, false))) return true;
                if (preferred != G_Unknown && _resources.ContainsKey(new Key(species, form, G_Unknown, isGmax, false))) return true;
            }
            return false;
        }
    }

    // -- Internal helpers ----------------------------------------------------

    /// <summary>
    /// Looks up the master (cached, hi-res) bitmap for an exact key, decoding from
    /// the manifest stream on first access.  Returns null if no resource matches
    /// the key, OR if decode failed previously (negative cache).
    /// </summary>
    private static Bitmap? TryGetMaster(ushort species, byte form, byte gender, bool shiny, bool gmax)
    {
        var key = new Key(species, form, gender, gmax, shiny);
        lock (_gate)
        {
            if (_master.TryGetValue(key, out var existing))
                return existing;

            if (!_resources.TryGetValue(key, out var resourceName) || _spriteAssembly is null)
            {
                _master[key] = null;
                return null;
            }

            try
            {
                var bmp = DecodeAndNormalizeToMaster(_spriteAssembly, resourceName);
                _master[key] = bmp;
                return bmp;
            }
            catch
            {
                _master[key] = null;
                return null;
            }
        }
    }

    /// <summary>
    /// Loads a PNG from the assembly manifest and downscales it to the master
    /// cache resolution (CacheW × CacheH), preserving aspect ratio with
    /// transparent padding around the centered image.
    /// </summary>
    private static Bitmap? DecodeAndNormalizeToMaster(Assembly asm, string resourceName)
    {
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        // Buffer the resource into a private memory stream so the source bitmap doesn't hold
        // a reference to the manifest stream after we leave this method.
        byte[] raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        using var ms = new MemoryStream(raw);
        using var src = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

        return Resample(src, CacheW, CacheH);
    }

    /// <summary>
    /// Returns a fresh bitmap at exactly (targetW × targetH) sourced from the
    /// given master bitmap, using bicubic interpolation.  No-op clone path when
    /// dimensions already match — saves a redundant pixel copy.
    /// </summary>
    private static Bitmap ResampleToTarget(Bitmap master, int targetW, int targetH)
    {
        // Always allocate a fresh bitmap so the caller can dispose freely without
        // affecting the cached master.
        return Resample(master, targetW, targetH);
    }

    /// <summary>
    /// Aspect-preserving bicubic resample of <paramref name="src"/> onto a fresh
    /// (targetW × targetH) ARGB canvas, centered with transparent padding.
    /// </summary>
    private static Bitmap Resample(Image src, int targetW, int targetH)
    {
        var dst = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.SmoothingMode      = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            float scale = Math.Min((float)targetW / src.Width, (float)targetH / src.Height);
            int w = (int)Math.Round(src.Width  * scale);
            int h = (int)Math.Round(src.Height * scale);
            int x = (targetW - w) / 2;
            int y = (targetH - h) / 2;
            g.DrawImage(src, new Rectangle(x, y, w, h));
        }
        return dst;
    }

    private static void DisposeMaster()
    {
        foreach (var bmp in _master.Values)
            bmp?.Dispose();
        _master.Clear();
    }
}
