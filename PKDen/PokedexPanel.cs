using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PKHeX.Core;
using PKHeX.Drawing.PokeSprite;

namespace PKDen;

/// <summary>
/// National Pokédex view. Renders a scrollable grid of all species (and their distinct forms)
/// and shows which ones the user owns in their Den.
///
/// Architecture (rewritten 2026-04-27):
/// - The dex grid is a SINGLE custom-painted Panel, not ~1400 PictureBoxes. Creating 1400 Win32
///   HWNDs takes ~32 seconds (measured); a single panel with custom paint is instant. Tiles
///   are now virtual: a list of (species, form, bounds, owned flag, sprite cache) records.
/// - The canvas Paint handler iterates only tiles that intersect the clip rectangle, so scrolling
///   only paints the visible viewport regardless of total tile count.
/// - Click → coordinate hit-test → tile selection. Mirrors what 1400 PictureBox click handlers
///   would have done, but with one event handler.
/// </summary>
public sealed class PokedexPanel : UserControl
{
    private const int MaxSpecies = 1025;
    /// <summary>
    /// Pokédex tile dimension.  Users can change it from the top-bar "Tile Size" combo
    /// to make the dex grid more compact (32px) or more detailed (96px).  Changing this
    /// invalidates every tile's cached <c>OwnedImage</c>/<c>UnownedImage</c> so the
    /// next paint redraws at the new size with bicubic-downscaled hi-res masters.
    /// </summary>
    /// <remarks>
    /// Static (not instance) because the tile-image cache lives on individual <c>DexTile</c>
    /// records that have no back-reference to the panel.  Static keeps the lookup
    /// trivial inside <see cref="GetTileImage"/> without needing to plumb a panel reference.
    /// </remarks>
    private static int TileSize = 48;
    /// <summary>Persisted preference name for the tile size — written to PKDen.dexsettings on change.</summary>
    private const string TileSizeSettingKey = "TileSize";
    /// <summary>
    /// Pixel dimension of the detail-panel sprite PictureBox.  Bitmaps fed to
    /// <c>_detailSprite</c> are sized to (DetailSpriteSize × DetailSpriteSize) so
    /// the PictureBox.Zoom blit is 1:1 — no upscale interpolation, the source PNG's
    /// detail survives all the way to the display.  Must match the Width/Height of
    /// the PictureBox itself (see ctor / panel layout).
    /// </summary>
    private const int DetailSpriteSize = 160;
    private const int TileGap = 2;
    private const int TilePadding = 4;
    private const int DetailPanelWidth = 240;

    private readonly DenStorageManager _den;
    private readonly Action<PKM, IWin32Window?> _exportPK;

    private readonly ComboBox _filterCombo;
    private readonly Label _ownedTotalLabel;

    // The dex canvas — one scrollable Panel that custom-paints all tiles
    private readonly Panel _dexCanvas;
    private readonly List<DexTile> _tiles = [];

    // Right detail panel
    private readonly Panel _detailPanel;
    private readonly PictureBox _detailSprite;
    private readonly Label _detailDexNumber;
    private readonly Label _detailSpeciesName;
    private readonly Button _detailPrev;
    private readonly Button _detailNext;
    private readonly Label _detailPkIndex;
    private readonly Label _detailSummary;
    private readonly Button _detailExport;

    private DexTile? _selectedTile;
    private List<PKM> _selectedOwnedPks = [];
    private int _selectedPkIndex;

    private readonly Dictionary<(ushort Species, byte Form), List<PKM>> _ownedByForm = new();

    private int _columns = 1;
    private int _gridRows;

    private int _generationFilter;
    /// <summary>
    /// Owned-state filter for the dex grid. 0 = Both (default), 1 = Owned only, 2 = Unowned only.
    /// Combines with shiny/gender filters: when "Owned only" + Shiny + Male, a tile is highlighted
    /// only if the user owns a shiny male of that species. "Unowned only" hides owned tiles
    /// regardless of the other filters.
    /// </summary>
    private int _ownedStateFilter;
    private bool _shinyFilter;
    private bool _genderMaleFilter;
    private bool _genderFemaleFilter;
    private bool _genderGenderlessFilter;
    /// <summary>When true, hidden tiles are still rendered (faded with a strikethrough X) so
    /// the user can right-click to unhide them. When false (default), hidden tiles are skipped
    /// entirely from layout, paint, and hit-testing — the grid reflows seamlessly.</summary>
    private bool _showHidden;
    private CheckBox _showHiddenCheck = null!;

    /// <summary>
    /// When true, the dex grid inserts a blank row between consecutive generations so
    /// the user can visually identify gen boundaries while scrolling.  Pure cosmetic —
    /// doesn't filter any species out, just adds a visual break.  Persisted to
    /// PKDen.dexsettings so the preference survives across sessions.
    /// </summary>
    private bool _groupByGen;

    // === Selection style — mirrors the Den's View → Selection Style options.
    // Color and thickness apply to the rectangle drawn around the currently-selected
    // tile in the dex grid.  Persisted to PKDen.dexsettings.
    private Color _dexSelectionColor = Color.FromArgb(80, 140, 220);
    private int _dexSelectionThickness = 3;

    // === Detail-panel summary font — controls the font of the long body text in
    // the right-hand detail panel ("Click a Pokémon to view its summary here.").
    // Defaults match the previous hardcoded Segoe UI 8.5pt regular.
    private float _dexSummaryFontSize = 8.5f;
    private bool _dexSummaryFontBold;

    public PokedexPanel(DenStorageManager den, Action<PKM, IWin32Window?> exportPkCallback)
    {
        _den = den;
        _exportPK = exportPkCallback;
        BackColor = Color.FromArgb(40, 40, 48);
        ForeColor = Color.White;

        // Load saved view preferences (tile size, group-by-gen) BEFORE building UI so the
        // initial control state matches.  Failure here is non-fatal — defaults kick in.
        LoadDexSettings();

        // Top filter bar
        // Top bar holds two stacked filter rows (format + ownership/attributes). Total height 64.
        var topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.FromArgb(50, 55, 70),
        };

