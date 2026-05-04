using System;
using System.Windows.Forms;
using PKHeX.Core;

namespace PKDen;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Global unhandled exception handlers so crashes aren't silent
            Application.ThreadException += (_, e) =>
            {
                MessageBox.Show($"Unhandled exception:\n\n{e.Exception}", "PKDen — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                MessageBox.Show($"Fatal error:\n\n{e.ExceptionObject}", "PKDen — Fatal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // Initialize PKHeX core strings (English)
            LocalizeUtil.InitializeStrings("en");

            // Default to Artwork sprite mode — the only sprite set with complete Gen 9 coverage.
            try
            {
                PKHeX.Drawing.PokeSprite.SpriteUtil.ChangeMode(PKHeX.Drawing.PokeSprite.SpriteBuilderMode.SpritesArtwork5668);
                PKHeX.Drawing.PokeSprite.SpriteUtil.Initialize(new SAV9SV());
            }
            catch { /* fall back to default if anything goes wrong */ }

            // Index sprite-override PNGs that are embedded directly in PKDen's assembly
            // (see EmbeddedResource glob in PKDen.csproj). No external folders required —
            // a fresh PKDen.exe is fully self-contained. Falls back silently to PKHeX's
            // built-in artwork if anything goes wrong with manifest enumeration.
            // Must run AFTER SpriteUtil.ChangeMode so SpriteOverrides knows the canvas size to scale to.
            try { SpriteOverrides.Initialize(); }
            catch { /* overrides are best-effort; silent on failure */ }

            // Run MainForm — it shows its own "Loading PKDen…" overlay during the first Shown event
            // and pre-warms the Pokédex while that overlay is visible. Sprite generation requires
            // the form to be realized (visible HWNDs), so we can't reliably do it before Application.Run.
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed:\n\n{ex}", "PKDen — Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
