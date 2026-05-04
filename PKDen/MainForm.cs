using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PKHeX.Core;
using PKHeX.Drawing.PokeSprite;

namespace PKDen;

public sealed class MainForm : Form
{
    // --- Data ---
    private SaveFile? SAV;
    private readonly DenStorageManager Den = new();

    // --- Layout ---
    private readonly MenuStrip menuBar;
    private readonly SplitContainer splitMain;
    /// <summary>Top-level tab control with two tabs: Den (existing UI) and Pokédex (new dex view).</summary>
    private TabControl _topTabs = null!;
    private TabPage _denTab = null!;
    private TabPage _dexTab = null!;
    private PokedexPanel _pokedexPanel = null!;
    /// <summary>True once the Pokédex has been pre-warmed; prevents duplicate prewarm runs if Shown fires again.</summary>
    private bool _pokedexPrewarmed;
    private readonly StatusStrip statusBar;
    private readonly ToolStripStatusLabel statusLabel;

    // --- Den Panel (Left) ---
    private readonly Panel denPanel;
    private readonly ComboBox denBoxSelector;
    private readonly Button denPrev;
    private readonly Button denNext;
    private readonly FlowLayoutPanel denGrid;
    private readonly Label denCountLabel;

    // --- Save Panel (Right) ---
    private readonly Panel savePanel;
    private readonly Label saveTitle;
    /// <summary>Inline trainer-info label shown next to the save box selector.</summary>
    private Label _saveInfoLabel = null!;
    /// <summary>Box-name header shown above the Den grid; updates as the current box changes.</summary>
    private Label _denBoxNameHeader = null!;
    private readonly ComboBox saveBoxSelector;
    private readonly Button savePrev;
    private readonly Button saveNext;
    private readonly FlowLayoutPanel saveGrid;
    private readonly Label saveCountLabel;
    private readonly Panel saveEmptyPanel;

    // --- Summary Panel (bottom-left of Den) ---
    private Panel _summaryPanel = null!;
    private PictureBox _summarySprite = null!;
    private Label _summaryDexNumber = null!;
    private Label _summaryTitle = null!;
    private Label _summaryDetails = null!;
    private TextBox _summaryNote = null!;
    private Button _summaryNoteSave = null!;
    /// <summary>Held as a field so the View → Show Notes toggle can hide it.</summary>
    private Label _summaryNoteLabel = null!;
    private int _summaryDenBox = -1;
    private int _summaryDenSlot = -1;
    private PKM? _summaryCurrentPk;
    private bool _summarySourceIsDen;

    // --- Party Panel (below save panel, shows 6-slot party when save is loaded) ---
    private Panel _partyPanel = null!;
    private FlowLayoutPanel _partyGrid = null!;
    private readonly List<PictureBox> _partySlots = [];

    // --- Slot State ---
    private readonly List<PictureBox> denSlots = [];
    private readonly List<PictureBox> saveSlots = [];
    private readonly HashSet<int> selectedDenSlots = [];
    private readonly HashSet<int> selectedSaveSlots = [];

    // --- Clipboard (multi-select) ---
    private readonly List<(PKM Pk, string? Note, DateTime? Timestamp)> _clipboard = [];

    // --- Recently Deleted ---
    // Capacity 50 (user-requested). UI shows 10 at a time with scroll. Persists between sessions
    // in PKDen.recent (encrypted PKM bytes + metadata) so accidental deletes survive restart.
    private readonly List<(PKM Pk, string? Note, DateTime DeletedAt)> _recentlyDeleted = [];
    private const int RecentlyDeletedCapacity = 50;
    private const int RecentlyDeletedVisibleCount = 10;
    private readonly List<PictureBox> _recentSlots = [];
    private int _recentScrollOffset;
    private Panel _recentPanel = null!;
    private FlowLayoutPanel _recentGrid = null!;
    private Label _recentCountLabel = null!;
    private Button _recentPrev = null!;
    private Button _recentNext = null!;
    private Button _recentClear = null!;

    // --- Undo ---
    // (See UndoSnapshot struct and _undoStackTyped near the Undo methods)
    private const int MaxUndo = 20;

    // --- Search state (flat view across all boxes) ---
    private bool _isSearchActive;
    private readonly List<(int Box, int Slot, PKM Pk)> _searchResults = [];

    // --- Search/Filter ---
    private readonly TextBox _searchBox;
    private readonly TextBox _filterOT;

    // --- Constants ---
    private const int SlotsPerDenBox = 30;
    private float _spriteScale = 1.0f; // Den sprite scale: 0.5x, 1x, 1.5x, 2x, 2.5x, 3x, 4x
    private float _saveSpriteScale = 1.0f; // Save panel sprite scale

    /// <summary>
    /// Party sprite scale — separate from <see cref="_saveSpriteScale"/> so the user can
    /// have, say, 1× boxes but 2× party slots (or vice versa).  Defaults to mirroring the
    /// save scale on a fresh install for backwards compatibility — gets divorced once the
    /// user picks a value from the View menu.
    /// </summary>
    private float _partySpriteScale = 1.0f;

    /// <summary>
    /// When false, the Notes textbox + label + Save button are hidden in the summary panel
    /// and the detail label is widened to use the freed space.  Toggleable from View menu.
    /// </summary>
    private bool _showSummaryNotes = true;

    /// <summary>
    /// Horizontal alignment for the summary panel content (sprite + dex# on left,
    /// title + details centered around).  Stored as ContentAlignment so we can reuse
    /// it directly with Label.TextAlign without converting.
    /// Possible values: MiddleLeft, MiddleCenter, MiddleRight.
    /// </summary>
    private ContentAlignment _summaryAlignment = ContentAlignment.MiddleLeft;
    private float _summaryTextSize = 8.5f; // Pokémon Summary detail text size
    private float _slotLabelTextSize = 7.0f; // Font size for sprite slot labels (name/gender/origin)
    private bool _showHeldItem = true; // Whether to show held-item icon overlay on sprites
    private bool _showBoxTotals = true; // Whether to show the "X/30 in box — Y total" labels at the bottom of each grid
    // === SELECTION STYLE ===
    // Selection used to flood the entire slot's BackColor blue. We now draw a colored outline
    // around the slot's edges instead. The user picks the color (red/blue/green/yellow) and
    // outline thickness in View → Selection Style.
    private Color _selectionOutlineColor = Color.FromArgb(80, 140, 220);  // default = the old "blue" hue
    private int _selectionOutlineThickness = 3;  // pixels; 1-6 supported
    // Summary text styling: black instead of white, optional bold weight
    private bool _summaryTextBlack;             // when true, summary detail text renders in black
    private bool _summaryTextBold;              // when true, summary detail text renders bold
    /// <summary>
    /// When true, the Den grid is forced into a centered 6×5 layout (6 columns, 5 rows = 30 slots).
    /// Defaults to true so a fresh install presents the boxes in the layout most users expect
    /// from in-game PC boxes; users who prefer auto-flow can toggle it off in the View menu.
    /// </summary>
    private bool _use6x5DenGrid = true;
    // Same option for the Save panel — only really meaningful for saves with at least 30 slots/box.
    private bool _use6x5SaveGrid;
    private bool _showNames = false;
    private bool _showGenders = false;
    private bool _showOrigin = false;
    private static int SpriteW => SpriteUtil.Spriter.Width;
    private static int SpriteH => SpriteUtil.Spriter.Height;

    // --- File paths ---
    /// <summary>Path used to LOAD the Den — falls back to legacy PokemonDen.den if new PKDen.den missing.</summary>
    private string DenLoadPath => DenStorageManager.GetDefaultSavePath();
    /// <summary>Path used to SAVE the Den — always writes to the new PKDen.den name.</summary>
    private string DenSavePath => DenStorageManager.GetSavePathForWriting();
    private string? _currentSavePath;

    /// <summary>
    /// User-chosen directory containing save files for the selector view.  Persisted
    /// to PKDen.cfg.  Null = no directory set; selector shows a "set directory" prompt.
    /// </summary>
    private string? _savesDirectory;

    /// <summary>The save-file-selector card list.  Lives inside savePanel; toggled visible
    /// when no save is loaded.  See <see cref="SaveFileSelectorPanel"/>.</summary>
    private SaveFileSelectorPanel _saveSelector = null!;

    /// <summary>
    /// Background music player — created lazily on first use.  Loaded with the
    /// path/volume saved in the den file when the den loads (see <see cref="StartMusicIfConfigured"/>).
    /// </summary>
    private MusicPlayer? _musicPlayer;

    public MainForm()
    {
        Text = "PKDen";
        MinimumSize = new Size(900, 550);
        Size = new Size(1050, 650);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // Wire the den storage into the sprite resolution layer so the custom-sprite
        // right-click feature can be picked up by every sprite call site without each
        // call site needing to know about the Den directly.
        PKDenSpriteUtil.DenRef = Den;

        // Load pokeball icon from embedded resource
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var iconStream = asm.GetManifestResourceStream("PKDen.pokeball.ico");
        if (iconStream is not null)
            Icon = new Icon(iconStream);

        // --- Load saved view preferences BEFORE menu is created so checkmarks reflect state ---
        LoadSettings();

        // --- Menu Bar ---
        menuBar = CreateMenuBar();
        Controls.Add(menuBar);

        // --- Status Bar ---
        statusBar = new StatusStrip();
        statusLabel = new ToolStripStatusLabel("Welcome to PKDen. Open a save file or import Pokémon to get started.");
        statusBar.Items.Add(statusLabel);
        Controls.Add(statusBar);

        // --- Main Split ---
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 620,
            Orientation = Orientation.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
        };