        // === Row 1: format filter + owned counter + Show Hidden toggle ===
        var topRow1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 32,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(8, 6, 8, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        var filterLbl = new Label
        {
            Text = "Filter by .pk format:", AutoSize = true,
            Margin = new Padding(0, 6, 6, 0), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        _filterCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 140,
            Margin = new Padding(0, 3, 12, 0), FlatStyle = FlatStyle.Flat,
        };
        _filterCombo.Items.AddRange(["All formats", "Gen 1 (.pk1)", "Gen 2 (.pk2)", "Gen 3 (.pk3)", "Gen 4 (.pk4)", "Gen 5 (.pk5)", "Gen 6 (.pk6)", "Gen 7 (.pk7)", "Gen 8 (.pk8/.pa8/.pb7/.pb8)", "Gen 9 (.pk9/.pa9)"]);
        _filterCombo.SelectedIndex = 0;
        _filterCombo.SelectedIndexChanged += (_, _) => { _generationFilter = _filterCombo.SelectedIndex; RecomputeOwnedAndRedraw(); };

        _ownedTotalLabel = new Label
        {
            AutoSize = true, Margin = new Padding(8, 6, 0, 0),
            ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        // "Show Hidden" toggle — lets the user see entries they've right-clicked → Hide so
        // they can unhide them. Without this, hidden tiles are completely invisible.
        // Event handler registered AFTER _dexCanvas is constructed (further down) so the
        // compiler's flow analysis sees the field as assigned at lambda-capture time.
        _showHiddenCheck = new CheckBox
        {
            Text = "Show Hidden",
            AutoSize = true,
            Margin = new Padding(16, 8, 0, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.Flat,
        };

        // === Tile size selector ===
        // Lets the user trade information density for sprite detail.  Smaller tiles fit more
        // species per screen but make individual sprites hard to identify; larger tiles are
        // the opposite.  Choices are integer multiples of 16 because the tile sprite cache
        // pre-warms images at exactly TileSize × TileSize, and clean integer ratios keep the
        // bicubic downscale crisp.  When the user changes the size, every cached
        // OwnedImage/UnownedImage is invalidated so the next paint redraws at the new size.
        var tileSizeLabel = new Label
        {
            Text = "Tile Size:", AutoSize = true,
            Margin = new Padding(16, 6, 4, 0), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        var tileSizeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 80,
            Margin = new Padding(0, 3, 0, 0), FlatStyle = FlatStyle.Flat,
        };
        var tileSizeOptions = new[] { 32, 40, 48, 64, 80, 96 };
        foreach (var s in tileSizeOptions) tileSizeCombo.Items.Add($"{s}px");
        // Pick the option matching the loaded TileSize, or default to 48px (index 2).
        int initialIndex = Array.IndexOf(tileSizeOptions, TileSize);
        tileSizeCombo.SelectedIndex = initialIndex >= 0 ? initialIndex : 2;
        tileSizeCombo.SelectedIndexChanged += (_, _) =>
        {
            int newSize = tileSizeOptions[tileSizeCombo.SelectedIndex];
            ApplyTileSizeChange(newSize);
        };

        // === Group by Generation toggle ===
        // When on, the dex grid inserts a "blank row" gap between generations so the user
        // can visually identify gen boundaries while scrolling.  Doesn't filter — every
        // species is still in the grid; they're just laid out with breaks between gens.
        var groupByGenCheck = new CheckBox
        {
            Text = "Group by Gen",
            AutoSize = true,
            Margin = new Padding(16, 8, 0, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.Flat,
            Checked = _groupByGen,
        };
        groupByGenCheck.CheckedChanged += (_, _) =>
        {
            _groupByGen = groupByGenCheck.Checked;
            RecomputeLayout();
            // null-conditional: the compiler can't prove _dexCanvas is initialized at the
            // point the lambda is captured (it's assigned later in this same ctor), but in
            // practice the handler can only fire after the user interacts with the checkbox,
            // which happens long after the form is fully constructed.
            _dexCanvas?.Invalidate();
            SaveDexSettings();
        };

        // === Display ▾ menu ===
        // Lives next to the existing filters but opens a popup of "look-and-feel" options
        // — selection outline color/thickness, summary font size/bold.  Mirrors the
        // structure of the Den's View → Selection Style submenus, just attached to a
        // per-panel popup rather than the top-level menubar (the Pokédex tab doesn't
        // surface to the menubar).
        var displayBtn = new Button
        {
            Text = "Display ▾",
            AutoSize = false, Width = 90, Height = 26,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 65, 80),
            Margin = new Padding(16, 4, 0, 0),
        };
        var displayMenu = BuildDisplayMenu();
        displayBtn.Click += (_, _) => displayMenu.Show(displayBtn, new Point(0, displayBtn.Height));

        topRow1.Controls.AddRange([filterLbl, _filterCombo, _ownedTotalLabel, _showHiddenCheck, tileSizeLabel, tileSizeCombo, groupByGenCheck, displayBtn]);

        // === Row 2: ownership state + shiny + gender (combinable filters) ===
        // All three groups apply jointly as AND filters: an owned tile must match ALL
        // active criteria to be highlighted. "Unowned only" mode hides all owned tiles
        // regardless of other selections.
        var topRow2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 32,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(50, 55, 70),
        };

        Label MakeFilterLabel(string text) => new()
        {
            Text = text, AutoSize = true,
            // Labels render text at the top of their bounding box; CheckBoxes vertically center
            // their text relative to the checkbox glyph. A 9px top margin on the label aligns
            // its baseline with the checkbox text on the same row (which has 8px top margin).
            Margin = new Padding(0, 9, 4, 0), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        // Owned state combo — clearer than 3 radio buttons in a tight row
        var ownedCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 120,
            Margin = new Padding(0, 3, 12, 0), FlatStyle = FlatStyle.Flat,
        };
        ownedCombo.Items.AddRange(["Owned + Unowned", "Owned only", "Unowned only"]);
        ownedCombo.SelectedIndex = 0;
        ownedCombo.SelectedIndexChanged += (_, _) => { _ownedStateFilter = ownedCombo.SelectedIndex; RecomputeOwnedAndRedraw(); };

        // Shiny filter — when checked, requires the user to own a shiny variant
        var shinyCheck = new CheckBox
        {
            Text = "Shiny ★", AutoSize = true,
            Margin = new Padding(0, 8, 12, 0),
            ForeColor = Color.FromArgb(255, 230, 100), // gold to evoke the in-game shiny indicator
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.Flat,
        };
        shinyCheck.CheckedChanged += (_, _) => { _shinyFilter = shinyCheck.Checked; RecomputeOwnedAndRedraw(); };

        // Gender filters — independent checkboxes, OR'd together (any checked gender qualifies)
        var maleCheck = new CheckBox { Text = "♂ Male", AutoSize = true, Margin = new Padding(0, 8, 6, 0), ForeColor = Color.FromArgb(120, 180, 255), Font = new Font("Segoe UI", 9), FlatStyle = FlatStyle.Flat };
        var femaleCheck = new CheckBox { Text = "♀ Female", AutoSize = true, Margin = new Padding(0, 8, 6, 0), ForeColor = Color.FromArgb(255, 150, 180), Font = new Font("Segoe UI", 9), FlatStyle = FlatStyle.Flat };
        var genderlessCheck = new CheckBox { Text = "⚲ Genderless", AutoSize = true, Margin = new Padding(0, 8, 0, 0), ForeColor = Color.FromArgb(200, 200, 200), Font = new Font("Segoe UI", 9), FlatStyle = FlatStyle.Flat };
        maleCheck.CheckedChanged += (_, _) => { _genderMaleFilter = maleCheck.Checked; RecomputeOwnedAndRedraw(); };
        femaleCheck.CheckedChanged += (_, _) => { _genderFemaleFilter = femaleCheck.Checked; RecomputeOwnedAndRedraw(); };
        genderlessCheck.CheckedChanged += (_, _) => { _genderGenderlessFilter = genderlessCheck.Checked; RecomputeOwnedAndRedraw(); };

        topRow2.Controls.AddRange(new Control[]
        {
            MakeFilterLabel("Show:"), ownedCombo,
            shinyCheck,
            MakeFilterLabel("Gender:"), maleCheck, femaleCheck, genderlessCheck,
        });

        topBar.Controls.Add(topRow2);
        topBar.Controls.Add(topRow1);

        // Right detail panel
        _detailPanel = new Panel
        {
            Dock = DockStyle.Right, Width = DetailPanelWidth,
            BackColor = Color.FromArgb(48, 50, 60),
            BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8),
        };
        _detailSprite = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom, Width = DetailSpriteSize, Height = DetailSpriteSize,
            BackColor = Color.FromArgb(60, 62, 72),
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point((DetailPanelWidth - DetailSpriteSize) / 2 - 8, 12),
        };
        _detailPrev = new Button
        {
            Text = "◀", Width = 30, Height = 26,
            Location = new Point(_detailSprite.Left, _detailSprite.Bottom + 6),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 62, 72),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        _detailNext = new Button
        {
            Text = "▶", Width = 30, Height = 26,
            Location = new Point(_detailSprite.Right - 30, _detailSprite.Bottom + 6),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 62, 72),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        _detailPkIndex = new Label
        {
            AutoSize = false, Width = _detailSprite.Width - 64, Height = 24,
            Location = new Point(_detailSprite.Left + 32, _detailSprite.Bottom + 7),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Text = "",
        };
        _detailPrev.Click += (_, _) => CyclePk(-1);
        _detailNext.Click += (_, _) => CyclePk(+1);

