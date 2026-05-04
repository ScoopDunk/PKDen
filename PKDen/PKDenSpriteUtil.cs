using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using PKHeX.Core;
using PKHeX.Drawing;
using PKHeX.Drawing.PokeSprite;
using PKHeXResources = PKHeX.Drawing.PokeSprite.Properties.Resources;

namespace PKDen;

/// <summary>
/// PKDen's sprite resolution layer.  Resolution priority for any given Pokémon:
///
///   1. <b>Custom sprite</b>   — user-supplied PNG via right-click "Set Custom Sprite..."
///                                Lives in <see cref="DenStorageManager"/>; cached
///                                via <see cref="CustomSpriteCache"/>.
///   2. <b>Embedded override</b> — HOME-format sprite shipped inside the assembly
///                                (see <see cref="SpriteOverrides"/>).
///   3. <b>PKHeX sprite</b>     — fallback to whatever PKHeX's pipeline produces.
///
/// === Custom sprites and overlays ===
/// When a custom sprite is set, we DO NOT apply held-item, shiny-star, or alpha-aura
/// overlays.  Rationale: the user picked a specific image; overlays would obscure or
/// look weird on top of it.  The user can edit the source image themselves to include
/// any decorations they want.
///
/// === API surface ===
/// Two flavors of every entry point:
///   • <c>GetSprite</c> / <c>PKDenSprite</c>     — returns at canvas size (68×56 for SB8a)
///   • <c>GetSpriteAt</c> / <c>PKDenSpriteAt</c> — returns at an arbitrary target size,
///                                                composed in one pass at that resolution
///
/// === Overlay rendering quality (V0.1.4 update) ===
/// Previously, item icons and shiny stars were just bicubic-upscaled from their tiny
/// PKHeX source assets (25×25 items, 20×20 shiny star).  At 4× zoom that produced
/// visible blur because we were upscaling already-small antialiased source images.
///
/// Two fixes:
///   • <b>Shiny star</b>: Rendered programmatically as vector geometry via
///     <c>RenderShinyStar</c>.  Sharp at any size — no upscale artifacts.
///   • <b>Item icon</b>: Two-stage upscale via <c>UpscaleQuality</c>.  Hits
///     bilinear-prescale-to-2× → bicubic-to-target, which preserves edges
///     better than direct bicubic on small sources.
/// </summary>
public static class PKDenSpriteUtil
{
    // -- Canvas-size entry points (existing contract) ------------------------

    /// <summary>
    /// Override-aware version of <see cref="SpriteUtil.GetSprite(ushort, byte, byte, uint, int, bool, Shiny, EntityContext)"/>.
    /// Returns at the active SpriteBuilder canvas size (68×56 for SB8a Artwork).
    /// </summary>
    public static Bitmap GetSprite(ushort species, byte form, byte gender, uint formarg, int item, bool isegg, Shiny shiny, EntityContext context = EntityContext.None)
        => GetSpriteAt(species, form, gender, formarg, item, isegg, shiny, context,
                       SpriteUtil.Spriter.Width, SpriteUtil.Spriter.Height);

    /// <summary>
    /// Override-aware version of <c>pk.Sprite()</c>.
    /// Returns at the active SpriteBuilder canvas size (68×56 for SB8a Artwork).
    /// </summary>
    public static Bitmap PKDenSprite(this PKM pk)
        => pk.PKDenSpriteAt(SpriteUtil.Spriter.Width, SpriteUtil.Spriter.Height);

    // -- Target-size entry points --------------------------------------------

    /// <summary>
    /// Returns a sprite for a species/form combo at exactly (targetW × targetH).
    /// This entry point has no PKM context, so custom-sprite lookup is skipped.
    /// </summary>
    public static Bitmap GetSpriteAt(ushort species, byte form, byte gender, uint formarg, int item, bool isegg, Shiny shiny, EntityContext context, int targetW, int targetH)
    {
        if (isegg)
        {
            var pkhex = SpriteUtil.GetSprite(species, form, gender, formarg, item, isegg, shiny, context);
            return ResizeIfNeeded(pkhex, targetW, targetH);
        }

        var ovr = SpriteOverrides.TryGet(species, form, gender, shiny.IsShiny(), isGmax: false, targetW, targetH);
        if (ovr is null)
        {
            var pkhex = SpriteUtil.GetSprite(species, form, gender, formarg, item, isegg, shiny, context);
            return ResizeIfNeeded(pkhex, targetW, targetH);
        }

        return ApplyOverlays(ovr, item, shiny, context, targetW, targetH);
    }

    /// <summary>
    /// Override-aware version of <c>pk.Sprite()</c> at a specific target size.
    /// Checks for a per-Pokémon custom sprite first, then falls back to the embedded
    /// HOME overrides, then to PKHeX's pipeline.
    /// </summary>
    public static Bitmap PKDenSpriteAt(this PKM pk, int targetW, int targetH)
        => pk.PKDenSpriteAt(targetW, targetH, hideItem: false);