        // Wrap splitMain inside a top-level TabControl so we can switch between the Den
        // (existing two-pane UI) and the Pokédex (new dex grid). The TabControl docks fill,
        // tabs are drawn at the top — clicking either tab switches the active view.
        //
        // Owner-draw is used so we can make the tab labels readable (bigger font, bold,
        // higher contrast, distinctive selected/unselected styling).  Default Windows
        // theme tabs against PKDen's dark background looked washed-out and were hard to
        // tell apart at a glance — this gives them clear visual weight.
        _topTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(140, 34),
            Padding = new Point(14, 6),
            DrawMode = TabDrawMode.OwnerDrawFixed,
        };
        _topTabs.DrawItem += TopTabs_DrawItem;
        _denTab = new TabPage("Den") { BackColor = Color.FromArgb(40, 40, 48), UseVisualStyleBackColor = false };
        _dexTab = new TabPage("Pokédex") { BackColor = Color.FromArgb(40, 40, 48), UseVisualStyleBackColor = false };
        _denTab.Controls.Add(splitMain);
        _topTabs.TabPages.Add(_denTab);
        _topTabs.TabPages.Add(_dexTab);

        Controls.Add(_topTabs);
        _topTabs.BringToFront();

        // === DEN PANEL (Left) ===
        denPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 48) };
        denBoxSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        denPrev = new Button { Text = "◀", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        denNext = new Button { Text = "▶", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        var denRename = new Button { Text = "✏", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        denRename.Click += (_, _) => RenameDenBox();
        denGrid = CreateGrid();
        // When the den grid is resized (e.g. user resizes window or splitter), re-apply the 6×5 centering padding.
        denGrid.SizeChanged += (_, _) => ApplyDenGridLayout();
        denCountLabel = new Label { AutoSize = true, Text = "0 Pokémon", Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0, 4, 0, 4) };

        // Nav panel with box navigation, arrange, send box, AND search — all in one row
        var denArrange = new Button { Text = "Arrange", Width = 60, Height = 26, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(70, 100, 160) };
        denArrange.Click += (_, _) => OpenArrangeWindow();
        var denSendBox = new Button { Text = "Send Box →", Width = 80, Height = 26, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        denSendBox.Click += (_, _) => SendBoxToAnotherBox();

        _searchBox = new TextBox { Width = 100, Height = 22, PlaceholderText = "Search name..." };
        _searchBox.TextChanged += (_, _) => ApplySearchFilter();
        _filterOT = new TextBox { Width = 80, Height = 22, PlaceholderText = "Search OT..." };
        _filterOT.TextChanged += (_, _) => ApplySearchFilter();
        var btnClearFilter = new Button { Text = "✕", Width = 26, Height = 22, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        btnClearFilter.Click += (_, _) => ClearSearchFilter();

        var denNav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        denNav.Controls.AddRange([denPrev, denBoxSelector, denNext, denRename, denArrange, denSendBox, _searchBox, _filterOT, btnClearFilter]);

        // Build the bottom summary panel
        BuildSummaryPanel();

        // Box name header — shown above the Den grid, updates with the current box.
        // BackColor matches the nav row so visually they read as a single banner.
        // Use the double-buffered Label subclass for the title header so it doesn't flash
        // during grid scrolls.  Plain Label has no double-buffering by default — when the
        // grid scrolls, WinForms repaints the label in two passes (background, then text)
        // and the brief gap between them shows as a flicker.  See <see cref="DoubleBufferedLabel"/>.
        _denBoxNameHeader = new DoubleBufferedLabel
        {
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = Color.FromArgb(50, 55, 70),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "",
        };

        denPanel.Controls.Add(denCountLabel);
        denPanel.Controls.Add(denGrid);
        denPanel.Controls.Add(_summaryPanel);
        denPanel.Controls.Add(_denBoxNameHeader);
        denPanel.Controls.Add(denNav);
        splitMain.Panel1.Controls.Add(denPanel);

        // Right-click on the empty area of the Den grid → layout / display options.
        // (Right-clicking a slot is intercepted by that slot's MouseClick handler before this.)
        denGrid.MouseClick += DenGrid_MouseClick;
        // Mouse-wheel handling: smooth vertical scroll within a box, and at the boundaries
        // (scroll past the top or past the bottom) advance to the previous/next box.
        // FlowLayoutPanel doesn't natively bubble wheel events for AutoScroll past the edges,
        // so we hook MouseWheel and post-process the scroll position to detect "scroll-past-end".
        denGrid.MouseWheel += DenGrid_MouseWheel;
        // FlowLayoutPanel only receives MouseWheel events when it has keyboard focus.
        // At startup the focus is on the form itself or another tab-stop, so wheel events go nowhere
        // until the user clicks into the grid (which is what was happening before).
        // Stealing focus on hover makes the wheel work immediately.
        denGrid.MouseEnter += (_, _) => { if (denGrid.CanFocus && !denGrid.Focused) denGrid.Focus(); };

        // === SAVE PANEL (Right) ===
        savePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 48) };
        saveTitle = CreateTitleLabel("No Save Loaded");
        saveBoxSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        savePrev = new Button { Text = "◀", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        saveNext = new Button { Text = "▶", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        saveGrid = CreateGrid();
        saveGrid.SizeChanged += (_, _) => ApplySaveGridLayout();
        saveGrid.MouseClick += SaveGrid_MouseClick;
        saveCountLabel = new Label { AutoSize = true, Text = "", Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0, 4, 0, 4) };

        // Empty state message
        saveEmptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 48) };
        // Empty-state UI: a label and an "Open Save File…" button, vertically centered.
        // We use a TableLayoutPanel with three rows (top spacer / content / bottom spacer)
        // so the contents stay centered as the panel resizes.
        var emptyContent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(40, 40, 48),
        };
        emptyContent.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        emptyContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        emptyContent.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        emptyContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Inner stacking panel holds the label and button vertically centered as one block
        var emptyInner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None, // centered horizontally within its TableLayoutPanel cell
            BackColor = Color.FromArgb(40, 40, 48),
            Padding = new Padding(8),
        };
        var emptyLabel = new Label
        {
            Text = "No save file loaded.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14),
        };
        var emptyHint = new Label
        {
            Text = "Load a Pokémon save to view its boxes.",
            ForeColor = Color.FromArgb(160, 160, 170),
            Font = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 18),
        };
        var emptyOpenBtn = new Button
        {
            Text = "Open Save File…",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Width = 220,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 130, 180),  // matches the menubar version-label cyan
            Margin = new Padding(0, 0, 0, 0),
        };
        emptyOpenBtn.FlatAppearance.BorderColor = Color.FromArgb(40, 90, 130);
        emptyOpenBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(95, 155, 205);
        emptyOpenBtn.Click += (_, _) => OpenSaveFile();
        // Center the button itself horizontally inside the inner FlowLayoutPanel
        // (FlowLayoutPanel's TopDown flow lays children left-aligned; AutoSize on the panel
        // means the panel shrinks to fit the widest child, so anchoring the panel = button centered.)
        emptyInner.Controls.Add(emptyLabel);
        emptyInner.Controls.Add(emptyHint);
        emptyInner.Controls.Add(emptyOpenBtn);

        emptyContent.Controls.Add(emptyInner, 0, 1);
        saveEmptyPanel.Controls.Add(emptyContent);

        // Transfer buttons
        var saveTransferPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 62, AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        // Close save button — leftmost position so it's the first thing the user
        // sees when they want to leave the current save and return to the selector.
        // Distinctive red-ish color so it doesn't get confused with a transfer action.
        var btnCloseSave = new Button
        {
            Text = "← Close Save",
            Width = 110, Height = 24,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(140, 70, 70),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        btnCloseSave.FlatAppearance.MouseOverBackColor = Color.FromArgb(170, 85, 85);
        btnCloseSave.Click += (_, _) => CloseSaveFile();
        var btnSelectedToDen = new Button { Text = "Move Currently Selected to Den", Width = 200, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        btnSelectedToDen.Click += BtnCopySelectedToDen_Click;
        var btnAllToDen = new Button { Text = "Move Current Box to Den", Width = 170, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        btnAllToDen.Click += BtnCopyAllToDen_Click;
        var btnDumpAll = new Button { Text = "Move All Pokémon from Save File to Den", Width = 260, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(70, 100, 160) };
        btnDumpAll.Click += (_, _) => DumpEntireSaveToDen();
        var btnExportSave = new Button { Text = "Export Entire Save as .pk Files", Width = 200, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(100, 70, 160) };
        btnExportSave.Click += (_, _) => ExportEntireSaveAsPKFiles();
        var btnExportSavePCData = new Button { Text = "Export Save as pcdata.bin", Width = 170, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(70, 130, 130) };
        btnExportSavePCData.Click += (_, _) => ExportSaveAsPCDataBin();
        saveTransferPanel.Controls.AddRange([btnCloseSave, btnSelectedToDen, btnAllToDen, btnDumpAll, btnExportSave, btnExportSavePCData]);

        var saveNav = CreateNavPanel(savePrev, saveBoxSelector, saveNext);
        // Inline trainer-info label next to the box selector — replaces the old top-bar title.
        // Populated when a save loads (OT name, TID, game name, generation).
        _saveInfoLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(10, 7, 4, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Text = "",
        };
        saveNav.Controls.Add(_saveInfoLabel);

        // Build party panel (6-slot party display, docked bottom of save panel)
        BuildPartyPanel();

        savePanel.Controls.Add(saveCountLabel);
        // Save-file selector — scrollable card list for the user's chosen saves directory.
        // Shown ONLY when no save is currently loaded.  Sits in the SAME slot as
        // saveEmptyPanel (both Dock=Fill); we toggle Visible based on which one should
        // be active at any given time.  saveEmptyPanel takes precedence when no
        // directory is set OR when the directory yields no save files, so the existing
        // "Open Save File…" path always remains discoverable.
        _saveSelector = new SaveFileSelectorPanel(
            onOpenSave: path => LoadSaveFile(path),
            onPickDirectory: PickSavesDirectory,
            onBrowseForSave: OpenSaveFile)
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };
        savePanel.Controls.Add(_saveSelector);
        savePanel.Controls.Add(saveEmptyPanel);
        savePanel.Controls.Add(saveGrid);
        savePanel.Controls.Add(_partyPanel);
        savePanel.Controls.Add(saveTransferPanel);
        savePanel.Controls.Add(saveNav);
        savePanel.Controls.Add(saveTitle);

        // === Recently Deleted Panel (below save panel) ===
        BuildRecentlyDeletedPanel();

        // Split Panel2 into Save (top) and Recently Deleted (bottom)
        // Recently Deleted matches the Summary panel height for visual alignment.
        // Add a small top spacer so Recently Deleted sits slightly lower than the
        // savePanel's bottom edge — improves visual separation between the two areas.
        _recentPanel.Dock = DockStyle.Bottom;
        _recentPanel.Height = SummaryPanelHeight;
        savePanel.Dock = DockStyle.Fill;

        // Two stacked separators give the boundary visible thickness AND a small gap:
        //   • 6px transparent spacer (background-colored) — pushes Recently Deleted down
        //   • 2px highlight bar — the actual visible boundary line
        var recentSpacer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 6,
            BackColor = Color.FromArgb(40, 40, 48),
        };
        var recentSeparator = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = Color.FromArgb(80, 80, 90),
        };

        // Order matters with Dock: add bottom-most first (recent panel), then the
        // boundary separator, then the spacer that sits between separator and the
        // save panel above, then the fill (save panel).
        splitMain.Panel2.Controls.Add(savePanel);
        splitMain.Panel2.Controls.Add(recentSpacer);
        splitMain.Panel2.Controls.Add(recentSeparator);
        splitMain.Panel2.Controls.Add(_recentPanel);

        // Initially hide save controls, show empty panel
        saveGrid.Visible = false;
        saveTransferPanel.Visible = false;
        saveNav.Visible = false;

        // --- Initialize Den slots ---
        InitializeSlotPictureBoxes(denGrid, denSlots, true, SlotsPerDenBox);

        // --- Wire Events ---
        denBoxSelector.SelectedIndexChanged += (_, _) => { if (!_suppressBoxSelectorEvents) RefreshDenGrid(); };
        denPrev.Click += (_, _) => NavigateDenBox(-1);
        denNext.Click += (_, _) => NavigateDenBox(+1);
        saveBoxSelector.SelectedIndexChanged += (_, _) => RefreshSaveGrid();
        savePrev.Click += (_, _) => NavigateSaveBox(-1);
        saveNext.Click += (_, _) => NavigateSaveBox(+1);
        KeyDown += MainForm_KeyDown;
        FormClosing += MainForm_FormClosing;
        // Apply saved window size and splitter once form is fully shown (layout is stable)
        Shown += (_, _) => ApplySavedWindowLayout();
        // Apply persisted view options that need controls to exist first
        Shown += (_, _) => ApplyBoxTotalsVisibility();
        Shown += (_, _) => ApplySummaryTextStyle();
        Shown += (_, _) => CenterRecentlyDeletedRow();
        // Initialize the save-file selector from the persisted directory (if any).  Done in
        // Shown rather than in the constructor because the SaveFileSelectorPanel needs a
        // realized HWND for accurate layout, and the embedded artwork resource cache builds
        // lazily on first use anyway.
        Shown += (_, _) =>
        {
            _saveSelector.SetDirectory(_savesDirectory);
            UpdateSaveSelectorVisibility();
        };
        // Pre-warm the Pokédex AFTER the form is fully realized — Bitmap and sprite resource
        // operations need the controls to exist as Win32 HWNDs to behave reliably.
        // Runs once thanks to the _prewarmed flag inside the helper.
        Shown += (_, _) => RunFirstShowPokedexPrewarm();

        // --- Load Den data ---
        AutoLoadDen();
        LoadRecentlyDeleted();
        PopulateDenBoxNames();
        if (denBoxSelector.Items.Count > 0)
        {
            // Restore the last-used Den box if it's still valid; fall back to 0
            int startIdx = 0;
            if (_savedLastDenBox.HasValue && _savedLastDenBox.Value < denBoxSelector.Items.Count)
                startIdx = _savedLastDenBox.Value;
            denBoxSelector.SelectedIndex = startIdx;
        }
        // Explicit refresh — the SelectedIndex assignment may not fire an event if suppressed or already 0
        RefreshDenGrid();

        // --- Pokédex ---
        // Construct the panel now (cheap), but defer building the 1000+ tile grid until the user
        // first clicks the Pokédex tab. That keeps app startup snappy. We still hand it the export
        // callback up front so it can wire its "Export to .PK" button.
        _pokedexPanel = new PokedexPanel(Den, (pk, owner) => ExportSinglePKMFromPokedex(pk, owner))
        {
            Dock = DockStyle.Fill,
        };
        _dexTab.Controls.Add(_pokedexPanel);
        _topTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_topTabs.SelectedTab == _dexTab)
            {
                // Build tiles on first switch (one-time, ~1100 picture boxes)
                _pokedexPanel.BuildDexTiles();
                // Always recompute ownership when entering the tab — Den state may have changed
                _pokedexPanel.RecomputeOwnedAndRedraw();
            }
        };
    }

    /// <summary>
    /// Helper used by the Pokédex's Export button. Reuses the existing PKHeX-style filename
    /// builder so files exported from the dex match files exported from the Den.
    /// </summary>
    /// <summary>
    /// Pre-warms the Pokédex panel: builds all tile picture boxes AND eagerly generates every
    /// sprite (owned + unowned variants). Called from the startup splash so the Pokédex tab
    /// is instantly responsive when first opened during the session.
    ///
    /// The progress callback receives status messages like "Loading sprites… 320 / 1100" suitable
    /// for display on the splash screen. Pumps the message loop internally so the splash repaints.
    /// </summary>
    /// <summary>
    /// Runs the Pokédex pre-warm AFTER the form becomes visible — at this point all controls
    /// are realized as Win32 HWNDs, which Bitmap/sprite generation needs to behave reliably.
    /// Shows a full-form "Loading Pokédex…" overlay during the work so the menu bar and tabs
    /// can't be clicked. Removes the overlay when done.
    ///
    /// Guarded by <see cref="_pokedexPrewarmed"/> so it only runs once even though the Shown
    /// event can in principle fire again.
    /// </summary>
    private void RunFirstShowPokedexPrewarm()
    {
        if (_pokedexPrewarmed) return;
        _pokedexPrewarmed = true;
        if (_pokedexPanel is null) return;

        // Full-form opaque overlay covering everything (menu bar excluded so the user sees a
        // recognizable PKDen frame; the rest of the UI is hidden behind a "loading" panel).
        var overlay = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 32, 40),
        };
        var title = new Label
        {
            Text = "Loading Pokédex…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
        };
        var status = new Label
        {
            Text = "Building tiles…",
            Dock = DockStyle.Bottom,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(180, 180, 200),
            Font = new Font("Segoe UI", 10),
        };
        overlay.Controls.Add(title);
        overlay.Controls.Add(status);

        // Insert overlay over the tab control so it covers the full app body. We add it last
        // so it renders on top in the z-order (or BringToFront after add).
        Controls.Add(overlay);
        overlay.BringToFront();
        overlay.Refresh();          // force the overlay to paint NOW, before any blocking work
        Application.DoEvents();      // pump once so the overlay is fully visible

        try
        {
            void Progress(string msg)
            {
                status.Text = msg;
                status.Refresh();
                Application.DoEvents();
            }

            Progress("Building Pokédex tiles…");
            _pokedexPanel.BuildDexTiles();
            Progress("Computing ownership…");
            _pokedexPanel.RecomputeOwnedAndRedraw();
            Progress("Loading sprites…");
            _pokedexPanel.PrewarmAllSprites(Progress);
        }
        catch (Exception ex)
        {
            // Log but don't crash the app — the dex will fall back to lazy loading on tab switch.
            System.Diagnostics.Debug.WriteLine($"Pokédex prewarm failed: {ex}");
        }
        finally
        {
            Controls.Remove(overlay);
            overlay.Dispose();
            // After the overlay is gone, the Den grid was hidden behind it during prewarm and
            // its FlowLayoutPanel scroll bounds may not be computed yet — symptom is "scroll
            // wheel scrolls into blank space below the visible row of slots." Force a layout
            // pass + refresh now, then focus the grid so the wheel handler fires immediately
            // without requiring a click into the panel first.
            try
            {
                denGrid.PerformLayout();
                RefreshDenGrid();
                if (denGrid.CanFocus) denGrid.Focus();
            }
            catch { /* best-effort cleanup */ }
        }
    }

    public void PrewarmPokedex(Action<string>? progressCallback = null)
    {
        if (_pokedexPanel is null) return;
        progressCallback?.Invoke("Building Pokédex tiles…");
        _pokedexPanel.BuildDexTiles();
        progressCallback?.Invoke("Loading Pokédex sprites…");
        _pokedexPanel.RecomputeOwnedAndRedraw();
        _pokedexPanel.PrewarmAllSprites(progressCallback);
    }

    private void ExportSinglePKMFromPokedex(PKM pk, IWin32Window? owner)
    {
        try
        {
            ExportUtil.ExportSinglePKM(pk, owner ?? this);
            SetStatus($"Exported {GetSpeciesName(pk)} to .pk file.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n\n{ex.Message}", "Export to .PK",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ========================================================================
    //  MENU BAR
    // ========================================================================

    private MenuStrip CreateMenuBar()
    {
        var ms = new MenuStrip();

        // --- File ---
        var fileMenu = new ToolStripMenuItem("File");

        var openSave = new ToolStripMenuItem("Open Save File...", null, (_, _) => OpenSaveFile()) { ShortcutKeys = Keys.Control | Keys.O };
        var setSavesDir = new ToolStripMenuItem("Set Saves Directory...", null, (_, _) => PickSavesDirectory());
        var closeSave = new ToolStripMenuItem("Close Save File", null, (_, _) => CloseSaveFile());
        var saveDen = new ToolStripMenuItem("Save Den", null, (_, _) => SaveDenStorage(DenSavePath)) { ShortcutKeys = Keys.Control | Keys.S };
        var saveDenAs = new ToolStripMenuItem("Save Den As...", null, (_, _) => SaveDenAs());
        var loadDen = new ToolStripMenuItem("Load Den...", null, (_, _) => LoadDenFile());

        fileMenu.DropDownItems.AddRange([openSave, setSavesDir, closeSave, new ToolStripSeparator(), saveDen, saveDenAs, loadDen]);

        // --- Import ---
        var importMenu = new ToolStripMenuItem("Import .pk(s)");

        var importFolder = new ToolStripMenuItem("Import Folder...", null, (_, _) => ImportFolder());
        var importFiles = new ToolStripMenuItem("Import Files...", null, (_, _) => ImportFiles());
        var importPCData = new ToolStripMenuItem("Import pcdata.bin...", null, (_, _) => ImportPCDataBin());
        var importMG = new ToolStripMenuItem("Import Mystery Gifts...", null, (_, _) => ImportMysteryGifts());
        // Note: "Dump Entire Save → Den" used to live here but was redundant with the inline
        // "Move All Pokémon from Save File to Den" button in the save panel — removed.
        importMenu.DropDownItems.AddRange([importFolder, importFiles, importPCData, importMG]);

        // --- Export ---
        var exportMenu = new ToolStripMenuItem("Export");

        var exportSelected = new ToolStripMenuItem("Export Selected to .pk Files...", null, (_, _) => ExportSelectedPKFiles());
        var exportBox = new ToolStripMenuItem("Export Boxes to .pk Files...", null, (_, _) => ExportCurrentBoxPKFiles());
        var exportAllPK = new ToolStripMenuItem("Export Entire Den to .pk Files...", null, (_, _) => ExportDatabaseFlat());
        var exportByBox = new ToolStripMenuItem("Export Entire Den to .pk Files (by box)...", null, (_, _) => ExportDatabaseByBox());

        // Export by Generation submenu
        var exportByGen = new ToolStripMenuItem("Export by Generation");
        for (int g = 1; g <= 9; g++)
        {
            int gen = g;
            exportByGen.DropDownItems.Add(new ToolStripMenuItem($"Gen {gen}", null, (_, _) => ExportByGeneration(gen)));
        }

        exportMenu.DropDownItems.AddRange([exportSelected, exportBox, new ToolStripSeparator(), exportAllPK, exportByBox, new ToolStripSeparator(), exportByGen]);
        var exportPCData = new ToolStripMenuItem("Export Den as pcdata.bin...", null, (_, _) => ExportPCDataBin());
        exportMenu.DropDownItems.Add(new ToolStripSeparator());
        exportMenu.DropDownItems.Add(exportPCData);

        // --- Edit ---
        var editMenu = new ToolStripMenuItem("Edit");

        var addBoxes = new ToolStripMenuItem("Add More Boxes...", null, (_, _) => AddMoreBoxes());

        var sortDropDown = new ToolStripMenuItem("Sort Den (All Boxes)");
        var sortNames = DenSortComparers.GetSortModeNames();
        for (int i = 0; i < sortNames.Count; i++)
        {
            var mode = (DenSortMode)i;
            sortDropDown.DropDownItems.Add(new ToolStripMenuItem(sortNames[i], null, (_, _) =>
            {
                // Global sort: all Pokémon across every box are treated as a single continuous
                // list, sorted once, and re-laid-out starting at Box 1, slot 1. Pokémon will
                // move between boxes — confirm with the user so this isn't surprising.
                int totalPK = Den.GetTotalCount();
                if (totalPK == 0)
                {
                    MessageBox.Show(this, "Den is empty — nothing to sort.", "Sort Den",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                int boxesNeeded = (totalPK + SlotsPerDenBox - 1) / SlotsPerDenBox;
                var msg = $"This will sort all {totalPK:N0} Pokémon across every box by {mode}, " +
                          $"then re-pack them sequentially starting at Box 1, slot 1.\n\n" +
                          $"Result: {totalPK:N0} Pokémon will fill Boxes 1–{boxesNeeded}; later boxes will be emptied.\n\n" +
                          $"Pokémon WILL move between boxes. Box names and backgrounds stay attached to the box position " +
                          $"(not the contents).\n\nContinue?";
                if (MessageBox.Show(this, msg, "Sort Den (All Boxes)",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                PushUndo();
                Den.SortAll(DenSortComparers.GetComparison(mode));
                RefreshDenGrid();
                SetStatus($"Sorted {totalPK:N0} Pokémon across Boxes 1–{boxesNeeded} by {mode}.");
            }));
        }

        var sortBoxDropDown = new ToolStripMenuItem("Sort Current Box");
        for (int i = 0; i < sortNames.Count; i++)
        {
            var mode = (DenSortMode)i;
            sortBoxDropDown.DropDownItems.Add(new ToolStripMenuItem(sortNames[i], null, (_, _) =>
            {
                int box = CurrentDenBox;
                if (box < 0) return;
                PushUndo(); Den.SortBox(box, DenSortComparers.GetComparison(mode));
                RefreshDenGrid();
                SetStatus($"Sorted Den box {box + 1} by {mode}.");
            }));
        }

        // --- Sort Selected Boxes... — pick multiple boxes, then sort them ---
        var sortSelectedDropDown = new ToolStripMenuItem("Sort Selected Boxes...");
        for (int i = 0; i < sortNames.Count; i++)
        {
            var mode = (DenSortMode)i;
            sortSelectedDropDown.DropDownItems.Add(new ToolStripMenuItem(sortNames[i], null, (_, _) =>
            {
                int defBox = CurrentDenBox;
                var picked = ShowBoxMultiPicker(
                    "Sort Selected Boxes",
                    $"Select which boxes to sort by {mode}:",
                    defBox >= 0 ? new HashSet<int> { defBox } : []);
                if (picked is null || picked.Count == 0) return;
                PushUndo();
                var cmp = DenSortComparers.GetComparison(mode);
                foreach (int b in picked) Den.SortBox(b, cmp);
                RefreshDenGrid();
                SetStatus($"Sorted {picked.Count} box(es) by {mode}.");
            }));
        }

        var compact = new ToolStripMenuItem("Compact Current Box", null, (_, _) =>
        {
            int box = CurrentDenBox;
            if (box < 0) return;
            PushUndo(); Den.CompactBox(box);
            RefreshDenGrid();
            SetStatus($"Compacted Den box {box + 1}.");
        });

        // --- Background sub-menu ---
        // Image-only background mode. Solid-color box backgrounds were removed in V0.1.6 —
        // for solid coloring inside slots, the user wants the per-box "Box Color" submenu
        // (slot fill color), not a panel background tint.
        var bgMenu = new ToolStripMenuItem("Box Background");
        bgMenu.DropDownItems.Add(new ToolStripMenuItem("Set Image Background (All Boxes)...", null, (_, _) => SetBackground(true)));
        bgMenu.DropDownItems.Add(new ToolStripMenuItem("Set Image Background (This Box)...", null, (_, _) => SetBackground(false)));
        bgMenu.DropDownItems.Add(new ToolStripSeparator());
        bgMenu.DropDownItems.Add(new ToolStripMenuItem("Clear Background (All Boxes)", null, (_, _) =>
        {
            Den.SetGlobalBackground(null);
            for (int i = 0; i < Den.BoxCount; i++)
                Den.SetBoxBackground(i, null);
            RefreshDenGrid();
            SetStatus("All image backgrounds cleared.");
        }));
        bgMenu.DropDownItems.Add(new ToolStripMenuItem("Clear Background (This Box)", null, (_, _) =>
        {
            Den.SetBoxBackground(CurrentDenBox, null);
            RefreshDenGrid();
            SetStatus("Box image background cleared.");
        }));

        // --- Background Music ---
        // Plays a single user-supplied audio track on loop.  Settings persist in the
        // den file so the chosen track + volume travel with the den across machines.
        var musicMenu = new ToolStripMenuItem("Background Music");
        musicMenu.DropDownItems.Add(new ToolStripMenuItem("Set Track...", null, (_, _) => PickBackgroundMusic()));
        musicMenu.DropDownItems.Add(new ToolStripMenuItem("Stop / Clear Track", null, (_, _) => StopBackgroundMusic(clearSetting: true)));
        musicMenu.DropDownItems.Add(new ToolStripSeparator());
        musicMenu.DropDownItems.Add(new ToolStripMenuItem("Volume...", null, (_, _) => ShowVolumeSlider()));

        var deleteBox = new ToolStripMenuItem("Delete Current Box", null, (_, _) => DeleteCurrentBox());
        var clearDen = new ToolStripMenuItem("Clear ALL Den Storage", null, (_, _) => ClearDenStorage());

        var undoItem = new ToolStripMenuItem("Undo", null, (_, _) => Undo()) { ShortcutKeys = Keys.Control | Keys.Z };

        editMenu.DropDownItems.AddRange([undoItem, new ToolStripSeparator(), addBoxes, deleteBox, new ToolStripSeparator(), sortDropDown, sortBoxDropDown, sortSelectedDropDown, compact, new ToolStripSeparator(), bgMenu, musicMenu, new ToolStripSeparator(), clearDen]);

        // --- View ---
        var viewMenu = new ToolStripMenuItem("View");

        // Sprite size submenus support fractional zooms (0.5x, 1.5x, 2.5x) for finer control.
        float[] spriteScales = [0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f];

        var zoomMenu = new ToolStripMenuItem("Den Sprite Size");
        foreach (float z in spriteScales)
        {
            float zoom = z;
            var item = new ToolStripMenuItem(FormatScale(zoom), null, (_, _) => SetSpriteScale(zoom))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_spriteScale - zoom) < 0.001f,
            };
            zoomMenu.DropDownItems.Add(item);
        }
        var saveZoomMenu = new ToolStripMenuItem("Save Sprite Size");
        foreach (float z in spriteScales)
        {
            float zoom = z;
            var item = new ToolStripMenuItem(FormatScale(zoom), null, (_, _) => SetSaveSpriteScale(zoom))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_saveSpriteScale - zoom) < 0.001f,
            };
            saveZoomMenu.DropDownItems.Add(item);
        }
        // Party Sprite Size — independent scale for the party row.  Often the user wants
        // the boxes at one zoom (compact, see lots of mons) and the party row at another
        // (larger, since it's only 6 slots and they want to see them clearly).
        var partyZoomMenu = new ToolStripMenuItem("Party Sprite Size");
        foreach (float z in spriteScales)
        {
            float zoom = z;
            var item = new ToolStripMenuItem(FormatScale(zoom), null, (_, _) => SetPartySpriteScale(zoom))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_partySpriteScale - zoom) < 0.001f,
            };
            partyZoomMenu.DropDownItems.Add(item);
        }
        // Summary text size — controls the Pokémon Summary detail label font size
        var summaryTextSizeMenu = new ToolStripMenuItem("Summary Text Size");
        float[] textSizes = [8.0f, 8.5f, 9.0f, 10.0f, 11.0f, 12.0f, 14.0f];
        foreach (float size in textSizes)
        {
            float capture = size;
            var item = new ToolStripMenuItem($"{capture:0.#} pt", null, (_, _) => SetSummaryTextSize(capture))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_summaryTextSize - capture) < 0.01f,
            };
            summaryTextSizeMenu.DropDownItems.Add(item);
        }
        // Slot label text size — controls the per-slot name/gender/origin labels drawn over each Pokémon
        var slotLabelSizeMenu = new ToolStripMenuItem("Slot Label Text Size");
        float[] slotSizes = [6.0f, 7.0f, 8.0f, 9.0f, 10.0f, 11.0f, 12.0f];
        foreach (float size in slotSizes)
        {
            float capture = size;
            var item = new ToolStripMenuItem($"{capture:0.#} pt", null, (_, _) => SetSlotLabelTextSize(capture))
            {
                CheckOnClick = true,
                Checked = Math.Abs(_slotLabelTextSize - capture) < 0.01f,
            };
            slotLabelSizeMenu.DropDownItems.Add(item);
        }
        var showNameItem = new ToolStripMenuItem("Show Pokémon Name", null, (s, _) => { _showNames = !((ToolStripMenuItem)s!).Checked; ((ToolStripMenuItem)s).Checked = _showNames; RebuildDenGrid(); RebuildSaveGrid(); RefreshPartyGrid(); RebuildRecentlyDeletedGrid(); SaveSettings(); }) { Checked = _showNames };
        var showGenderItem = new ToolStripMenuItem("Show Gender Symbol", null, (s, _) => { _showGenders = !((ToolStripMenuItem)s!).Checked; ((ToolStripMenuItem)s).Checked = _showGenders; RebuildDenGrid(); RebuildSaveGrid(); RefreshPartyGrid(); RebuildRecentlyDeletedGrid(); SaveSettings(); }) { Checked = _showGenders };
        var showOriginItem = new ToolStripMenuItem("Show Origin Game", null, (s, _) => { _showOrigin = !((ToolStripMenuItem)s!).Checked; ((ToolStripMenuItem)s).Checked = _showOrigin; RebuildDenGrid(); RebuildSaveGrid(); RefreshPartyGrid(); RebuildRecentlyDeletedGrid(); SaveSettings(); }) { Checked = _showOrigin };
        var showHeldItemMenuItem = new ToolStripMenuItem("Show Held Item Icon", null, (s, _) => { _showHeldItem = !((ToolStripMenuItem)s!).Checked; ((ToolStripMenuItem)s).Checked = _showHeldItem; RebuildDenGrid(); RebuildSaveGrid(); RefreshPartyGrid(); RebuildRecentlyDeletedGrid(); SaveSettings(); }) { Checked = _showHeldItem };
        var showBoxTotalsMenuItem = new ToolStripMenuItem("Show Box Totals", null, (s, _) => { _showBoxTotals = !((ToolStripMenuItem)s!).Checked; ((ToolStripMenuItem)s).Checked = _showBoxTotals; ApplyBoxTotalsVisibility(); RefreshDenGrid(); if (SAV is not null) RefreshSaveGrid(); SaveSettings(); }) { Checked = _showBoxTotals };

        // === Selection style submenu — color picker (R/G/B/Y) and thickness (1-6 px) ===
        var selectionStyleMenu = new ToolStripMenuItem("Selection Style");
        var colorMenu = new ToolStripMenuItem("Outline Color");
        // The original four colors live at the top — keeping them stable so existing
        // muscle-memory works.  Below them are extras that round out the spectrum.
        // "Custom..." opens a standard ColorDialog for any specific shade the user wants.
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
            var item = new ToolStripMenuItem(label, null, (_, _) => SetSelectionOutlineColor(local))
            {
                CheckOnClick = true,
                Checked = _selectionOutlineColor.ToArgb() == local.ToArgb(),
            };
            colorMenu.DropDownItems.Add(item);
        }
        colorMenu.DropDownItems.Add(new ToolStripSeparator());
        colorMenu.DropDownItems.Add(new ToolStripMenuItem("Custom...", null, (_, _) =>
        {
            using var dlg = new ColorDialog { Color = _selectionOutlineColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) SetSelectionOutlineColor(dlg.Color);
        }));
        var thicknessMenu = new ToolStripMenuItem("Outline Thickness");
        for (int t = 1; t <= 6; t++)
        {
            int captured = t;
            var item = new ToolStripMenuItem($"{captured} px", null, (_, _) => SetSelectionOutlineThickness(captured))
            {
                CheckOnClick = true,
                Checked = _selectionOutlineThickness == captured,
            };
            thicknessMenu.DropDownItems.Add(item);
        }
        selectionStyleMenu.DropDownItems.Add(colorMenu);
        selectionStyleMenu.DropDownItems.Add(thicknessMenu);

        // === Summary text styling — black-vs-white and bold-vs-regular toggles ===
        var summaryStyleMenu = new ToolStripMenuItem("Summary Text Style");
        var summaryBlackItem = new ToolStripMenuItem("Black Text", null, (s, _) =>
        {
            _summaryTextBlack = !((ToolStripMenuItem)s!).Checked;
            ((ToolStripMenuItem)s).Checked = _summaryTextBlack;
            ApplySummaryTextStyle();
            SaveSettings();
        }) { Checked = _summaryTextBlack };
        var summaryBoldItem = new ToolStripMenuItem("Bold", null, (s, _) =>
        {
            _summaryTextBold = !((ToolStripMenuItem)s!).Checked;
            ((ToolStripMenuItem)s).Checked = _summaryTextBold;
            ApplySummaryTextStyle();
            SaveSettings();
        }) { Checked = _summaryTextBold };
        summaryStyleMenu.DropDownItems.AddRange([summaryBlackItem, summaryBoldItem]);

        // --- Show Notes in Summary ---
        // When off, the notes textbox/label/Save button are hidden in the summary panel
        // and the detail label can extend into the freed space.  Doesn't delete anyone's
        // existing notes — they're just not shown.
        var showNotesItem = new ToolStripMenuItem("Show Notes in Summary", null, (s, _) =>
        {
            _showSummaryNotes = !((ToolStripMenuItem)s!).Checked;
            ((ToolStripMenuItem)s).Checked = _showSummaryNotes;
            ApplySummaryLayout();
            SaveSettings();
        }) { Checked = _showSummaryNotes };

        // --- Summary Alignment submenu (Left/Center/Right) ---
        // Repositions the title + details labels horizontally inside the summary panel.
        // Sprite stays anchored to its current side of the panel — only the text moves —
        // because anchoring the sprite too looked unbalanced in testing.
        var alignMenu = new ToolStripMenuItem("Summary Alignment");
        var alignLeft   = new ToolStripMenuItem("Left",   null, (_, _) => SetSummaryAlignment(ContentAlignment.MiddleLeft))   { CheckOnClick = true, Checked = _summaryAlignment == ContentAlignment.MiddleLeft };
        var alignCenter = new ToolStripMenuItem("Center", null, (_, _) => SetSummaryAlignment(ContentAlignment.MiddleCenter)) { CheckOnClick = true, Checked = _summaryAlignment == ContentAlignment.MiddleCenter };
        var alignRight  = new ToolStripMenuItem("Right",  null, (_, _) => SetSummaryAlignment(ContentAlignment.MiddleRight))  { CheckOnClick = true, Checked = _summaryAlignment == ContentAlignment.MiddleRight };
        alignMenu.DropDownItems.AddRange([alignLeft, alignCenter, alignRight]);

        viewMenu.DropDownItems.AddRange([zoomMenu, saveZoomMenu, partyZoomMenu, summaryTextSizeMenu, slotLabelSizeMenu, new ToolStripSeparator(), showNameItem, showGenderItem, showOriginItem, showHeldItemMenuItem, showBoxTotalsMenuItem, showNotesItem, new ToolStripSeparator(), alignMenu, selectionStyleMenu, summaryStyleMenu]);

        // --- Help ---
        var helpMenu = new ToolStripMenuItem("Help");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("About PKDen", null, (_, _) => ShowHelpDialog()));

        ms.Items.AddRange([fileMenu, importMenu, exportMenu, editMenu, viewMenu, helpMenu]);

        // Quick-access buttons: Save Den (green) and Exit Without Saving (red), right of Help
        var msSaveDen = new ToolStripButton("Save Den")
        {
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 130, 80),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(8, 2, 2, 2),
            Padding = new Padding(6, 2, 6, 2),
        };
        msSaveDen.Click += (_, _) => SaveDenStorage(DenSavePath);

        var msExitWithoutSaving = new ToolStripButton("Exit Without Saving")
        {
            ForeColor = Color.White,
            BackColor = Color.FromArgb(150, 60, 60),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(2, 2, 2, 2),
            Padding = new Padding(6, 2, 6, 2),
        };
        msExitWithoutSaving.Click += (_, _) => ExitWithoutSaving();

        ms.Items.Add(msSaveDen);
        ms.Items.Add(msExitWithoutSaving);

        // Version label (right-aligned)
        var versionLabel = new ToolStripLabel("V0.1.3")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = Color.Black,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        ms.Items.Add(versionLabel);

        return ms;
    }

    // ========================================================================
    //  HELPERS — UI
    // ========================================================================

    private const int SummaryPanelHeight = 220;
    /// <summary>
    /// Pixel dimension of the summary-panel sprite PictureBox.  Bitmaps fed to
    /// <c>_summarySprite</c> are sized to this exact width/height so the
    /// PictureBox.Zoom blit is 1:1 — no upscale interpolation, the source
    /// PNG's detail survives all the way to the display.
    /// </summary>
    private const int SummarySpriteSize = 96;

    private void BuildSummaryPanel()
    {
        _summaryPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = SummaryPanelHeight,
            BackColor = Color.FromArgb(48, 50, 58),
        };
        // Re-run the layout calculation when the panel resizes so Center / Right alignment
        // tracks the new width.  Without this, the info block would stay glued to its
        // last-computed position when the user resizes the window or moves the splitter.
        _summaryPanel.SizeChanged += (_, _) => ApplySummaryLayout();

        var title = new Label
        {
            Text = "Pokémon Summary",
            Dock = DockStyle.Top, Height = 28,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White, BackColor = Color.FromArgb(60, 62, 72),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        // Sprite on left (doesn't need to match sprite scale — fixed size for consistency)
        _summarySprite = new PictureBox
        {
            Location = new Point(12, 40),
            Width = SummarySpriteSize, Height = SummarySpriteSize,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(40, 42, 50),
            BorderStyle = BorderStyle.FixedSingle,
        };

        // National dex number shown under the sprite (e.g. "#001" for Bulbasaur)
        _summaryDexNumber = new Label
        {
            Location = new Point(12, 138),
            Width = 96, Height = 20,
            AutoSize = false,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        // Title (species — level — shiny star)
        _summaryTitle = new Label
        {
            Location = new Point(120, 40),
            AutoSize = false,
            Width = 380, Height = 22,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White,
        };

        // Detail text (nature, ability, IVs, moves, game, etc.) — multiline label
        _summaryDetails = new Label
        {
            Location = new Point(120, 66),
            AutoSize = false,
            Width = 380, Height = 144,
            Font = new Font("Segoe UI", _summaryTextSize),
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.TopLeft,
        };

        // Notes box on the right side (Den only).  Hidden when _showSummaryNotes is false.
        // Half-height (60px) and no scrollbar — short notes fit, longer ones soft-wrap and
        // the user can scroll within by dragging the cursor (no visible scrollbar by request).
        _summaryNoteLabel = new Label
        {
            Location = new Point(520, 40),
            AutoSize = true,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Text = "Note:",
        };
        _summaryNote = new TextBox
        {
            Location = new Point(520, 60),
            Width = 220, Height = 60,
            Multiline = true,
            ScrollBars = ScrollBars.None,
            BackColor = Color.FromArgb(60, 62, 72),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _summaryNoteSave = new Button
        {
            // Repositioned slightly higher to follow the now-shorter textbox
            // (was 186 when textbox was 120 tall; now textbox bottom is at 60+60 = 120,
            // so the button sits at 126 with 6px gap).
            Location = new Point(520, 126),
            Width = 90, Height = 26,
            Text = "Save",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 100, 160),
        };
        _summaryNoteSave.Click += (_, _) => SaveSummaryNote();

        _summaryPanel.Controls.Add(title);
        _summaryPanel.Controls.Add(_summarySprite);
        _summaryPanel.Controls.Add(_summaryDexNumber);
        _summaryPanel.Controls.Add(_summaryTitle);
        _summaryPanel.Controls.Add(_summaryDetails);
        _summaryPanel.Controls.Add(_summaryNoteLabel);
        _summaryPanel.Controls.Add(_summaryNote);
        _summaryPanel.Controls.Add(_summaryNoteSave);

        // Apply initial layout (notes visibility + alignment) based on saved preferences.
        ApplySummaryLayout();

        ClearSummaryPanel();
    }

    /// <summary>
    /// Repositions/shows/hides the summary-panel controls based on the current
    /// <see cref="_showSummaryNotes"/> and <see cref="_summaryAlignment"/> settings.
    /// Called from the View menu toggles so changes take effect without restart.
    /// </summary>
    /// <remarks>
    /// Layout strategy:
    ///   • Sprite + dex# always sit at x=12 (left edge of summary panel).
    ///   • Title + details labels are anchored at x=120 (just right of the 96px sprite).
    ///   • When notes are visible, those labels stretch from 120 to ~510 (380px wide).
    ///   • When notes are hidden, those labels can stretch all the way to the right edge,
    ///     giving moves/IVs more room to breathe.
    ///   • Alignment (Left/Center/Right) only affects TextAlign of the title + details
    ///     labels — the sprite and notes positions don't move because that would look
    ///     weird (the sprite ending up centered between text and notes etc.).
    /// </remarks>
    private void ApplySummaryLayout()
    {
        if (_summaryDetails is null || _summaryNote is null) return;

        // 1. Notes visibility
        _summaryNoteLabel.Visible = _showSummaryNotes;
        _summaryNote.Visible      = _showSummaryNotes;
        _summaryNoteSave.Visible  = _showSummaryNotes;

        // 2. Notes column — anchored to the RIGHT edge of the summary panel.
        // Previously the notes were nailed to x=520, which left a gap on the right at wider
        // panel widths AND collided with the centered summary block at narrower widths.
        // Now we compute the notes column's left edge from the panel's actual right edge
        // every time, so the notes always sit flush against the right side regardless of
        // panel width.
        const int notesPadding = 12;
        const int notesWidth = 220;
        int notesLeft = Math.Max(notesPadding, _summaryPanel.Width - notesWidth - notesPadding);
        _summaryNoteLabel.Left = notesLeft;
        _summaryNote.Left      = notesLeft;
        _summaryNote.Width     = notesWidth;
        _summaryNoteSave.Left  = notesLeft;

        // 3. Layout positions for the info block (sprite + dex# + title + details).
        // The info block is one cohesive unit that moves as a whole — the sprite, dex#,
        // title, and details labels all shift horizontally together based on the user's
        // chosen alignment.
        const int leftPadding = 12;
        const int spriteColumnWidth = 96;       // width of sprite (matches SummarySpriteSize)
        const int spriteToTextGap = 12;          // breathing room between sprite and text labels
        const int textColumnWidth = 380;
        int blockWidth = spriteColumnWidth + spriteToTextGap + textColumnWidth;

        // The info block's right edge must stay clear of the notes column (when visible).
        // When notes are hidden, the info block can use the full panel width.
        int infoBlockRightEdge = _showSummaryNotes
            ? notesLeft - notesPadding
            : _summaryPanel.Width - leftPadding;
        int availStart = leftPadding;
        int availEnd = Math.Max(availStart + blockWidth, infoBlockRightEdge);
        int availWidth = availEnd - availStart;

        // Compute starting X of the info block based on alignment.
        // Falls back to left-aligned if availWidth is too narrow (small windows) so nothing clips.
        int blockX = availStart;
        if (availWidth > blockWidth)
        {
            blockX = _summaryAlignment switch
            {
                ContentAlignment.MiddleCenter => availStart + (availWidth - blockWidth) / 2,
                ContentAlignment.MiddleRight  => availEnd - blockWidth,
                _                             => availStart, // MiddleLeft / fallback
            };
        }

        // Reposition the info-block controls.  Y coordinates stay where they were originally
        // placed; we only move horizontally.
        _summarySprite.Left    = blockX;
        _summaryDexNumber.Left = blockX;
        int textX = blockX + spriteColumnWidth + spriteToTextGap;
        _summaryTitle.Left   = textX;
        _summaryDetails.Left = textX;

        // Title/details widths — clamp to whatever fits between textX and infoBlockRightEdge.
        int titleMaxWidth = Math.Max(200, Math.Min(textColumnWidth, infoBlockRightEdge - textX));
        _summaryTitle.Width   = titleMaxWidth;
        _summaryDetails.Width = titleMaxWidth;

        // 4. Text alignment within the labels — title is single-line so MiddleX works;
        // details is multiline so we use TopX to keep text starting from the top.
        _summaryTitle.TextAlign = _summaryAlignment;
        _summaryDetails.TextAlign = _summaryAlignment switch
        {
            ContentAlignment.MiddleLeft   => ContentAlignment.TopLeft,
            ContentAlignment.MiddleCenter => ContentAlignment.TopCenter,
            ContentAlignment.MiddleRight  => ContentAlignment.TopRight,
            _ => ContentAlignment.TopLeft,
        };
    }

    private void SetSummaryAlignment(ContentAlignment alignment)
    {
        _summaryAlignment = alignment;
        ApplySummaryLayout();
        // Update the radio-style check marks in View → Summary Alignment.
        // Walk the menu manually since UpdateMenuRadioChecks expects 3 levels of nesting
        // (top → sub → inner → leaves) and this menu is only 2 levels (top → sub → leaves).
        foreach (Control c in Controls)
        {
            if (c is not MenuStrip ms) continue;
            foreach (ToolStripItem topItem in ms.Items)
            {
                if (topItem is not ToolStripMenuItem top || top.Text != "View") continue;
                foreach (ToolStripItem sub in top.DropDownItems)
                {
                    if (sub is not ToolStripMenuItem subMenu || subMenu.Text != "Summary Alignment") continue;
                    foreach (ToolStripItem leaf in subMenu.DropDownItems)
                    {
                        if (leaf is not ToolStripMenuItem item) continue;
                        var leafAlign = (item.Text ?? "") switch
                        {
                            "Left"   => ContentAlignment.MiddleLeft,
                            "Center" => ContentAlignment.MiddleCenter,
                            "Right"  => ContentAlignment.MiddleRight,
                            _        => ContentAlignment.MiddleLeft,
                        };
                        item.Checked = leafAlign == _summaryAlignment;
                    }
                }
            }
        }
        SaveSettings();
    }

    private void ClearSummaryPanel()
    {
        _summaryCurrentPk = null;
        _summaryDenBox = -1;
        _summaryDenSlot = -1;
        _summarySourceIsDen = false;
        _summarySprite.Image = null;
        _summaryDexNumber.Text = "";
        _summaryTitle.Text = "(no Pokémon selected)";
        _summaryDetails.Text = "Click a Pokémon to view its details here.";
        _summaryNote.Text = "";
        _summaryNote.Enabled = false;
        _summaryNoteSave.Enabled = false;
    }

    /// <summary>Refreshes the summary panel with the given Pokémon's data.</summary>
    /// <param name="pk">Pokémon to display, or null to clear.</param>
    /// <param name="srcBox">Den box index (for notes) — pass -1 for save slots.</param>
    /// <param name="srcSlot">Den slot index.</param>
    /// <param name="fromDen">True if this Pokémon is from the Den (enables note editing).</param>
    private void RefreshSummary(PKM? pk, int srcBox, int srcSlot, bool fromDen)
    {
        if (pk is null or { Species: 0 }) { ClearSummaryPanel(); return; }

        _summaryCurrentPk = pk;
        _summaryDenBox = srcBox;
        _summaryDenSlot = srcSlot;
        _summarySourceIsDen = fromDen;

        // Sprite — use PKDenSpriteAt to request an exact 96×96 bitmap so the summary
        // PictureBox (Zoom mode) blits 1:1, no upscale interpolation.  PKDenSpriteAt()
        // also always returns a fresh bitmap (see ResizeIfNeeded ownership notes), so
        // we must dispose the previous one before assignment to avoid leaking on every
        // summary refresh.
        try
        {
            var oldImg = _summarySprite.Image;
            _summarySprite.Image = pk.PKDenSpriteAt(SummarySpriteSize, SummarySpriteSize);
            oldImg?.Dispose();
        }
        catch
        {
            var oldImg = _summarySprite.Image;
            _summarySprite.Image = null;
            oldImg?.Dispose();
        }

        // National Dex number — always use Species (which IS the National Dex number in PKHeX)
        try { _summaryDexNumber.Text = $"#{pk.Species:D3}"; } catch { _summaryDexNumber.Text = ""; }

        try
        {
            var strings = GameInfo.Strings;
            var speciesList = strings.specieslist;
            string speciesName = (uint)pk.Species < speciesList.Length ? speciesList[pk.Species] : $"#{pk.Species}";

            string nickname = "";
            try { nickname = pk.Nickname ?? ""; } catch { }
            bool hasCustomNick = !string.IsNullOrEmpty(nickname) && !string.Equals(nickname, speciesName, StringComparison.OrdinalIgnoreCase);
            string titleText = hasCustomNick ? $"{nickname} ({speciesName})" : speciesName;

            string shinyMark = "";
            try { if (pk.IsShiny) shinyMark = " ★"; } catch { }

            int level = 0;
            try { level = pk.CurrentLevel; } catch { }

            string genderMark = "";
            try { genderMark = pk.Gender switch { 0 => " ♂", 1 => " ♀", _ => "" }; } catch { }

            _summaryTitle.Text = $"{titleText}{shinyMark} — Lv. {level}{genderMark}";

            // Assemble details
            var sb = new System.Text.StringBuilder();

            // Nature / Ability
            try
            {
                int natureIdx = (int)pk.Nature;
                string nature = (uint)natureIdx < strings.natures.Length ? strings.natures[natureIdx] : "—";
                sb.Append($"Nature: {nature}");
            }
            catch { sb.Append("Nature: —"); }

            try
            {
                int abilIdx = pk.Ability;
                string ability = (uint)abilIdx < strings.abilitylist.Length ? strings.abilitylist[abilIdx] : "—";
                sb.Append($"   Ability: {ability}");
            }
            catch { }

            sb.AppendLine();

            // IVs
            try
            {
                int hp = pk.IV_HP, atk = pk.IV_ATK, def = pk.IV_DEF, spa = pk.IV_SPA, spd = pk.IV_SPD, spe = pk.IV_SPE;
                sb.AppendLine($"IVs: {hp}/{atk}/{def}/{spa}/{spd}/{spe}  (Total: {hp + atk + def + spa + spd + spe})");
            }
            catch { }

            // OT / Game
            try
            {
                string ot = pk.OriginalTrainerName;
                uint tid = pk.DisplayTID;
                string game = GameInfo.GetVersionName(pk.Version);
                if (string.IsNullOrEmpty(game)) game = $"V{pk.Version}";
                sb.AppendLine($"OT: {ot} (ID: {tid})   Game: {game}");
            }
            catch { }

            // Moves
            try
            {
                var moves = strings.movelist;
                var moveNames = new List<string>();
                int m1 = pk.Move1, m2 = pk.Move2, m3 = pk.Move3, m4 = pk.Move4;
                foreach (int m in new[] { m1, m2, m3, m4 })
                {
                    if (m > 0 && m < moves.Length) moveNames.Add(moves[m]);
                }
                if (moveNames.Count > 0)
                    sb.AppendLine($"Moves: {string.Join(", ", moveNames)}");
            }
            catch { }

            // Held item
            try
            {
                int item = pk.HeldItem;
                if (item > 0 && item < strings.itemlist.Length)
                    sb.AppendLine($"Held Item: {strings.itemlist[item]}");
            }
            catch { }

            // Format / Generation / Origin game
            try
            {
                string originGame = "";
                try
                {
                    string v = GameInfo.GetVersionName(pk.Version);
                    if (!string.IsNullOrEmpty(v)) originGame = $" {v}";
                }
                catch { }
                sb.Append($"Format: {pk.GetType().Name}  (Gen {pk.Generation}){originGame}");
            }
            catch { }

            _summaryDetails.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            _summaryDetails.Text = $"Could not load details: {ex.Message}";
        }

        // Note handling
        if (fromDen && srcBox >= 0 && srcSlot >= 0)
        {
            _summaryNote.Text = Den.GetNote(srcBox, srcSlot) ?? "";
            _summaryNote.Enabled = true;
            _summaryNoteSave.Enabled = true;

            // Add timestamp if available
            var ts = Den.GetTimestamp(srcBox, srcSlot);
            if (ts.HasValue)
                _summaryDetails.Text += $"\nAdded to Den: {ts.Value:yyyy-MM-dd HH:mm}";
        }
        else
        {
            _summaryNote.Text = "";
            _summaryNote.Enabled = false;
            _summaryNoteSave.Enabled = false;
        }
    }

    private void SaveSummaryNote()
    {
        if (!_summarySourceIsDen || _summaryDenBox < 0 || _summaryDenSlot < 0) return;
        string note = _summaryNote.Text.Trim();
        Den.SetNote(_summaryDenBox, _summaryDenSlot, string.IsNullOrEmpty(note) ? null : note);
        SetStatus(string.IsNullOrEmpty(note) ? "Note removed." : "Note saved.");
    }

    private void BuildPartyPanel()
    {
        // Outer wrapper with a transparent top spacer so the party header doesn't visually
        // collide with the save grid's bottom edge — previously they sat flush against
        // each other and the dotted slot borders bled into the party "Party" header bar.
        // The wrapper also bumps the overall party-panel footprint by 10px so the save
        // grid above gets that much more vertical room before reaching the boundary.
        _partyPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 138,                              // was 120 — +10 spacer + +8 internal padding
            BackColor = Color.FromArgb(40, 40, 48),    // matches the save panel for the spacer to read as gap, not as a header
            Visible = false, // hidden until a save is loaded
        };

        // Inner content panel — holds the actual title + party grid.  Sits below a 10px
        // transparent (background-colored) spacer.  Without this nested layout the
        // DockStyle.Top title would touch the savePanel's bottom edge with zero gap.
        var innerContent = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 48, 58),
            Padding = new Padding(0),
        };

        var topSpacer = new Panel
        {
            Dock = DockStyle.Top,
            Height = 10,
            BackColor = Color.FromArgb(40, 40, 48),
        };

        var title = new Label
        {
            Text = "Party",
            Dock = DockStyle.Top, Height = 24,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 60, 75),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        _partyGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(4),
            BackColor = Color.FromArgb(50, 48, 58),
        };

        innerContent.Controls.Add(_partyGrid);
        innerContent.Controls.Add(title);
        _partyPanel.Controls.Add(innerContent);
        _partyPanel.Controls.Add(topSpacer);

        // Center the party row horizontally within the panel — without this, slots align to the
        // left edge with only the default 4px padding, which looks misaligned vs. the save grid above.
        _partyGrid.SizeChanged += (_, _) => CenterPartyGrid();

        BuildPartySlotControls();
    }

    /// <summary>
    /// Computes left/right padding on the party grid to horizontally center the 6 slot row
    /// inside its container. Called after slot rebuild and any time the party panel resizes.
    /// </summary>
    private void CenterPartyGrid()
    {
        if (_partyGrid is null || _partySlots.Count == 0) return;
        try
        {
            int slotW = _partySlots[0].Width;
            int slotMargin = _partySlots[0].Margin.Horizontal;
            int rowWidth = (_partySlots.Count * slotW) + (_partySlots.Count * slotMargin);
            int sidePadding = Math.Max(4, (_partyGrid.ClientSize.Width - rowWidth) / 2);
            var target = new Padding(sidePadding, 4, sidePadding, 4);
            if (_partyGrid.Padding != target)
            {
                _partyGrid.SuspendLayout();
                _partyGrid.Padding = target;
                _partyGrid.ResumeLayout();
            }
        }
        catch { /* layout is best-effort */ }
    }

    private void BuildPartySlotControls()
    {
        _partyGrid.SuspendLayout();
        foreach (var pb in _partySlots)
        {
            bool owned = _ownedImageSlots.Remove(pb);
            var img = pb.Image;
            pb.Image = null;
            if (owned && img is not null) { try { img.Dispose(); } catch { } }
            _partyGrid.Controls.Remove(pb);
            pb.Dispose();
        }
        _partySlots.Clear();

        float scale = _partySpriteScale;
        int topArea = _showOrigin ? Math.Max(12, (int)Math.Round(14 * scale)) : 0;
        int bottomArea = (_showNames || _showGenders) ? Math.Max(14, (int)Math.Round(16 * scale)) : 0;
        int w = (int)Math.Round(SpriteW * scale) + 4;
        int h = (int)Math.Round(SpriteH * scale) + 4 + topArea + bottomArea;
        var sizeMode = Math.Abs(scale - 1.0f) < 0.001f ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;

        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            var pb = new PictureBox
            {
                Width = w, Height = h, SizeMode = sizeMode,
                BackColor = Color.FromArgb(60, 62, 72),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(2),
                Tag = idx,
            };
            pb.MouseClick += (_, e) => PartySlotClicked(pb, e, idx);

            // Drag source — party → Den (save is read-only, so party is copy-only)
            pb.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && GetPartyPKM(idx) is { Species: > 0 })
                {
                    _dragSource = pb; _mouseDownPos = e.Location;
                }
            };
            pb.MouseMove += (_, e) =>
            {
                if (_dragSource != pb || e.Button != MouseButtons.Left) return;
                if (Math.Abs(e.X - _mouseDownPos.X) < DragThreshold && Math.Abs(e.Y - _mouseDownPos.Y) < DragThreshold) return;
                var pk = GetPartyPKM(idx);
                if (pk is not { Species: > 0 }) { _dragSource = null; return; }
                pb.DoDragDrop($"PARTY:0:{idx}", DragDropEffects.Copy);
                _dragSource = null;
            };

            var tt = new ToolTip { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };
            pb.MouseEnter += (_, _) =>
            {
                var pk = GetPartyPKM(idx);
                if (pk is { Species: > 0 })
                {
                    try { tt.SetToolTip(pb, BuildHoverText(pk, -1, idx)); }
                    catch { tt.SetToolTip(pb, null); }
                }
                else tt.SetToolTip(pb, null);
            };

            _partySlots.Add(pb);
            _partyGrid.Controls.Add(pb);
        }

        // Adjust panel height to fit the slots.  +46 accounts for: 10 (top spacer) +
        // 24 (title bar) + 12 (internal padding around the grid).
        _partyPanel.Height = h + 46;
        _partyGrid.ResumeLayout();
        // Re-center after slot widths are known
        CenterPartyGrid();
    }

    private void RebuildPartyGrid()
    {
        if (_partySlots.Count == 0) return;
        BuildPartySlotControls();
        RefreshPartyGrid();
    }

    private void RefreshPartyGrid()
    {
        if (_partySlots.Count == 0) return;
        if (SAV is null) { _partyPanel.Visible = false; return; }

        _partyPanel.Visible = true;
        for (int i = 0; i < _partySlots.Count; i++)
        {
            // Set BackColor BEFORE UpdateSlotImage — see RefreshDenGrid for the explanation.
            _partySlots[i].BackColor = Color.FromArgb(60, 62, 72);
            var pk = GetPartyPKM(i);
            UpdateSlotImage(_partySlots[i], pk, SlotKind.Party);
        }
    }

    private PKM? GetPartyPKM(int slot)
    {
        if (SAV is null) return null;
        try
        {
            if (slot >= SAV.PartyCount) return null;
            var pk = SAV.GetPartySlotAtIndex(slot);
            return pk is { Species: > 0 } ? pk : null;
        }
        catch { return null; }
    }

    private void PartySlotClicked(PictureBox pb, MouseEventArgs e, int slot)
    {
        var pk = GetPartyPKM(slot);
        if (pk is null) return;

        if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("View Summary", null, (_, _) => RefreshSummary(pk, -1, -1, fromDen: false)));
            menu.Items.Add(new ToolStripMenuItem("Move to Den", null, (_, _) =>
            {
                var (db, ds) = Den.FindNextEmpty(CurrentDenBox < 0 ? 0 : CurrentDenBox);
                if (db < 0) { Den.EnsureBoxCount(Den.BoxCount + 5); PopulateDenBoxNames(); (db, ds) = Den.FindNextEmpty(0); }
                if (db >= 0)
                {
                    PushUndo();
                    Den.SetSlot(db, ds, pk.Clone());
                    Den.SetTimestamp(db, ds, DateTime.Now);
                    RefreshDenGrid();
                    SetStatus($"Moved {GetSpeciesName(pk)} from party to Den.");
                }
            }));
            menu.Show(pb, e.Location);
            return;
        }

        // Left click: update summary
        RefreshSummary(pk, -1, -1, fromDen: false);
    }

    private void BuildRecentlyDeletedPanel()
    {
        _recentPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 48) };

        var title = new Label
        {
            Text = "Recently Deleted",
            Dock = DockStyle.Top, Height = 28,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White, BackColor = Color.FromArgb(60, 45, 45),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        _recentPrev = new Button { Text = "◀", Width = 32, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _recentNext = new Button { Text = "▶", Width = 32, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _recentClear = new Button { Text = "Clear", Width = 60, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(120, 55, 55) };
        _recentPrev.Click += (_, _) => ScrollRecentlyDeleted(-RecentlyDeletedVisibleCount);
        _recentNext.Click += (_, _) => ScrollRecentlyDeleted(+RecentlyDeletedVisibleCount);
        _recentClear.Click += (_, _) => ClearRecentlyDeleted();

        // Center the nav buttons (◀ ▶ Clear) inside a fixed-height host panel.
        // We tried a TableLayoutPanel with an AutoSize Anchor=None FlowLayoutPanel inside,
        // but the AutoSize child confused Dock layout so the grid below ended up overlapping
        // this nav row. Simpler: a plain Dock=Top Panel with explicit height, and the inner
        // FlowLayoutPanel re-positioned to horizontal center on every Resize.
        var navHost = new Panel
        {
            Dock = DockStyle.Top, Height = 30,
            BackColor = Color.FromArgb(50, 55, 70),
        };
        var navPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 2, 0, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        navPanel.Controls.AddRange([_recentPrev, _recentNext, _recentClear]);
        navHost.Controls.Add(navPanel);
        // Center horizontally whenever the host or the inner row resizes.
        // Vertical center: middle of host minus half the inner panel's height.
        void CenterNav()
        {
            if (navHost.Width <= 0) return;
            navPanel.Location = new Point(
                Math.Max(0, (navHost.ClientSize.Width - navPanel.Width) / 2),
                Math.Max(0, (navHost.ClientSize.Height - navPanel.Height) / 2));
        }
        navHost.Resize += (_, _) => CenterNav();
        navPanel.Resize += (_, _) => CenterNav();

        _recentCountLabel = new Label { AutoSize = false, Height = 22, Dock = DockStyle.Bottom, ForeColor = Color.LightGray, TextAlign = ContentAlignment.MiddleCenter };

        _recentGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = false, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4),
            BackColor = Color.FromArgb(40, 40, 48),
        };

        BuildRecentSlotControls();

        // Center the row of recently-deleted slots horizontally as the panel resizes.
        // Without this, the slots left-align inside the FlowLayoutPanel which makes the
        // section visually mis-aligned with the centered Party row above.
        _recentGrid.Resize += (_, _) => CenterRecentlyDeletedRow();

        _recentPanel.Controls.Add(_recentGrid);
        _recentPanel.Controls.Add(_recentCountLabel);
        _recentPanel.Controls.Add(navHost);
        _recentPanel.Controls.Add(title);

        RefreshRecentlyDeletedGrid();
    }

    private void BuildRecentSlotControls()
    {
        _recentGrid.SuspendLayout();
        foreach (var pb in _recentSlots)
        {
            bool owned = _ownedImageSlots.Remove(pb);
            var img = pb.Image;
            pb.Image = null;
            if (owned && img is not null) { try { img.Dispose(); } catch { } }
            _recentGrid.Controls.Remove(pb);
            pb.Dispose();
        }
        _recentSlots.Clear();

        // Recently Deleted always renders at 1× scale with NO labels — kept compact and uniform
        // regardless of the user's main-grid sprite size or label preferences.
        int sw = SpriteW + 4;
        int sh = SpriteH + 4;

        for (int i = 0; i < RecentlyDeletedVisibleCount; i++)
        {
            int localIdx = i;
            var pb = new PictureBox
            {
                Width = sw, Height = sh, SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Color.FromArgb(60, 62, 72), BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(1), Tag = localIdx,
            };
            pb.MouseClick += (s, e) => RecentSlotClicked(s as PictureBox, e, localIdx);

            // Drag source: dragging a recently-deleted Pokémon onto a Den slot moves it back.
            // Uses a "RECENT:0:<index>" payload that the Den drop handler recognizes.
            pb.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int idx = _recentScrollOffset + localIdx;
                if (idx < 0 || idx >= _recentlyDeleted.Count) return;
                if (s is PictureBox sourcePb)
                    sourcePb.DoDragDrop($"RECENT:0:{idx}", DragDropEffects.Move | DragDropEffects.Copy);
            };

            var tt = new ToolTip { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };
            pb.MouseEnter += (s, _) =>
            {
                int idx = _recentScrollOffset + localIdx;
                if (idx >= 0 && idx < _recentlyDeleted.Count)
                {
                    var entry = _recentlyDeleted[idx];
                    tt.SetToolTip(pb, BuildRecentHoverText(entry.Pk, entry.Note, entry.DeletedAt));
                }
                else tt.SetToolTip(pb, null);
            };
            _recentSlots.Add(pb);
            _recentGrid.Controls.Add(pb);
        }
        _recentGrid.ResumeLayout();
    }

    /// <summary>Adds a Pokémon to the recently-deleted buffer. Call this whenever a PKM is removed from Den storage.</summary>
    private void AddToRecentlyDeleted(PKM pk, string? note)
    {
        if (pk is null || pk.Species == 0) return;
        _recentlyDeleted.Insert(0, (pk.Clone(), note, DateTime.Now));
        while (_recentlyDeleted.Count > RecentlyDeletedCapacity)
            _recentlyDeleted.RemoveAt(_recentlyDeleted.Count - 1);
        _recentScrollOffset = 0; // jump back to most recent
        RefreshRecentlyDeletedGrid();
        // Persist immediately so a hard-crash or "Exit Without Saving" doesn't lose accidental deletes
        SaveRecentlyDeleted();
    }

    private void RefreshRecentlyDeletedGrid()
    {
        if (_recentSlots.Count == 0) return;

        // Clamp scroll offset
        if (_recentScrollOffset < 0) _recentScrollOffset = 0;
        int maxOffset = Math.Max(0, _recentlyDeleted.Count - RecentlyDeletedVisibleCount);
        if (_recentScrollOffset > maxOffset) _recentScrollOffset = maxOffset;

        for (int i = 0; i < _recentSlots.Count; i++)
        {
            int idx = _recentScrollOffset + i;
            var pb = _recentSlots[i];
            if (idx < _recentlyDeleted.Count)
            {
                // Recently Deleted always uses raw 1× sprite — no labels, no zoom — independent of main grid settings.
                UpdateRecentSlotImage(pb, _recentlyDeleted[idx].Pk);
            }
            else
            {
                UpdateRecentSlotImage(pb, null);
            }
            pb.BackColor = Color.FromArgb(60, 62, 72);
        }

        int total = _recentlyDeleted.Count;
        if (total == 0)
            _recentCountLabel.Text = "(empty)";
        else
            _recentCountLabel.Text = $"Showing {_recentScrollOffset + 1}–{Math.Min(_recentScrollOffset + RecentlyDeletedVisibleCount, total)} of {total}";

        _recentPrev.Enabled = _recentScrollOffset > 0;
        _recentNext.Enabled = _recentScrollOffset + RecentlyDeletedVisibleCount < total;
        _recentClear.Enabled = total > 0;
    }

    private void ScrollRecentlyDeleted(int delta)
    {
        _recentScrollOffset += delta;
        RefreshRecentlyDeletedGrid();
    }

    /// <summary>Rebuilds recent slot PictureBoxes — used when view toggles change label sizing.</summary>
    private void RebuildRecentlyDeletedGrid()
    {
        BuildRecentSlotControls();
        RefreshRecentlyDeletedGrid();
    }

    /// <summary>Public helper for Arrange window to add a Pokémon to recently-deleted buffer (e.g. on paste-overwrite).</summary>
    public void CaptureDeletedForRecent(PKM pk, string? note) => AddToRecentlyDeleted(pk, note);

    /// <summary>Captures the Pokémon at (box, slot) into recently-deleted and clears the slot.</summary>
    public void ClearSlotWithCapture(int box, int slot)
    {
        var pk = Den.GetSlot(box, slot);
        if (pk is { Species: > 0 })
        {
            var note = Den.GetNote(box, slot);
            AddToRecentlyDeleted(pk, note);
        }
        Den.ClearSlot(box, slot);
    }

    /// <summary>Builds a simple hover tooltip for a recently-deleted Pokémon (doesn't belong to a box).</summary>
    private static string BuildRecentHoverText(PKM pk, string? note, DateTime deletedAt)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var species = GameInfo.Strings.specieslist;
            string name = (uint)pk.Species < species.Length ? species[pk.Species] : $"#{pk.Species}";
            sb.Append(name);

            try { if (pk.IsShiny) sb.Append(" ★"); } catch { }
            try { sb.Append($"  Lv.{pk.CurrentLevel}"); } catch { }

            try
            {
                string g = pk.Gender switch { 0 => "♂", 1 => "♀", _ => "" };
                if (g.Length > 0) sb.Append($"  {g}");
            }
            catch { }

            try
            {
                string game = GameInfo.GetVersionName(pk.Version);
                if (!string.IsNullOrEmpty(game)) sb.Append($"\nGame: {game}");
            }
            catch { }

            try
            {
                string ot = pk.OriginalTrainerName;
                if (!string.IsNullOrEmpty(ot)) sb.Append($"\nOT: {ot}");
            }
            catch { }

            if (!string.IsNullOrEmpty(note)) sb.Append($"\n\nNote: {note}");
            sb.Append($"\n\nDeleted: {deletedAt:yyyy-MM-dd HH:mm}");
            return sb.ToString();
        }
        catch
        {
            return $"Deleted at {deletedAt:yyyy-MM-dd HH:mm}";
        }
    }

    private void ClearRecentlyDeleted()
    {
        if (_recentlyDeleted.Count == 0) return;
        if (MessageBox.Show(this, $"Clear all {_recentlyDeleted.Count} recently-deleted Pokémon?\n\nThis cannot be undone.", "Clear Recently Deleted", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _recentlyDeleted.Clear();
        _recentScrollOffset = 0;
        RefreshRecentlyDeletedGrid();
        SaveRecentlyDeleted();
        SetStatus("Recently deleted cleared.");
    }

    private void RecentSlotClicked(PictureBox? pb, MouseEventArgs e, int localIdx)
    {
        if (pb is null) return;
        int idx = _recentScrollOffset + localIdx;
        if (idx < 0 || idx >= _recentlyDeleted.Count) return;
        var entry = _recentlyDeleted[idx];

        if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("View Summary", null, (_, _) => ViewSummary(entry.Pk, entry.Note, entry.DeletedAt)));
            menu.Items.Add(new ToolStripMenuItem("Send to Box…", null, (_, _) => RestoreFromRecentlyDeleted(idx)));
            menu.Show(pb, e.Location);
            return;
        }

        // Left click: show this Pokémon's summary in the existing summary panel.
        // We use box=-1, slot=-1, fromDen=false so the panel renders as "view-only" (no
        // notes textbox or save buttons) — recently-deleted entries aren't tied to a Den slot.
        if (e.Button == MouseButtons.Left)
        {
            RefreshSummary(entry.Pk, -1, -1, fromDen: false);
        }
    }

    /// <summary>Sends a recently-deleted Pokémon back to a chosen Den box, removing it from the recently-deleted buffer.</summary>
    private void RestoreFromRecentlyDeleted(int idx)
    {
        if (idx < 0 || idx >= _recentlyDeleted.Count) return;
        var entry = _recentlyDeleted[idx];

        using var dlg = new SendToBoxDialog(Den, referenceBox: -1, pkCount: 1);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int destBox = dlg.SelectedBox;
        if (destBox < 0) return;

        int destSlot = FindNextEmptySlotInBox(destBox);
        if (destSlot < 0)
        {
            MessageBox.Show(this, $"Destination box \"{Den.GetBoxName(destBox)}\" is full.", "Send to Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PushUndo();
        Den.SetSlot(destBox, destSlot, entry.Pk);
        if (entry.Note is not null) Den.SetNote(destBox, destSlot, entry.Note);
        Den.SetTimestamp(destBox, destSlot, DateTime.Now);

        _recentlyDeleted.RemoveAt(idx);
        RefreshRecentlyDeletedGrid();
        RefreshDenGrid();
        NotifyArrangeWindows();
        SaveRecentlyDeleted();
        SetStatus($"Restored {GetSpeciesName(entry.Pk)} to \"{Den.GetBoxName(destBox)}\".");
    }

    private static FlowLayoutPanel CreateGrid() => new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4),
        BackColor = Color.FromArgb(40, 40, 48),
    };

    private static Label CreateTitleLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Top,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        ForeColor = Color.White, BackColor = Color.FromArgb(50, 55, 70),
        TextAlign = ContentAlignment.MiddleCenter, Height = 30,
    };

    private static Panel CreateNavPanel(Button prev, ComboBox selector, Button next, params Button[] extras)
    {
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        nav.Controls.Add(prev);
        nav.Controls.Add(selector);
        nav.Controls.Add(next);
        foreach (var btn in extras) nav.Controls.Add(btn);
        return nav;
    }

    private void InitializeSlotPictureBoxes(FlowLayoutPanel grid, List<PictureBox> slots, bool isDen, int slotCount)
    {
        grid.SuspendLayout();
        float scale = isDen ? _spriteScale : _saveSpriteScale;
        // Label area scales with sprite size — account for both fractional and integer zooms
        int topArea = _showOrigin ? Math.Max(12, (int)Math.Round(14 * scale)) : 0;
        int bottomArea = (_showNames || _showGenders) ? Math.Max(14, (int)Math.Round(16 * scale)) : 0;
        int w = (int)Math.Round(SpriteW * scale) + 4;
        int h = (int)Math.Round(SpriteH * scale) + 4 + topArea + bottomArea;
        // For 1x exactly we use CenterImage to avoid bilinear blur on pixel-perfect art.
        // Anything else uses Zoom (with NearestNeighbor in the rendering pass) for clean scaling.
        var sizeMode = Math.Abs(scale - 1.0f) < 0.001f ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
        for (int i = 0; i < slotCount; i++)
        {
            int slotIndex = i;
            var pb = new PictureBox
            {
                Width = w, Height = h, SizeMode = sizeMode,
                BackColor = GetSlotBaseColor(), BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(1), AllowDrop = true, Tag = i,
            };
            pb.MouseClick += (s, e) => SlotClicked(s as PictureBox, e, isDen, slotIndex);
            pb.MouseDown += (s, e) => SlotMouseDown(s as PictureBox, e, isDen, slotIndex);
            pb.MouseMove += (s, e) => SlotMouseMove(s as PictureBox, e, isDen, slotIndex);
            // Selection outline rendered by the Paint handler — fires after PictureBox draws its image.
            pb.Paint += SlotSelection_Paint;
            pb.DragEnter += (_, e) =>
            {
                bool hasText = e.Data?.GetDataPresent(DataFormats.Text) == true
                            || e.Data?.GetDataPresent(DataFormats.UnicodeText) == true
                            || e.Data?.GetDataPresent(DataFormats.StringFormat) == true;
                if (hasText) e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
                else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
                else e.Effect = DragDropEffects.None;
            };
            pb.DragOver += (_, e) =>
            {
                bool hasText = e.Data?.GetDataPresent(DataFormats.Text) == true
                            || e.Data?.GetDataPresent(DataFormats.UnicodeText) == true
                            || e.Data?.GetDataPresent(DataFormats.StringFormat) == true;
                if (hasText) e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
                else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
                else e.Effect = DragDropEffects.None;
            };
            pb.DragDrop += (s, e) => SlotDragDrop(s as PictureBox, e, isDen, slotIndex);
            pb.GiveFeedback += (_, e) => e.UseDefaultCursors = true;

            var tt = new ToolTip { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };
            pb.MouseHover += (_, _) =>
            {
                var pk = GetPKMForSlot(isDen, slotIndex);
                if (pk is { Species: > 0 })
                {
                    tt.SetToolTip(pb, BuildHoverText(pk, isDen ? CurrentDenBox : -1, slotIndex));
                }
                else tt.SetToolTip(pb, null);
            };
            pb.MouseLeave += (_, _) => tt.SetToolTip(pb, null);
            slots.Add(pb);
            grid.Controls.Add(pb);
        }
        grid.ResumeLayout();
    }

    public string BuildHoverText(PKM pk, int denBox, int slot)
    {
        try
        {
            var strings = GameInfo.Strings;
            var species = strings.specieslist;
            var natures = strings.natures;
            var abilities = strings.abilitylist;
            var moves = strings.movelist;
            var items = strings.itemlist;

            string name = (uint)pk.Species < species.Length ? species[pk.Species] : $"#{pk.Species}";

            var sb = new System.Text.StringBuilder();
            sb.Append(name);
            try { if (pk.IsShiny) sb.Append(" ★"); } catch { }
            try { if (pk.IsEgg) sb.Append(" (Egg)"); } catch { }
            sb.AppendLine();

            // Level + Nature + Ability (Gen 1/2 may not have these)
            var line2 = $"Lv.{pk.CurrentLevel}";
            try
            {
                int natIdx = (int)pk.Nature;
                if (natIdx >= 0 && natIdx < natures.Length)
                    line2 += $"  |  {natures[natIdx]}";
            }
            catch { }
            try
            {
                int abiIdx = pk.Ability;
                if (abiIdx > 0 && abiIdx < abilities.Length)
                    line2 += $"  |  {abilities[abiIdx]}";
            }
            catch { }
            sb.AppendLine(line2);

            // OT + Game
            var line3 = "";
            try { line3 = $"OT: {pk.OriginalTrainerName}"; } catch { line3 = "OT: ???"; }
            try
            {
                string game = GameInfo.GetVersionName(pk.Version);
                if (!string.IsNullOrEmpty(game)) line3 += $"  |  {game}";
            }
            catch { }
            sb.AppendLine(line3);

            // IVs
            try
            {
                int ivTotal = pk.IV_HP + pk.IV_ATK + pk.IV_DEF + pk.IV_SPA + pk.IV_SPD + pk.IV_SPE;
                sb.AppendLine($"IVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE} ({ivTotal})");
            }
            catch { }

            // Moves
            try
            {
                ushort[] moveIds = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
                var moveNames = new List<string>();
                foreach (var m in moveIds)
                {
                    if (m > 0 && (uint)m < moves.Length)
                        moveNames.Add(moves[m]);
                }
                if (moveNames.Count > 0)
                    sb.AppendLine(string.Join(" / ", moveNames));
            }
            catch { }

            // Held item (Gen 1 doesn't have items)
            try
            {
                if (pk.HeldItem > 0 && (uint)pk.HeldItem < items.Length)
                    sb.AppendLine($"Item: {items[pk.HeldItem]}");
            }
            catch { }

            // Pokerus
            try { if (pk.IsPokerusInfected) sb.AppendLine(pk.IsPokerusCured ? "Pokérus (Cured)" : "Pokérus (Active)"); } catch { }

            // Den metadata
            if (denBox >= 0)
            {
                var note = Den.GetNote(denBox, slot);
                if (!string.IsNullOrEmpty(note)) sb.AppendLine($"📝 {note}");
                var ts = Den.GetTimestamp(denBox, slot);
                if (ts.HasValue) sb.AppendLine($"📅 {ts.Value:yyyy-MM-dd HH:mm}");
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return $"#{pk.Species} (Lv.{pk.CurrentLevel})";
        }
    }

    // ========================================================================
    //  SAVE FILE LOADING
    // ========================================================================

    private void OpenSaveFile()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Open Pokémon Save File",
            // Common save extensions across emulators/flashcards. PKHeX's loader sniffs the bytes,
            // so the extension is only used for filtering the file picker — listing the popular ones
            // (RetroArch's .srm, BizHawk's .SaveRAM, etc.) avoids forcing users to switch to "All Files".
            Filter = "Save Files|*.sav;*.dsv;*.dat;*.gci;*.bin;*.sa1;*.sa2;*.main;*.bak;*.fla;*.raw;*.srm;*.SaveRAM|All Files|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        LoadSaveFile(ofd.FileName);
    }

    /// <summary>
    /// Prompts the user for a saves directory, persists the choice, scans + caches
    /// the contained save files, and shows the selector card list.  If a save is
    /// currently loaded, the selector stays hidden until the save is closed.
    /// </summary>
    private void PickSavesDirectory()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose a folder containing Pokémon save files",
            ShowNewFolderButton = false,
            // Re-seed with the previously chosen directory if any, so the user can quickly
            // bounce around inside their saves tree without restarting at My Documents.
            InitialDirectory = !string.IsNullOrEmpty(_savesDirectory) && Directory.Exists(_savesDirectory)
                ? _savesDirectory : "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _savesDirectory = dlg.SelectedPath;
        // Drop any cached metadata from a different directory — paths might overlap so we
        // can't risk stale results from a previous folder.
        SaveInfoCache.Clear();
        _saveSelector.SetDirectory(_savesDirectory);
        UpdateSaveSelectorVisibility();
        SaveSettings();
        SetStatus($"Saves directory set: {_savesDirectory}");
    }

    /// <summary>
    /// Toggles the save selector vs the empty-state panel based on whether a save is
    /// currently loaded and whether a saves directory is configured.
    ///   • Save loaded                 → hide both (the save grid is showing instead)
    ///   • Save not loaded, no dir     → show saveEmptyPanel ("Open Save File…" button)
    ///   • Save not loaded, dir set    → show selector panel (card list)
    /// Always called after LoadSaveFile / CloseSaveFile / PickSavesDirectory.
    /// </summary>
    private void UpdateSaveSelectorVisibility()
    {
        bool savLoaded = SAV is not null;
        bool hasDir = !string.IsNullOrEmpty(_savesDirectory);
        if (savLoaded)
        {
            _saveSelector.Visible = false;
            saveEmptyPanel.Visible = false;
        }
        else if (hasDir)
        {
            _saveSelector.Visible = true;
            saveEmptyPanel.Visible = false;
        }
        else
        {
            _saveSelector.Visible = false;
            saveEmptyPanel.Visible = true;
        }
    }

    private void LoadSaveFile(string path)
    {
        SaveFile? sav;
        try
        {
            sav = SaveUtil.GetSaveFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load save file:\n\n{ex.Message}\n\nThe file may be corrupt or in an unsupported format.",
                "Load Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (sav is null)
        {
            MessageBox.Show(this, "Could not load this file as a Pokémon save.\nMake sure it's a valid save file.", "Load Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SAV = sav;
        _currentSavePath = path;

        // Initialize sprite builder for this save — but always force SB8a (Artwork) mode
        // so Gen 9 Pokémon in the Den continue to render even when viewing older saves.
        SpriteUtil.Initialize(sav);
        SpriteUtil.ChangeMode(SpriteBuilderMode.SpritesArtwork5668);

        // Update the title bar to include trainer metadata inline:
        //   "Save File — Name | TID: 12345 | Game | Gen N"
        // The em-dash separates the label from the data so the line reads cleanly.
        string ot = sav.OT;
        string tid = sav.DisplayTID.ToString();
        string game = GameInfo.GetVersionName(sav.Version);
        if (string.IsNullOrEmpty(game)) game = sav.Version.ToString();
        int gen = sav.Generation;
        saveTitle.Text = $"Save File — {ot}  |  TID: {tid}  |  {game}  |  Gen {gen}";
        // The inline label next to the box selector is no longer used for save info.
        // Clearing it lets future tweaks re-purpose it (e.g., box-specific stats) without surprises.
        _saveInfoLabel.Text = "";

        // Initialize save slot PictureBoxes (clear old ones)
        saveGrid.SuspendLayout();
        foreach (var pb in saveSlots) { pb.Dispose(); }
        saveSlots.Clear();
        saveGrid.Controls.Clear();
        saveGrid.ResumeLayout();

        if (sav.HasBox)
        {
            InitializeSlotPictureBoxes(saveGrid, saveSlots, false, sav.BoxSlotCount);
            PopulateSaveBoxNames();

            UpdateSaveSelectorVisibility();
            saveGrid.Visible = true;
            // Show transfer buttons and nav
            foreach (Control c in savePanel.Controls)
            {
                if (c is FlowLayoutPanel fp && fp != saveGrid)
                    c.Visible = true;
            }

            int box = Math.Clamp(sav.CurrentBox, 0, sav.BoxCount - 1);
            if (saveBoxSelector.Items.Count > 0)
                saveBoxSelector.SelectedIndex = box;
        }

        // Re-render den grid with correct sprites for this save
        RefreshDenGrid();
        SetStatus($"Loaded save: {ot} — {game} ({Path.GetFileName(path)})");
    }

    private void CloseSaveFile()
    {
        SAV = null;
        _currentSavePath = null;
        saveTitle.Text = "No Save Loaded";
        _saveInfoLabel.Text = "";
        // Re-show the appropriate empty-state UI: card list if a saves directory is
        // configured, otherwise the original "Open Save File…" button.
        UpdateSaveSelectorVisibility();
        saveGrid.Visible = false;
        foreach (Control c in savePanel.Controls)
        {
            if (c is FlowLayoutPanel fp && fp != saveGrid && fp.BackColor == Color.FromArgb(50, 55, 70))
                c.Visible = false;
        }
        saveBoxSelector.Items.Clear();
        saveSlots.Clear();
        saveGrid.Controls.Clear();
        saveCountLabel.Text = "";
        SetStatus("Save file closed.");
    }

    // ========================================================================
    //  BOX NAVIGATION
    // ========================================================================

    private bool _suppressBoxSelectorEvents;

    /// <summary>Refreshes ALL labels in the box selector dropdown (with current counts). Call after any mutation that changes box contents.</summary>
    private void RefreshAllBoxLabels()
    {
        _suppressBoxSelectorEvents = true;
        try
        {
            denBoxSelector.BeginUpdate();
            // If item count is out of sync with Den.BoxCount, rebuild entirely
            if (denBoxSelector.Items.Count != Den.BoxCount)
            {
                int prevSel = denBoxSelector.SelectedIndex;
                denBoxSelector.Items.Clear();
                for (int i = 0; i < Den.BoxCount; i++)
                    denBoxSelector.Items.Add(FormatBoxLabel(i));
                if (prevSel >= 0 && prevSel < denBoxSelector.Items.Count)
                    denBoxSelector.SelectedIndex = prevSel;
                else if (denBoxSelector.Items.Count > 0)
                    denBoxSelector.SelectedIndex = 0;
            }
            else
            {
                // Just update each label in place
                for (int i = 0; i < Den.BoxCount; i++)
                    denBoxSelector.Items[i] = FormatBoxLabel(i);
            }
            denBoxSelector.EndUpdate();
            UpdateBoxSelectorWidth();
        }
        finally { _suppressBoxSelectorEvents = false; }
    }

    /// <summary>Measures all box labels and expands the box selector dropdown to fit the longest.</summary>
    private void UpdateBoxSelectorWidth()
    {
        try
        {
            int maxWidth = 0;
            // Use TextRenderer — it works before the control is visible/realized,
            // unlike Graphics.MeasureString which needs a live Graphics context.
            for (int i = 0; i < denBoxSelector.Items.Count; i++)
            {
                string? label = denBoxSelector.Items[i]?.ToString();
                if (string.IsNullOrEmpty(label)) continue;
                int w = TextRenderer.MeasureText(label, denBoxSelector.Font).Width;
                if (w > maxWidth) maxWidth = w;
            }
            // Padding for dropdown arrow (~20 px) + margin; clamp to keep UI reasonable
            int desiredWidth = Math.Clamp(maxWidth + 40, 140, 600);
            if (denBoxSelector.Width != desiredWidth)
            {
                denBoxSelector.Size = new Size(desiredWidth, denBoxSelector.Height);
            }
            denBoxSelector.DropDownWidth = desiredWidth;
            // Force the containing FlowLayoutPanel to re-layout around the new size
            denBoxSelector.Parent?.PerformLayout();
        }
        catch { /* silent fallback if something goes sideways */ }
    }

    /// <summary>
    /// Applies a centered grid layout (6 columns × 5 rows) to a FlowLayoutPanel by computing
    /// left/right padding equal to (availableWidth - rowWidth) / 2. When the flag is off, padding resets.
    /// Called after slot resize, mode toggle, window resize, and grid rebuild.
    /// </summary>
    /// <param name="grid">The FlowLayoutPanel to lay out (denGrid or saveGrid).</param>
    /// <param name="slots">The slot picture boxes (used to read actual width/margin).</param>
    /// <param name="useGridLayout">Whether to apply 6×5 centering or fall back to default flow.</param>
    /// <param name="fallbackScale">Sprite scale used to estimate slot width when slots are empty.</param>
    private void ApplyCenteredGridLayout(FlowLayoutPanel grid, List<PictureBox> slots, bool useGridLayout, float fallbackScale)
    {
        try
        {
            if (!useGridLayout)
            {
                if (grid.Padding != new Padding(4))
                {
                    grid.SuspendLayout();
                    grid.Padding = new Padding(4);
                    grid.ResumeLayout();
                }
                return;
            }

            // 6×5 mode: enough horizontal room for 6 slots side-by-side, padded equally on both sides.
            // Read the actual first slot to honor margins/borders. Falls back to a sane default if grid is empty.
            int slotW;
            int slotMargin;
            if (slots.Count > 0)
            {
                slotW = slots[0].Width;
                slotMargin = slots[0].Margin.Horizontal; // Left + Right margins combined
            }
            else
            {
                // Pre-grid-creation fallback — match the formula in InitializeSlotPictureBoxes
                slotW = (int)Math.Round(SpriteW * fallbackScale) + 4;
                slotMargin = 6; // PictureBox default Margin = 3 each side
            }

            int rowWidth = (6 * slotW) + (6 * slotMargin);
            // Reserve space for vertical scrollbar so we don't go off-center when scroll appears
            int scrollbarReserve = SystemInformation.VerticalScrollBarWidth + 2;
            int availableWidth = grid.ClientSize.Width - scrollbarReserve;
            int sidePadding = Math.Max(4, (availableWidth - rowWidth) / 2);

            var target = new Padding(sidePadding, 4, sidePadding, 4);
            if (grid.Padding != target)
            {
                grid.SuspendLayout();
                grid.Padding = target;
                grid.ResumeLayout();
            }
        }
        catch { /* layout is best-effort — never throw out of a SizeChanged handler */ }
    }

    private void ApplyDenGridLayout() => ApplyCenteredGridLayout(denGrid, denSlots, _use6x5DenGrid, _spriteScale);
    private void ApplySaveGridLayout() => ApplyCenteredGridLayout(saveGrid, saveSlots, _use6x5SaveGrid, _saveSpriteScale);

    /// <summary>
    /// Centers the row of recently-deleted slot picture boxes inside their FlowLayoutPanel.
    /// The Party row above and the Save grid both use centered layouts, so without this the
    /// Recently Deleted section looks visually offset to the left.
    ///
    /// Implementation: compute the natural row width (slot width × count + horizontal margins)
    /// and apply equal left/right padding so the slots sit in the middle of the available space.
    /// </summary>
    private void CenterRecentlyDeletedRow()
    {
        try
        {
            if (_recentSlots.Count == 0) return;
            int slotW = _recentSlots[0].Width;
            int slotMargin = _recentSlots[0].Margin.Horizontal;
            int rowWidth = (RecentlyDeletedVisibleCount * slotW) + (RecentlyDeletedVisibleCount * slotMargin);
            int availableWidth = _recentGrid.ClientSize.Width;
            int sidePadding = Math.Max(4, (availableWidth - rowWidth) / 2);
            var target = new Padding(sidePadding, 4, sidePadding, 4);
            if (_recentGrid.Padding != target)
            {
                _recentGrid.SuspendLayout();
                _recentGrid.Padding = target;
                _recentGrid.ResumeLayout();
            }
        }
        catch { /* layout best-effort — never throw out of a Resize handler */ }
    }

    /// <summary>Hides or shows the bottom box-total counters in both Den and Save panels per the user's View setting.</summary>
    private void ApplyBoxTotalsVisibility()
    {
        denCountLabel.Visible = _showBoxTotals;
        saveCountLabel.Visible = _showBoxTotals;
    }

    /// <summary>Sets the selection-outline color and repaints any currently-selected slots.</summary>
    private void SetSelectionOutlineColor(Color color)
    {
        _selectionOutlineColor = color;
        // Update the radio-style check marks in the View → Selection Style → Outline Color submenu
        UpdateMenuRadioChecks("View", "Selection Style", "Outline Color", item =>
        {
            // Compare against the four canonical colors to decide which item is "active"
            var label = item.Text ?? "";
            return (label.StartsWith("Blue") && color.ToArgb() == Color.FromArgb(80, 140, 220).ToArgb())
                || (label.StartsWith("Red") && color.ToArgb() == Color.FromArgb(220, 80, 80).ToArgb())
                || (label.StartsWith("Green") && color.ToArgb() == Color.FromArgb(80, 200, 110).ToArgb())
                || (label.StartsWith("Yellow") && color.ToArgb() == Color.FromArgb(230, 200, 70).ToArgb());
        });
        // Repaint every currently-decorated slot so the new color shows immediately
        foreach (var pb in _selectionDecoratedSlots) pb.Invalidate();
        SaveSettings();
        SetStatus("Selection outline color updated.");
    }

    /// <summary>Sets the selection-outline thickness (1-6 px) and repaints affected slots.</summary>
    private void SetSelectionOutlineThickness(int px)
    {
        if (px < 1 || px > 6) return;
        _selectionOutlineThickness = px;
        UpdateMenuRadioChecks("View", "Selection Style", "Outline Thickness", item => item.Text == $"{px} px");
        foreach (var pb in _selectionDecoratedSlots) pb.Invalidate();
        SaveSettings();
        SetStatus($"Selection outline thickness set to {px} px.");
    }

    /// <summary>Applies the user's chosen summary text color and weight to the summary detail label.</summary>
    private void ApplySummaryTextStyle()
    {
        try
        {
            _summaryDetails.ForeColor = _summaryTextBlack ? Color.Black : Color.White;
            var style = _summaryTextBold ? FontStyle.Bold : FontStyle.Regular;
            _summaryDetails.Font = new Font("Segoe UI", _summaryTextSize, style);
            // Re-render any currently-displayed summary so the new style is visible immediately
            if (_summaryCurrentPk is { } pk)
                RefreshSummary(pk, _summaryDenBox, _summaryDenSlot, _summarySourceIsDen);
        }
        catch { }
    }

    /// <summary>
    /// Walks the menu strip to find a deeply nested submenu and applies a predicate to determine
    /// which item should appear "checked." Used for radio-style menus like Outline Color / Thickness.
    /// </summary>
    private void UpdateMenuRadioChecks(string topMenuText, string subMenuText, string innerMenuText, Func<ToolStripMenuItem, bool> isMatch)
    {
        foreach (Control c in Controls)
        {
            if (c is not MenuStrip ms2) continue;
            foreach (ToolStripItem topItem in ms2.Items)
            {
                if (topItem is not ToolStripMenuItem top || top.Text != topMenuText) continue;
                foreach (ToolStripItem subItem in top.DropDownItems)
                {
                    if (subItem is not ToolStripMenuItem sub || sub.Text != subMenuText) continue;
                    foreach (ToolStripItem innerItem in sub.DropDownItems)
                    {
                        if (innerItem is not ToolStripMenuItem inner || inner.Text != innerMenuText) continue;
                        foreach (ToolStripItem leaf in inner.DropDownItems)
                        {
                            if (leaf is ToolStripMenuItem l)
                                l.Checked = isMatch(l);
                        }
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Toggles the centered 6×5 layout for the Den panel.</summary>
    private void ToggleDenGridLayout()
    {
        _use6x5DenGrid = !_use6x5DenGrid;
        ApplyDenGridLayout();
        SaveSettings();
        SetStatus(_use6x5DenGrid ? "Den layout: centered 6×5 grid." : "Den layout: default (flow).");
    }

    /// <summary>Toggles the centered 6×5 layout for the Save panel.</summary>
    private void ToggleSaveGridLayout()
    {
        _use6x5SaveGrid = !_use6x5SaveGrid;
        ApplySaveGridLayout();
        SaveSettings();
        SetStatus(_use6x5SaveGrid ? "Save layout: centered 6×5 grid." : "Save layout: default (flow).");
    }

    /// <summary>Right-click anywhere on the Den panel's empty area opens a layout/display menu.</summary>
    private void DenGrid_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var menu = new ContextMenuStrip();
        var layoutItem = new ToolStripMenuItem(
            _use6x5DenGrid ? "✓ Centered 6×5 Grid Layout" : "Centered 6×5 Grid Layout",
            null,
            (_, _) => ToggleDenGridLayout());
        layoutItem.ToolTipText = "Force the Den into a centered 6-column × 5-row grid layout.";
        menu.Items.Add(layoutItem);
        menu.Show(denGrid, e.Location);
    }

    /// <summary>Right-click anywhere on the Save panel's empty area opens a layout/display menu.</summary>
    private void SaveGrid_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var menu = new ContextMenuStrip();
        var layoutItem = new ToolStripMenuItem(
            _use6x5SaveGrid ? "✓ Centered 6×5 Grid Layout" : "Centered 6×5 Grid Layout",
            null,
            (_, _) => ToggleSaveGridLayout());
        layoutItem.ToolTipText = "Force the Save panel into a centered 6-column × 5-row grid layout.";
        menu.Items.Add(layoutItem);
        menu.Show(saveGrid, e.Location);
    }

    /// <summary>
    /// Updates the box-name header above the Den grid. Pass any string;
    /// during search this shows a result-count message instead of a box name.
    /// </summary>
    private void UpdateDenBoxNameHeader(string text)
    {
        if (_denBoxNameHeader is null) return;
        if (_denBoxNameHeader.Text != text)
            _denBoxNameHeader.Text = text;
    }

    // Tracks how many "extra" wheel notches the user has scrolled past the top/bottom of the
    // current box. When the cumulative count exceeds the threshold we advance to the next/prev box.
    // This lets the user scroll naturally within a box and only "spill over" into the next box
    // after a deliberate continued scroll past the edge — matching the user's "stop anywhere" intent.
    private int _denWheelEdgeAccum;
    private const int DenWheelEdgeThreshold = 240;  // ~2 wheel notches (a notch is normally 120)

    /// <summary>
    /// Mouse-wheel handler for the Den grid. Scrolls vertically as normal until the user hits
    /// the top or bottom; further scrolling past the edge advances the box selector.
    /// Search mode falls through to default scroll only — no box switching while filtered.
    /// </summary>
    private void DenGrid_MouseWheel(object? sender, MouseEventArgs e)
    {
        // While search is active there's only one logical "list" of results, so don't switch boxes.
        if (_isSearchActive) return;

        // FlowLayoutPanel.AutoScroll already consumes the wheel for in-bounds scroll.
        // We only need to act when the scroll position is at an edge AND the user keeps scrolling.
        var pos = denGrid.VerticalScroll;
        // VerticalScroll.Value can lag behind the active scroll; query both Value and the visible state.
        bool atTop = !pos.Visible || pos.Value <= 0;
        bool atBottom = !pos.Visible || pos.Value >= pos.Maximum - pos.LargeChange + 1;

        if (e.Delta > 0 && atTop)
        {
            // Scrolling up past the top of the current box — accumulate; switch when threshold met.
            _denWheelEdgeAccum -= e.Delta;
            if (_denWheelEdgeAccum <= -DenWheelEdgeThreshold)
            {
                _denWheelEdgeAccum = 0;
                if (denBoxSelector.Items.Count > 0 && denBoxSelector.SelectedIndex > 0)
                {
                    // Suspend layout on the parent panel during the box switch so intermediate
                    // paints don't flash through (the header label and grid would otherwise paint
                    // separately, briefly exposing the parent panel's BackColor between repaints).
                    denPanel.SuspendLayout();
                    try
                    {
                        denBoxSelector.SelectedIndex--;
                        // Place scroll at bottom of new box so continued upward wheel feels continuous
                        var newPos = denGrid.VerticalScroll;
                        if (newPos.Visible)
                            denGrid.VerticalScroll.Value = Math.Max(0, newPos.Maximum - newPos.LargeChange);
                    }
                    finally { denPanel.ResumeLayout(); }
                }
            }
        }
        else if (e.Delta < 0 && atBottom)
        {
            _denWheelEdgeAccum += -e.Delta;
            if (_denWheelEdgeAccum >= DenWheelEdgeThreshold)
            {
                _denWheelEdgeAccum = 0;
                if (denBoxSelector.Items.Count > 0 && denBoxSelector.SelectedIndex < denBoxSelector.Items.Count - 1)
                {
                    denPanel.SuspendLayout();
                    try
                    {
                        denBoxSelector.SelectedIndex++;
                        // Place scroll at top of new box for continued downward wheel
                        if (denGrid.VerticalScroll.Visible)
                            denGrid.VerticalScroll.Value = 0;
                    }
                    finally { denPanel.ResumeLayout(); }
                }
            }
        }
        else
        {
            // Reset the edge accumulator any time we're scrolling within the box bounds
            _denWheelEdgeAccum = 0;
        }
    }

    private void PopulateDenBoxNames()
    {
        _suppressBoxSelectorEvents = true;
        try
        {
            int prevSel = denBoxSelector.SelectedIndex;
            denBoxSelector.BeginUpdate();
            denBoxSelector.Items.Clear();
            for (int i = 0; i < Den.BoxCount; i++)
                denBoxSelector.Items.Add(FormatBoxLabel(i));
            if (prevSel >= 0 && prevSel < denBoxSelector.Items.Count)
                denBoxSelector.SelectedIndex = prevSel;
            else if (denBoxSelector.Items.Count > 0)
                denBoxSelector.SelectedIndex = 0;
            denBoxSelector.EndUpdate();
            UpdateBoxSelectorWidth();
        }
        finally { _suppressBoxSelectorEvents = false; }
    }

    private string FormatBoxLabel(int box)
    {
        string name = Den.GetBoxName(box);
        int count = Den.GetBoxCount(box);
        return count == 0
            ? $"{name} — EMPTY"
            : $"{name} — {count}/{DenStorageManager.SlotsPerBox}";
    }

    private void PopulateSaveBoxNames()
    {
        saveBoxSelector.Items.Clear();
        if (SAV is null || !SAV.HasBox) return;
        var names = BoxUtil.GetBoxNames(SAV);
        foreach (var n in names) saveBoxSelector.Items.Add(n);
    }

    private int CurrentDenBox => denBoxSelector.SelectedIndex;
    private int CurrentSaveBox => saveBoxSelector.SelectedIndex;

    private void NavigateDenBox(int delta)
    {
        int next = CurrentDenBox + delta;
        if (next < 0) next = Den.BoxCount - 1;
        if (next >= Den.BoxCount) next = 0;
        denBoxSelector.SelectedIndex = next;
    }

    private void NavigateSaveBox(int delta)
    {
        int count = saveBoxSelector.Items.Count;
        if (count == 0) return;
        int next = CurrentSaveBox + delta;
        if (next < 0) next = count - 1;
        if (next >= count) next = 0;
        saveBoxSelector.SelectedIndex = next;
    }

    // ========================================================================
    //  GRID REFRESH
    // ========================================================================

    private void RefreshDenGrid()
    {
        // If search is active, refresh the results and display them instead
        if (_isSearchActive)
        {
            // Re-collect matches (in case data changed)
            var nameFilter = _searchBox.Text.Trim();
            var otFilter = _filterOT.Text.Trim();
            var species = GameInfo.Strings.specieslist;
            _searchResults.Clear();
            for (int b = 0; b < Den.BoxCount; b++)
                for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
                {
                    var p = Den.GetSlot(b, s);
                    if (p is { Species: > 0 } && MatchesFilter(p, nameFilter, otFilter, species))
                        _searchResults.Add((b, s, p));
                }
            UpdateDenBoxNameHeader($"Search Results — {_searchResults.Count} matches");
            DisplaySearchResults();
            if (!_isRefreshing) { _isRefreshing = true; NotifyArrangeWindows(); _isRefreshing = false; }
            return;
        }

        selectedDenSlots.Clear();
        int box = CurrentDenBox;
        if (box < 0) return;
        UpdateDenBoxNameHeader(Den.GetBoxName(box));
        var slotBase = GetSlotBaseColor(box);
        for (int i = 0; i < denSlots.Count; i++)
        {
            // CRITICAL: Set BackColor BEFORE UpdateSlotImage / UpdateEmptySlotImage.  Those methods
            // build a composed Bitmap (sprite + label areas) and use pb.BackColor to fill the
            // bitmap's background.  If we set BackColor afterwards, the composed bitmap is filled
            // with the OLD color and the new color isn't visible until the slot is rebuilt — which
            // was the "scroll up / next box loses color" bug.
            denSlots[i].BackColor = slotBase;

            var pk = Den.GetSlot(box, i);
            // For empty Den slots, render any slot-label text the user set on this position
            if (pk is null or { Species: 0 })
            {
                var label = Den.GetSlotLabel(box, i);
                UpdateEmptySlotImage(denSlots[i], label);
            }
            else
            {
                UpdateSlotImage(denSlots[i], pk, true);
            }
            // Refresh selection rendering — clear any stale selection from a previous box
            if (!selectedDenSlots.Contains(box * SlotsPerDenBox + i))
                MarkSlotSelected(denSlots[i], false);
            denSlots[i].Visible = true;
        }
        denCountLabel.Text = $"{Den.GetBoxCount(box)}/{SlotsPerDenBox} in box — {Den.GetTotalCount()} total";

        // Keep ALL box selector labels in sync with current counts
        RefreshAllBoxLabels();

        // Apply background.  Resolution order:
        //   1. Image background (per-box wins, then global) — handled here
        //   2. Solid color background (per-box wins, then global) — handled here
        //   3. Default panel color — handled here
        // Slot fill color (the color INSIDE each individual slot) is independent
        // and was already applied in the loop above via slotBase.
        var bgPath = Den.GetEffectiveBackground(box);
        if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
        {
            try
            {
                denGrid.BackgroundImage?.Dispose();
                denGrid.BackgroundImage = Image.FromFile(bgPath);
                denGrid.BackgroundImageLayout = ImageLayout.Stretch;
                // Make slots semi-transparent so background shows through
                foreach (var pb in denSlots)
                    if (pb.Image is null) pb.BackColor = Color.FromArgb(120, 40, 40, 48);
                    else pb.BackColor = Color.FromArgb(180, 50, 52, 62);
            }
            catch { denGrid.BackgroundImage = null; }
        }
        else
        {
            denGrid.BackgroundImage?.Dispose();
            denGrid.BackgroundImage = null;
            // No solid-color background mode anymore (removed in V0.1.6).  Slot fill
            // colors (per-box, set via Edit → Box Color) are the only color
            // customization left; the panel itself uses the default theme color.
            denGrid.BackColor  = Color.FromArgb(40, 40, 48);
            denPanel.BackColor = Color.FromArgb(40, 40, 48);
        }

        // Notify any open arrange windows
        if (!_isRefreshing)
        {
            _isRefreshing = true;
            NotifyArrangeWindows();
            _isRefreshing = false;
        }
    }

    private bool _isRefreshing;

    private void RefreshSaveGrid()
    {
        ClearSaveSelection();
        if (SAV is null || !SAV.HasBox) return;
        int box = CurrentSaveBox;
        if (box < 0) return;
        for (int i = 0; i < saveSlots.Count; i++)
        {
            PKM? pk = i < SAV.BoxSlotCount ? SAV.GetBoxSlotAtIndex(box, i) : null;
            UpdateSlotImage(saveSlots[i], pk, false);
            saveSlots[i].Visible = i < SAV.BoxSlotCount;
        }
        int count = 0;
        for (int i = 0; i < SAV.BoxSlotCount; i++)
            if (SAV.GetBoxSlotAtIndex(box, i).Species > 0) count++;
        saveCountLabel.Text = $"{count}/{SAV.BoxSlotCount} in box";
        RefreshPartyGrid();
    }

    private void SetSpriteScale(float scale)
    {
        if (scale < 0.5f || scale > 4f) return;
        if (Math.Abs(_spriteScale - scale) < 0.001f) return;
        _spriteScale = scale;
        UpdateZoomMenuChecks("Den Sprite Size", scale);
        RebuildDenGrid();
        SaveSettings();
        SetStatus($"Den sprite size set to {FormatScale(scale)}.");
    }

    private void SetSaveSpriteScale(float scale)
    {
        if (scale < 0.5f || scale > 4f) return;
        if (Math.Abs(_saveSpriteScale - scale) < 0.001f) return;
        _saveSpriteScale = scale;
        UpdateZoomMenuChecks("Save Sprite Size", scale);
        RebuildSaveGrid();
        // NOTE: party grid is no longer rebuilt here — party has its own scale (_partySpriteScale).
        SaveSettings();
        SetStatus($"Save sprite size set to {FormatScale(scale)}.");
    }

    /// <summary>
    /// Sets the party-row sprite scale.  Independent from <see cref="_saveSpriteScale"/>
    /// so the user can keep box slots compact while making party slots large (or vice
    /// versa).  See <see cref="_partySpriteScale"/> for the rationale.
    /// </summary>
    private void SetPartySpriteScale(float scale)
    {
        if (scale < 0.5f || scale > 4f) return;
        if (Math.Abs(_partySpriteScale - scale) < 0.001f) return;
        _partySpriteScale = scale;
        UpdateZoomMenuChecks("Party Sprite Size", scale);
        RebuildPartyGrid();
        SaveSettings();
        SetStatus($"Party sprite size set to {FormatScale(scale)}.");
    }

    /// <summary>Renders a float scale as a clean menu label, e.g. 0.5x, 1x, 1.5x, 2x.</summary>
    private static string FormatScale(float scale)
    {
        // Avoid trailing ".0" — show "1x" not "1.0x"
        if (Math.Abs(scale - Math.Round(scale)) < 0.001f) return $"{(int)Math.Round(scale)}x";
        return $"{scale:0.#}x";
    }

    /// <summary>Applies a new font size to the Pokémon Summary detail label and refreshes the view.</summary>
    private void SetSummaryTextSize(float pt)
    {
        if (pt < 6f || pt > 20f) return;
        if (Math.Abs(_summaryTextSize - pt) < 0.01f) return;
        _summaryTextSize = pt;
        // Update the label's font in place
        try
        {
            _summaryDetails.Font = new Font("Segoe UI", _summaryTextSize);
        }
        catch { }
        // Update check marks in the Summary Text Size submenu
        foreach (Control c in Controls)
        {
            if (c is MenuStrip ms2)
            {
                foreach (ToolStripItem item in ms2.Items)
                {
                    if (item is ToolStripMenuItem tmi && tmi.Text == "View")
                    {
                        foreach (ToolStripItem sub in tmi.DropDownItems)
                        {
                            if (sub is ToolStripMenuItem zm && zm.Text == "Summary Text Size")
                            {
                                foreach (ToolStripMenuItem zi in zm.DropDownItems)
                                    zi.Checked = zi.Text == $"{pt:0.#} pt";
                            }
                        }
                    }
                }
                break;
            }
        }
        // Re-render current summary so spacing matches
        if (_summaryCurrentPk is { } pk)
            RefreshSummary(pk, _summaryDenBox, _summaryDenSlot, _summarySourceIsDen);
        SaveSettings();
        SetStatus($"Summary text size set to {pt:0.#} pt.");
    }

    /// <summary>Applies a new font size to the per-slot label (name/gender/origin shown on each Pokémon).</summary>
    private void SetSlotLabelTextSize(float pt)
    {
        if (pt < 5f || pt > 16f) return;
        if (Math.Abs(_slotLabelTextSize - pt) < 0.01f) return;
        _slotLabelTextSize = pt;
        // Update check marks in the Slot Label Text Size submenu
        foreach (Control c in Controls)
        {
            if (c is MenuStrip ms2)
            {
                foreach (ToolStripItem item in ms2.Items)
                {
                    if (item is ToolStripMenuItem tmi && tmi.Text == "View")
                    {
                        foreach (ToolStripItem sub in tmi.DropDownItems)
                        {
                            if (sub is ToolStripMenuItem zm && zm.Text == "Slot Label Text Size")
                            {
                                foreach (ToolStripMenuItem zi in zm.DropDownItems)
                                    zi.Checked = zi.Text == $"{pt:0.#} pt";
                            }
                        }
                    }
                }
                break;
            }
        }
        RebuildDenGrid();
        RebuildSaveGrid();
        RefreshPartyGrid();
        RebuildRecentlyDeletedGrid();
        SaveSettings();
        SetStatus($"Slot label text size set to {pt:0.#} pt.");
    }

    private void UpdateZoomMenuChecks(string submenuText, float scale)
    {
        string targetLabel = FormatScale(scale);
        foreach (Control c in Controls)
        {
            if (c is MenuStrip ms2)
            {
                foreach (ToolStripItem item in ms2.Items)
                {
                    if (item is ToolStripMenuItem tmi && tmi.Text == "View")
                    {
                        foreach (ToolStripItem sub in tmi.DropDownItems)
                        {
                            if (sub is ToolStripMenuItem zm && zm.Text == submenuText)
                            {
                                foreach (ToolStripMenuItem zi in zm.DropDownItems)
                                    zi.Checked = zi.Text == targetLabel;
                            }
                        }
                    }
                }
                break;
            }
        }
    }

    /// <summary>Destroys and recreates the Save grid with current scale. Preserves selection is not practical — gets cleared.</summary>
    private void RebuildSaveGrid()
    {
        if (SAV is null || !SAV.HasBox) return;
        saveGrid.SuspendLayout();
        foreach (var pb in saveSlots)
        {
            bool owned = _ownedImageSlots.Remove(pb);
            var img = pb.Image;
            pb.Image = null;
            if (owned && img is not null) { try { img.Dispose(); } catch { } }
            saveGrid.Controls.Remove(pb);
            pb.Dispose();
        }
        saveSlots.Clear();
        selectedSaveSlots.Clear();
        InitializeSlotPictureBoxes(saveGrid, saveSlots, false, SAV.BoxSlotCount);
        saveGrid.ResumeLayout();
        // Re-apply 6×5 centering padding now that slot widths are known
        ApplySaveGridLayout();
        RefreshSaveGrid();
    }

    /// <summary>Destroys and recreates the Den grid with current scale and label settings.</summary>
    private void RebuildDenGrid()
    {
        denGrid.SuspendLayout();
        foreach (var pb in denSlots)
        {
            bool owned = _ownedImageSlots.Remove(pb);
            var img = pb.Image;
            pb.Image = null;
            if (owned && img is not null)
            {
                try { img.Dispose(); } catch { }
            }
            denGrid.Controls.Remove(pb);
            pb.Dispose();
        }
        denSlots.Clear();
        InitializeSlotPictureBoxes(denGrid, denSlots, true, SlotsPerDenBox);
        denGrid.ResumeLayout();
        // Re-apply 6×5 centering padding now that slot widths are known
        ApplyDenGridLayout();
        RefreshDenGrid();
    }

    // Tracks which PictureBox.Image references are bitmaps we created (and thus must dispose).
    private readonly HashSet<PictureBox> _ownedImageSlots = new();
    // Tracks which slot PictureBoxes are currently rendered with a selection outline.
    // Populated by MarkSlotSelected; the Paint handler reads this to decide whether to draw a border.
    private readonly HashSet<PictureBox> _selectionDecoratedSlots = new();

    /// <summary>
    /// Returns the slot fill color.  Always the theme default since the user-customizable
    /// slot/box color menus were removed (they were redundant with each other).  Both
    /// overloads kept for source compatibility with existing callers — the box parameter
    /// is now ignored.
    /// </summary>
    private Color GetSlotBaseColor(int box) => Color.FromArgb(60, 62, 72);

    /// <summary>Convenience overload for non-den contexts that don't have a box index.</summary>
    private Color GetSlotBaseColor() => Color.FromArgb(60, 62, 72);

    /// <summary>
    /// Marks (or unmarks) a slot as selected. Selection is rendered as a coloured outline
    /// border around the slot — drawn by the slot's Paint handler — so the slot's BackColor
    /// stays at the normal "empty" tone and the highlight doesn't bleed across the whole tile.
    /// </summary>
    private void MarkSlotSelected(PictureBox pb, bool selected)
    {
        bool wasSelected = _selectionDecoratedSlots.Contains(pb);
        if (selected == wasSelected) return;
        if (selected) _selectionDecoratedSlots.Add(pb);
        else _selectionDecoratedSlots.Remove(pb);
        pb.Invalidate();  // trigger repaint so the border shows/hides
    }

    /// <summary>
    /// Owner-draw handler for the top-level Den/Pokédex tabs.  Draws a bigger, bolder,
    /// higher-contrast tab label with a thick selection outline so the active tab is
    /// obvious against PKDen's dark theme.
    /// </summary>
    /// <remarks>
    /// Three pieces:
    ///   1. Background fill — bright accent for the selected tab, muted dark for inactive.
    ///   2. Thick outline (3px) on all four sides for the selected tab; thin (1px) for inactive.
    ///   3. Tab label drawn centered with bold Segoe UI 11pt — substantially more readable
    ///      than the default Windows tab font at the same size.
    /// </remarks>
    private void TopTabs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var tabs = sender as TabControl ?? _topTabs;
        if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;

        var page = tabs.TabPages[e.Index];
        var rect = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Background — selected tab uses a clear blue accent; inactive uses muted gray.
        Color fill = selected
            ? Color.FromArgb(60, 100, 170)
            : Color.FromArgb(50, 52, 62);
        using (var bg = new SolidBrush(fill))
            g.FillRectangle(bg, rect);

        // Outline — thick (3px) when selected, thin (1px) when not.  Drawn inset by half the
        // pen width so the line lives fully inside the tab rect (otherwise a 3px pen clips).
        int penWidth = selected ? 3 : 1;
        Color penColor = selected ? Color.FromArgb(120, 180, 240) : Color.FromArgb(80, 82, 92);
        using (var pen = new Pen(penColor, penWidth))
        {
            float inset = penWidth / 2f;
            g.DrawRectangle(pen,
                rect.X + inset, rect.Y + inset,
                rect.Width - penWidth, rect.Height - penWidth);
        }

        // Label — centered, bold, 11pt for clear readability.
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var fg = new SolidBrush(Color.White);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(page.Text, font, fg, rect, sf);
    }

    /// <summary>
    /// Paint handler attached to every slot. Draws the user-configured selection outline
    /// when the slot is currently selected. Called BEFORE the PictureBox's normal image draw
    /// because we want the border on the OUTSIDE of the image — so we paint it after,
    /// using the post-paint event below by attaching to <see cref="Control.Paint"/>.
    /// </summary>
    private void SlotSelection_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not PictureBox pb) return;
        if (!_selectionDecoratedSlots.Contains(pb)) return;

        int t = Math.Max(1, _selectionOutlineThickness);
        using var pen = new Pen(_selectionOutlineColor, t);
        // Inset by half the pen width so the line draws fully inside the picture box bounds
        // (otherwise a 3px pen would clip half-off the right/bottom edges).
        float inset = t / 2f;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawRectangle(
            pen,
            inset, inset,
            pb.ClientSize.Width - 1 - t + 1,   // matches inset on both sides
            pb.ClientSize.Height - 1 - t + 1);
    }

    /// <summary>
    /// Builds a Pokémon sprite respecting the global "show held item" toggle, sized
    /// to the requested target dimensions.
    ///
    /// IMPORTANT: We ALWAYS go through the PKM-aware <see cref="PKDenSpriteUtil.PKDenSpriteAt(PKM, int, int, bool)"/>
    /// overload so per-Pokémon custom sprites stay in scope.  Previously, when the
    /// "Show Held Item" toggle was off, this method called the species/form-only
    /// <c>GetSpriteAt</c> overload to suppress the item — but that overload has no
    /// PKM context and silently skipped the custom-sprite lookup, causing custom
    /// sprites to disappear from the den grid while still showing in the summary.
    ///
    /// The (targetW × targetH) parameters let the slot renderer request sprites at
    /// the exact zoom level it intends to display, so the slot composer can blit
    /// 1:1 instead of upscaling a canvas-sized sprite.
    /// </summary>
    private Bitmap GetSlotSprite(PKM pk, int targetW, int targetH)
        => pk.PKDenSpriteAt(targetW, targetH, hideItem: !_showHeldItem);

    /// <summary>
    /// Distinguishes which sprite-scale field (and therefore which UI surface) a slot
    /// belongs to.  Replaces the previous bool isDenSlot, which couldn't differentiate
    /// between "Save grid slot" and "Party slot" — both used <c>_saveSpriteScale</c>.
    /// Now that party has its own scale (<see cref="_partySpriteScale"/>), the renderer
    /// needs to know which one to apply.
    /// </summary>
    private enum SlotKind { Den, Save, Party }

    /// <summary>
    /// Pre-existing two-arg overload preserved for callers that still use the bool.
    /// New code should pass <see cref="SlotKind"/> directly.
    /// </summary>
    private void UpdateSlotImage(PictureBox pb, PKM? pk, bool isDenSlot)
        => UpdateSlotImage(pb, pk, isDenSlot ? SlotKind.Den : SlotKind.Save);

    private void UpdateSlotImage(PictureBox pb, PKM? pk, SlotKind kind)
    {
        // Clear and dispose prior image ONLY if we created it
        var oldImage = pb.Image;
        bool oldWasOwned = _ownedImageSlots.Remove(pb);
        pb.Image = null;
        if (oldWasOwned && oldImage is not null)
        {
            try { oldImage.Dispose(); } catch { }
        }

        if (pk is null or { Species: 0 }) return;

        try
        {
            // Compute target dimensions FIRST so we can request a sprite that's already
            // the right size — no need for the composer to upscale anything.  Two cases:
            //
            //   • 1× scale with no labels: render path is `pb.Image = sprite` direct.
            //     The slot PictureBox uses CenterImage at 1×, which CLIPS oversized
            //     images, so we must request canvas size (68×56 for SB8a).
            //
            //   • Everything else (zoom > 1×, OR has labels): goes through the
            //     composer which builds a (spriteW × totalH) canvas.  Request the
            //     sprite at exactly (spriteW × spriteH) so the composer's DrawImage
            //     is a 1:1 blit — preserves every pixel of the bicubic-downscaled
            //     master in SpriteOverrides.
            float scale = kind switch
            {
                SlotKind.Den   => _spriteScale,
                SlotKind.Party => _partySpriteScale,
                _              => _saveSpriteScale, // Save
            };
            bool showOrigin = _showOrigin;
            bool showBottom = _showNames || _showGenders;
            bool hasAnyLabel = showOrigin || showBottom;
            bool earlyReturn = Math.Abs(scale - 1.0f) < 0.001f && !hasAnyLabel;

            int spriteW = (int)Math.Round(SpriteW * scale);
            int spriteH = (int)Math.Round(SpriteH * scale);
            int targetW = earlyReturn ? SpriteW : spriteW;
            int targetH = earlyReturn ? SpriteH : spriteH;

            var sprite = GetSlotSprite(pk, targetW, targetH);
            // PKDenSpriteUtil now ALWAYS returns a freshly allocated bitmap (see
            // ResizeIfNeeded ownership notes).  We always own and must dispose.
            bool spriteIsOwned = true;

            if (earlyReturn)
            {
                // 1x and no labels — use the sprite directly
                pb.Image = sprite;
                if (spriteIsOwned) _ownedImageSlots.Add(pb);
                return;
            }

            int topH = showOrigin ? Math.Max(12, (int)Math.Round(14 * scale)) : 0;
            int botH = showBottom ? Math.Max(14, (int)Math.Round(16 * scale)) : 0;
            int totalH = topH + spriteH + botH;

            var composed = new Bitmap(spriteW, totalH);
            using (var g = Graphics.FromImage(composed))
            {
                // Fill the composed bitmap with the slot's CURRENT BackColor first, so per-box
                // color settings show through behind the sprite & labels.  A fresh Bitmap
                // contains opaque black pixels by default — without this Clear() the slot's
                // BackColor would be invisible behind any sprite or label area, which is
                // exactly the "color disappears on box switch" bug users were hitting.
                g.Clear(pb.BackColor);

                // Sprite is now pre-sized to (spriteW × spriteH), so the DrawImage call
                // below is effectively a 1:1 blit — interpolation mode shouldn't matter
                // in practice, but bicubic is the safe default in case of off-by-one
                // rounding between the request and the actual returned bitmap.
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                // Draw sprite offset by topH (below the origin area)
                g.DrawImage(sprite, 0, topH, spriteW, spriteH);

                using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var fg = new SolidBrush(Color.White);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                };

                if (showOrigin)
                {
                    string originLabel = BuildOriginLabel(pk);
                    if (!string.IsNullOrEmpty(originLabel))
                    {
                        // Per-slot label font scales with both user-chosen size AND sprite zoom
                        float topPt = Math.Max(5.5f, _slotLabelTextSize * 0.85f * Math.Max(1.0f, scale));
                        using var fntTop = new Font("Segoe UI", topPt, FontStyle.Bold);
                        var topRect = new RectangleF(0, 0, spriteW, topH);
                        g.FillRectangle(bg, topRect);
                        g.DrawString(originLabel, fntTop, fg, topRect, sf);
                    }
                }

                if (showBottom)
                {
                    string bottomLabel = BuildBottomLabel(pk);
                    if (!string.IsNullOrEmpty(bottomLabel))
                    {
                        float botPt = Math.Max(6f, _slotLabelTextSize * Math.Max(1.0f, scale));
                        using var fntBot = new Font("Segoe UI", botPt, FontStyle.Bold);
                        var botRect = new RectangleF(0, topH + spriteH, spriteW, botH);
                        g.FillRectangle(bg, botRect);
                        g.DrawString(bottomLabel, fntBot, fg, botRect, sf);
                    }
                }
            }

            pb.Image = composed;
            _ownedImageSlots.Add(pb); // composed bitmap is always owned

            // If we built a custom sprite (no held item), dispose it now that we've drawn it onto `composed`
            if (spriteIsOwned)
            {
                try { sprite.Dispose(); } catch { }
            }
        }
        catch
        {
            pb.Image = null;
        }
    }

    /// <summary>
    /// Renders a recently-deleted slot at fixed 1× scale with no labels.
    /// Independent of the main grid's view settings (sprite size, show name/gender/origin)
    /// so the Recently Deleted shelf stays compact and uniform.
    /// Held-item icon is still respected via the global toggle.
    /// </summary>
    private void UpdateRecentSlotImage(PictureBox pb, PKM? pk)
    {
        // Clear and dispose prior owned image
        var oldImage = pb.Image;
        bool oldWasOwned = _ownedImageSlots.Remove(pb);
        pb.Image = null;
        if (oldWasOwned && oldImage is not null)
        {
            try { oldImage.Dispose(); } catch { }
        }

        if (pk is null or { Species: 0 }) return;

        try
        {
            // Recently-deleted shelf is fixed at 1× scale and uses CenterImage SizeMode,
            // so request the sprite at canvas size (68×56) — anything larger would clip.
            var sprite = GetSlotSprite(pk, SpriteW, SpriteH);
            // PKDenSpriteUtil now ALWAYS returns a freshly allocated bitmap that we own
            // and must dispose later, regardless of the _showHeldItem toggle.
            pb.Image = sprite;
            _ownedImageSlots.Add(pb);
        }
        catch
        {
            pb.Image = null;
        }
    }

    /// <summary>
    /// Renders a per-slot text label on an empty Den slot. The text is drawn centered over the slot;
    /// when null/empty the slot is left blank. Word-wraps long text within the slot bounds.
    /// </summary>
    private void UpdateEmptySlotImage(PictureBox pb, string? label)
    {
        // Always clear/dispose any previous owned image so we don't leak when transitioning
        // between "label", "no label", and "Pokémon" states.
        var oldImage = pb.Image;
        bool oldWasOwned = _ownedImageSlots.Remove(pb);
        pb.Image = null;
        if (oldWasOwned && oldImage is not null)
        {
            try { oldImage.Dispose(); } catch { }
        }

        if (string.IsNullOrEmpty(label)) return;

        try
        {
            int w = Math.Max(1, pb.ClientSize.Width);
            int h = Math.Max(1, pb.ClientSize.Height);
            var composed = new Bitmap(w, h);
            using (var g = Graphics.FromImage(composed))
            {
                // Same reason as in UpdateSlotImage: a fresh Bitmap is opaque black,
                // which would mask the slot's per-box BackColor.  Clear with BackColor
                // so the labeled empty slot blends with the rest of the box's color.
                g.Clear(pb.BackColor);

                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Subtle dotted border so the label-bearing empty slot looks distinct
                using var pen = new Pen(Color.FromArgb(120, 200, 200, 200))
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dot,
                };
                g.DrawRectangle(pen, 1, 1, w - 3, h - 3);

                // Pick a font size scaled to the slot's height so it stays readable across zoom levels
                float pt = Math.Max(7f, Math.Min(16f, _slotLabelTextSize * 1.0f));
                using var font = new Font("Segoe UI", pt, FontStyle.Bold);
                using var brush = new SolidBrush(Color.FromArgb(220, 230, 230, 240));
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisWord,
                };
                var rect = new RectangleF(2, 2, w - 4, h - 4);
                g.DrawString(label, font, brush, rect, sf);
            }
            pb.Image = composed;
            _ownedImageSlots.Add(pb);
        }
        catch
        {
            pb.Image = null;
        }
    }

    /// <summary>Bottom label: name and/or gender (only). Origin rendered separately at top.</summary>
    private string BuildBottomLabel(PKM pk)
    {
        var parts = new List<string>();
        if (_showNames)
        {
            var species = GameInfo.Strings.specieslist;
            string name = (uint)pk.Species < species.Length ? species[pk.Species] : $"#{pk.Species}";
            parts.Add(name);
        }
        if (_showGenders)
        {
            try
            {
                // Genderless Pokémon — leave blank instead of showing "-"
                string g = pk.Gender switch { 0 => "♂", 1 => "♀", _ => "" };
                if (!string.IsNullOrEmpty(g)) parts.Add(g);
            }
            catch { }
        }
        return string.Join(" ", parts);
    }

    /// <summary>Top label: origin game name.</summary>
    private static string BuildOriginLabel(PKM pk)
    {
        try
        {
            string game = GameInfo.GetVersionName(pk.Version);
            return game ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // ========================================================================
    //  CLICK / SELECTION
    // ========================================================================

    private int _lastClickedDenSlot = -1;
    private int _lastClickedSaveSlot = -1;

    private void SlotClicked(PictureBox? pb, MouseEventArgs e, bool isDen, int slot)
    {
        if (pb is null) return;
        if (e.Button == MouseButtons.Right)
        {
            // Also update the summary panel on right-click before showing the context menu
            var rpk = GetPKMForSlot(isDen, slot);
            if (rpk is { Species: > 0 })
            {
                if (isDen)
                {
                    var (sBox, sSlot) = GetDenLocationForSlot(slot);
                    RefreshSummary(rpk, sBox, sSlot, fromDen: true);
                }
                else RefreshSummary(rpk, -1, -1, fromDen: false);
            }
            ShowContextMenu(pb, e.Location, isDen, slot);
            return;
        }

        var selectedSet = isDen ? selectedDenSlots : selectedSaveSlots;
        var slotList = isDen ? denSlots : saveSlots;
        int slotsPerBox = isDen ? SlotsPerDenBox : (SAV?.BoxSlotCount ?? 30);
        int currentBox = isDen ? CurrentDenBox : CurrentSaveBox;
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        // In search mode, flatIndex = display slot (we'll look up source later)
        bool searchMode = isDen && _isSearchActive;

        if (shift)
        {
            int lastSlot = isDen ? _lastClickedDenSlot : _lastClickedSaveSlot;
            if (lastSlot < 0) lastSlot = slot;
            int from = Math.Min(lastSlot, slot);
            int to = Math.Max(lastSlot, slot);
            if (!ctrl) { if (isDen) ClearDenSelection(); else ClearSaveSelection(); }

            for (int i = from; i <= to && i < slotList.Count; i++)
            {
                var pk = GetPKMForSlot(isDen, i);
                if (pk is not { Species: > 0 }) continue;
                int flatIndex = searchMode ? i : currentBox * slotsPerBox + i;
                selectedSet.Add(flatIndex);
                MarkSlotSelected(slotList[i], true);
            }
            SetStatus($"{selectedSet.Count} selected.");
        }
        else if (ctrl)
        {
            int flatIndex = searchMode ? slot : currentBox * slotsPerBox + slot;
            if (selectedSet.Contains(flatIndex)) { selectedSet.Remove(flatIndex); MarkSlotSelected(pb, false); }
            else
            {
                var pk = GetPKMForSlot(isDen, slot);
                if (pk is { Species: > 0 }) { selectedSet.Add(flatIndex); MarkSlotSelected(pb, true); }
            }
            if (isDen) _lastClickedDenSlot = slot; else _lastClickedSaveSlot = slot;
        }
        else
        {
            if (isDen) ClearDenSelection(); else ClearSaveSelection();
            var pk = GetPKMForSlot(isDen, slot);
            if (pk is { Species: > 0 })
            {
                int flatIndex = searchMode ? slot : currentBox * slotsPerBox + slot;
                selectedSet.Add(flatIndex);
                MarkSlotSelected(pb, true);

                // Update summary panel — use source (box, slot) for Den so notes work even in search mode
                if (isDen)
                {
                    var (sBox, sSlot) = GetDenLocationForSlot(slot);
                    RefreshSummary(pk, sBox, sSlot, fromDen: true);
                }
                else
                {
                    RefreshSummary(pk, -1, -1, fromDen: false);
                }
            }
            else
            {
                ClearSummaryPanel();
            }
            if (isDen) _lastClickedDenSlot = slot; else _lastClickedSaveSlot = slot;
        }
    }

    private void ClearDenSelection() { selectedDenSlots.Clear(); var c = GetSlotBaseColor(CurrentDenBox); foreach (var pb in denSlots) { MarkSlotSelected(pb, false); pb.BackColor = c; } }
    private void ClearSaveSelection() { selectedSaveSlots.Clear(); foreach (var pb in saveSlots) { MarkSlotSelected(pb, false); pb.BackColor = GetSlotBaseColor(); } }

    // ========================================================================
    //  CONTEXT MENU
    // ========================================================================

    private void ShowContextMenu(PictureBox pb, Point location, bool isDen, int slot)
    {
        var pk = GetPKMForSlot(isDen, slot);
        var menu = new ContextMenuStrip();

        // In search mode, operations use the source box/slot, not the display slot
        (int srcBox, int srcSlot) = isDen ? GetDenLocationForSlot(slot) : (CurrentSaveBox, slot);

        if (pk is { Species: > 0 })
        {
            menu.Items.Add(new ToolStripMenuItem("View Summary", null, (_, _) => ViewSummary(pk,
                isDen ? Den.GetNote(srcBox, srcSlot) : null,
                isDen ? Den.GetTimestamp(srcBox, srcSlot) : null)));

            // Jump to Box — only in search mode
            if (isDen && _isSearchActive)
            {
                string boxName = Den.GetBoxName(srcBox);
                menu.Items.Add(new ToolStripMenuItem($"Jump to Box: {boxName}", null, (_, _) => JumpToBox(srcBox, srcSlot)));
                menu.Items.Add(new ToolStripSeparator());
            }

            // Copy — if multiple selected, copy all selected; otherwise copy this one
            if (isDen && selectedDenSlots.Count > 1)
            {
                menu.Items.Add(new ToolStripMenuItem($"Copy {selectedDenSlots.Count} Selected", null, (_, _) => CopySelectedToClipboard()));
            }
            else
            {
                menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) =>
                {
                    _clipboard.Clear();
                    _clipboard.Add((pk.Clone(),
                        isDen ? Den.GetNote(srcBox, srcSlot) : null,
                        isDen ? Den.GetTimestamp(srcBox, srcSlot) : null));
                    SetStatus($"Copied {GetSpeciesName(pk)} to clipboard.");
                }));
            }

            if (isDen)
            {
                // Paste clipboard here — only when not in search mode (destination is ambiguous in search)
                if (!_isSearchActive && _clipboard.Count > 0)
                {
                    string pasteLabel = _clipboard.Count == 1
                        ? $"Paste ({GetSpeciesName(_clipboard[0].Pk)}) — Replace"
                        : $"Paste {_clipboard.Count} Pokémon Here";
                    menu.Items.Add(new ToolStripMenuItem(pasteLabel, null, (_, _) => PasteClipboardAt(CurrentDenBox, slot)));
                }

                menu.Items.Add(new ToolStripSeparator());

                // Send to Box — works on selection if any; otherwise just this one
                if (selectedDenSlots.Count > 1)
                    menu.Items.Add(new ToolStripMenuItem($"Send {selectedDenSlots.Count} Selected to Box…", null, (_, _) => SendSelectedToBox()));
                else
                    menu.Items.Add(new ToolStripMenuItem("Send to Box…", null, (_, _) => SendSingleToBox(srcBox, srcSlot)));

                menu.Items.Add(new ToolStripSeparator());

                var existingNote = Den.GetNote(srcBox, srcSlot);
                menu.Items.Add(new ToolStripMenuItem(string.IsNullOrEmpty(existingNote) ? "Add Note" : "Edit Note", null, (_, _) => EditSlotNote(srcBox, srcSlot)));
                if (!string.IsNullOrEmpty(existingNote))
                    menu.Items.Add(new ToolStripMenuItem("Remove Note", null, (_, _) => { Den.SetNote(srcBox, srcSlot, null); SetStatus("Note removed."); }));

                // Custom sprite — replace this Pokémon's sprite with a user-supplied image.
                // Only available on den slots (not save slots) since the PNG bytes are
                // persisted in the den file, not the save.  Also only for single-selection
                // (mass-applying the same image to many mons would surprise users more
                // than help them).
                if (selectedDenSlots.Count <= 1)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    bool hasCustom = Den.HasCustomSprite(pk);
                    menu.Items.Add(new ToolStripMenuItem(hasCustom ? "Replace Custom Sprite..." : "Set Custom Sprite...", null,
                        (_, _) => SetCustomSpriteForPk(pk, srcBox, srcSlot)));
                    if (hasCustom)
                        menu.Items.Add(new ToolStripMenuItem("Remove Custom Sprite", null,
                            (_, _) => RemoveCustomSpriteForPk(pk, srcBox, srcSlot)));
                }

                menu.Items.Add(new ToolStripSeparator());

                if (selectedDenSlots.Count > 1)
                    menu.Items.Add(new ToolStripMenuItem($"Delete {selectedDenSlots.Count} Selected", null, (_, _) => DeleteSelectedDen()));
                else
                    menu.Items.Add(new ToolStripMenuItem("Delete", null, (_, _) => { PushUndo(); ClearSlotWithCapture(srcBox, srcSlot); RefreshDenGrid(); }));
            }
            else
            {
                if (selectedSaveSlots.Count > 1)
                    menu.Items.Add(new ToolStripMenuItem($"Move {selectedSaveSlots.Count} Selected to Den", null, (_, _) => BtnCopySelectedToDen_Click(null, EventArgs.Empty)));
                else
                    menu.Items.Add(new ToolStripMenuItem("Move to Den", null, (_, _) => CopySavePKMToDen(CurrentSaveBox, slot)));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Export .pk file", null, (_, _) => ExportUtil.ExportSinglePKM(pk, this)));
        }
        else if (isDen && !_isSearchActive)
        {
            // Paste into empty slot
            if (_clipboard.Count > 0)
            {
                string pasteLabel = _clipboard.Count == 1
                    ? $"Paste ({GetSpeciesName(_clipboard[0].Pk)})"
                    : $"Paste {_clipboard.Count} Pokémon Here";
                menu.Items.Add(new ToolStripMenuItem(pasteLabel, null, (_, _) => PasteClipboardAt(CurrentDenBox, slot)));
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add(new ToolStripMenuItem("Import .pk file here", null, (_, _) => ImportSingleFile(CurrentDenBox, slot)));

            // Slot label — text shown on this empty slot until a Pokémon takes it
            menu.Items.Add(new ToolStripSeparator());
            string? existingLabel = Den.GetSlotLabel(CurrentDenBox, slot);
            string itemText = string.IsNullOrEmpty(existingLabel) ? "Set Slot Text..." : "Edit Slot Text...";
            menu.Items.Add(new ToolStripMenuItem(itemText, null, (_, _) => EditSlotLabel(CurrentDenBox, slot)));
            if (!string.IsNullOrEmpty(existingLabel))
                menu.Items.Add(new ToolStripMenuItem("Clear Slot Text", null, (_, _) => { Den.RemoveSlotLabel(CurrentDenBox, slot); RefreshDenGrid(); SetStatus("Slot text cleared."); }));
        }

        if (menu.Items.Count > 0) menu.Show(pb, location);
    }

    private void CopySelectedToClipboard()
    {
        _clipboard.Clear();
        foreach (var flat in selectedDenSlots.OrderBy(x => x))
        {
            int box, slot;
            if (_isSearchActive)
            {
                if (flat >= _searchResults.Count) continue;
                (box, slot, _) = _searchResults[flat];
            }
            else
            {
                box = flat / SlotsPerDenBox; slot = flat % SlotsPerDenBox;
            }
            var pk = Den.GetSlot(box, slot);
            if (pk is not { Species: > 0 }) continue;
            _clipboard.Add((pk.Clone(), Den.GetNote(box, slot), Den.GetTimestamp(box, slot)));
        }
        SetStatus($"Copied {_clipboard.Count} Pokémon to clipboard.");
    }

    private void PasteClipboardAt(int destBox, int startSlot)
    {
        PushUndo();
        int pasted = 0;
        int slot = startSlot;
        foreach (var (pk, note, ts) in _clipboard)
        {
            // Find next available slot from startSlot
            while (slot < DenStorageManager.SlotsPerBox && Den.GetSlot(destBox, slot) is { Species: > 0 })
                slot++;
            if (slot >= DenStorageManager.SlotsPerBox)
            {
                // If the first paste position was occupied, overwrite starting from startSlot
                if (pasted == 0) slot = startSlot;
                else break;
            }
            // Capture any existing Pokémon at destination before overwriting
            var existing = Den.GetSlot(destBox, slot);
            if (existing is { Species: > 0 })
                AddToRecentlyDeleted(existing, Den.GetNote(destBox, slot));
            Den.SetSlot(destBox, slot, pk.Clone());
            if (note is not null) Den.SetNote(destBox, slot, note);
            Den.SetTimestamp(destBox, slot, ts ?? DateTime.Now);
            slot++;
            pasted++;
        }
        RefreshDenGrid();
        NotifyArrangeWindows();
        SetStatus($"Pasted {pasted} Pokémon.");
    }

    private void DeleteSelectedDen()
    {
        PushUndo();
        foreach (var flat in selectedDenSlots)
        {
            if (_isSearchActive)
            {
                if (flat >= _searchResults.Count) continue;
                var (b, s, _) = _searchResults[flat];
                ClearSlotWithCapture(b, s);
            }
            else
            {
                ClearSlotWithCapture(flat / SlotsPerDenBox, flat % SlotsPerDenBox);
            }
        }
        ClearDenSelection();
        RefreshDenGrid();
        NotifyArrangeWindows();
    }

    public static string GetSpeciesName(PKM pk)
    {
        var species = GameInfo.Strings.specieslist;
        return pk.Species < species.Length ? species[pk.Species] : $"#{pk.Species}";
    }

    // ========================================================================
    //  DRAG & DROP
    // ========================================================================

    private Point _mouseDownPos;
    private PictureBox? _dragSource;
    private const int DragThreshold = 6;

    private void SlotMouseDown(PictureBox? pb, MouseEventArgs e, bool isDen, int slot)
    {
        if (pb is null || e.Button != MouseButtons.Left) return;
        _mouseDownPos = e.Location;
        _dragSource = pb;
    }

    private void SlotMouseMove(PictureBox? pb, MouseEventArgs e, bool isDen, int slot)
    {
        if (pb is null || _dragSource != pb || e.Button != MouseButtons.Left) return;
        if (Math.Abs(e.X - _mouseDownPos.X) < DragThreshold && Math.Abs(e.Y - _mouseDownPos.Y) < DragThreshold) return;
        var pk = GetPKMForSlot(isDen, slot);
        if (pk is not { Species: > 0 }) { _dragSource = null; return; }
        string prefix = isDen ? "DEN" : "SAVE";
        int box = isDen ? CurrentDenBox : CurrentSaveBox;
        pb.DoDragDrop($"{prefix}:{box}:{slot}", DragDropEffects.Move | DragDropEffects.Copy);
        _dragSource = null;
    }

    private void SlotDragDrop(PictureBox? pb, DragEventArgs e, bool destIsDen, int destSlot)
    {
        if (pb is null) return;

        // File drops into Den
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && destIsDen)
        {
            int box = CurrentDenBox; int count = 0;
            if (files.Length == 1 && Directory.Exists(files[0]))
            {
                count = Den.ImportFromFolder(files[0], SAV ?? CreateBlankSave(), box);
            }
            else
            {
                int slot = destSlot;
                foreach (var file in files)
                {
                    if (!File.Exists(file)) continue;
                    var obj = FileUtil.GetSupportedFile(file, SAV);
                    PKM? pk = obj switch { PKM p when p.Species > 0 => p, MysteryGift { IsEntity: true } mg => mg.ConvertToPKM(SAV ?? CreateBlankSave()), _ => null };
                    if (pk is null) continue;
                    while (slot < SlotsPerDenBox && Den.GetSlot(box, slot) is { Species: > 0 }) slot++;
                    if (slot >= SlotsPerDenBox) break;
                    Den.SetSlot(box, slot, pk);
                    Den.SetTimestamp(box, slot, DateTime.Now);
                    slot++; count++;
                }
            }
            RefreshDenGrid();
            SetStatus($"Imported {count} Pokémon from dropped files.");
            return;
        }

        string? dragData = e.Data?.GetData(DataFormats.Text) as string
                        ?? e.Data?.GetData(DataFormats.UnicodeText) as string
                        ?? e.Data?.GetData(DataFormats.StringFormat) as string;
        if (string.IsNullOrEmpty(dragData)) { SetStatus("Drop failed: no data payload."); return; }
        var parts = dragData.Split(':');
        if (parts.Length != 3) { SetStatus($"Drop failed: unexpected payload '{dragData}'."); return; }
        bool srcIsDen = parts[0] is "DEN" or "ARRANGE";
        bool srcIsParty = parts[0] == "PARTY";
        bool srcIsRecent = parts[0] == "RECENT";
        if (!int.TryParse(parts[1], out int srcBox) || !int.TryParse(parts[2], out int srcSlot))
        {
            SetStatus($"Drop failed: bad payload format '{dragData}'.");
            return;
        }
        int destBox = destIsDen ? CurrentDenBox : CurrentSaveBox;

        try
        {
            // Restore from Recently Deleted: pull the entry at srcSlot (index in _recentlyDeleted),
            // place into the target Den slot, capture any existing occupant back into Recently Deleted,
            // then remove the source entry. Drops onto the save panel are ignored (PKDen never writes saves).
            if (srcIsRecent && destIsDen)
            {
                if (srcSlot < 0 || srcSlot >= _recentlyDeleted.Count) return;
                var entry = _recentlyDeleted[srcSlot];
                PushUndo();
                var existing = Den.GetSlot(destBox, destSlot);
                _recentlyDeleted.RemoveAt(srcSlot);  // remove BEFORE adding existing back, so indices stay correct
                if (existing is { Species: > 0 })
                    AddToRecentlyDeleted(existing, Den.GetNote(destBox, destSlot));
                Den.SetSlot(destBox, destSlot, entry.Pk);
                if (entry.Note is not null) Den.SetNote(destBox, destSlot, entry.Note);
                Den.SetTimestamp(destBox, destSlot, DateTime.Now);
                RefreshRecentlyDeletedGrid();
                RefreshDenGrid();
                SaveRecentlyDeleted();
                SetStatus($"Restored {GetSpeciesName(entry.Pk)} to \"{Den.GetBoxName(destBox)}\".");
                return;
            }

            if (srcIsDen && destIsDen) { PushUndo(); Den.SwapSlots(srcBox, srcSlot, destBox, destSlot); RefreshDenGrid(); }
            else if (srcIsParty && destIsDen && SAV is not null)
            {
                var pk = GetPartyPKM(srcSlot);
                if (pk is null || pk.Species == 0) return;
                PushUndo();
                var existing = Den.GetSlot(destBox, destSlot);
                if (existing is { Species: > 0 })
                    AddToRecentlyDeleted(existing, Den.GetNote(destBox, destSlot));
                Den.SetSlot(destBox, destSlot, pk.Clone());
                Den.SetTimestamp(destBox, destSlot, DateTime.Now);
                RefreshDenGrid();
                SetStatus($"Moved {GetSpeciesName(pk)} from party to Den.");
            }
            else if (!srcIsDen && !srcIsParty && destIsDen && SAV is not null)
            {
                var pk = SAV.GetBoxSlotAtIndex(srcBox, srcSlot);
                if (pk.Species == 0) return;
                PushUndo();
                // If the destination slot is occupied, capture the existing Pokémon into recently-deleted
                var existing = Den.GetSlot(destBox, destSlot);
                if (existing is { Species: > 0 })
                {
                    AddToRecentlyDeleted(existing, Den.GetNote(destBox, destSlot));
                }
                Den.SetSlot(destBox, destSlot, pk.Clone());
                Den.SetTimestamp(destBox, destSlot, DateTime.Now);
                RefreshDenGrid();
                SetStatus($"Moved {GetSpeciesName(pk)} from save to Den.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Drop failed: {ex.Message}");
        }
        // Save-to-Save and Den-to-Save drops are not allowed (save is read-only)
    }

    // ========================================================================
    //  ACTIONS
    // ========================================================================

    private void ImportFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Select a folder containing .pk* files" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;
        bool sub = MessageBox.Show(this, "Include files in subfolders?", "Import", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        int startBox = PromptImportBox();
        if (startBox < 0) return;

        PushUndo();
        int count = Den.ImportFromFolder(fbd.SelectedPath, SAV ?? CreateBlankSave(), startBox, subfolders: sub);
        PopulateDenBoxNames();
        if (denBoxSelector.Items.Count > 0) denBoxSelector.SelectedIndex = Math.Min(startBox, denBoxSelector.Items.Count - 1);
        RefreshDenGrid();
        SetStatus($"Imported {count} Pokémon from folder.");
        MessageBox.Show(this, $"Successfully imported {count} Pokémon from folder.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportFiles()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select Pokémon files to import",
            Filter = "Pokémon Files (*.pk* *.pb* *.pa* *.ek*)|*.pk*;*.pb*;*.pa*;*.ek*;*.bak;*.gp1|All Files|*.*",
            Multiselect = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        int startBox = PromptImportBox();
        if (startBox < 0) return;

        PushUndo();
        int count = Den.ImportFromFiles(ofd.FileNames, SAV ?? CreateBlankSave(), startBox);
        PopulateDenBoxNames();
        if (denBoxSelector.Items.Count > 0) denBoxSelector.SelectedIndex = Math.Min(startBox, denBoxSelector.Items.Count - 1);
        RefreshDenGrid();
        SetStatus($"Imported {count} Pokémon.");
        MessageBox.Show(this, $"Successfully imported {count} Pokémon.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Prompts for which box to start importing into. Returns -1 if cancelled.</summary>
    private int PromptImportBox()
    {
        using var dlg = new ImportBoxPickerDialog(Den, CurrentDenBox < 0 ? 0 : CurrentDenBox);
        if (dlg.ShowDialog(this) != DialogResult.OK) return -1;
        return dlg.SelectedBox;
    }

    private void ExportDatabaseFlat()
    {
        int count = ExportUtil.ExportDatabase(Den, this);
        if (count >= 0)
        {
            SetStatus($"Exported {count} Pokémon to folder.");
            MessageBox.Show(this, $"Successfully exported {count} Pokémon to .pk files.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ExportDatabaseByBox()
    {
        int count = ExportUtil.ExportDatabaseByBox(Den, this);
        if (count >= 0)
        {
            SetStatus($"Exported {count} Pokémon to box folders.");
            MessageBox.Show(this, $"Successfully exported {count} Pokémon to .pk files (by box).", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ExportSelectedPKFiles()
    {
        if (selectedDenSlots.Count == 0)
        {
            MessageBox.Show(this, "Select Pokémon in the Den first.\nClick to select, Ctrl+Click for multi-select.", "Export Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var fbd = new FolderBrowserDialog { Description = "Select folder to export selected Pokémon as .pk files" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        int count = 0;
        foreach (var flat in selectedDenSlots.OrderBy(x => x))
        {
            int box = flat / SlotsPerDenBox, slot = flat % SlotsPerDenBox;
            var pk = Den.GetSlot(box, slot);
            if (pk is not { Species: > 0 }) continue;
            ExportUtil.WritePKMToFolder(pk, fbd.SelectedPath, Den.GetBoxName(box), slot);
            count++;
        }
        SetStatus($"Exported {count} selected Pokémon.");
        MessageBox.Show(this, $"Successfully exported {count} Pokémon to .pk files.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Exports selected boxes' Pokémon as .pk files. User picks which boxes via a checkbox dialog.</summary>
    private void ExportCurrentBoxPKFiles()
    {
        // Show picker — defaults to checking the current box only
        int defaultBox = CurrentDenBox;
        var selected = ShowBoxMultiPicker("Export Boxes", "Select which boxes to export:",
            defaultBox >= 0 ? new HashSet<int> { defaultBox } : []);
        if (selected is null || selected.Count == 0) return;

        // Count Pokémon across selected boxes
        int totalPkm = 0;
        foreach (int b in selected) totalPkm += Den.GetBoxCount(b);
        if (totalPkm == 0)
        {
            MessageBox.Show(this, "The selected box(es) contain no Pokémon.", "Export Boxes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var fbd = new FolderBrowserDialog { Description = $"Select folder to export {totalPkm} Pokémon from {selected.Count} box(es)" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        // Ask whether to organize into subfolders per box
        bool subfolders = selected.Count > 1 && MessageBox.Show(this,
            "Organize exports into subfolders named after each box?\n\n" +
            "• Yes — create one subfolder per box\n" +
            "• No — put all files into the chosen folder",
            "Export Boxes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        int count = 0;
        foreach (int box in selected.OrderBy(x => x))
        {
            string targetFolder = fbd.SelectedPath;
            if (subfolders)
            {
                string safeName = SanitizeFolderName(Den.GetBoxName(box));
                targetFolder = Path.Combine(fbd.SelectedPath, safeName);
                Directory.CreateDirectory(targetFolder);
            }
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = Den.GetSlot(box, s);
                if (pk is not { Species: > 0 }) continue;
                ExportUtil.WritePKMToFolder(pk, targetFolder, Den.GetBoxName(box), s);
                count++;
            }
        }
        SetStatus($"Exported {count} Pokémon from {selected.Count} box(es).");
        MessageBox.Show(this, $"Successfully exported {count} Pokémon from {selected.Count} box(es) to .pk files.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Box" : name;
    }

    /// <summary>
    /// Shows a dialog letting the user pick multiple boxes via checkboxes.
    /// Returns the set of selected box indices, or null if cancelled.
    /// </summary>
    private HashSet<int>? ShowBoxMultiPicker(string title, string prompt, HashSet<int> defaultSelected)
    {
        using var dlg = new Form
        {
            Text = title,
            Size = new Size(420, 520),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            BackColor = Color.FromArgb(40, 42, 50),
        };

        var lbl = new Label
        {
            Text = prompt,
            Dock = DockStyle.Top, Height = 28,
            ForeColor = Color.White,
            Padding = new Padding(8, 6, 0, 0),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };

        var clb = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(55, 58, 68),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            CheckOnClick = true,
        };
        for (int i = 0; i < Den.BoxCount; i++)
        {
            clb.Items.Add(FormatBoxLabel(i), defaultSelected.Contains(i));
        }

        // Controls panel (Select All / None / OK / Cancel)
        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = Color.FromArgb(40, 42, 50) };
        var btnSelectAll = new Button { Text = "Select All", Location = new Point(8, 6), Width = 90, Height = 28, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(60, 80, 120) };
        var btnSelectNone = new Button { Text = "Select None", Location = new Point(104, 6), Width = 90, Height = 28, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(80, 80, 100) };
        var btnNonEmpty = new Button { Text = "Non-empty", Location = new Point(200, 6), Width = 90, Height = 28, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(60, 100, 80) };
        var btnOK = new Button { Text = "OK", Location = new Point(200, 40), Width = 90, Height = 28, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(70, 130, 180) };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(300, 40), Width = 90, Height = 28, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(120, 120, 120) };

        btnSelectAll.Click += (_, _) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true); };
        btnSelectNone.Click += (_, _) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false); };
        btnNonEmpty.Click += (_, _) =>
        {
            for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, Den.GetBoxCount(i) > 0);
        };

        btnPanel.Controls.AddRange([btnSelectAll, btnSelectNone, btnNonEmpty, btnOK, btnCancel]);
        dlg.Controls.Add(clb);
        dlg.Controls.Add(btnPanel);
        dlg.Controls.Add(lbl);
        dlg.AcceptButton = btnOK;
        dlg.CancelButton = btnCancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        var result = new HashSet<int>();
        for (int i = 0; i < clb.Items.Count; i++)
            if (clb.GetItemChecked(i)) result.Add(i);
        return result;
    }

    /// <summary>Exports every Pokémon in the Den matching the given generation to .pk files in a chosen folder.</summary>
    private void ExportByGeneration(int generation)
    {
        // First, count how many Pokémon match so we can warn if there are none
        int matchCount = 0;
        for (int b = 0; b < Den.BoxCount; b++)
        {
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = Den.GetSlot(b, s);
                if (pk is { Species: > 0 } && pk.Generation == generation) matchCount++;
            }
        }

        if (matchCount == 0)
        {
            MessageBox.Show(this, $"No Generation {generation} Pokémon found in the Den.", "Export by Generation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var fbd = new FolderBrowserDialog { Description = $"Select folder to export all Generation {generation} Pokémon" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        int exported = 0;
        int failed = 0;
        for (int b = 0; b < Den.BoxCount; b++)
        {
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = Den.GetSlot(b, s);
                if (pk is not { Species: > 0 } || pk.Generation != generation) continue;
                try
                {
                    ExportUtil.WritePKMToFolder(pk, fbd.SelectedPath, Den.GetBoxName(b), s);
                    exported++;
                }
                catch { failed++; }
            }
        }

        SetStatus($"Exported {exported} Gen {generation} Pokémon.");
        string msg = $"Successfully exported {exported} Generation {generation} Pokémon to .pk files.";
        if (failed > 0) msg += $"\n\n{failed} failed to export.";
        MessageBox.Show(this, msg, "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ========================================================================
    //  NEW FEATURES — dump save, add boxes, backgrounds
    // ========================================================================

    private void AddMoreBoxes()
    {
        using var dlg = new AddBoxesDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.BoxCount <= 0) return;
        Den.EnsureBoxCount(Den.BoxCount + dlg.BoxCount);
        PopulateDenBoxNames();
        SetStatus($"Added {dlg.BoxCount} boxes. Total: {Den.BoxCount}");
    }

    private void DumpEntireSaveToDen()
    {
        if (SAV is null || !SAV.HasBox) { MessageBox.Show(this, "No save file loaded.", "Dump Save", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        int totalPK = 0;
        for (int b = 0; b < SAV.BoxCount; b++)
            for (int s = 0; s < SAV.BoxSlotCount; s++)
                if (SAV.GetBoxSlotAtIndex(b, s).Species > 0) totalPK++;

        if (totalPK == 0) { MessageBox.Show(this, "Save file has no Pokémon in boxes.", "Dump Save", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        if (MessageBox.Show(this, $"Copy ALL {totalPK} Pokémon from every box in the save file into Den?\n\nThis will not modify the save file.", "Dump Entire Save",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        int startBox = PromptImportBox();
        if (startBox < 0) return;

        PushUndo();
        // Collect all PKM first, then use ImportPKMs for sequential fill
        var allPK = new List<PKM>();
        for (int b = 0; b < SAV.BoxCount; b++)
        {
            for (int s = 0; s < SAV.BoxSlotCount; s++)
            {
                var pk = SAV.GetBoxSlotAtIndex(b, s);
                if (pk.Species > 0) allPK.Add(pk.Clone());
            }
        }

        int copied = Den.ImportPKMs(allPK, startBox);

        PopulateDenBoxNames();
        if (denBoxSelector.Items.Count > 0) denBoxSelector.SelectedIndex = Math.Min(startBox, denBoxSelector.Items.Count - 1);
        RefreshDenGrid();
        SetStatus($"Dumped {copied} Pokémon.");
        MessageBox.Show(this, $"Successfully imported {copied} Pokémon from save file into Den.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Exports all Pokémon from every box in the currently-loaded save file as .pk files.</summary>
    private void ExportEntireSaveAsPKFiles()
    {
        if (SAV is null || !SAV.HasBox) { MessageBox.Show(this, "No save file loaded.", "Export Save", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        int totalPK = 0;
        for (int b = 0; b < SAV.BoxCount; b++)
            for (int s = 0; s < SAV.BoxSlotCount; s++)
                if (SAV.GetBoxSlotAtIndex(b, s).Species > 0) totalPK++;

        if (totalPK == 0) { MessageBox.Show(this, "Save file has no Pokémon in boxes.", "Export Save", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        using var fbd = new FolderBrowserDialog { Description = $"Select folder to export all {totalPK} Pokémon from the save file" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        // Offer to organize into per-box subfolders
        bool subfolders = MessageBox.Show(this,
            "Organize exports into subfolders named after each save box?\n\n" +
            "• Yes — create one subfolder per box\n" +
            "• No — put all files into the chosen folder",
            "Export Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        var boxNames = BoxUtil.GetBoxNames(SAV);
        int count = 0;
        int failed = 0;
        for (int b = 0; b < SAV.BoxCount; b++)
        {
            string targetFolder = fbd.SelectedPath;
            if (subfolders)
            {
                string name = b < boxNames.Length ? boxNames[b] : $"Box {b + 1}";
                targetFolder = Path.Combine(fbd.SelectedPath, SanitizeFolderName(name));
                try { Directory.CreateDirectory(targetFolder); } catch { failed++; continue; }
            }
            for (int s = 0; s < SAV.BoxSlotCount; s++)
            {
                var pk = SAV.GetBoxSlotAtIndex(b, s);
                if (pk.Species == 0) continue;
                try
                {
                    ExportUtil.WritePKMToFolder(pk, targetFolder, b < boxNames.Length ? boxNames[b] : $"Box {b + 1}", s);
                    count++;
                }
                catch { failed++; }
            }
        }
        SetStatus($"Exported {count} Pokémon from save.");
        string msg = $"Successfully exported {count} Pokémon from the save file.";
        if (failed > 0) msg += $"\n\n{failed} file(s) could not be written.";
        MessageBox.Show(this, msg, "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Imports a pcdata.bin file into the Den. Auto-detects format from file size and (for SwSh/SV) content.
    /// </summary>
    private void ImportPCDataBin()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Import pcdata.bin",
            Filter = "PC Data Binary|*.bin;pcdata.bin|All Files|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var format = PCDataBinSupport.DetectFormat(ofd.FileName, out var error);
        if (format is null)
        {
            MessageBox.Show(this, error ?? "Could not detect format.", "Import pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Read all entities and import into the Den starting at the current box
        var entities = new List<PKM>();
        try { entities.AddRange(PCDataBinSupport.ReadEntities(ofd.FileName, format)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error reading file:\n\n{ex.Message}", "Import pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (entities.Count == 0)
        {
            MessageBox.Show(this, $"Detected format: {format.Label}\n\nNo Pokémon found in the file.",
                "Import pcdata.bin", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Detected format: {format.Label}\n" +
            $"Found {entities.Count:N0} Pokémon.\n\n" +
            $"Import them into the Den starting at \"{Den.GetBoxName(CurrentDenBox)}\"?",
            "Import pcdata.bin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        PushUndo();
        int imported = Den.ImportPKMs(entities, CurrentDenBox);
        UpdateBoxSelectorWidth();
        RefreshDenGrid();
        SetStatus($"Imported {imported} Pokémon from pcdata.bin.");
        MessageBox.Show(this,
            $"Imported {imported:N0} Pokémon from {format.Label} pcdata.bin.",
            "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Imports one or more Mystery Gift files (.pgt, .pcd, .pgf, .wc6/7/8/9, .wb7/8, .wa8/9, .wr7,
    /// .wc5full/6full/7full/8full) into the Den. Each gift is materialized into a Pokémon entity
    /// via <see cref="MysteryGift.ConvertToPKM"/> using a synthesized trainer based on the gift's
    /// target game/generation, since PKDen never modifies real saves.
    ///
    /// This is how event distribution files (Project Pokémon's EventsGallery) become real Pokémon
    /// in your Den — no save file required.
    /// </summary>
    private void ImportMysteryGifts()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Import Mystery Gift Files",
            Multiselect = true,
            Filter = "Mystery Gifts (*.pgt;*.pcd;*.pgf;*.wc*;*.wb*;*.wa*;*.wr7)" +
                     "|*.pgt;*.pcd;*.pgf;*.wc4;*.wc5full;*.wc6;*.wc6full;*.wc7;*.wc7full;*.wc8;*.wc8full;*.wc9;*.wb7;*.wb7full;*.wb8;*.wa8;*.wa9;*.wr7" +
                     "|All Files|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        if (ofd.FileNames.Length == 0) return;

        // Snapshot for undo before any changes
        PushUndo();

        var imported = new List<PKM>();
        var failures = new List<(string File, string Reason)>();
        foreach (var path in ofd.FileNames)
        {
            try
            {
                var data = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path);

                // First try the strict ext-aware overload, then fall back to size-based detection
                // (which can disambiguate WC6 vs WC7 by year, WA8 vs WC9 vs WA9 by content, etc.)
                var mg = MysteryGift.GetMysteryGift(data, ext) ?? MysteryGift.GetMysteryGift(data);
                if (mg is null)
                {
                    failures.Add((Path.GetFileName(path), "Unrecognized format or invalid size"));
                    continue;
                }

                // Synthesize a trainer matching the gift's target game so the materialized PKM
                // gets a sensible OT/TID. Without a real save loaded we can't transfer ownership;
                // PKHeX's SimpleTrainerInfo gives a clean default ("PKHeX", TID 12345).
                var trainer = new SimpleTrainerInfo(mg.Version);
                var pk = mg.ConvertToPKM(trainer);
                if (pk is null || pk.Species == 0)
                {
                    failures.Add((Path.GetFileName(path), "Conversion produced no Pokémon"));
                    continue;
                }
                imported.Add(pk);
            }
            catch (Exception ex)
            {
                failures.Add((Path.GetFileName(path), ex.Message));
            }
        }

        if (imported.Count == 0)
        {
            string msg = "No Mystery Gifts could be imported.";
            if (failures.Count > 0)
                msg += "\n\n" + string.Join("\n", failures.Select(f => $"• {f.File}: {f.Reason}"));
            MessageBox.Show(this, msg, "Import Mystery Gifts", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Drop into the Den starting at the current box, using the same sequential-fill
        // logic the regular folder/file import uses (skips occupied slots, advances boxes as needed).
        int placed = Den.ImportPKMs(imported, CurrentDenBox);
        UpdateBoxSelectorWidth();
        RefreshDenGrid();

        string summary = $"Imported {placed:N0} Pokémon from {imported.Count:N0} Mystery Gift file(s).";
        if (failures.Count > 0)
            summary += $"\n\n{failures.Count} file(s) could not be processed:\n" +
                       string.Join("\n", failures.Take(10).Select(f => $"• {f.File}: {f.Reason}")) +
                       (failures.Count > 10 ? $"\n…and {failures.Count - 10} more" : "");
        SetStatus($"Imported {placed} Mystery Gift Pokémon.");
        MessageBox.Show(this, summary, "Mystery Gifts Imported",
            MessageBoxButtons.OK, failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Exports selected Den boxes as a pcdata.bin file in a user-chosen target format.
    /// </summary>
    private void ExportPCDataBin()
    {
        // Gather all known formats for the user to choose from
        var formats = PCDataBinSupport.KnownFormats;

        // Build a dropdown picker dialog
        using var dlg = new Form
        {
            Text = "Export Den as pcdata.bin",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            Width = 460, Height = 200,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(28, 30, 36),
            ForeColor = Color.White,
        };
        var lbl = new Label
        {
            Text = "Choose target format. The Den's Pokémon will be converted to match.",
            Location = new Point(12, 12), Width = 420, Height = 36,
        };
        var combo = new ComboBox
        {
            Location = new Point(12, 56), Width = 420,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var f in formats) combo.Items.Add(f.Label);
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(260, 110), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(350, 110), Width = 80 };
        dlg.Controls.AddRange([lbl, combo, ok, cancel]);
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var chosen = formats[combo.SelectedIndex];

        // Pick which Den boxes to include
        var picked = ShowBoxMultiPicker("Export pcdata.bin", "Select which Dens to include:",
            CurrentDenBox >= 0 ? new HashSet<int> { CurrentDenBox } : []);
        if (picked is null || picked.Count == 0) return;

        // Gather Pokémon from those boxes in order
        var pks = new List<PKM>();
        foreach (int box in picked.OrderBy(b => b))
        {
            for (int slot = 0; slot < DenStorageManager.SlotsPerBox; slot++)
            {
                var pk = Den.GetSlot(box, slot);
                if (pk is not null && pk.Species > 0) pks.Add(pk);
            }
        }

        if (pks.Count == 0)
        {
            MessageBox.Show(this, "Selected Dens contain no Pokémon.", "Export pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (pks.Count > chosen.SlotCount)
        {
            var resp = MessageBox.Show(this,
                $"Selected {pks.Count:N0} Pokémon, but {chosen.Label} pcdata.bin holds only {chosen.SlotCount:N0}.\n\n" +
                $"The first {chosen.SlotCount:N0} will be exported, the rest skipped.\n\nContinue?",
                "Export pcdata.bin", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (resp != DialogResult.OK) return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Save pcdata.bin",
            Filter = "PC Data Binary|*.bin",
            FileName = "pcdata.bin",
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var (written, skipped) = PCDataBinSupport.WriteEntities(sfd.FileName, pks, chosen);
            SetStatus($"Exported {written} Pokémon as {chosen.Label} pcdata.bin.");
            string msg = $"Wrote {written:N0} Pokémon to pcdata.bin as {chosen.Label}.";
            if (skipped > 0) msg += $"\n\n{skipped:N0} Pokémon could not be converted to {chosen.PKMType.Name} and were skipped.";
            MessageBox.Show(this, msg, "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error writing file:\n\n{ex.Message}", "Export pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Exports the currently-loaded save file's PC region as a pcdata.bin (one-liner via PKHeX's GetPCBinary).
    /// </summary>
    private void ExportSaveAsPCDataBin()
    {
        if (SAV is null || !SAV.HasBox)
        {
            MessageBox.Show(this, "No save file loaded.", "Export Save as pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Save as pcdata.bin",
            Filter = "PC Data Binary|*.bin",
            FileName = "pcdata.bin",
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            byte[] data = SAV.GetPCBinary();
            File.WriteAllBytes(sfd.FileName, data);
            SetStatus($"Exported save as pcdata.bin ({data.Length:N0} bytes).");
            MessageBox.Show(this,
                $"Wrote save's PC region as pcdata.bin.\n\nSize: {data.Length:N0} bytes ({SAV.BoxCount} boxes × {SAV.BoxSlotCount} slots).",
                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error writing file:\n\n{ex.Message}", "Export Save as pcdata.bin",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBackground(bool global)
    {
        using var ofd = new OpenFileDialog
        {
            Title = global ? "Set Background for All Boxes" : $"Set Background for \"{Den.GetBoxName(CurrentDenBox)}\"",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        // Copy the image to a backgrounds folder next to the exe so it persists
        var bgDir = Path.Combine(AppContext.BaseDirectory, "backgrounds");
        Directory.CreateDirectory(bgDir);
        var destName = $"bg_{(global ? "global" : $"box{CurrentDenBox}")}_{Path.GetFileName(ofd.FileName)}";
        var destPath = Path.Combine(bgDir, destName);
        try { File.Copy(ofd.FileName, destPath, true); } catch { destPath = ofd.FileName; }

        if (global)
        {
            Den.SetGlobalBackground(destPath);
            SetStatus("Background set for all boxes.");
        }
        else
        {
            Den.SetBoxBackground(CurrentDenBox, destPath);
            SetStatus($"Background set for \"{Den.GetBoxName(CurrentDenBox)}\".");
        }
        RefreshDenGrid();
    }

    // ========================================================================
    //  BACKGROUND MUSIC
    // ========================================================================

    /// <summary>
    /// File picker → start playing.  Path + volume are saved into the den so they
    /// travel with the den (shared between machines just by copying the .den file).
    /// </summary>
    private void PickBackgroundMusic()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Choose background music",
            Filter = "Audio files (*.mp3;*.wav;*.flac)|*.mp3;*.wav;*.flac|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        Den.SetMusicPath(ofd.FileName);
        StartMusicIfConfigured(showStatusOnSuccess: true);
    }

    /// <summary>Stops any currently playing track and (optionally) clears the saved setting.</summary>
    private void StopBackgroundMusic(bool clearSetting)
    {
        _musicPlayer?.Stop();
        if (clearSetting)
        {
            Den.SetMusicPath(null);
            SetStatus("Background music cleared.");
        }
        else
        {
            SetStatus("Background music stopped.");
        }
    }

    /// <summary>
    /// Modal volume slider 0–100.  Changes apply LIVE while dragging so the user can
    /// hear the result immediately, but the saved value only commits when they hit OK.
    /// Cancel reverts to the previous volume.
    /// </summary>
    private void ShowVolumeSlider()
    {
        int original = Den.GetMusicVolume();
        using var dlg = new Form
        {
            Text = "Music Volume",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(320, 110),
        };
        var label = new Label { Text = $"Volume: {original}", Location = new Point(12, 12), AutoSize = true };
        var bar = new TrackBar
        {
            Minimum = 0, Maximum = 100, TickFrequency = 10,
            Value = original,
            Location = new Point(12, 32), Width = 296,
        };
        bar.ValueChanged += (_, _) =>
        {
            label.Text = $"Volume: {bar.Value}";
            // Live preview — update player volume but don't persist yet.
            _musicPlayer?.SetVolume(bar.Value);
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(150, 75), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(232, 75), Width = 75 };
        dlg.Controls.AddRange(new Control[] { label, bar, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        var result = dlg.ShowDialog(this);
        if (result == DialogResult.OK)
        {
            Den.SetMusicVolume(bar.Value);
            SetStatus($"Music volume: {bar.Value}.");
        }
        else
        {
            // Revert the live-preview change back to original.
            _musicPlayer?.SetVolume(original);
        }
    }

    /// <summary>
    /// Tries to start the music player using the path + volume saved in the den.
    /// Called after den load AND after the user picks a track.  No-ops gracefully if
    /// there's no path set, the file is gone, or the format isn't supported.
    /// </summary>
    private void StartMusicIfConfigured(bool showStatusOnSuccess = false)
    {
        var path = Den.GetMusicPath();
        if (string.IsNullOrEmpty(path)) return;

        _musicPlayer ??= new MusicPlayer();
        _musicPlayer.SetVolume(Den.GetMusicVolume());
        if (_musicPlayer.Play(path, out var error))
        {
            if (showStatusOnSuccess)
                SetStatus($"Now playing: {Path.GetFileName(path)}");
        }
        else
        {
            // Common: file moved/deleted, or FLAC on a Windows version that can't decode it.
            // Surface in the status bar — non-blocking, no popup, since music is auxiliary.
            SetStatus($"Couldn't play music: {error}");
        }
    }

    // ========================================================================
    //  SEARCH / FILTER
    // ========================================================================

    private void ApplySearchFilter()
    {
        string nameFilter = _searchBox.Text.Trim();
        string otFilter = _filterOT.Text.Trim();
        bool hasFilter = !string.IsNullOrEmpty(nameFilter) || !string.IsNullOrEmpty(otFilter);

        if (!hasFilter)
        {
            if (_isSearchActive)
            {
                _isSearchActive = false;
                _searchResults.Clear();
                denBoxSelector.Enabled = true;
                denPrev.Enabled = true;
                denNext.Enabled = true;
                RefreshDenGrid();
            }
            return;
        }

        _isSearchActive = true;
        _searchResults.Clear();
        var species = GameInfo.Strings.specieslist;

        // Collect ALL matches across ALL boxes
        for (int b = 0; b < Den.BoxCount; b++)
        {
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = Den.GetSlot(b, s);
                if (pk is { Species: > 0 } && MatchesFilter(pk, nameFilter, otFilter, species))
                    _searchResults.Add((b, s, pk));
            }
        }

        // Disable box nav while in search mode
        denBoxSelector.Enabled = false;
        denPrev.Enabled = false;
        denNext.Enabled = false;

        DisplaySearchResults();
    }

    private void DisplaySearchResults()
    {
        selectedDenSlots.Clear();
        var slotBase = GetSlotBaseColor();
        for (int i = 0; i < denSlots.Count; i++)
        {
            // Set BackColor BEFORE UpdateSlotImage — see RefreshDenGrid for the explanation.
            // Search results span multiple boxes, so we use the global slot fill (no per-box).
            denSlots[i].BackColor = slotBase;
            if (i < _searchResults.Count)
                UpdateSlotImage(denSlots[i], _searchResults[i].Pk, true);
            else
                denSlots[i].Image = null;
            MarkSlotSelected(denSlots[i], false);
            denSlots[i].Visible = true;
        }

        int total = _searchResults.Count;
        int shown = Math.Min(total, denSlots.Count);
        if (total == 0)
            denCountLabel.Text = "No matches found";
        else if (total > denSlots.Count)
            denCountLabel.Text = $"Showing {shown} of {total} — refine search to see more";
        else
            denCountLabel.Text = $"Found {total} result{(total == 1 ? "" : "s")}";
    }

    private static bool MatchesFilter(PKM pk, string nameFilter, string otFilter, string[] species)
    {
        if (!string.IsNullOrEmpty(nameFilter))
        {
            string name = (uint)pk.Species < species.Length ? species[pk.Species] : "";
            if (!name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrEmpty(otFilter))
        {
            try { if (!pk.OriginalTrainerName.Contains(otFilter, StringComparison.OrdinalIgnoreCase)) return false; }
            catch { return false; }
        }
        return true;
    }

    private void ClearSearchFilter()
    {
        _searchBox.Text = "";
        _filterOT.Text = "";
        RefreshDenGrid();
    }

    /// <summary>Right-click → Send to Box… on a single Pokémon.</summary>
    private void SendSingleToBox(int srcBox, int srcSlot)
    {
        var pk = Den.GetSlot(srcBox, srcSlot);
        if (pk is not { Species: > 0 }) return;

        using var dlg = new SendToBoxDialog(Den, srcBox, pkCount: 1);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int destBox = dlg.SelectedBox;
        if (destBox < 0 || destBox == srcBox) return;

        PushUndo();
        int destSlot = FindNextEmptySlotInBox(destBox);
        if (destSlot < 0)
        {
            MessageBox.Show(this, $"Destination box \"{Den.GetBoxName(destBox)}\" is full.", "Send to Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Den.SetSlot(destBox, destSlot, pk);
        var note = Den.GetNote(srcBox, srcSlot);
        var ts = Den.GetTimestamp(srcBox, srcSlot);
        if (note is not null) Den.SetNote(destBox, destSlot, note);
        Den.SetTimestamp(destBox, destSlot, ts ?? DateTime.Now);
        Den.ClearSlot(srcBox, srcSlot);

        ClearDenSelection();
        RefreshDenGrid();
        NotifyArrangeWindows();
        SetStatus($"Sent {GetSpeciesName(pk)} to \"{Den.GetBoxName(destBox)}\".");
    }

    /// <summary>Right-click → Send N Selected to Box… (moves all currently-selected Pokémon).</summary>
    private void SendSelectedToBox()
    {
        if (selectedDenSlots.Count == 0) return;

        // Collect source locations first — critical because we're mutating storage while iterating
        var sources = new List<(int Box, int Slot)>();
        foreach (var flat in selectedDenSlots.OrderBy(x => x))
        {
            if (_isSearchActive)
            {
                if (flat < _searchResults.Count) sources.Add((_searchResults[flat].Box, _searchResults[flat].Slot));
            }
            else
            {
                sources.Add((flat / SlotsPerDenBox, flat % SlotsPerDenBox));
            }
        }

        if (sources.Count == 0) return;

        // Use current box as the "source" just for display; actual sources are per-Pokémon
        int referenceBox = _isSearchActive ? -1 : CurrentDenBox;
        using var dlg = new SendToBoxDialog(Den, referenceBox, pkCount: sources.Count);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int destBox = dlg.SelectedBox;
        if (destBox < 0) return;

        PushUndo();
        int moved = 0;
        int skipped = 0;
        foreach (var (sBox, sSlot) in sources)
        {
            var pk = Den.GetSlot(sBox, sSlot);
            if (pk is not { Species: > 0 }) continue;
            if (sBox == destBox) { skipped++; continue; } // already there

            int destSlot = FindNextEmptySlotInBox(destBox);
            if (destSlot < 0)
            {
                MessageBox.Show(this, $"Destination box \"{Den.GetBoxName(destBox)}\" is full.\nMoved {moved} of {sources.Count} Pokémon.", "Send to Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            }

            Den.SetSlot(destBox, destSlot, pk);
            var note = Den.GetNote(sBox, sSlot);
            var ts = Den.GetTimestamp(sBox, sSlot);
            if (note is not null) Den.SetNote(destBox, destSlot, note);
            Den.SetTimestamp(destBox, destSlot, ts ?? DateTime.Now);
            Den.ClearSlot(sBox, sSlot);
            moved++;
        }

        ClearDenSelection();
        RefreshDenGrid();
        NotifyArrangeWindows();
        string statusSuffix = skipped > 0 ? $" ({skipped} already in destination)" : "";
        SetStatus($"Sent {moved} Pokémon to \"{Den.GetBoxName(destBox)}\".{statusSuffix}");
    }

    private int FindNextEmptySlotInBox(int box)
    {
        for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
        {
            if (Den.GetSlot(box, s) is null or { Species: 0 }) return s;
        }
        return -1;
    }

    private void SendBoxToAnotherBox()
    {
        int srcBox = CurrentDenBox;
        if (srcBox < 0) return;
        if (Den.GetBoxCount(srcBox) == 0)
        {
            MessageBox.Show(this, "Current box is empty.", "Send Box", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Show a picker for the destination box
        using var dlg = new SendBoxDialog(Den, srcBox);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int destBox = dlg.SelectedBox;
        if (destBox == srcBox) return;

        PushUndo();
        int moved = 0;
        for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
        {
            var pk = Den.GetSlot(srcBox, s);
            if (pk is not { Species: > 0 }) continue;

            // Find empty slot in dest box
            int destSlot = -1;
            for (int ds = 0; ds < DenStorageManager.SlotsPerBox; ds++)
            {
                if (Den.GetSlot(destBox, ds) is null or { Species: 0 }) { destSlot = ds; break; }
            }
            if (destSlot < 0) { MessageBox.Show(this, $"Destination box \"{Den.GetBoxName(destBox)}\" is full.\nMoved {moved} Pokémon.", "Send Box", MessageBoxButtons.OK, MessageBoxIcon.Warning); break; }

            // Move with metadata
            Den.SetSlot(destBox, destSlot, pk);
            var note = Den.GetNote(srcBox, s);
            var ts = Den.GetTimestamp(srcBox, s);
            if (note is not null) Den.SetNote(destBox, destSlot, note);
            if (ts.HasValue) Den.SetTimestamp(destBox, destSlot, ts.Value);
            Den.ClearSlot(srcBox, s);
            moved++;
        }

        RefreshDenGrid();
        NotifyArrangeWindows();
        SetStatus($"Moved {moved} Pokémon from \"{Den.GetBoxName(srcBox)}\" to \"{Den.GetBoxName(destBox)}\".");
    }

    private void DeleteCurrentBox()
    {
        int box = CurrentDenBox;
        if (box < 0) return;
        if (Den.BoxCount <= 1)
        {
            MessageBox.Show(this, "Cannot delete the last box.", "Delete Box", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int count = Den.GetBoxCount(box);
        string msg = count > 0
            ? $"Delete \"{Den.GetBoxName(box)}\" and its {count} Pokémon?\nThis cannot be undone."
            : $"Delete empty box \"{Den.GetBoxName(box)}\"?";

        if (MessageBox.Show(this, msg, "Delete Box", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        PushUndo();
        Den.DeleteBox(box);
        PopulateDenBoxNames();
        int newBox = Math.Min(box, Den.BoxCount - 1);
        if (denBoxSelector.Items.Count > 0)
            denBoxSelector.SelectedIndex = newBox;
        RefreshDenGrid();
        NotifyArrangeWindows();
        SetStatus($"Deleted box. {Den.BoxCount} boxes remaining.");
    }

    private void ClearDenStorage()
    {
        using var dlg = new ConfirmDeleteDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        PushUndo(); Den.ClearAllBoxes();
        ClearDenSelection();
        RefreshDenGrid();
        SetStatus("Den Storage cleared.");
    }

    private void BtnCopySelectedToDen_Click(object? sender, EventArgs e)
    {
        if (SAV is null) return;
        if (selectedSaveSlots.Count == 0) { MessageBox.Show(this, "Select Pokémon in the Save box first.\nClick to select, Ctrl+Click for multi-select.", "Move to Den", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        PushUndo();
        int copied = 0;
        foreach (var flat in selectedSaveSlots.OrderBy(x => x))
        {
            int srcBox = flat / SAV.BoxSlotCount, srcSlot = flat % SAV.BoxSlotCount;
            var pk = SAV.GetBoxSlotAtIndex(srcBox, srcSlot);
            if (pk.Species == 0) continue;
            var (db, ds) = Den.FindNextEmpty(CurrentDenBox);
            if (db < 0) { Den.EnsureBoxCount(Den.BoxCount + 5); PopulateDenBoxNames(); (db, ds) = Den.FindNextEmpty(CurrentDenBox); if (db < 0) break; }
            Den.SetSlot(db, ds, pk.Clone());
            Den.SetTimestamp(db, ds, DateTime.Now);
            copied++;
        }
        ClearSaveSelection(); RefreshDenGrid(); RefreshSaveGrid();
        SetStatus($"Copied {copied} Pokémon from Save to Den.");
    }

    private void BtnCopyAllToDen_Click(object? sender, EventArgs e)
    {
        if (SAV is null || !SAV.HasBox) return;
        int box = CurrentSaveBox;
        if (box < 0) return;
        int available = 0;
        for (int s = 0; s < SAV.BoxSlotCount; s++) if (SAV.GetBoxSlotAtIndex(box, s).Species > 0) available++;
        if (available == 0) { MessageBox.Show(this, "This Save box is empty.", "Copy All", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show(this, $"Copy all {available} Pokémon from \"{saveBoxSelector.Text}\" to Den?", "Copy All to Den", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        PushUndo();
        int copied = 0;
        for (int s = 0; s < SAV.BoxSlotCount; s++)
        {
            var pk = SAV.GetBoxSlotAtIndex(box, s);
            if (pk.Species == 0) continue;
            var (db, ds) = Den.FindNextEmpty(CurrentDenBox < 0 ? 0 : CurrentDenBox);
            if (db < 0) { Den.EnsureBoxCount(Den.BoxCount + 5); PopulateDenBoxNames(); (db, ds) = Den.FindNextEmpty(0); if (db < 0) break; }
            Den.SetSlot(db, ds, pk.Clone());
            Den.SetTimestamp(db, ds, DateTime.Now);
            copied++;
        }
        PopulateDenBoxNames();
        if (denBoxSelector.Items.Count > 0 && denBoxSelector.SelectedIndex < 0) denBoxSelector.SelectedIndex = 0;
        RefreshDenGrid();
        SetStatus($"Copied {copied} Pokémon from Save box to Den.");
        MessageBox.Show(this, $"Successfully copied {copied} Pokémon to Den.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void CopySavePKMToDen(int srcBox, int srcSlot)
    {
        if (SAV is null) return;
        var pk = SAV.GetBoxSlotAtIndex(srcBox, srcSlot);
        if (pk.Species == 0) return;
        var (db, ds) = Den.FindNextEmpty(CurrentDenBox);
        if (db < 0) { Den.EnsureBoxCount(Den.BoxCount + 5); PopulateDenBoxNames(); (db, ds) = Den.FindNextEmpty(CurrentDenBox); }
        if (db < 0) return;
        Den.SetSlot(db, ds, pk.Clone());
        Den.SetTimestamp(db, ds, DateTime.Now);
        RefreshDenGrid();
        SetStatus("Copied Pokémon to Den.");
    }

    // ========================================================================
    //  SAVE / LOAD DEN
    // ========================================================================

    private void SaveDenStorage(string path)
    {
        try
        {
            Den.SaveToFile(path); Den.MarkClean();
            SetStatus($"Den saved to {Path.GetFileName(path)}.");
            MessageBox.Show(this, $"Den saved successfully to:\n{path}", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Failed to save: {ex.Message}", "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private bool _skipSaveOnClose;

    /// <summary>Prompts the user to confirm, then closes the app without saving the Den.</summary>
    private void ExitWithoutSaving()
    {
        string message = Den.IsDirty
            ? "You have unsaved changes to your Den.\n\nAre you sure you want to exit WITHOUT saving?\nAny changes made since the last save will be lost."
            : "Exit PKDen?";
        var result = MessageBox.Show(this, message, "Exit Without Saving",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        _skipSaveOnClose = true;
        Close();
    }

    private void ShowHelpDialog()
    {
        using var dlg = new Form
        {
            Text = "About PKDen",
            Size = new Size(720, 600),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            BackColor = Color.FromArgb(40, 42, 50),
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(40, 42, 50),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10),
            DetectUrls = true,
        };
        rtb.LinkClicked += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); }
            catch { /* ignore failures — the user just can't open links */ }
        };

        var closeBtn = new Button
        {
            Text = "Close", Dock = DockStyle.Bottom, Height = 36,
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 100, 160),
            DialogResult = DialogResult.OK,
        };

        dlg.Controls.Add(rtb);
        dlg.Controls.Add(closeBtn);
        dlg.AcceptButton = closeBtn;

        PopulateHelpText(rtb);
        dlg.ShowDialog(this);
    }

    private static void PopulateHelpText(RichTextBox rtb)
    {
        rtb.SuspendLayout();
        rtb.Clear();

        void Header(string text)
        {
            rtb.SelectionFont = new Font("Segoe UI", 13, FontStyle.Bold);
            rtb.SelectionColor = Color.FromArgb(120, 180, 255);
            rtb.AppendText(text + Environment.NewLine);
            rtb.SelectionFont = new Font("Segoe UI", 10);
            rtb.SelectionColor = Color.White;
        }
        void SubHeader(string text)
        {
            rtb.SelectionFont = new Font("Segoe UI", 11, FontStyle.Bold);
            rtb.SelectionColor = Color.FromArgb(200, 200, 220);
            rtb.AppendText(text + Environment.NewLine);
            rtb.SelectionFont = new Font("Segoe UI", 10);
            rtb.SelectionColor = Color.White;
        }
        void Text(string text)
        {
            rtb.AppendText(text + Environment.NewLine);
        }
        void Warning(string text)
        {
            rtb.SelectionColor = Color.FromArgb(255, 180, 100);
            rtb.AppendText(text + Environment.NewLine);
            rtb.SelectionColor = Color.White;
        }
        void Blank() => rtb.AppendText(Environment.NewLine);

        Header("PKDen — Overview");
        Text("PKDen is a .pk file viewer, organizer, and backup tool for Pokémon data across all main-series games (Generations 1 through 9). Boxes in PKDen are called \"Dens\" and hold up to 30 Pokémon each.");
        Blank();

        Warning("IMPORTANT: PKDen is NOT a save editor.");
        Text("Your Pokémon game save files are opened in READ-ONLY mode. PKDen cannot and will not modify, overwrite, or inject data into your original save files. Save files are exclusively a source — you can copy Pokémon FROM a save into your Den, never the other way.");
        Text("To edit Pokémon or save files directly, use PKHeX or another dedicated save editor.");
        Blank();

        Header("What PKDen CAN Do");
        Text("• Organize Pokémon across 50+ custom-named Dens (30 slots each)");
        Text("• Copy Pokémon from any save file into PKDen for long-term storage");
        Text("• Import .pk files (all formats pk1 through pk9) from folders or individual files");
        Text("• Export Pokémon as PKHeX-compatible .pk files (filenames match PKHeX's naming scheme)");
        Text("• Export by generation — pull every pk1 from PKDen in one action, for example");
        Text("• Search across all Dens by species name or Original Trainer");
        Text("• Sort Dens by species, level, IVs, shiny status, OT, and more");
        Text("• Add personal notes and transfer timestamps to each Pokémon");
        Text("• Set custom background images per Den or globally");
        Text("• View detailed summaries: nature, ability, IVs, moves, held item, origin game");
        Text("• Undo any change (up to 20 levels)");
        Text("• Recover accidentally deleted Pokémon from the Recently Deleted panel");
        Blank();

        Header("What PKDen CANNOT Do");
        Text("• Modify, edit, or inject Pokémon into save files");
        Text("• Edit any attribute of a Pokémon (moves, IVs, stats, etc.)");
        Text("• Check legality or flag illegal Pokémon");
        Text("• Generate or breed new Pokémon");
        Text("• Modify trainer info, Pokédex, items, money, or anything else in a save file");
        Text("For those features, use PKHeX directly.");
        Blank();

        Header("Typical Workflow");
        SubHeader("1. Back up your Pokémon collection");
        Text("Open a save file (File → Open Save) and use \"Move All Pokémon from Save File to Den\" to copy your entire collection into PKDen. Your save file is never touched — only read.");
        Blank();
        SubHeader("2. Organize and curate");
        Text("Create custom Dens (rename them to \"Shinies\", \"Competitive\", \"Living Dex\", etc.), search for specific Pokémon, sort to find duplicates, add notes to remember breeding plans or trade histories.");
        Blank();
        SubHeader("3. Export for use elsewhere");
        Text("Export individual Pokémon, a whole Den, your entire collection, or just one generation's worth as .pk files. These are compatible with PKHeX and any other tool that reads pk files.");
        Blank();

        Header("Using PKDen Alongside PKHeX");
        Text("PKDen and PKHeX complement each other:");
        Text("• PKDen is a library — large-scale storage and organization");
        Text("• PKHeX is a workshop — editing, injection, legality checking, breeding");
        Text("Common flow: export from PKDen → edit in PKHeX → import back to PKDen, or import directly to your game save.");
        Blank();

        Header("Data Storage");
        Text("PKDen stores everything in two files next to the executable:");
        Text("• PKDen.den — your Pokémon collection (custom binary format)");
        Text("• PKDen.settings — your view preferences (sprite size, labels, etc.)");
        Text("A \"backgrounds\" folder is created if you set custom Den backgrounds.");
        Text("No registry entries, no hidden AppData files — the app is fully portable.");
        Blank();

        Header("Credits");
        Text("PKDen uses PKHeX as its backend for reading, writing, and rendering Pokémon data. PKHeX is a mature, trusted tool maintained by kwsch and a community of contributors.");
        Blank();
        Text("• PKHeX Version: 26.04.11");
        Text("• PKHeX Repository: https://github.com/kwsch/PKHeX");
        Text("• PKHeX License: GPL-3.0");
        Blank();
        Text("All sprite assets, species data, move/ability strings, entity parsing, and save-file format handling come from PKHeX. Huge thanks to the PKHeX team for making such a robust library available to the community.");
        Blank();
        Text("PKDen Version: 0.1.3");
        Blank();

        Header("Disclaimer");
        Text("PKDen is a fan-made tool. Pokémon and all related trademarks are property of Nintendo, Game Freak, and The Pokémon Company. This application is not affiliated with or endorsed by any of these entities.");
        Blank();

        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
        rtb.ResumeLayout();
    }

    private void SaveDenAs()
    {
        using var sfd = new SaveFileDialog { Title = "Save Den As", Filter = "Den Files|*.den|All Files|*.*", FileName = "PKDen.den" };
        if (sfd.ShowDialog(this) == DialogResult.OK) SaveDenStorage(sfd.FileName);
    }

    private void LoadDenFile()
    {
        using var ofd = new OpenFileDialog { Title = "Load Den File", Filter = "Den Files|*.den|Home Storage|*.bin|All Files|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        if (Den.LoadFromFile(ofd.FileName, SAV ?? CreateBlankSave()))
        {
            // The new den brings new (or zero) custom-sprite mappings — drop everything
            // the cache thinks it knows so subsequent renders re-decode from the loaded bytes.
            CustomSpriteCache.ClearAll();
            PopulateDenBoxNames();
            if (denBoxSelector.Items.Count > 0) denBoxSelector.SelectedIndex = 0;
            RefreshDenGrid();
            Den.MarkClean();
            SetStatus($"Loaded Den from {Path.GetFileName(ofd.FileName)}.");
            // Music settings may have changed — stop any current track and reload from the new den.
            _musicPlayer?.Stop();
            StartMusicIfConfigured();
        }
        else MessageBox.Show(this, "Failed to load file.", "Load Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void AutoLoadDen()
    {
        var path = DenLoadPath;
        if (!DenStorageManager.SaveFileExists(path)) return;
        if (Den.LoadFromFile(path, CreateBlankSave()))
        {
            // Same reason as in LoadDenFile — fresh den, clear stale cache.
            CustomSpriteCache.ClearAll();
            Den.MarkClean();
            SetStatus($"Den loaded ({Den.GetTotalCount()} Pokémon).");
            // Background music: if the den has a saved track, start playing.
            // Errors here are non-fatal (file moved, unsupported format) and are
            // surfaced in the status bar by StartMusicIfConfigured.
            StartMusicIfConfigured();
        }
    }

    /// <summary>Path to the recently-deleted persistence file (next to PKDen.exe).</summary>
    private static string RecentlyDeletedPath => Path.Combine(AppContext.BaseDirectory, "PKDen.recent");

    /// <summary>
    /// Persists the recently-deleted list to disk so deleted Pokémon survive restart.
    /// File format: magic "PRCT" | version u8 | count u32 | entries.
    /// Each entry: pkmDataLen u32 | pkmDecryptedPartyBytes | noteLen u16 | noteBytes | timestamp i64.
    /// We use EntityFormat.GetFromBytes on load to auto-detect the PKM type from the bytes.
    /// </summary>
    private void SaveRecentlyDeleted()
    {
        try
        {
            using var fs = new FileStream(RecentlyDeletedPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("PRCT"));  // magic
            bw.Write((byte)1);                                      // format version
            bw.Write(_recentlyDeleted.Count);
            foreach (var (pk, note, deletedAt) in _recentlyDeleted)
            {
                pk.ForcePartyData();
                var data = new byte[pk.SIZE_PARTY];
                pk.WriteDecryptedDataParty(data);
                bw.Write(data.Length);
                bw.Write(data);
                if (string.IsNullOrEmpty(note)) bw.Write((ushort)0);
                else
                {
                    var nb = System.Text.Encoding.UTF8.GetBytes(note);
                    bw.Write((ushort)Math.Min(nb.Length, ushort.MaxValue));
                    bw.Write(nb, 0, Math.Min(nb.Length, ushort.MaxValue));
                }
                bw.Write(deletedAt.Ticks);
            }
        }
        catch { /* not critical — silent fallback */ }
    }

    /// <summary>Loads the recently-deleted list from disk if present. Called once at startup.</summary>
    private void LoadRecentlyDeleted()
    {
        try
        {
            string path = RecentlyDeletedPath;
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'P' || magic[1] != 'R' || magic[2] != 'C' || magic[3] != 'T')
                return;
            byte version = br.ReadByte();
            if (version > 1) return;  // future-proof: skip newer formats

            int count = br.ReadInt32();
            if (count < 0 || count > 10000) return;  // sanity bound

            _recentlyDeleted.Clear();
            for (int i = 0; i < count; i++)
            {
                int dataLen = br.ReadInt32();
                if (dataLen < 0 || dataLen > 1024) return;  // sanity
                var data = br.ReadBytes(dataLen);
                int noteLen = br.ReadUInt16();
                string? note = null;
                if (noteLen > 0)
                {
                    var nb = br.ReadBytes(noteLen);
                    note = System.Text.Encoding.UTF8.GetString(nb);
                }
                long ticks = br.ReadInt64();
                var ts = ticks > 0 ? new DateTime(ticks) : DateTime.Now;

                var pk = EntityFormat.GetFromBytes(data);
                if (pk is { Species: > 0 })
                {
                    _recentlyDeleted.Add((pk, note, ts));
                    if (_recentlyDeleted.Count >= RecentlyDeletedCapacity) break;
                }
            }
        }
        catch { /* corrupt or missing file — start fresh */ }

        // Push the loaded entries into the visible grid so the user sees them immediately at startup.
        // Without this, _recentlyDeleted has the data but the slot PictureBoxes stay blank
        // until the next AddToRecentlyDeleted call triggers a refresh.
        _recentScrollOffset = 0;
        RefreshRecentlyDeletedGrid();
    }

    /// <summary>Path used to LOAD settings — falls back to legacy PokemonDen.settings if new PKDen.settings missing.</summary>
    private static string SettingsLoadPath
    {
        get
        {
            string newPath = Path.Combine(AppContext.BaseDirectory, "PKDen.settings");
            if (File.Exists(newPath)) return newPath;
            string legacyPath = Path.Combine(AppContext.BaseDirectory, "PokemonDen.settings");
            if (File.Exists(legacyPath)) return legacyPath;
            return newPath;
        }
    }

    /// <summary>Path used to SAVE settings — always writes to the new PKDen.settings name.</summary>
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "PKDen.settings");

    // --- Window layout persistence (set during LoadSettings, applied on Shown) ---
    private int? _savedWindowWidth;
    private int? _savedWindowHeight;
    private bool _savedWindowMaximized;
    private int? _savedSplitDistance;
    private int? _savedLastDenBox;

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsLoadPath)) return;
            foreach (var line in File.ReadAllLines(SettingsLoadPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "SpriteScale":
                        // Accept legacy int value AND new float (0.5/1.5/2.5/etc).
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sf) && sf >= 0.5f && sf <= 4f)
                            _spriteScale = sf;
                        break;
                    case "SaveSpriteScale":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ssf) && ssf >= 0.5f && ssf <= 4f)
                            _saveSpriteScale = ssf;
                        break;
                    case "PartySpriteScale":
                        // Party scale was added in V0.1.5; older settings files don't have it.
                        // On first load after upgrade, _partySpriteScale stays at its 1.0 default.
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float psf) && psf >= 0.5f && psf <= 4f)
                            _partySpriteScale = psf;
                        break;
                    case "ShowSummaryNotes":
                        _showSummaryNotes = value != "false";
                        break;
                    case "SummaryAlignment":
                        // Stored as the enum's ToString() (e.g. "MiddleLeft").  Fail safe to Left
                        // on any unparseable value so the layout never breaks from a bad settings file.
                        if (Enum.TryParse<ContentAlignment>(value, out var sa) &&
                            (sa == ContentAlignment.MiddleLeft || sa == ContentAlignment.MiddleCenter || sa == ContentAlignment.MiddleRight))
                            _summaryAlignment = sa;
                        break;
                    case "SummaryTextSize":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sts) && sts >= 6f && sts <= 20f)
                            _summaryTextSize = sts;
                        break;
                    case "SlotLabelTextSize":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float slts) && slts >= 5f && slts <= 16f)
                            _slotLabelTextSize = slts;
                        break;
                    case "ShowHeldItem":
                        _showHeldItem = value != "false";
                        break;
                    case "ShowBoxTotals":
                        _showBoxTotals = value != "false";
                        break;
                    case "SelectionOutlineColor":
                        // Stored as "R,G,B" decimal triple. Validate channel ranges before applying.
                        try
                        {
                            var parts = value.Split(',');
                            if (parts.Length == 3
                                && byte.TryParse(parts[0], out var r)
                                && byte.TryParse(parts[1], out var g)
                                && byte.TryParse(parts[2], out var b))
                                _selectionOutlineColor = Color.FromArgb(r, g, b);
                        }
                        catch { /* keep default */ }
                        break;
                    case "SelectionOutlineThickness":
                        if (int.TryParse(value, out int thickness) && thickness >= 1 && thickness <= 6)
                            _selectionOutlineThickness = thickness;
                        break;
                    case "SummaryTextBlack":
                        _summaryTextBlack = value == "true";
                        break;
                    case "SummaryTextBold":
                        _summaryTextBold = value == "true";
                        break;
                    // FillEmptySlotsBlack and SlotFillColor are V0.1.x legacy keys.  The slot
                    // fill color feature was removed in V0.1.7 (it duplicated the per-box color
                    // menu).  Silently ignore these on load so old .cfg files don't error out.
                    case "FillEmptySlotsBlack":
                    case "SlotFillColor":
                        break;
                    case "Use5x6Grid":
                    case "Use6x5DenGrid":
                        // "Use5x6Grid" is the legacy key from V0.1.2; "Use6x5DenGrid" is current.
                        _use6x5DenGrid = value == "true";
                        break;
                    case "Use6x5SaveGrid":
                        _use6x5SaveGrid = value == "true";
                        break;
                    case "SavesDirectory":
                        // Empty value means the user has never set a directory (or cleared it).
                        // Don't validate Directory.Exists here — defer that to UpdateSaveSelectorVisibility
                        // so a temporarily-unmounted network drive doesn't silently lose the setting.
                        _savesDirectory = string.IsNullOrWhiteSpace(value) ? null : value;
                        break;
                    case "ShowNames":
                        _showNames = value == "true";
                        break;
                    case "ShowGenders":
                        _showGenders = value == "true";
                        break;
                    case "ShowOrigin":
                        _showOrigin = value == "true";
                        break;
                    case "WindowWidth":
                        if (int.TryParse(value, out int ww) && ww >= 600) _savedWindowWidth = ww;
                        break;
                    case "WindowHeight":
                        if (int.TryParse(value, out int wh) && wh >= 400) _savedWindowHeight = wh;
                        break;
                    case "WindowMaximized":
                        _savedWindowMaximized = value == "true";
                        break;
                    case "SplitDistance":
                        if (int.TryParse(value, out int sd) && sd >= 200) _savedSplitDistance = sd;
                        break;
                    case "LastDenBox":
                        if (int.TryParse(value, out int ldb) && ldb >= 0) _savedLastDenBox = ldb;
                        break;
                }
            }
        }
        catch { /* ignore — use defaults */ }
    }

    /// <summary>Applies saved window size/position/splitter values. Called after layout has stabilized.</summary>
    private void ApplySavedWindowLayout()
    {
        try
        {
            if (_savedWindowWidth.HasValue && _savedWindowHeight.HasValue)
            {
                // Clamp to screen bounds so app isn't off-screen if monitor changed
                var screen = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(_savedWindowWidth.Value, screen.Width);
                int h = Math.Min(_savedWindowHeight.Value, screen.Height);
                Size = new Size(w, h);
            }
            if (_savedWindowMaximized)
                WindowState = FormWindowState.Maximized;

            if (_savedSplitDistance.HasValue)
            {
                int min = splitMain.Panel1MinSize;
                int max = splitMain.Width - splitMain.Panel2MinSize - splitMain.SplitterWidth;
                if (max > min)
                {
                    int d = Math.Clamp(_savedSplitDistance.Value, min, max);
                    splitMain.SplitterDistance = d;
                }
            }
        }
        catch { /* ignore failures — fall back to defaults */ }
        // After window/splitter sizes settle, apply centering padding for 6×5 layouts if enabled
        ApplyDenGridLayout();
        ApplySaveGridLayout();
    }

    private void SaveSettings()
    {
        try
        {
            // Capture current window state
            int saveW, saveH;
            bool saveMax = WindowState == FormWindowState.Maximized;
            if (saveMax)
            {
                // When maximized, Size reports the maximized size. Use RestoreBounds for the
                // "normal" size that should be restored when un-maximizing.
                saveW = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width;
                saveH = RestoreBounds.Height > 0 ? RestoreBounds.Height : Height;
            }
            else
            {
                saveW = Width;
                saveH = Height;
            }
            int splitDist = splitMain?.SplitterDistance ?? 620;

            var lines = new[]
            {
                "# PKDen view preferences",
                $"SpriteScale={_spriteScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"SaveSpriteScale={_saveSpriteScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"PartySpriteScale={_partySpriteScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"ShowSummaryNotes={(_showSummaryNotes ? "true" : "false")}",
                $"SummaryAlignment={_summaryAlignment}",
                $"SummaryTextSize={_summaryTextSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"SlotLabelTextSize={_slotLabelTextSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"ShowNames={(_showNames ? "true" : "false")}",
                $"ShowGenders={(_showGenders ? "true" : "false")}",
                $"ShowOrigin={(_showOrigin ? "true" : "false")}",
                $"ShowHeldItem={(_showHeldItem ? "true" : "false")}",
                $"ShowBoxTotals={(_showBoxTotals ? "true" : "false")}",
                $"SelectionOutlineColor={_selectionOutlineColor.R},{_selectionOutlineColor.G},{_selectionOutlineColor.B}",
                $"SelectionOutlineThickness={_selectionOutlineThickness}",
                $"SummaryTextBlack={(_summaryTextBlack ? "true" : "false")}",
                $"SummaryTextBold={(_summaryTextBold ? "true" : "false")}",
                $"Use6x5DenGrid={(_use6x5DenGrid ? "true" : "false")}",
                $"Use6x5SaveGrid={(_use6x5SaveGrid ? "true" : "false")}",
                $"SavesDirectory={_savesDirectory ?? ""}",
                $"WindowWidth={saveW}",
                $"WindowHeight={saveH}",
                $"WindowMaximized={(saveMax ? "true" : "false")}",
                $"SplitDistance={splitDist}",
                $"LastDenBox={CurrentDenBox}",
            };
            File.WriteAllLines(SettingsPath, lines);
        }
        catch { /* silent — settings are a nicety, not essential */ }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Save view preferences regardless of Den dirty state
        SaveSettings();
        // Always persist the recently-deleted list so accidental deletes survive restart
        SaveRecentlyDeleted();

        // If user clicked "Exit Without Saving", skip the save prompt entirely
        if (_skipSaveOnClose)
        {
            _musicPlayer?.Dispose();
            _musicPlayer = null;
            return;
        }

        if (!Den.IsDirty)
        {
            // Dispose music player before exiting so the WMP COM object is released cleanly.
            _musicPlayer?.Dispose();
            _musicPlayer = null;
            return;
        }
        var result = MessageBox.Show(this, "Your Den has unsaved changes.\n\nSave before closing?", "Save Den", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) { e.Cancel = true; return; }
        if (result == DialogResult.Yes) SaveDenStorage(DenSavePath);
        _musicPlayer?.Dispose();
        _musicPlayer = null;
    }

    // ========================================================================
    //  HELPERS
    // ========================================================================

    private PKM? GetPKMForSlot(bool isDen, int slot)
    {
        if (isDen)
        {
            if (_isSearchActive)
            {
                if (slot < _searchResults.Count) return _searchResults[slot].Pk;
                return null;
            }
            return Den.GetSlot(CurrentDenBox, slot);
        }
        if (SAV is not null && SAV.HasBox && slot < SAV.BoxSlotCount) return SAV.GetBoxSlotAtIndex(CurrentSaveBox, slot);
        return null;
    }

    /// <summary>Gets the actual (box, slot) location for a display slot — handles search mode.</summary>
    private (int Box, int Slot) GetDenLocationForSlot(int displaySlot)
    {
        if (_isSearchActive && displaySlot < _searchResults.Count)
            return (_searchResults[displaySlot].Box, _searchResults[displaySlot].Slot);
        return (CurrentDenBox, displaySlot);
    }

    /// <summary>Creates a blank Gen 9 save for importing PKM when no save is loaded.</summary>
    private static SaveFile CreateBlankSave() => new SAV9SV();

    private static void ViewSummary(PKM pk, string? note = null, DateTime? timestamp = null)
    {
        try
        {
            var strings = GameInfo.Strings;
            string name = (uint)pk.Species < strings.specieslist.Length ? strings.specieslist[pk.Species] : $"#{pk.Species}";
            string nature = (uint)(int)pk.Nature < strings.natures.Length ? strings.natures[(int)pk.Nature] : "N/A";
            string ability = (uint)pk.Ability < strings.abilitylist.Length ? strings.abilitylist[pk.Ability] : "N/A";
            string game = GameInfo.GetVersionName(pk.Version);
            if (string.IsNullOrEmpty(game)) game = pk.Version.ToString();
            int ivTotal = pk.IV_HP + pk.IV_ATK + pk.IV_DEF + pk.IV_SPA + pk.IV_SPD + pk.IV_SPE;
            string msg = $"Species: {name}\nLevel: {pk.CurrentLevel}\nNature: {nature}\nAbility: {ability}\nOT: {pk.OriginalTrainerName}\nGame: {game}\nFormat: {pk.GetType().Name}\nIVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE} (Total: {ivTotal})";
            try { if (pk.IsShiny) msg += "\n★ Shiny"; } catch { }
            try { if (pk.IsEgg) msg += "\n🥚 Egg"; } catch { }
            if (timestamp.HasValue) msg += $"\n\n📅 Added to Den: {timestamp.Value:yyyy-MM-dd HH:mm}";
            if (!string.IsNullOrEmpty(note)) msg += $"\n📝 Note: {note}";
            MessageBox.Show(msg, $"{name} — Summary", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not display summary: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EditSlotNote(int box, int slot)
    {
        var existing = Den.GetNote(box, slot) ?? "";
        using var dlg = new NoteEditDialog(existing);
        if (dlg.ShowDialog(this) == DialogResult.OK) { Den.SetNote(box, slot, dlg.NoteText); SetStatus(string.IsNullOrEmpty(dlg.NoteText) ? "Note removed." : "Note saved."); }
    }

    /// <summary>Sets the per-slot text shown in an empty slot (cleared automatically when a Pokémon takes the slot).</summary>
    private void EditSlotLabel(int box, int slot)
    {
        var existing = Den.GetSlotLabel(box, slot) ?? "";
        using var dlg = new SlotLabelDialog(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Den.SetSlotLabel(box, slot, dlg.LabelText);
        RefreshDenGrid();
        SetStatus(string.IsNullOrEmpty(dlg.LabelText) ? "Slot text cleared." : $"Slot text set to \"{Truncate(dlg.LabelText, 30)}\".");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Opens a file picker for the user to select a PNG/JPG/etc. image to use as
    /// a custom sprite for <paramref name="pk"/>.  Stores the raw bytes in the den
    /// (so they persist across save/load) and refreshes the slot at <paramref name="box"/>/<paramref name="slot"/>.
    /// </summary>
    /// <remarks>
    /// We accept a wider set of formats than just PNG (BMP/JPG/GIF) because the user
    /// may not have a PNG handy.  GDI+ decodes them all transparently.  Storage is
    /// always the original bytes — we don't re-encode, so quality and transparency
    /// are preserved exactly as the user supplied them.  If they pick a non-image
    /// or corrupt file, we show an error and don't change anything.
    /// </remarks>
    private void SetCustomSpriteForPk(PKM pk, int box, int slot)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a custom sprite",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read file:\n{ex.Message}", "Custom Sprite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Sanity guards before storing in the den file:
        //   • Reject empty/garbage files via decode-validate
        //   • Reject huge images that would bloat the .den unreasonably
        const long MaxBytes = 8L * 1024 * 1024; // 8 MB — well above any typical sprite source
        if (bytes.Length > MaxBytes)
        {
            MessageBox.Show(this, $"Image is too large ({bytes.Length / 1024 / 1024} MB).\nPlease use an image under {MaxBytes / 1024 / 1024} MB.",
                "Custom Sprite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!DenStorageManager.ValidateImageBytes(bytes))
        {
            MessageBox.Show(this, "That file doesn't appear to be a valid image.", "Custom Sprite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PushUndo();
        // Drop any prior cached bitmap for this identity BEFORE saving new bytes,
        // so the next sprite render decodes from the new bytes.
        ulong identityKey = DenStorageManager.GetPkIdentityKey(pk);
        CustomSpriteCache.Invalidate(identityKey);
        Den.SetCustomSprite(pk, bytes);
        RefreshDenGrid();
        // Also refresh the summary panel if this slot is currently summarized.
        RefreshSummary(pk, box, slot, fromDen: true);
        SetStatus($"Custom sprite set ({bytes.Length / 1024} KB).");
    }

    /// <summary>Removes the custom sprite override for <paramref name="pk"/> and refreshes the slot.</summary>
    private void RemoveCustomSpriteForPk(PKM pk, int box, int slot)
    {
        PushUndo();
        ulong identityKey = DenStorageManager.GetPkIdentityKey(pk);
        CustomSpriteCache.Invalidate(identityKey);
        Den.RemoveCustomSprite(pk);
        RefreshDenGrid();
        RefreshSummary(pk, box, slot, fromDen: true);
        SetStatus("Custom sprite removed.");
    }

    private void RenameDenBox()
    {
        int box = CurrentDenBox;
        if (box < 0) return;
        using var dlg = new RenameBoxDialog(Den.GetBoxName(box));
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.BoxName))
        {
            Den.SetBoxName(box, dlg.BoxName);
            _suppressBoxSelectorEvents = true;
            try { denBoxSelector.Items[box] = FormatBoxLabel(box); }
            finally { _suppressBoxSelectorEvents = false; }
            UpdateBoxSelectorWidth();
            UpdateDenBoxNameHeader(dlg.BoxName);  // refresh the header above the grid
            SetStatus($"Box renamed to \"{dlg.BoxName}\".");
        }
    }

    private void ImportSingleFile(int box, int slot)
    {
        using var ofd = new OpenFileDialog { Title = "Select a Pokémon file", Filter = "Pokémon Files (*.pk* *.pb* *.pa* *.ek*)|*.pk*;*.pb*;*.pa*;*.ek*;*.gp1|All Files|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var obj = FileUtil.GetSupportedFile(ofd.FileName, SAV);
            PKM? pk = obj switch { PKM p when p.Species > 0 => p, MysteryGift { IsEntity: true } mg => mg.ConvertToPKM(SAV ?? CreateBlankSave()), _ => null };
            if (pk is null) { MessageBox.Show(this, "Could not load as Pokémon.", "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            // Capture existing slot if occupied
            var existing = Den.GetSlot(box, slot);
            if (existing is { Species: > 0 })
                AddToRecentlyDeleted(existing, Den.GetNote(box, slot));
            Den.SetSlot(box, slot, pk);
            Den.SetTimestamp(box, slot, DateTime.Now);
            RefreshDenGrid();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Error: {ex.Message}", "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public void SetStatus(string message) => statusLabel.Text = message;

    // ========================================================================
    //  KEYBOARD
    // ========================================================================

    /// <summary>Walks ContainerControl.ActiveControl recursively to find the deepest focused control.</summary>
    private static Control? GetDeepestFocusedControl(Control root)
    {
        Control current = root;
        while (current is ContainerControl cc && cc.ActiveControl is not null)
            current = cc.ActiveControl;
        return current;
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        // Don't steal Ctrl+A/C/V/Delete etc. when the user is typing in a text box.
        // ActiveControl only returns the IMMEDIATE child of the form, not the deeply-nested
        // textbox inside the summary panel, so we have to walk all the way down.
        var focused = GetDeepestFocusedControl(this);
        if (focused is TextBox or ComboBox or RichTextBox or NumericUpDown) return;

        switch (e.KeyCode)
        {
            case Keys.A when e.Control:
                selectedDenSlots.Clear();
                if (_isSearchActive)
                {
                    for (int i = 0; i < _searchResults.Count && i < denSlots.Count; i++)
                    { selectedDenSlots.Add(i); MarkSlotSelected(denSlots[i], true); }
                }
                else
                {
                    int box = CurrentDenBox; if (box < 0) break;
                    for (int i = 0; i < SlotsPerDenBox; i++)
                    { if (Den.GetSlot(box, i) is { Species: > 0 }) { selectedDenSlots.Add(box * SlotsPerDenBox + i); MarkSlotSelected(denSlots[i], true); } }
                }
                e.Handled = true; break;
            case Keys.C when e.Control:
                // Copy all selected Den pokemon
                if (selectedDenSlots.Count > 0) CopySelectedToClipboard();
                e.Handled = true; break;
            case Keys.V when e.Control:
                // Paste clipboard — disabled in search mode
                if (_clipboard.Count > 0 && !_isSearchActive)
                {
                    int pb2 = CurrentDenBox; if (pb2 < 0) break;
                    var (db, ds) = Den.FindNextEmpty(pb2);
                    if (db >= 0) PasteClipboardAt(db, ds);
                }
                e.Handled = true; break;
            case Keys.Delete:
                if (selectedDenSlots.Count > 0) DeleteSelectedDen();
                e.Handled = true; break;
            case Keys.Escape:
                ClearDenSelection(); ClearSaveSelection(); e.Handled = true; break;
        }
    }

    private void JumpToBox(int box, int slot)
    {
        // Clear search to exit flat view
        _searchBox.Text = "";
        _filterOT.Text = "";
        // ClearSearchFilter will be invoked automatically via TextChanged → ApplySearchFilter
        // But call it explicitly to ensure state is reset
        _isSearchActive = false;
        _searchResults.Clear();
        denBoxSelector.Enabled = true;
        denPrev.Enabled = true;
        denNext.Enabled = true;

        if (box >= 0 && box < denBoxSelector.Items.Count)
            denBoxSelector.SelectedIndex = box;
        RefreshDenGrid();

        // Highlight the jumped-to slot
        if (slot >= 0 && slot < denSlots.Count)
        {
            MarkSlotSelected(denSlots[slot], true);
            selectedDenSlots.Clear();
            selectedDenSlots.Add(box * SlotsPerDenBox + slot);
            SetStatus($"Jumped to {Den.GetBoxName(box)}.");
        }
    }

    // ========================================================================
    //  UNDO
    // ========================================================================

    /// <summary>Records the full Den state plus current UI position for undo purposes.</summary>
    private readonly struct UndoSnapshot
    {
        public byte[] DenBytes { get; init; }
        public int BoxIndex { get; init; }
    }

    private readonly Stack<UndoSnapshot> _undoStackTyped = new();

    private void PushUndo()
    {
        try
        {
            var snap = new UndoSnapshot
            {
                DenBytes = Den.SaveToBytes(),
                BoxIndex = CurrentDenBox,
            };
            _undoStackTyped.Push(snap);
            // Enforce MaxUndo hard cap — drop OLDEST entries when exceeded
            while (_undoStackTyped.Count > MaxUndo)
            {
                var arr = _undoStackTyped.ToArray(); // LIFO order (newest first)
                _undoStackTyped.Clear();
                for (int i = arr.Length - 2; i >= 0; i--) _undoStackTyped.Push(arr[i]);
            }
        }
        catch { }
    }

    private void Undo()
    {
        if (_undoStackTyped.Count == 0) { SetStatus("Nothing to undo."); return; }
        var snap = _undoStackTyped.Pop();
        var sav = SAV ?? new PKHeX.Core.SAV9SV();

        // Clear search mode, selections, and clipboard references that may point to stale state
        _isSearchActive = false;
        _searchResults.Clear();
        _searchBox.Text = "";
        _filterOT.Text = "";
        denBoxSelector.Enabled = true;
        denPrev.Enabled = true;
        denNext.Enabled = true;
        selectedDenSlots.Clear();

        if (!Den.LoadFromBytes(snap.DenBytes, sav))
        {
            SetStatus("Undo failed — snapshot corrupted.");
            return;
        }

        // Undo restored a different set of custom-sprite mappings; flush stale cache.
        CustomSpriteCache.ClearAll();

        PopulateDenBoxNames();
        // Restore the user's previous box position instead of jumping to box 0
        if (denBoxSelector.Items.Count > 0)
        {
            int target = snap.BoxIndex;
            if (target < 0 || target >= denBoxSelector.Items.Count) target = 0;
            denBoxSelector.SelectedIndex = target;
        }
        RefreshDenGrid();
        NotifyArrangeWindows();
        SetStatus($"Undo successful. ({_undoStackTyped.Count} more undo{(_undoStackTyped.Count == 1 ? "" : "s")} available)");
    }

    // ========================================================================
    //  ARRANGE WINDOW
    // ========================================================================

    private readonly List<ArrangeForm> _arrangeWindows = [];

    private void OpenArrangeWindow()
    {
        var arrangeForm = new ArrangeForm(Den, _clipboard, this);
        _arrangeWindows.Add(arrangeForm);
        arrangeForm.FormClosed += (_, _) => _arrangeWindows.Remove(arrangeForm);
        arrangeForm.Show(this);
    }

    /// <summary>Called after any Den change so arrange windows can refresh.</summary>
    public void NotifyArrangeWindows()
    {
        foreach (var w in _arrangeWindows)
        {
            if (!w.IsDisposed)
                w.RefreshGrid();
        }
    }

    /// <summary>Called by ArrangeForm when it modifies data, so main window refreshes too.</summary>
    public void NotifyMainDenChanged()
    {
        RefreshDenGrid();
    }

    /// <summary>
    /// Label subclass with <c>OptimizedDoubleBuffer</c> turned on.  Used for the den
    /// title-bar header so it doesn't flash during grid scrolls.
    /// </summary>
    /// <remarks>
    /// Plain <see cref="Label"/> doesn't enable double-buffering by default.  When the
    /// grid below it scrolls, Windows invalidates the label too (it's a sibling control
    /// in the same panel), and the label repaints in two passes — first clearing the
    /// background, then drawing the text — with a visible gap between them at scroll
    /// rates.  Enabling <see cref="ControlStyles.OptimizedDoubleBuffer"/> renders both
    /// passes into a back buffer and flips them to the screen in one operation.
    ///
    /// Why this and not <c>WS_EX_COMPOSITED</c> on the parent panel?  The composited
    /// approach buffers EVERY child of the panel, which slowed slot loading and tab
    /// switches by seconds.  This targeted fix touches only the one label that was
    /// flickering — slot painting performance is unaffected.
    /// </remarks>
    private sealed class DoubleBufferedLabel : Label
    {
        public DoubleBufferedLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            UpdateStyles();
        }
    }
}

// ========================================================================
//  DIALOGS
// ========================================================================

internal sealed class NoteEditDialog : Form
{
    private readonly TextBox tbNote;
    public string NoteText => tbNote.Text;
    public NoteEditDialog(string existing)
    {
        Text = "Edit Note"; Size = new Size(400, 200);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var lbl = new Label { Text = "Enter a note for this Pokémon:", Location = new Point(15, 10), AutoSize = true };
        tbNote = new TextBox { Multiline = true, Location = new Point(15, 30), Size = new Size(350, 80), Text = existing, ScrollBars = ScrollBars.Vertical };
        var btnOK = new Button { Text = "Save", Location = new Point(200, 120), Width = 80, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(290, 120), Width = 80, DialogResult = DialogResult.Cancel };
        Controls.AddRange([lbl, tbNote, btnOK, btnCancel]); AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

/// <summary>Dialog for editing the per-slot text shown on empty slots.</summary>
internal sealed class SlotLabelDialog : Form
{
    private readonly TextBox tbLabel;
    public string LabelText => tbLabel.Text.Trim();
    public SlotLabelDialog(string existing)
    {
        Text = "Slot Text";
        Size = new Size(420, 200);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var lbl = new Label
        {
            Text = "Text shown on this empty slot.\nIt will be cleared automatically when a Pokémon takes the slot.",
            Location = new Point(15, 10), AutoSize = false, Width = 380, Height = 36,
        };
        tbLabel = new TextBox
        {
            Location = new Point(15, 55), Width = 380,
            Text = existing, MaxLength = 80,
        };
        tbLabel.SelectAll();
        var btnOK = new Button { Text = "Save", Location = new Point(220, 95), Width = 80, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(310, 95), Width = 80, DialogResult = DialogResult.Cancel };
        Controls.AddRange([lbl, tbLabel, btnOK, btnCancel]);
        AcceptButton = btnOK;
        CancelButton = btnCancel;
    }
}

internal sealed class RenameBoxDialog : Form
{
    private readonly TextBox tbName;
    public string BoxName => tbName.Text;
    public RenameBoxDialog(string current)
    {
        Text = "Rename Box"; Size = new Size(320, 130);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var lbl = new Label { Text = "Box name:", Location = new Point(15, 10), AutoSize = true };
        tbName = new TextBox { Location = new Point(15, 30), Width = 270, Text = current };
        tbName.SelectAll();
        var btnOK = new Button { Text = "Rename", Location = new Point(120, 65), Width = 80, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(210, 65), Width = 70, DialogResult = DialogResult.Cancel };
        Controls.AddRange([lbl, tbName, btnOK, btnCancel]); AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

internal sealed class ConfirmDeleteDialog : Form
{
    public ConfirmDeleteDialog()
    {
        Text = "Clear ALL Den Storage"; Size = new Size(420, 220);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var lbl = new Label { Text = "⚠ WARNING ⚠\n\nThis will permanently delete ALL Pokémon\nfrom Den Storage. This cannot be undone.\n\nType DEL below to confirm:", Location = new Point(15, 10), AutoSize = true };
        var tbConfirm = new TextBox { Location = new Point(15, 120), Width = 370, Font = new Font("Segoe UI", 11) };
        var btnOK = new Button { Text = "Delete All", Location = new Point(210, 155), Width = 90, DialogResult = DialogResult.OK, Enabled = false, BackColor = Color.FromArgb(200, 50, 50), ForeColor = Color.White };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(310, 155), Width = 80, DialogResult = DialogResult.Cancel };
        tbConfirm.TextChanged += (_, _) => btnOK.Enabled = string.Equals(tbConfirm.Text.Trim(), "DEL", StringComparison.Ordinal);
        Controls.AddRange([lbl, tbConfirm, btnOK, btnCancel]); AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

internal sealed class AddBoxesDialog : Form
{
    private readonly NumericUpDown nudCount;
    public int BoxCount => (int)nudCount.Value;

    public AddBoxesDialog()
    {
        Text = "Add More Boxes";
        Size = new Size(300, 140);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new Label { Text = "Number of boxes to add:", Location = new Point(15, 15), AutoSize = true };
        nudCount = new NumericUpDown { Location = new Point(15, 40), Width = 100, Minimum = 1, Maximum = 500, Value = 10 };
        var btnOK = new Button { Text = "Add", Location = new Point(120, 70), Width = 70, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(200, 70), Width = 70, DialogResult = DialogResult.Cancel };

        Controls.AddRange([lbl, nudCount, btnOK, btnCancel]);
        AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

internal sealed class SendBoxDialog : Form
{
    private readonly ComboBox cbBox;
    private readonly int[] _boxMap;

    public int SelectedBox
    {
        get
        {
            if (cbBox.SelectedIndex >= 0 && cbBox.SelectedIndex < _boxMap.Length)
                return _boxMap[cbBox.SelectedIndex];
            return -1;
        }
    }

    public SendBoxDialog(DenStorageManager den, int currentBox)
    {
        Text = "Send Box To...";
        Size = new Size(320, 150);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new Label { Text = $"Move all Pokémon from \"{den.GetBoxName(currentBox)}\" to:", Location = new Point(15, 12), AutoSize = true };
        cbBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(15, 45), Width = 270 };

        var indices = new List<int>();
        for (int i = 0; i < den.BoxCount; i++)
        {
            if (i == currentBox) continue;
            int free = DenStorageManager.SlotsPerBox - den.GetBoxCount(i);
            cbBox.Items.Add($"{den.GetBoxName(i)}  ({free} free)");
            indices.Add(i);
        }
        _boxMap = indices.ToArray();
        if (cbBox.Items.Count > 0) cbBox.SelectedIndex = 0;

        var btnOK = new Button { Text = "Send", Location = new Point(130, 80), Width = 70, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(210, 80), Width = 70, DialogResult = DialogResult.Cancel };

        Controls.AddRange([lbl, cbBox, btnOK, btnCancel]);
        AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

/// <summary>Picker dialog for the right-click "Send to Box…" action (single or multi-select).</summary>
internal sealed class SendToBoxDialog : Form
{
    private readonly ComboBox cbBox;
    private readonly int[] _boxMap;

    public int SelectedBox
    {
        get
        {
            if (cbBox.SelectedIndex >= 0 && cbBox.SelectedIndex < _boxMap.Length)
                return _boxMap[cbBox.SelectedIndex];
            return -1;
        }
    }

    /// <param name="referenceBox">Source box to exclude from list, or -1 to include all.</param>
    /// <param name="pkCount">Number of Pokémon being sent — shown in title for clarity.</param>
    public SendToBoxDialog(DenStorageManager den, int referenceBox, int pkCount)
    {
        Text = pkCount == 1 ? "Send to Box" : $"Send {pkCount} Pokémon to Box";
        Size = new Size(340, 150);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new Label { Text = pkCount == 1 ? "Send this Pokémon to:" : $"Send {pkCount} Pokémon to:", Location = new Point(15, 12), AutoSize = true };
        cbBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(15, 40), Width = 290 };

        var indices = new List<int>();
        for (int i = 0; i < den.BoxCount; i++)
        {
            if (referenceBox >= 0 && i == referenceBox) continue;
            int free = DenStorageManager.SlotsPerBox - den.GetBoxCount(i);
            cbBox.Items.Add($"{den.GetBoxName(i)}  ({free} free)");
            indices.Add(i);
        }
        _boxMap = indices.ToArray();
        if (cbBox.Items.Count > 0) cbBox.SelectedIndex = 0;

        var btnOK = new Button { Text = "Send", Location = new Point(150, 75), Width = 70, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(230, 75), Width = 70, DialogResult = DialogResult.Cancel };

        Controls.AddRange([lbl, cbBox, btnOK, btnCancel]);
        AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

internal sealed class ImportBoxPickerDialog : Form
{
    private readonly ComboBox cbBox;
    public int SelectedBox => cbBox.SelectedIndex;

    public ImportBoxPickerDialog(DenStorageManager den, int defaultBox)
    {
        Text = "Import — Choose Starting Box";
        Size = new Size(360, 170);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new Label
        {
            Text = "Start importing into which box?\n(Will fill this box and overflow into next boxes as needed)",
            Location = new Point(15, 12), AutoSize = true,
        };
        cbBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(15, 60), Width = 310 };
        for (int i = 0; i < den.BoxCount; i++)
        {
            int count = den.GetBoxCount(i);
            string label = count == 0
                ? $"{den.GetBoxName(i)} — EMPTY"
                : $"{den.GetBoxName(i)} — {count}/{DenStorageManager.SlotsPerBox}";
            cbBox.Items.Add(label);
        }
        if (cbBox.Items.Count > 0)
            cbBox.SelectedIndex = Math.Max(0, Math.Min(defaultBox, cbBox.Items.Count - 1));

        var btnOK = new Button { Text = "Import", Location = new Point(170, 95), Width = 75, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(250, 95), Width = 75, DialogResult = DialogResult.Cancel };

        Controls.AddRange([lbl, cbBox, btnOK, btnCancel]);
        AcceptButton = btnOK; CancelButton = btnCancel;
    }
}

/// <summary>
/// Secondary Den box viewer for arranging Pokémon between boxes.
/// Shares the same DenStorageManager and clipboard as the main window.
/// </summary>
internal sealed class ArrangeForm : Form
{
    private readonly DenStorageManager Den;
    private readonly List<(PKM Pk, string? Note, DateTime? Timestamp)> _clipboard;
    private readonly MainForm _main;
    private readonly ComboBox boxSelector;
    private readonly FlowLayoutPanel grid;
    private readonly Label countLabel;
    private readonly List<PictureBox> slots = [];
    private readonly HashSet<int> selected = [];
    private int _lastClicked = -1;
    private const int SlotsPerBox = 30;

    private static int SpriteW => SpriteUtil.Spriter.Width;
    private static int SpriteH => SpriteUtil.Spriter.Height;

    public ArrangeForm(DenStorageManager den, List<(PKM Pk, string? Note, DateTime? Timestamp)> clipboard, MainForm main)
    {
        Den = den;
        _clipboard = clipboard;
        _main = main;

        Text = "Arrange Pokémon";
        Size = new Size(480, 520);
        MinimumSize = new Size(400, 400);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        // Load icon from main form
        if (main.Icon is not null) Icon = main.Icon;

        var title = new Label
        {
            Text = "Arrange — Drag & Drop or Copy/Paste between windows",
            Dock = DockStyle.Top, Height = 28,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White, BackColor = Color.FromArgb(50, 55, 70),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        boxSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        var btnPrev = new Button { Text = "◀", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };
        var btnNext = new Button { Text = "▶", Width = 32, Height = 26, FlatStyle = FlatStyle.Flat };

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(50, 55, 70),
        };
        nav.Controls.AddRange([btnPrev, boxSelector, btnNext]);

        grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4),
            BackColor = Color.FromArgb(40, 40, 48),
        };

        countLabel = new Label { AutoSize = true, Text = "", Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0, 4, 0, 4) };

        Controls.Add(countLabel);
        Controls.Add(grid);
        Controls.Add(nav);
        Controls.Add(title);

        // Create slots
        int w = SpriteW + 4, h = SpriteH + 4;
        grid.SuspendLayout();
        for (int i = 0; i < SlotsPerBox; i++)
        {
            int slotIndex = i;
            var pb = new PictureBox
            {
                Width = w, Height = h, SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Color.FromArgb(60, 62, 72), BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(1), AllowDrop = true, Tag = i,
            };
            pb.MouseClick += (_, e) => SlotClicked(pb, e, slotIndex);
            pb.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragSource = pb; _dragStart = e.Location; } };
            pb.MouseMove += (_, e) => SlotMouseMove(pb, e, slotIndex);
            pb.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.Text) == true) e.Effect = DragDropEffects.Move; else e.Effect = DragDropEffects.None; };
            pb.DragDrop += (_, e) => SlotDragDrop(e, slotIndex);
            pb.GiveFeedback += (_, e) => e.UseDefaultCursors = true;

            var tt = new ToolTip { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };
            pb.MouseHover += (_, _) =>
            {
                var pk = Den.GetSlot(CurrentBox, slotIndex);
                if (pk is { Species: > 0 })
                    tt.SetToolTip(pb, _main.BuildHoverText(pk, CurrentBox, slotIndex));
                else
                    tt.SetToolTip(pb, null);
            };
            pb.MouseLeave += (_, _) => tt.SetToolTip(pb, null);

            slots.Add(pb);
            grid.Controls.Add(pb);
        }
        grid.ResumeLayout();

        // Populate boxes
        for (int i = 0; i < Den.BoxCount; i++)
            boxSelector.Items.Add(Den.GetBoxName(i));

        boxSelector.SelectedIndexChanged += (_, _) => RefreshGrid();
        btnPrev.Click += (_, _) => { int n = CurrentBox - 1; if (n < 0) n = boxSelector.Items.Count - 1; boxSelector.SelectedIndex = n; };
        btnNext.Click += (_, _) => { int n = CurrentBox + 1; if (n >= boxSelector.Items.Count) n = 0; boxSelector.SelectedIndex = n; };
        KeyDown += ArrangeForm_KeyDown;

        if (boxSelector.Items.Count > 0)
            boxSelector.SelectedIndex = 0;
    }

    private int CurrentBox => boxSelector.SelectedIndex;
    private PictureBox? _dragSource;
    private Point _dragStart;

    public void RefreshGrid()
    {
        // Re-populate box names if count changed
        if (boxSelector.Items.Count != Den.BoxCount)
        {
            int sel = CurrentBox;
            boxSelector.Items.Clear();
            for (int i = 0; i < Den.BoxCount; i++) boxSelector.Items.Add(Den.GetBoxName(i));
            if (sel >= 0 && sel < boxSelector.Items.Count) boxSelector.SelectedIndex = sel;
            else if (boxSelector.Items.Count > 0) boxSelector.SelectedIndex = 0;
            return; // SelectedIndex change triggers RefreshGrid
        }

        selected.Clear();
        int box = CurrentBox;
        if (box < 0) return;
        for (int i = 0; i < slots.Count; i++)
        {
            // Dispose any prior slot image before assignment.  PKDenSprite() always
            // returns a freshly allocated bitmap, so each refresh would otherwise
            // leak one bitmap per slot — quickly accumulating across box switches.
            var oldImg = slots[i].Image;
            slots[i].Image = null;

            var pk = Den.GetSlot(box, i);
            if (pk is not null and not { Species: 0 })
            {
                try { slots[i].Image = pk.PKDenSprite(); }
                catch { slots[i].Image = null; }
            }
            oldImg?.Dispose();

            slots[i].BackColor = Color.FromArgb(60, 62, 72);
        }
        countLabel.Text = $"{Den.GetBoxCount(box)}/{SlotsPerBox} in box";
    }

    private void SlotClicked(PictureBox pb, MouseEventArgs e, int slot)
    {
        if (e.Button == MouseButtons.Right) { ShowMenu(pb, e.Location, slot); return; }

        int box = CurrentBox;
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        int flat = box * SlotsPerBox + slot;

        if (shift && _lastClicked >= 0)
        {
            if (!ctrl) ClearSelection();
            int from = Math.Min(_lastClicked, slot), to = Math.Max(_lastClicked, slot);
            for (int i = from; i <= to; i++)
            {
                if (Den.GetSlot(box, i) is { Species: > 0 })
                {
                    selected.Add(box * SlotsPerBox + i);
                    slots[i].BackColor = Color.FromArgb(80, 140, 220);
                }
            }
        }
        else if (ctrl)
        {
            if (selected.Contains(flat)) { selected.Remove(flat); pb.BackColor = Color.FromArgb(60, 62, 72); }
            else if (Den.GetSlot(box, slot) is { Species: > 0 }) { selected.Add(flat); pb.BackColor = Color.FromArgb(80, 140, 220); }
            _lastClicked = slot;
        }
        else
        {
            ClearSelection();
            if (Den.GetSlot(box, slot) is { Species: > 0 }) { selected.Add(flat); pb.BackColor = Color.FromArgb(80, 140, 220); }
            _lastClicked = slot;
        }
    }

    private void ClearSelection() { selected.Clear(); foreach (var pb in slots) pb.BackColor = Color.FromArgb(60, 62, 72); }

    private void ShowMenu(PictureBox pb, Point location, int slot)
    {
        var pk = Den.GetSlot(CurrentBox, slot);
        var menu = new ContextMenuStrip();

        if (pk is { Species: > 0 })
        {
            if (selected.Count > 1)
            {
                menu.Items.Add(new ToolStripMenuItem($"Copy {selected.Count} Selected", null, (_, _) =>
                {
                    _clipboard.Clear();
                    foreach (var flat in selected.OrderBy(x => x))
                    {
                        int b = flat / SlotsPerBox, s = flat % SlotsPerBox;
                        var p = Den.GetSlot(b, s);
                        if (p is { Species: > 0 }) _clipboard.Add((p.Clone(), Den.GetNote(b, s), Den.GetTimestamp(b, s)));
                    }
                    _main.SetStatus($"Copied {_clipboard.Count} Pokémon to clipboard.");
                }));
                menu.Items.Add(new ToolStripMenuItem($"Delete {selected.Count} Selected", null, (_, _) =>
                {
                    foreach (var flat in selected) _main.ClearSlotWithCapture(flat / SlotsPerBox, flat % SlotsPerBox);
                    ClearSelection(); RefreshGrid(); _main.NotifyMainDenChanged();
                }));
            }
            else
            {
                menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) =>
                {
                    _clipboard.Clear();
                    _clipboard.Add((pk.Clone(), Den.GetNote(CurrentBox, slot), Den.GetTimestamp(CurrentBox, slot)));
                    _main.SetStatus($"Copied {MainForm.GetSpeciesName(pk)} to clipboard.");
                }));
                menu.Items.Add(new ToolStripMenuItem("Delete", null, (_, _) => { _main.ClearSlotWithCapture(CurrentBox, slot); RefreshGrid(); _main.NotifyMainDenChanged(); }));
            }
        }

        // Paste (works on both empty and occupied)
        if (_clipboard.Count > 0)
        {
            string label = _clipboard.Count == 1 ? $"Paste ({MainForm.GetSpeciesName(_clipboard[0].Pk)})" : $"Paste {_clipboard.Count} Pokémon";
            if (pk is { Species: > 0 }) label += " — Replace";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                int s = slot;
                foreach (var (p, note, ts) in _clipboard)
                {
                    while (s < SlotsPerBox && Den.GetSlot(CurrentBox, s) is { Species: > 0 }) s++;
                    if (s >= SlotsPerBox && _clipboard.IndexOf((p, note, ts)) == 0) s = slot; // overwrite if first
                    else if (s >= SlotsPerBox) break;
                    // Capture existing slot if this paste position is occupied
                    var existingPaste = Den.GetSlot(CurrentBox, s);
                    if (existingPaste is { Species: > 0 })
                        _main.CaptureDeletedForRecent(existingPaste, Den.GetNote(CurrentBox, s));
                    Den.SetSlot(CurrentBox, s, p.Clone());
                    if (note is not null) Den.SetNote(CurrentBox, s, note);
                    Den.SetTimestamp(CurrentBox, s, ts ?? DateTime.Now);
                    s++;
                }
                RefreshGrid(); _main.NotifyMainDenChanged();
            }));
        }

        if (menu.Items.Count > 0) menu.Show(pb, location);
    }

    // Drag & drop
    private void SlotMouseMove(PictureBox pb, MouseEventArgs e, int slot)
    {
        if (_dragSource != pb || e.Button != MouseButtons.Left) return;
        if (Math.Abs(e.X - _dragStart.X) < 6 && Math.Abs(e.Y - _dragStart.Y) < 6) return;
        var pk = Den.GetSlot(CurrentBox, slot);
        if (pk is not { Species: > 0 }) { _dragSource = null; return; }
        pb.DoDragDrop($"ARRANGE:{CurrentBox}:{slot}", DragDropEffects.Move);
        _dragSource = null;
    }

    private void SlotDragDrop(DragEventArgs e, int destSlot)
    {
        if (e.Data?.GetData(DataFormats.Text) is not string data) return;
        var parts = data.Split(':');
        if (parts.Length != 3) return;

        bool fromArrange = parts[0] == "ARRANGE";
        bool fromDen = parts[0] == "DEN";

        if (!fromArrange && !fromDen) return;

        int srcBox = int.Parse(parts[1]), srcSlot = int.Parse(parts[2]);
        Den.SwapSlots(srcBox, srcSlot, CurrentBox, destSlot);
        RefreshGrid();
        _main.NotifyMainDenChanged();
    }

    private void ArrangeForm_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.A when e.Control:
                ClearSelection();
                for (int i = 0; i < SlotsPerBox; i++)
                    if (Den.GetSlot(CurrentBox, i) is { Species: > 0 }) { selected.Add(CurrentBox * SlotsPerBox + i); slots[i].BackColor = Color.FromArgb(80, 140, 220); }
                e.Handled = true; break;
            case Keys.C when e.Control:
                if (selected.Count > 0)
                {
                    _clipboard.Clear();
                    foreach (var flat in selected.OrderBy(x => x))
                    {
                        int b = flat / SlotsPerBox, s = flat % SlotsPerBox;
                        var pk = Den.GetSlot(b, s);
                        if (pk is { Species: > 0 }) _clipboard.Add((pk.Clone(), Den.GetNote(b, s), Den.GetTimestamp(b, s)));
                    }
                    _main.SetStatus($"Copied {_clipboard.Count} Pokémon to clipboard.");
                }
                e.Handled = true; break;
            case Keys.V when e.Control:
                if (_clipboard.Count > 0)
                {
                    int slot = 0;
                    foreach (var (pk, note, ts) in _clipboard)
                    {
                        while (slot < SlotsPerBox && Den.GetSlot(CurrentBox, slot) is { Species: > 0 }) slot++;
                        if (slot >= SlotsPerBox) break;
                        Den.SetSlot(CurrentBox, slot, pk.Clone());
                        if (note is not null) Den.SetNote(CurrentBox, slot, note);
                        Den.SetTimestamp(CurrentBox, slot, ts ?? DateTime.Now);
                        slot++;
                    }
                    RefreshGrid(); _main.NotifyMainDenChanged();
                }
                e.Handled = true; break;
            case Keys.Delete:
                foreach (var flat in selected) _main.ClearSlotWithCapture(flat / SlotsPerBox, flat % SlotsPerBox);
                ClearSelection(); RefreshGrid(); _main.NotifyMainDenChanged();
                e.Handled = true; break;
            case Keys.Escape:
                ClearSelection(); e.Handled = true; break;
        }
    }
}