        _detailDexNumber = new Label
        {
            AutoSize = false, Width = DetailPanelWidth - 16, Height = 28,
            Location = new Point(0, _detailPrev.Bottom + 12),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Text = "",
        };
        _detailSpeciesName = new Label
        {
            AutoSize = false, Width = DetailPanelWidth - 16, Height = 22,
            Location = new Point(0, _detailDexNumber.Bottom),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(200, 200, 220),
            Font = new Font("Segoe UI", 10), Text = "",
        };
        _detailSummary = new Label
        {
            AutoSize = false, Width = DetailPanelWidth - 24,
            Location = new Point(8, _detailSpeciesName.Bottom + 10),
            ForeColor = Color.White, Font = new Font("Segoe UI", _dexSummaryFontSize, _dexSummaryFontBold ? FontStyle.Bold : FontStyle.Regular),
            Text = "Click a Pokémon to view its summary here.",
        };
        _detailExport = new Button
        {
            Text = "Export to .PK", Width = DetailPanelWidth - 32, Height = 32,
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 130, 180),
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Enabled = false,
        };
        _detailExport.Click += (_, _) =>
        {
            if (_selectedOwnedPks.Count == 0 || _selectedPkIndex < 0 || _selectedPkIndex >= _selectedOwnedPks.Count) return;
            _exportPK(_selectedOwnedPks[_selectedPkIndex], FindForm());
        };

        _detailPanel.Controls.AddRange([_detailSprite, _detailPrev, _detailNext, _detailPkIndex, _detailDexNumber, _detailSpeciesName, _detailSummary, _detailExport]);
        _detailPanel.Resize += (_, _) =>
        {
            _detailExport.Location = new Point(16, _detailPanel.ClientSize.Height - 44);
            int top = _detailSpeciesName.Bottom + 10;
            int bottom = _detailExport.Top - 6;
            _detailSummary.Location = new Point(8, top);
            _detailSummary.Height = Math.Max(20, bottom - top);
        };

        // Dex canvas — single double-buffered scrollable Panel; all tiles drawn via custom paint.
        _dexCanvas = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(40, 40, 48),
            AutoScroll = true,
        };
        _dexCanvas.Paint += DexCanvas_Paint;
        _dexCanvas.MouseClick += DexCanvas_MouseClick;
        _dexCanvas.MouseDown += DexCanvas_MouseDown;
        _dexCanvas.Resize += (_, _) => { RecomputeLayout(); _dexCanvas.Invalidate(); };

        // Now that _dexCanvas exists, register the Show Hidden checkbox handler that needs it.
        _showHiddenCheck.CheckedChanged += (_, _) =>
        {
            _showHidden = _showHiddenCheck.Checked;
            RecomputeLayout();
            _dexCanvas.Invalidate();
            UpdateOwnedTotalLabel();
        };

        Controls.Add(_dexCanvas);
        Controls.Add(_detailPanel);
        Controls.Add(topBar);
    }

    /// <summary>
    /// Applies a new tile size, invalidates every cached tile bitmap (so the next paint
    /// rebuilds at the new size), recomputes layout, persists the choice, and redraws.
    /// </summary>
    /// <remarks>
    /// Tile sprites are pre-warmed at exactly TileSize × TileSize so each paint blits 1:1.
    /// When the user changes the size, those cached bitmaps are now wrong-sized — we drop
    /// them by setting OwnedImage / UnownedImage to null on every tile.  The next paint
    /// pass calls <see cref="GetTileImage"/> which lazy-rebuilds at the new TileSize.
    /// </remarks>
    /// <summary>
    /// Builds the "Display ▾" popup menu shown when the user clicks the Display button
    /// in the top toolbar.  Contains:
    ///   • Selection Outline → Color (10 presets + Custom...)
    ///   • Selection Outline → Thickness (1-6 px)
    ///   • Summary Font → Size (8/9/10/11/12/14 pt)
    ///   • Summary Font → Bold (toggle)
    /// Mirrors the Den's View → Selection Style submenu structure.
    /// </summary>
    private ContextMenuStrip BuildDisplayMenu()
    {
        var menu = new ContextMenuStrip();

        // === Selection Outline → Color ===
        var colorMenu = new ToolStripMenuItem("Selection Color");
        var colorOptions = new (string Label, Color Color)[]
        {
            ("Blue (default)", Color.FromArgb(80, 140, 220)),
            ("Red",            Color.FromArgb(220, 80, 80)),
            ("Green",          Color.FromArgb(80, 200, 110)),
            ("Yellow",         Color.FromArgb(230, 200, 70)),
            ("Orange",         Color.FromArgb(230, 140, 60)),
            ("Purple",         Color.FromArgb(170, 110, 230)),
            ("Pink",           Color.FromArgb(240, 130, 180)),
            ("Cyan",           Color.FromArgb(80, 210, 220)),
            ("White",          Color.FromArgb(240, 240, 240)),
            ("Black",          Color.FromArgb(20,  20,  20)),
        };
        foreach (var (label, color) in colorOptions)
        {
            var local = color;
            var item = new ToolStripMenuItem(label, null, (_, _) => SetDexSelectionColor(local))
            {
                CheckOnClick = true,
                Checked = _dexSelectionColor.ToArgb() == local.ToArgb(),
            };
            colorMenu.DropDownItems.Add(item);
        }
        colorMenu.DropDownItems.Add(new ToolStripSeparator());
        colorMenu.DropDownItems.Add(new ToolStripMenuItem("Custom...", null, (_, _) =>
        {
            using var dlg = new ColorDialog { Color = _dexSelectionColor, FullOpen = true };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) SetDexSelectionColor(dlg.Color);
        }));

        // === Selection Outline → Thickness ===
        var thicknessMenu = new ToolStripMenuItem("Selection Thickness");
        for (int t = 1; t <= 6; t++)
        {
            int captured = t;
            var item = new ToolStripMenuItem($"{captured} px", null, (_, _) => SetDexSelectionThickness(captured))
            {
                CheckOnClick = true,
                Checked = _dexSelectionThickness == captured,
            };
            thicknessMenu.DropDownItems.Add(item);
        }

        // === Summary Font → Size ===
        var fontSizeMenu = new ToolStripMenuItem("Summary Font Size");
        // Including 8.5f because that's the previous default — preserves identical
        // appearance for users who don't change anything.
        float[] sizes = { 8f, 8.5f, 9f, 10f, 11f, 12f, 14f };
        foreach (var sz in sizes)
        {
            var captured = sz;
            var label = (sz % 1 == 0) ? $"{(int)sz} pt" : $"{sz} pt";
            var item = new ToolStripMenuItem(label, null, (_, _) => SetDexSummaryFontSize(captured))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_dexSummaryFontSize - captured) < 0.01f,
            };
            fontSizeMenu.DropDownItems.Add(item);
        }

        // === Summary Font → Bold toggle ===
        var boldItem = new ToolStripMenuItem("Bold Summary Font", null, (sender, _) =>
        {
            _dexSummaryFontBold = !_dexSummaryFontBold;
            if (sender is ToolStripMenuItem mi) mi.Checked = _dexSummaryFontBold;
            ApplyDexSummaryFont();
            SaveDexSettings();
        }) { Checked = _dexSummaryFontBold };

        menu.Items.Add(colorMenu);
        menu.Items.Add(thicknessMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(fontSizeMenu);
        menu.Items.Add(boldItem);
        return menu;
    }

    /// <summary>
    /// Updates the selection-outline color, repaints the canvas to show the new look,
    /// persists the choice, and refreshes the menu's radio-style check marks so the
    /// previously-checked color clears.
    /// </summary>
    private void SetDexSelectionColor(Color color)
    {
        _dexSelectionColor = color;
        _dexCanvas?.Invalidate();
        SaveDexSettings();
    }

    private void SetDexSelectionThickness(int thickness)
    {
        _dexSelectionThickness = Math.Clamp(thickness, 1, 6);
        _dexCanvas?.Invalidate();
        SaveDexSettings();
    }

    private void SetDexSummaryFontSize(float size)
    {
        _dexSummaryFontSize = size;
        ApplyDexSummaryFont();
        SaveDexSettings();
    }

    /// <summary>Builds a fresh Font from current size+bold and assigns it to the detail summary label.</summary>
    private void ApplyDexSummaryFont()
    {
        var oldFont = _detailSummary.Font;
        _detailSummary.Font = new Font("Segoe UI", _dexSummaryFontSize, _dexSummaryFontBold ? FontStyle.Bold : FontStyle.Regular);
        oldFont.Dispose();
    }

    private void ApplyTileSizeChange(int newSize)
    {
        if (newSize == TileSize) return;
        TileSize = newSize;

        // Invalidate cached per-tile bitmaps — they were built at the old size.
        foreach (var tile in _tiles)
        {
            tile.OwnedImage?.Dispose();
            tile.OwnedImage = null;
            tile.UnownedImage?.Dispose();
            tile.UnownedImage = null;
        }

        RecomputeLayout();
        _dexCanvas.Invalidate();
        SaveDexSettings();
    }

    /// <summary>Path to the dex-settings file — sits next to the exe alongside PKDen.den/.settings/.recent/.dexhidden.</summary>
    private static string DexSettingsPath => Path.Combine(AppContext.BaseDirectory, "PKDen.dexsettings");

    /// <summary>
    /// Reads tile size and group-by-gen preferences from <see cref="DexSettingsPath"/>.
    /// Format is one <c>key=value</c> per line, same shape as PKDen.cfg.  Missing file =
    /// defaults; unparseable values = ignored (default kicks in).
    /// </summary>
    private void LoadDexSettings()
    {
        try
        {
            if (!File.Exists(DexSettingsPath)) return;
            foreach (var raw in File.ReadAllLines(DexSettingsPath))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case TileSizeSettingKey:
                        if (int.TryParse(value, out int sz) && sz >= 16 && sz <= 256)
                            TileSize = sz;
                        break;
                    case "GroupByGen":
                        _groupByGen = value == "true";
                        break;
                    // Selection outline color stored as ARGB int (Color.ToArgb()).  Defending
                    // against malformed values by keeping the default if parse fails — the
                    // UI menu will simply show the current default as checked.
                    case "DexSelectionColor":
                        if (int.TryParse(value, out int argb))
                            _dexSelectionColor = Color.FromArgb(argb);
                        break;
                    case "DexSelectionThickness":
                        if (int.TryParse(value, out int t) && t >= 1 && t <= 6)
                            _dexSelectionThickness = t;
                        break;
                    case "DexSummaryFontSize":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fs)
                            && fs >= 6f && fs <= 24f)
                            _dexSummaryFontSize = fs;
                        break;
                    case "DexSummaryFontBold":
                        _dexSummaryFontBold = value == "true";
                        break;
                }
            }
        }
        catch { /* best-effort; defaults are already in place */ }
    }

    /// <summary>Persists tile size, gen-grouping, and selection/font preferences to <see cref="DexSettingsPath"/>.</summary>
    private void SaveDexSettings()
    {
        try
        {
            File.WriteAllLines(DexSettingsPath, new[]
            {
                "# PKDen Pokédex preferences",
                $"{TileSizeSettingKey}={TileSize}",
                $"GroupByGen={(_groupByGen ? "true" : "false")}",
                $"DexSelectionColor={_dexSelectionColor.ToArgb()}",
                $"DexSelectionThickness={_dexSelectionThickness}",
                $"DexSummaryFontSize={_dexSummaryFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"DexSummaryFontBold={(_dexSummaryFontBold ? "true" : "false")}",
            });
        }
        catch { /* best-effort; loss of preference on failure is non-fatal */ }
    }

    /// <summary>
    /// Builds the virtual tile list once. Cheap — no controls created, just data records.
    /// Also restores any user-hidden tile state from PKDen.dexhidden.
    /// </summary>
    public void BuildDexTiles()
    {
        if (_tiles.Count > 0) return;
        for (ushort species = 1; species <= MaxSpecies; species++)
        {
            foreach (byte form in GetDistinctSpriteForms(species))
                _tiles.Add(new DexTile(species, form));
        }
        LoadHiddenList();  // apply persisted hide flags to fresh tiles
        RecomputeLayout();
        _dexCanvas.Invalidate();
    }

    /// <summary>Path to the hidden-list file — sits next to the exe alongside PKDen.den/.settings/.recent.</summary>
    private static string HiddenListPath => Path.Combine(AppContext.BaseDirectory, "PKDen.dexhidden");

    /// <summary>Reads the hidden list and applies it to current tiles. Format: "species,form" per line.</summary>
    private void LoadHiddenList()
    {
        try
        {
            if (!File.Exists(HiddenListPath)) return;
            // Build a HashSet for O(1) lookup against the tile list
            var hidden = new HashSet<(ushort, byte)>();
            foreach (var line in File.ReadAllLines(HiddenListPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var parts = trimmed.Split(',');
                if (parts.Length != 2) continue;
                if (!ushort.TryParse(parts[0], out var sp)) continue;
                if (!byte.TryParse(parts[1], out var fm)) continue;
                hidden.Add((sp, fm));
            }
            foreach (var tile in _tiles)
                if (hidden.Contains((tile.Species, tile.Form)))
                    tile.IsHidden = true;
        }
        catch { /* malformed hidden list — start fresh */ }
    }

    /// <summary>Writes the current hidden list to disk. Called whenever a tile's hidden state changes.</summary>
    private void SaveHiddenList()
    {
        try
        {
            var hiddenLines = _tiles
                .Where(t => t.IsHidden)
                .Select(t => $"{t.Species},{t.Form}");
            File.WriteAllLines(HiddenListPath, new[] { "# PKDen hidden Pokédex entries", "# Format: species,form" }.Concat(hiddenLines));
        }
        catch { /* can't write — fail silently, user will redo on next launch */ }
    }

    /// <summary>
    /// Recomputes the column count and per-tile bounds based on the canvas's current width.
    /// When the user has hidden tiles AND "Show Hidden" is off, hidden tiles are SKIPPED
    /// from positioning entirely — so the grid reflows seamlessly with no empty slots.
    /// Hidden tiles still exist in <see cref="_tiles"/>; their bounds get set to Empty so
    /// paint and hit-test correctly ignore them.
    ///
    /// When <see cref="_groupByGen"/> is on, an additional rule kicks in: whenever the
    /// next tile would belong to a different generation than the previous one, we
    /// finish the current row (skip ahead in positionIndex) AND insert a blank row
    /// for visual separation, so each generation starts at the leftmost column with
    /// a clear gap between gens.
    /// </summary>
    private void RecomputeLayout()
    {
        if (_tiles.Count == 0) return;
        int availableWidth = Math.Max(TileSize + TilePadding * 2, _dexCanvas.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        int columnsFitting = Math.Max(1, (availableWidth - TilePadding * 2 + TileGap) / (TileSize + TileGap));
        _columns = columnsFitting;

        // Track the current "visible position" — only incremented for tiles that get a real slot.
        int positionIndex = 0;
        // Track the last gen we placed so we can detect crossings into a new gen.
        // 0 = "no previous gen" (first iteration).
        int lastGen = 0;
        foreach (var tile in _tiles)
        {
            // Hidden tiles get no bounds when Show Hidden is off — they're effectively invisible
            if (tile.IsHidden && !_showHidden)
            {
                tile.Bounds = Rectangle.Empty;
                continue;
            }

            // Generation-grouping: when this tile belongs to a different gen than the prior
            // tile, advance positionIndex to the start of the next row, then add an extra
            // blank row so there's a visible gap between generations.
            if (_groupByGen)
            {
                int thisGen = GetGenerationForSpecies(tile.Species);
                if (lastGen != 0 && thisGen != lastGen)
                {
                    // Round positionIndex up to the next multiple of _columns (next row),
                    // then add another _columns to leave a full blank row between gens.
                    int currentRow = positionIndex / _columns;
                    int currentCol = positionIndex % _columns;
                    if (currentCol != 0) currentRow++;       // finish the partial row
                    currentRow++;                             // skip a blank row
                    positionIndex = currentRow * _columns;
                }
                lastGen = thisGen;
            }

            int row = positionIndex / _columns;
            int col = positionIndex % _columns;
            int x = TilePadding + col * (TileSize + TileGap);
            int y = TilePadding + row * (TileSize + TileGap);
            tile.Bounds = new Rectangle(x, y, TileSize, TileSize);
            positionIndex++;
        }
        _gridRows = (positionIndex + _columns - 1) / _columns;
        int totalHeight = TilePadding * 2 + _gridRows * TileSize + Math.Max(0, _gridRows - 1) * TileGap;
        _dexCanvas.AutoScrollMinSize = new Size(0, totalHeight);
    }

    /// <summary>
    /// Maps a National Dex species ID to its generation number (1–9).  Boundaries reflect
    /// the actual Pokédex ranges: Gen 1 = 1–151, Gen 2 = 152–251, etc.  Used by the
    /// "Group by Gen" layout to decide where to insert visual breaks.
    /// </summary>
    private static int GetGenerationForSpecies(ushort species) => species switch
    {
        <= 151  => 1,
        <= 251  => 2,
        <= 386  => 3,
        <= 493  => 4,
        <= 649  => 5,
        <= 721  => 6,
        <= 809  => 7,
        <= 905  => 8,
        _       => 9,
    };

    private void DexCanvas_Paint(object? sender, PaintEventArgs e)
    {
        // Translate to virtual coordinates so we can use tile.Bounds directly
        e.Graphics.TranslateTransform(_dexCanvas.AutoScrollPosition.X, _dexCanvas.AutoScrollPosition.Y);
        var virtualClip = e.ClipRectangle;
        virtualClip.Offset(-_dexCanvas.AutoScrollPosition.X, -_dexCanvas.AutoScrollPosition.Y);

        using var slotBrush = new SolidBrush(Color.FromArgb(50, 52, 62));
        using var hiddenSlotBrush = new SolidBrush(Color.FromArgb(70, 30, 30)); // dim red tint for hidden tiles in Show Hidden mode
        // Selection outline color & thickness now configurable via the dex top bar's
        // Display ▾ menu.  Defaults match the prior hardcoded values so existing users
        // see no visual change unless they pick something else.
        using var selectionPen = new Pen(_dexSelectionColor, _dexSelectionThickness);
        using var hiddenStrikePen = new Pen(Color.FromArgb(200, 60, 60), 2);

        foreach (var tile in _tiles)
        {
            // Bounds.IsEmpty means the tile was excluded from layout (hidden + Show Hidden off)
            if (tile.Bounds.IsEmpty) continue;
            if (!tile.Bounds.IntersectsWith(virtualClip)) continue;

            // Slot background — darker red tint when this tile is hidden but Show Hidden is on
            e.Graphics.FillRectangle(tile.IsHidden ? hiddenSlotBrush : slotBrush, tile.Bounds);

            var img = GetTileImage(tile);
            if (img is not null)
            {
                // Preserve aspect ratio: scale uniformly so the longer dimension fits in TileSize,
                // then center inside the slot. The previous code clamped width and height
                // independently, which squashed non-square sprites.
                float scale = Math.Min((float)TileSize / img.Width, (float)TileSize / img.Height);
                if (scale > 1f) scale = 1f;
                int w = (int)Math.Round(img.Width * scale);
                int h = (int)Math.Round(img.Height * scale);
                int x = tile.Bounds.X + (TileSize - w) / 2;
                int y = tile.Bounds.Y + (TileSize - h) / 2;
                e.Graphics.DrawImage(img, new Rectangle(x, y, w, h));
            }

            // Hidden marker — draw a red diagonal X across the slot when Show Hidden mode is on,
            // so the user can clearly tell which entries are hidden and right-click to unhide.
            if (tile.IsHidden)
            {
                var r = tile.Bounds;
                r.Inflate(-4, -4);
                e.Graphics.DrawLine(hiddenStrikePen, r.Left, r.Top, r.Right, r.Bottom);
                e.Graphics.DrawLine(hiddenStrikePen, r.Left, r.Bottom, r.Right, r.Top);
            }

            if (tile == _selectedTile)
            {
                var r = tile.Bounds;
                r.Inflate(-1, -1);
                e.Graphics.DrawRectangle(selectionPen, r);
            }
        }
    }

    private static Image? GetTileImage(DexTile tile)
    {
        if (tile.IsOwned)
        {
            tile.OwnedImage ??= TryBuildSpeciesSprite(tile.Species, tile.Form, TileSize, TileSize, faded: false);
            return tile.OwnedImage;
        }
        else
        {
            tile.OwnedImage ??= TryBuildSpeciesSprite(tile.Species, tile.Form, TileSize, TileSize, faded: false);
            tile.UnownedImage ??= tile.OwnedImage is null ? null : ApplyAlpha(tile.OwnedImage, 0.25f);
            return tile.UnownedImage;
        }
    }

    /// <summary>Hit-test a virtual-space point against tile bounds; null if no tile under cursor.</summary>
    private DexTile? HitTestTile(Point virtualPt)
    {
        foreach (var tile in _tiles)
        {
            if (tile.Bounds.IsEmpty) continue;
            if (tile.Bounds.Contains(virtualPt)) return tile;
        }
        return null;
    }

    private void DexCanvas_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;  // right-click handled in MouseDown
        var p = new Point(e.X - _dexCanvas.AutoScrollPosition.X, e.Y - _dexCanvas.AutoScrollPosition.Y);
        var tile = HitTestTile(p);
        if (tile is not null)
        {
            SelectTile(tile);
            _dexCanvas.Invalidate();
        }
    }

    /// <summary>
    /// Right-click on a tile shows a context menu with "Hide Pokémon" (or "Unhide" if already hidden).
    /// Hiding removes the tile from the visible grid; the rest reflows to fill the space.
    /// Hidden state persists across sessions in PKDen.dexhidden.
    /// </summary>
    private void DexCanvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var p = new Point(e.X - _dexCanvas.AutoScrollPosition.X, e.Y - _dexCanvas.AutoScrollPosition.Y);
        var tile = HitTestTile(p);
        if (tile is null) return;

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(50, 52, 62),
            ForeColor = Color.White,
            // Custom renderer paints item backgrounds in our dark colors instead of the default
            // light Windows toolstrip palette — without this, ToolStripLabel and disabled items
            // render as a jarring white/gray box on the dark menu.
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
        };
        string speciesName = GetSpeciesName(tile.Species);
        string formSuffix = tile.Form > 0 ? $" (Form {tile.Form})" : "";
        // Header — disabled menu item shows the species name in muted text. Disabled items
        // don't fire clicks, but the renderer paints them with our dark colors thanks to
        // the custom DarkMenuColors above.
        var header = new ToolStripMenuItem($"#{tile.Species:000} {speciesName}{formSuffix}")
        {
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 220),
            Enabled = false,
        };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        if (tile.IsHidden)
        {
            menu.Items.Add(new ToolStripMenuItem("Unhide Pokémon", null, (_, _) => SetTileHidden(tile, false))
            {
                ForeColor = Color.White,
            });
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem("Hide Pokémon", null, (_, _) => SetTileHidden(tile, true))
            {
                ForeColor = Color.White,
            });
        }
        // Show the menu at the original screen-space click point (the input e is in canvas coords)
        var screenPt = _dexCanvas.PointToScreen(new Point(e.X, e.Y));
        menu.Show(screenPt);
    }

    /// <summary>Toggles a tile's hidden state, reflows the layout, persists to disk, and repaints.</summary>
    private void SetTileHidden(DexTile tile, bool hidden)
    {
        tile.IsHidden = hidden;
        // If we just hid the currently-selected tile, deselect it so the detail panel doesn't show stale info
        if (hidden && _selectedTile == tile) _selectedTile = null;
        SaveHiddenList();
        RecomputeLayout();
        UpdateOwnedTotalLabel();
        _dexCanvas.Invalidate();
    }

    /// <summary>
    /// Eagerly pre-generates all dex sprites. Reports progress and writes a diagnostic log.
    /// </summary>
    public void PrewarmAllSprites(Action<string>? progressCallback = null)
    {
        int total = _tiles.Count;
        int done = 0;
        int sinceYield = 0;
        var startTicks = Environment.TickCount64;
        var firstFailureMessages = new List<string>();
        int builtOwned = 0, builtUnowned = 0;

        foreach (var tile in _tiles)
        {
            try
            {
                tile.OwnedImage ??= PKDenSpriteUtil.GetSpriteAt(tile.Species, tile.Form, 0, 0, 0, false, Shiny.Never, EntityContext.Gen9, TileSize, TileSize);
                if (tile.OwnedImage is not null) builtOwned++;
            }
            catch (Exception ex)
            {
                if (firstFailureMessages.Count < 10)
                    firstFailureMessages.Add($"#{tile.Species:0000} form {tile.Form}: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (tile.OwnedImage is not null)
                {
                    tile.UnownedImage ??= ApplyAlpha(tile.OwnedImage, 0.25f);
                    if (tile.UnownedImage is not null) builtUnowned++;
                }
            }
            catch (Exception ex)
            {
                if (firstFailureMessages.Count < 10)
                    firstFailureMessages.Add($"#{tile.Species:0000} form {tile.Form} faded: {ex.GetType().Name}: {ex.Message}");
            }
            done++;
            if (++sinceYield >= 100)
            {
                sinceYield = 0;
                progressCallback?.Invoke($"Loading sprites… {done:N0} / {total:N0}");
                Application.DoEvents();
            }
        }
        var elapsedMs = Environment.TickCount64 - startTicks;
        progressCallback?.Invoke($"Loaded {total:N0} dex sprites.");

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "PKDen-prewarm-log.txt");
            var lines = new List<string>
            {
                $"=== Pokédex Prewarm Diagnostic Log ===",
                $"Architecture:        single-canvas custom paint (no per-tile HWNDs)",
                $"Timestamp:           {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Total tiles:         {total:N0}",
                $"Sprite gen elapsed:  {elapsedMs:N0} ms",
                $"Owned built:         {builtOwned:N0} / {total:N0}",
                $"Unowned built:       {builtUnowned:N0} / {total:N0}",
                $"",
                $"First failure messages (up to 10):",
            };
            lines.AddRange(firstFailureMessages.Select(m => $"  • {m}"));
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    /// <summary>
    /// Returns true if the tile should display as "owned" (full color sprite) given the current
    /// filter state. Combines format, ownership-mode, shiny, and gender filters with AND semantics
    /// across categories and OR within the gender group (Male+Female checked = either qualifies).
    ///
    /// In "Unowned only" mode, the highlighting is inverted: tiles the user doesn't have render
    /// in full color (these are the things the user is hunting for) and owned tiles are faded.
    /// </summary>
    private bool IsOwnedAfterFilter(ushort species, byte form)
    {
        bool hasAny = _ownedByForm.TryGetValue((species, form), out var pks) && pks.Count > 0;

        // "Unowned only" inverts the highlighting: NOT-owned tiles render colored.
        if (_ownedStateFilter == 2)
            return !hasAny;

        if (!hasAny) return false;

        // "Owned only" or "Both": tile passes if at least one owned PK satisfies the per-PK filters.
        return pks!.Any(pk => PkPassesAllPerPkFilters(pk));
    }

    /// <summary>True if a single PKM satisfies all currently-active per-PK filters.</summary>
    private bool PkPassesAllPerPkFilters(PKM pk)
    {
        // Format/gen filter
        if (_generationFilter > 0 && !MatchesGenFilter(pk, _generationFilter))
            return false;
        // Shiny filter — only owned shiny variants pass when shiny is required
        if (_shinyFilter)
        {
            try { if (!pk.IsShiny) return false; } catch { return false; }
        }
        // Gender filter — at least one checked gender must match. If NONE are checked, the
        // gender criterion is "match anything" (filter inactive).
        bool anyGenderChecked = _genderMaleFilter || _genderFemaleFilter || _genderGenderlessFilter;
        if (anyGenderChecked)
        {
            int g;
            try { g = pk.Gender; } catch { return false; }
            // PKHeX uses: 0 = male, 1 = female, 2 = genderless
            bool genderOk =
                (_genderMaleFilter && g == 0) ||
                (_genderFemaleFilter && g == 1) ||
                (_genderGenderlessFilter && g == 2);
            if (!genderOk) return false;
        }
        return true;
    }

    private static bool MatchesGenFilter(PKM pk, int gen) => gen switch
    {
        1 => pk is PK1, 2 => pk is PK2, 3 => pk is PK3, 4 => pk is PK4, 5 => pk is PK5,
        6 => pk is PK6, 7 => pk is PK7,
        8 => pk is PK8 or PB7 or PB8 or PA8,
        9 => pk is PK9 or PA9,
        _ => true,
    };

    public void RecomputeOwnedAndRedraw()
    {
        if (_tiles.Count == 0) return;
        _ownedByForm.Clear();
        for (int b = 0; b < _den.BoxCount; b++)
        {
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = _den.GetSlot(b, s);
                if (pk is null || pk.Species == 0) continue;
                var key = (pk.Species, pk.Form);
                if (!_ownedByForm.TryGetValue(key, out var list))
                {
                    list = [];
                    _ownedByForm[key] = list;
                }
                list.Add(pk);
            }
        }
        foreach (var key in _ownedByForm.Keys.ToList())
            _ownedByForm[key] = _ownedByForm[key].OrderBy(GetPkGen).ToList();

        foreach (var tile in _tiles)
            tile.IsOwned = IsOwnedAfterFilter(tile.Species, tile.Form);

        UpdateOwnedTotalLabel();
        if (_selectedTile is not null) SelectTile(_selectedTile);
        _dexCanvas.Invalidate();
    }

    private void UpdateOwnedTotalLabel()
    {
        int totalUniqueSpecies = _ownedByForm.Keys.Select(k => k.Species).Distinct().Count();
        int totalDexEntries = _tiles.Select(t => t.Species).Distinct().Count();
        int totalOwnedPks = _ownedByForm.Values.Sum(list => list.Count);
        int hiddenCount = _tiles.Count(t => t.IsHidden);
        string text = $"Owned: {totalUniqueSpecies}/{totalDexEntries} species  •  {totalOwnedPks:N0} total Pokémon";
        if (hiddenCount > 0)
            text += $"  •  {hiddenCount:N0} hidden";
        _ownedTotalLabel.Text = text;
    }

    private static int GetPkGen(PKM pk) => pk switch
    {
        PK1 => 1, PK2 => 2, PK3 => 3, PK4 => 4, PK5 => 5, PK6 => 6, PK7 => 7,
        PB7 or PK8 or PB8 or PA8 => 8,
        PK9 or PA9 => 9,
        _ => 99,
    };

    private void SelectTile(DexTile tile)
    {
        _selectedTile = tile;
        var key = (tile.Species, tile.Form);
        if (_ownedByForm.TryGetValue(key, out var pks))
        {
            _selectedOwnedPks = _generationFilter > 0
                ? pks.Where(pk => MatchesGenFilter(pk, _generationFilter)).ToList()
                : [.. pks];
        }
        else
        {
            _selectedOwnedPks = [];
        }
        _selectedPkIndex = 0;
        RefreshDetailPanel();
    }

    private void CyclePk(int delta)
    {
        if (_selectedOwnedPks.Count <= 1) return;
        _selectedPkIndex = (_selectedPkIndex + delta + _selectedOwnedPks.Count) % _selectedOwnedPks.Count;
        RefreshDetailPanel();
    }

    private void RefreshDetailPanel()
    {
        if (_selectedTile is null) return;
        ushort species = _selectedTile.Species;
        byte form = _selectedTile.Form;
        string speciesName = GetSpeciesName(species);
        _detailDexNumber.Text = $"#{species:000}";
        _detailSpeciesName.Text = form > 0 ? $"{speciesName} (Form {form})" : speciesName;

        if (_selectedOwnedPks.Count > 0 && _selectedPkIndex >= 0 && _selectedPkIndex < _selectedOwnedPks.Count)
        {
            var pk = _selectedOwnedPks[_selectedPkIndex];
            // Dispose old image before reassignment — PKDenSpriteAt() returns a fresh
            // bitmap every call, so the prior one would otherwise leak per refresh.
            // Request at the exact PictureBox dimensions so PictureBox.Zoom blits 1:1
            // and we keep all the bicubic-downscaled detail from the hi-res master.
            var oldImg = _detailSprite.Image;
            _detailSprite.Image = pk.PKDenSpriteAt(DetailSpriteSize, DetailSpriteSize);
            oldImg?.Dispose();
            _detailPkIndex.Text = _selectedOwnedPks.Count == 1
                ? $"PK{GetPkGen(pk)}"
                : $"PK{GetPkGen(pk)} ({_selectedPkIndex + 1}/{_selectedOwnedPks.Count})";
            _detailPrev.Enabled = _selectedOwnedPks.Count > 1;
            _detailNext.Enabled = _selectedOwnedPks.Count > 1;
            _detailExport.Enabled = true;
            _detailSummary.Text = BuildSummaryText(pk) + BuildFormatsList(species, form);
        }
        else
        {
            // Same disposal pattern as the owned-pk branch above.
            var oldImg = _detailSprite.Image;
            _detailSprite.Image = TryBuildSpeciesSprite(species, form, DetailSpriteSize, DetailSpriteSize, faded: true);
            oldImg?.Dispose();
            _detailPkIndex.Text = "Not owned";
            _detailPrev.Enabled = false;
            _detailNext.Enabled = false;
            _detailExport.Enabled = false;
            _detailSummary.Text = $"You don't own a {speciesName} matching the current filter." + BuildFormatsList(species, form);
        }
    }

    /// <summary>
    /// Returns a multi-line block listing every distinct .pk format the user owns for the given
    /// species/form. Each format is listed with its file extension (PK1, PK7, PA8, PB7, etc.) and
    /// a count if the user owns multiple of that type. Returns "" if no entries exist.
    /// </summary>
    private string BuildFormatsList(ushort species, byte form)
    {
        if (!_ownedByForm.TryGetValue((species, form), out var pks) || pks.Count == 0)
            return "\nAvailable formats: (none owned)";
        // Group by the file-extension name PKHeX would use for each PKM type. Sorted by gen ascending.
        var counts = pks
            .GroupBy(GetPkmExtensionName)
            .Select(g => new { Ext = g.Key, Count = g.Count(), Gen = GetPkGen(g.First()) })
            .OrderBy(x => x.Gen)
            .ThenBy(x => x.Ext)
            .ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Available formats:");
        foreach (var entry in counts)
            sb.AppendLine(entry.Count > 1 ? $"  • {entry.Ext} × {entry.Count}" : $"  • {entry.Ext}");
        return sb.ToString();
    }

    /// <summary>Returns the canonical PKHeX file-extension name for a PKM (PK1, PK7, PA8, PB7, etc.).</summary>
    private static string GetPkmExtensionName(PKM pk) => pk switch
    {
        PK1 => "PK1", PK2 => "PK2", PK3 => "PK3", PK4 => "PK4", PK5 => "PK5",
        PK6 => "PK6", PK7 => "PK7",
        PB7 => "PB7",   // Let's Go
        PK8 => "PK8",
        PB8 => "PB8",   // BDSP
        PA8 => "PA8",   // Legends Arceus
        PK9 => "PK9",
        PA9 => "PA9",   // Legends Z-A
        _ => pk.GetType().Name,
    };

    private static string BuildSummaryText(PKM pk)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            string nick = pk.Nickname ?? "";
            string ot = pk.OriginalTrainerName ?? "";
            int level = pk.CurrentLevel;
            int natId = (int)pk.Nature;
            var naturesList = GameInfo.Strings.natures;
            string nature = (uint)natId < naturesList.Length ? naturesList[natId] : "?";
            string ability = (uint)pk.Ability < GameInfo.Strings.abilitylist.Length ? GameInfo.Strings.abilitylist[pk.Ability] : "?";
            string heldItem = (uint)pk.HeldItem < GameInfo.Strings.itemlist.Length ? GameInfo.Strings.itemlist[pk.HeldItem] : "?";
            sb.AppendLine($"Nickname: {nick}");
            sb.AppendLine($"OT: {ot}  •  TID: {pk.DisplayTID}");
            sb.AppendLine($"Level: {level}  •  Nature: {nature}");
            sb.AppendLine($"Ability: {ability}");
            if (!string.IsNullOrEmpty(heldItem) && heldItem != "(None)")
                sb.AppendLine($"Held Item: {heldItem}");
            sb.AppendLine($"IVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}");
            try { if (pk.IsShiny) sb.AppendLine("★ Shiny"); } catch { }
            try { if (pk.IsEgg) sb.AppendLine("Egg"); } catch { }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error reading details: {ex.Message}");
        }
        return sb.ToString();
    }

    private static IEnumerable<byte> GetDistinctSpriteForms(ushort species)
    {
        var personal = PersonalTable.SV;
        if (species > 0 && species < personal.MaxSpeciesID + 1)
        {
            var entry = personal[species, 0];
            int count = entry?.FormCount ?? 1;
            if (count <= 1)
                yield return 0;
            else
                for (byte f = 0; f < count; f++) yield return f;
        }
        else
        {
            yield return 0;
        }
    }

    /// <summary>
    /// Builds a static species/form sprite at the requested target dimensions,
    /// optionally with a faded (low-alpha) overlay for unowned-tile rendering.
    /// </summary>
    /// <remarks>
    /// Requesting an exact target size lets callers feed the sprite straight into
    /// a fixed-size PictureBox (Zoom mode, 1:1 blit) or into a custom paint loop
    /// without any further upscale — preserving every pixel of detail from the
    /// hi-res master in <see cref="SpriteOverrides"/>.
    /// </remarks>
    private static Image? TryBuildSpeciesSprite(ushort species, byte form, int targetW, int targetH, bool faded)
    {
        try
        {
            var bmp = PKDenSpriteUtil.GetSpriteAt(species, form, 0, 0, 0, false, Shiny.Never, EntityContext.Gen9, targetW, targetH);
            if (!faded) return bmp;
            var alpha = ApplyAlpha(bmp, 0.25f);
            bmp.Dispose(); // ApplyAlpha returns a fresh bitmap; original is no longer needed
            return alpha;
        }
        catch { return null; }
    }

    private static Bitmap ApplyAlpha(Image source, float alphaMultiplier)
    {
        var dst = new Bitmap(source.Width, source.Height);
        using var g = Graphics.FromImage(dst);
        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alphaMultiplier };
        var attr = new System.Drawing.Imaging.ImageAttributes();
        attr.SetColorMatrix(matrix);
        g.DrawImage(source, new Rectangle(0, 0, dst.Width, dst.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attr);
        return dst;
    }

    private static string GetSpeciesName(ushort species)
    {
        var list = GameInfo.Strings.specieslist;
        return (uint)species < list.Length ? list[species] : $"#{species}";
    }

    /// <summary>Virtual tile data record. Not a Control — just position + sprite cache.</summary>
    private sealed class DexTile
    {
        public ushort Species { get; }
        public byte Form { get; }
        public Rectangle Bounds { get; set; }
        public bool IsOwned { get; set; }
        /// <summary>
        /// User has chosen to hide this entry (right-click → Hide Pokémon). Hidden tiles
        /// are excluded from layout, paint, and hit-testing so the grid reflows seamlessly
        /// without leaving an empty slot. Persisted in PKDen.dexhidden between sessions.
        /// </summary>
        public bool IsHidden { get; set; }
        public Image? OwnedImage { get; set; }
        public Image? UnownedImage { get; set; }

        public DexTile(ushort species, byte form)
        {
            Species = species;
            Form = form;
        }
    }

    /// <summary>
    /// Custom color table for ContextMenuStrip rendering. Overrides only the colors that show
    /// up as visible white/gray boxes on the default Windows toolstrip palette — the menu
    /// background, item highlight, and image-margin gutter. Everything else falls back to
    /// ProfessionalColorTable defaults which are fine.
    /// </summary>
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color MenuBg = Color.FromArgb(50, 52, 62);
        private static readonly Color HoverBg = Color.FromArgb(80, 100, 140);
        private static readonly Color BorderColor = Color.FromArgb(80, 90, 110);

        // Main menu surface
        public override Color ToolStripDropDownBackground => MenuBg;
        public override Color MenuStripGradientBegin => MenuBg;
        public override Color MenuStripGradientEnd => MenuBg;
        // The vertical gutter on the left of menu items (where icons would live)
        public override Color ImageMarginGradientBegin => MenuBg;
        public override Color ImageMarginGradientMiddle => MenuBg;
        public override Color ImageMarginGradientEnd => MenuBg;
        // Hover/selection highlight on enabled menu items
        public override Color MenuItemSelected => HoverBg;
        public override Color MenuItemSelectedGradientBegin => HoverBg;
        public override Color MenuItemSelectedGradientEnd => HoverBg;
        public override Color MenuItemBorder => BorderColor;
        // Outer menu border
        public override Color MenuBorder => BorderColor;
        // Separator line color
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => BorderColor;
    }

    /// <summary>Panel subclass with double-buffering enabled.</summary>
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            UpdateStyles();
        }
    }
}