    /// <summary>
    /// Same as <see cref="PKDenSpriteAt(PKM, int, int)"/> but lets the caller suppress
    /// the held-item overlay while keeping every other PKM-aware feature intact —
    /// most importantly, custom-sprite lookup.
    /// </summary>
    /// <remarks>
    /// This overload exists because the den-grid render path needs to honor the
    /// "Show Held Item" toggle.  Previously it called <see cref="GetSpriteAt"/>
    /// with item=0 to drop the item overlay, but that overload has NO PKM context,
    /// so it silently bypassed the custom-sprite lookup.  Symptom: custom sprites
    /// rendered correctly in the summary panel (which uses PKDenSpriteAt) but
    /// disappeared from the den grid as soon as the user turned off "Show Held Item".
    /// Always going through the PKM-aware path here keeps the custom-sprite lookup
    /// in scope regardless of the toggle.
    /// </remarks>
    public static Bitmap PKDenSpriteAt(this PKM pk, int targetW, int targetH, bool hideItem)
    {
        // 1. Custom sprite — highest priority. Skip ALL overlays (item & shiny).
        if (DenRef is not null && DenRef.HasCustomSprite(pk))
        {
            var bytes = DenRef.GetCustomSpriteBytes(pk);
            if (bytes is not null)
            {
                ulong key = DenStorageManager.GetPkIdentityKey(pk);
                var custom = CustomSpriteCache.GetSized(key, bytes, targetW, targetH);
                if (custom is not null)
                    return new Bitmap(custom); // Cache returns shared ref; clone for caller ownership.
            }
            // If we get here, the bytes failed to decode — fall through to normal pipeline.
        }

        // Defer to PKHeX for cases we don't override (eggs, shadow Lugia).
        if (pk.IsEgg) return ResizeIfNeeded(pk.Sprite(), targetW, targetH);
        if (pk is IShadowCapture { IsShadow: true }) return ResizeIfNeeded(pk.Sprite(), targetW, targetH);

        var formarg = pk is IFormArgument f ? f.FormArgument : 0u;
        var shiny = ShinyExtensions.GetType(pk);
        bool wantsGmax = pk is IGigantamaxReadOnly { CanGigantamax: true };

        var ovr = wantsGmax ? SpriteOverrides.TryGet(pk.Species, pk.Form, pk.Gender, shiny.IsShiny(), isGmax: true, targetW, targetH)
                            : SpriteOverrides.TryGet(pk.Species, pk.Form, pk.Gender, shiny.IsShiny(), isGmax: false, targetW, targetH);

        if (ovr is null)
            return ResizeIfNeeded(pk.Sprite(), targetW, targetH);

        // hideItem suppresses just the held-item icon while keeping the shiny star
        // and other overlays.  Pass item=0 to ApplyOverlays to skip the item layer
        // without touching the rest of the composition.
        int itemForOverlay = hideItem ? 0 : pk.SpriteItem;
        var img = ApplyOverlays(ovr, itemForOverlay, shiny, pk.Context, targetW, targetH);

        if (wantsGmax && !SpriteOverrides.HasOverride(pk.Species, pk.Form, pk.Gender, shiny.IsShiny(), isGmax: true))
        {
            var gm = ScaleOverlay(PKHeXResources.dyna, targetW, targetH);
            var layered = ImageUtil.LayerImage(img, gm, (img.Width - gm.Width) / 2, 0);
            if (!ReferenceEquals(layered, img)) { img.Dispose(); img = layered; }
            gm.Dispose();
        }

        if (pk is IAlphaReadOnly { IsAlpha: true })
        {
            var alphaScaled = ScaleOverlay(PKHeXResources.alpha_alt, targetW, targetH);
            int shiftX = img.Width - (int)Math.Round(19.0 * targetW / SpriteUtil.Spriter.Width);
            var layered = ImageUtil.LayerImage(img, alphaScaled, shiftX, 0);
            if (!ReferenceEquals(layered, img)) { img.Dispose(); img = layered; }
            alphaScaled.Dispose();
        }
        return img;
    }

    // -- Den manager reference (for custom sprite lookup) --------------------

    /// <summary>
    /// The active den storage instance.  Set once at startup by MainForm so the
    /// sprite util can look up custom sprites without a hard dependency on Form
    /// state.  Static field is acceptable here because PKDen is single-window.
    /// </summary>
    public static DenStorageManager? DenRef { get; set; }

    // -- Overlay composition -------------------------------------------------

