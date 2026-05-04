using System;
using System.Drawing;
using System.Windows.Forms;

namespace PKDen;

/// <summary>
/// Lightweight splash screen shown while PKDen does its expensive one-time initialization
/// (creating ~1100 Pokédex tiles, generating ~2200 sprite bitmaps with alpha-fade variants).
///
/// The splash exists primarily to give the user something to look at during the multi-second
/// startup. It's a borderless centered form with two labels: a big "Loading PKDen…" and a
/// smaller status line that updates as initialization proceeds.
///
/// Threading model: the splash runs on the SAME UI thread as everything else. We just call
/// <see cref="Application.DoEvents"/> periodically during long-running work so the splash
/// keeps painting and Windows doesn't mark the process as "Not Responding."
/// </summary>
public sealed class SplashForm : Form
{
    private readonly Label _statusLabel;

    public SplashForm()
    {
        // Borderless centered form with a dark theme matching PKDen's main UI.
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 480;
        Height = 200;
        BackColor = Color.FromArgb(30, 32, 40);
        ForeColor = Color.White;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;

        // Subtle outer border so the splash reads as a discrete window even with no chrome
        Padding = new Padding(2);

        // Title label — big, bold, centered
        var titleLabel = new Label
        {
            Text = "Loading PKDen…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
        };

        // Status label — smaller, sits at the bottom and updates with the current task
        _statusLabel = new Label
        {
            Text = "Initializing…",
            Dock = DockStyle.Bottom,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(180, 180, 200),
            Font = new Font("Segoe UI", 9),
        };

        Controls.Add(titleLabel);
        Controls.Add(_statusLabel);
    }

    /// <summary>
    /// Updates the status line and forces a repaint so the message is visible immediately.
    /// Call this from the UI thread between heavy initialization steps.
    /// </summary>
    public void SetStatus(string message)
    {
        _statusLabel.Text = message;
        // Refresh forces an immediate synchronous paint — without it the new text waits
        // for the next message-loop iteration and may not show before the next blocking call.
        _statusLabel.Refresh();
        // Pump the message loop briefly so Windows doesn't decide the app is hung.
        Application.DoEvents();
    }

    /// <summary>
    /// Paints a thin border around the splash so it reads as a discrete window
    /// even though it has no normal chrome.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(80, 90, 110), 2);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
