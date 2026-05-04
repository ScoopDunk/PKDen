using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using PKHeX.Core;
using PKHeX.Drawing.PokeSprite;

namespace PKDen;

/// <summary>
/// Decodes and caches user-supplied custom sprite PNG bytes (set via the
/// per-Pokémon "Set Custom Sprite..." right-click option) into bitmaps at
/// the requested display size.
///
/// Storage of the raw PNG bytes lives in <see cref="DenStorageManager"/>;
/// this class just handles decode + scale + cache for the sprite render path.
///
/// === Cache structure ===
/// We cache at two layers:
///   • Master cache: identity key → original-size decoded bitmap (one-time decode cost)
///   • Sized cache:  (identity key, w, h) → scaled bitmap for that target size
///
/// Both are dropped when the user removes a custom sprite or when the den is
/// reloaded.  The cache is keyed by identity (PID+EC) — same key as in
/// <see cref="DenStorageManager"/> — so it stays consistent across slot moves.
///
/// === Aspect ratio ===
/// User-supplied sprites can be any size or aspect ratio (a tall portrait,
/// a wide landscape, a perfect square, etc.).  We honor the source aspect
/// ratio: bicubic-scale the longer dimension to fit the target box, then
/// center the image with transparent padding.  This matches the project's
/// existing aspect-preserve-and-pad convention used everywhere else.
/// </summary>
public static class CustomSpriteCache
{
    private static readonly Dictionary<ulong, Bitmap?> _master = new();
    private static readonly Dictionary<(ulong key, int w, int h), Bitmap?> _sized = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Returns a bitmap of <paramref name="bytes"/> decoded and scaled to
    /// (<paramref name="targetW"/> × <paramref name="targetH"/>), or null if
    /// the bytes can't be decoded.  Caller does NOT own the returned bitmap —
    /// it lives in the cache until <see cref="Invalidate"/> or <see cref="ClearAll"/>
    /// is called.
    /// </summary>
    /// <remarks>
    /// We return a cached, shared reference here (rather than a fresh clone) so the
    /// slot composer can blit it freely on every paint without paying the bicubic
    /// resample cost each frame.  The slot path treats sprites returned from this
    /// system as borrowed (no dispose) — see <see cref="PKDenSpriteUtil"/> for the
    /// fresh-bitmap copy that bridges to the slot's owned-image lifecycle.
    /// </remarks>
    public static Bitmap? GetSized(ulong identityKey, byte[] bytes, int targetW, int targetH)
    {
        lock (_gate)
        {
            var sizedKey = (identityKey, targetW, targetH);
            if (_sized.TryGetValue(sizedKey, out var cached))
                return cached;

            var master = GetMaster(identityKey, bytes);
            if (master is null)
            {
                _sized[sizedKey] = null;
                return null;
            }

            try
            {
                var scaled = ResampleAspect(master, targetW, targetH);
                _sized[sizedKey] = scaled;
                return scaled;
            }
            catch
            {
                _sized[sizedKey] = null;
                return null;
            }
        }
    }

    /// <summary>
    /// Drops cached bitmaps for a single identity (e.g. when the user replaces or
    /// removes that Pokémon's custom sprite).  Call this BEFORE updating the bytes
    /// in <see cref="DenStorageManager"/> to ensure stale cache entries don't
    /// linger.
    /// </summary>
    public static void Invalidate(ulong identityKey)
    {
        lock (_gate)
        {
            if (_master.TryGetValue(identityKey, out var bmp))
            {
                bmp?.Dispose();
                _master.Remove(identityKey);
            }
            // Drop all sized variants for this identity
            var toRemove = new List<(ulong, int, int)>();
            foreach (var kv in _sized)
            {
                if (kv.Key.key == identityKey)
                {
                    kv.Value?.Dispose();
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var k in toRemove) _sized.Remove(k);
        }
    }

    /// <summary>Drops every cached bitmap.  Used when the den is reloaded.</summary>
    public static void ClearAll()
    {
        lock (_gate)
        {
            foreach (var bmp in _master.Values) bmp?.Dispose();
            foreach (var bmp in _sized.Values)  bmp?.Dispose();
            _master.Clear();
            _sized.Clear();
        }
    }

    // -- Internal helpers ----------------------------------------------------

    private static Bitmap? GetMaster(ulong identityKey, byte[] bytes)
    {
        if (_master.TryGetValue(identityKey, out var existing))
            return existing;

        try
        {
            using var ms = new MemoryStream(bytes);
            // validateImageData=false to avoid full pixel scan; we already validated at import time
            var src = (Bitmap)Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            // Clone into a private bitmap that doesn't reference the disposed MemoryStream.
            var owned = new Bitmap(src);
            src.Dispose();
            _master[identityKey] = owned;
            return owned;
        }
        catch
        {
            _master[identityKey] = null;
            return null;
        }
    }

    /// <summary>
    /// Aspect-preserving bicubic resample of <paramref name="src"/> onto a fresh
    /// (targetW × targetH) ARGB canvas, centered with transparent padding.
    /// Same convention used by <see cref="SpriteOverrides"/> for embedded sprites.
    /// </summary>
    private static Bitmap ResampleAspect(Image src, int targetW, int targetH)
    {
        var dst = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
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
        return dst;
    }
}