    private static Bitmap ApplyOverlays(Bitmap baseSprite, int heldItem, Shiny shiny, EntityContext context,
                                        int targetW, int targetH)
    {
        // baseSprite is already a fresh bitmap from SpriteOverrides — adopt ownership.
        Bitmap result = baseSprite;

        // Item overlay — see UpscaleQuality for the two-stage scaling logic that
        // produces sharper output than direct bicubic on small antialiased sources.
        if (heldItem > 0)
        {
            var itemImgRaw = SpriteUtil.GetItemSpriteA(heldItem);
            if (itemImgRaw is not null)
            {
                int itemMaxSize = (int)Math.Round(32.0 * targetW / SpriteUtil.Spriter.Width);
                // Scale the item icon using two-stage upscale rather than a single bicubic
                // call.  Final size matches PKHeX's item-on-sprite convention proportionally.
                float fx = (float)targetW / SpriteUtil.Spriter.Width;
                float fy = (float)targetH / SpriteUtil.Spriter.Height;
                float f  = Math.Min(fx, fy);
                int w = Math.Max(1, (int)Math.Round(itemImgRaw.Width  * f));
                int h = Math.Max(1, (int)Math.Round(itemImgRaw.Height * f));
                using var itemImg = UpscaleQuality(itemImgRaw, w, h);

                int itemShiftX = (int)Math.Round(2.0 * targetW / SpriteUtil.Spriter.Width);
                int itemShiftY = (int)Math.Round(2.0 * targetH / SpriteUtil.Spriter.Height);
                int x = result.Width  - itemImg.Width  - ((itemMaxSize - itemImg.Width) / 4) - itemShiftX;
                int y = result.Height - itemImg.Height - itemShiftY;
                var layered = ImageUtil.LayerImage(result, itemImg, x, y);
                if (!ReferenceEquals(layered, result)) { result.Dispose(); result = layered; }
            }
        }

        // Shiny star — vector-rendered for crisp edges at any size.
        if (shiny.IsShiny())
        {
            float fx = (float)targetW / SpriteUtil.Spriter.Width;
            float fy = (float)targetH / SpriteUtil.Spriter.Height;
            float f  = Math.Min(fx, fy);
            // PKHeX's source star is 20px on a 68px canvas — about 29% of canvas width.
            // We render at the same proportion of the target.
            int starSize = Math.Max(8, (int)Math.Round(20 * f));
            bool useSquareVariant = shiny == Shiny.AlwaysSquare && context.IsSquareShinyDifferentiated;
            using var star = RenderShinyStar(starSize, useSquareVariant);

            // Blend with 0.7 alpha to match PKHeX's existing visual weight.
            var layered = ImageUtil.LayerImage(result, star, 0, 0, 0.7);
            if (!ReferenceEquals(layered, result)) { result.Dispose(); result = layered; }
        }

        return result;
    }

    /// <summary>
    /// Renders a shiny-star icon at exactly (<paramref name="size"/> × <paramref name="size"/>)
    /// using vector geometry rather than upscaling a small raster source.  Result is
    /// edge-crisp at any size.
    /// </summary>
    /// <param name="size">Output bitmap dimension (square).</param>
    /// <param name="square">When true, draws a square-shiny variant (smaller central
    /// rhombus to visually distinguish from regular shiny — matches PKHeX's
    /// <c>rare_icon_alt_2</c> use case).</param>
    /// <remarks>
    /// Geometry: a 4-point star (also called a "sparkle" or "twinkle"), formed by two
    /// long axes (vertical, horizontal) and four shorter diagonal axes between them.
    /// The classic Pokémon shiny-symbol shape.  Filled with a yellow→white→yellow
    /// vertical gradient that matches the PKHeX asset's color scheme.
    /// </remarks>
    private static Bitmap RenderShinyStar(int size, bool square)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float cx = size / 2f;
        float cy = size / 2f;
        // Outer extent (arm tips) and inner extent (waist between arms).  Tweaked to
        // match PKHeX's rare_icon_alt visual proportions.
        float outerR = size * 0.46f;
        float innerR = size * (square ? 0.10f : 0.13f);

        // 8 vertices alternating outer/inner around 360°, starting at top.
        var pts = new PointF[8];
        for (int i = 0; i < 8; i++)
        {
            double angle = -Math.PI / 2 + i * (Math.PI / 4); // start at 12 o'clock, advance 45°
            float r = (i % 2 == 0) ? outerR : innerR;
            pts[i] = new PointF(cx + (float)(r * Math.Cos(angle)),
                                cy + (float)(r * Math.Sin(angle)));
        }

        using var path = new GraphicsPath();
        path.AddPolygon(pts);

        // Fill: vertical gradient yellow → white-ish → yellow, matches the PKHeX asset feel.
        var rect = new RectangleF(0, 0, size, size);
        using var brush = new LinearGradientBrush(rect,
            Color.FromArgb(255, 255, 220, 60),
            Color.FromArgb(255, 255, 250, 200),
            LinearGradientMode.Vertical);
        // Tweak color blend to keep edges saturated, center bright.
        brush.InterpolationColors = new ColorBlend(3)
        {
            Colors = new[]
            {
                Color.FromArgb(255, 255, 200, 40),
                Color.FromArgb(255, 255, 250, 220),
                Color.FromArgb(255, 255, 200, 40),
            },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        g.FillPath(brush, path);

        // Subtle outline so the star reads cleanly against light Pokémon sprites
        // (Pikachu, Cubone, etc.).  Width scales with size so it stays proportional.
        using var pen = new Pen(Color.FromArgb(220, 180, 100, 0), Math.Max(1f, size / 20f));
        pen.LineJoin = LineJoin.Round;
        g.DrawPath(pen, path);

        return bmp;
    }

    /// <summary>
    /// Two-stage upscale that produces noticeably sharper output on small antialiased
    /// source images than a single bicubic step.  Stage 1 is a fast NearestNeighbor
    /// upscale to ~2× source (preserves hard edges), Stage 2 is a HighQualityBicubic
    /// downscale to the final target (smooths the staircase artifacts).
    /// </summary>
    /// <remarks>
    /// Why this is better than direct bicubic upscale: bicubic interpolation samples
    /// from a 4×4 neighborhood weighted by a cubic kernel, which softens edges
    /// because every output pixel is a weighted blend of several inputs.  When the
    /// source is already tiny (25×25 item icons), each output pixel ends up too
    /// blended.  Pre-blowing-up the source via NearestNeighbor first gives bicubic
    /// more "real" pixels to sample from, recovering edge crispness.
    ///
    /// Falls back to a no-op clone when target size matches source.
    /// </remarks>
    private static Bitmap UpscaleQuality(Bitmap src, int targetW, int targetH)
    {
        if (targetW <= src.Width && targetH <= src.Height)
        {
            // We're downscaling — single bicubic pass is fine.
            var dst = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, 0, 0, targetW, targetH);
            return dst;
        }

        // Two-stage upscale path.  Pick an intermediate size that's at least 2× source
        // but capped at the target — for very small upscales (<2×) just go direct.
        int interW = Math.Min(targetW, src.Width * 2);
        int interH = Math.Min(targetH, src.Height * 2);
        if (interW <= src.Width || interH <= src.Height)
        {
            // Edge case: target barely larger than source. Direct bicubic.
            var dst = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            g.DrawImage(src, 0, 0, targetW, targetH);
            return dst;
        }

        // Stage 1: NearestNeighbor upscale to 2× (preserves edge contrast)
        using var inter = new Bitmap(interW, interH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(inter))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);
            g.DrawImage(src, 0, 0, interW, interH);
        }

        // Stage 2: HighQualityBicubic to final target (smooths the chunky pixels)
        var final = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(inter, 0, 0, targetW, targetH);
        }
        return final;
    }

    /// <summary>
    /// Scales an overlay icon proportionally to the target canvas size using
    /// HighQualityBicubic.  Used for the dyna marker and alpha aura — these come
    /// from larger source PNGs (44×44 and ~30×30 respectively) so the simpler path
    /// is fine here.  For the small-source case (item icons), see <see cref="UpscaleQuality"/>.
    /// </summary>
    private static Bitmap ScaleOverlay(Bitmap src, int targetW, int targetH)
    {
        float fx = (float)targetW / SpriteUtil.Spriter.Width;
        float fy = (float)targetH / SpriteUtil.Spriter.Height;
        float f  = Math.Min(fx, fy);
        int w = Math.Max(1, (int)Math.Round(src.Width  * f));
        int h = Math.Max(1, (int)Math.Round(src.Height * f));

        if (w == src.Width && h == src.Height)
            return new Bitmap(src);

        var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    /// <summary>
    /// Resamples <paramref name="src"/> to (<paramref name="targetW"/> × <paramref name="targetH"/>)
    /// using bicubic interpolation, ALWAYS returning a freshly allocated bitmap.
    /// See ownership note for rationale.
    /// </summary>
    /// <remarks>
    /// CRITICAL OWNERSHIP NOTE: PKHeX's <c>SpriteUtil.GetSprite</c> and
    /// <c>pk.Sprite()</c> return CACHED bitmaps that must NEVER be disposed by us.
    /// PKDen's slot composer disposes any sprite it receives.  To bridge these
    /// two contracts safely, this method always returns a freshly allocated bitmap.
    /// </remarks>
    private static Bitmap ResizeIfNeeded(Bitmap src, int targetW, int targetH)
    {
        if (src.Width == targetW && src.Height == targetH)
            return new Bitmap(src);

        var dst = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
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
