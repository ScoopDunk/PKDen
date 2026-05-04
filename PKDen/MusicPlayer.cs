using System;
using System.IO;

namespace PKDen;

/// <summary>
/// Background music player.  Wraps the Windows Media Player COM control to play
/// looping background music while PKDen is running.  Supports MP3, WAV, and
/// (on Windows 10+) FLAC out of the box without requiring any third-party
/// dependencies.
///
/// === Why WMP COM, not NAudio or another library? ===
/// PKDen is distributed as a single self-contained EXE for public release.
/// NAudio would add a NuGet dependency (~5 MB) and another moving part.
/// WMP COM is built into every Windows install since Vista, costs zero bytes
/// in the EXE, and gives us volume + seek + loop for free.
///
/// FLAC support: Windows 10 added native FLAC decode in 2015.  On Windows 10/11
/// FLAC files just play.  On older Windows we'll fail gracefully — the play
/// attempt throws and we surface a status message instead of crashing.
///
/// === Threading ===
/// All operations run on the UI thread.  WMP COM is single-threaded apartment
/// (STA) and PKDen's main thread is already STA (Windows Forms requires it),
/// so this is the natural fit.  We don't expose any threaded operations because
/// Form open/close already manages player lifetime correctly.
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    // We hold the COM object as a dynamic to avoid pulling in a primary interop
    // assembly (PIA) just for type definitions.  WMP's COM API is stable enough
    // that late binding is safe — and it keeps the build dependency-free.
    private dynamic? _wmp;
    private string? _currentPath;
    private int _volume = 50;
    private bool _isPlaying;
    private bool _disposed;

    public bool IsPlaying => _isPlaying;
    public string? CurrentPath => _currentPath;
    public int Volume => _volume;

    /// <summary>
    /// Loads <paramref name="filePath"/> and starts looping playback.  If a track
    /// is already playing, it's stopped first.  Returns true on success, false
    /// (with status reason in <paramref name="error"/>) on failure.
    /// </summary>
    /// <remarks>
    /// We set up a fresh WMP instance per Play() rather than reusing one so we
    /// don't carry stale event handlers between tracks.  The minor overhead
    /// (~5 ms COM activation) is invisible in normal use.
    /// </remarks>
    public bool Play(string filePath, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(filePath))
        {
            error = "No file path.";
            return false;
        }
        if (!File.Exists(filePath))
        {
            error = $"File not found: {filePath}";
            return false;
        }

        try
        {
            Stop();

            var wmpType = Type.GetTypeFromProgID("WMPlayer.OCX.7");
            if (wmpType is null)
            {
                error = "Windows Media Player not available on this system.";
                return false;
            }
            _wmp = Activator.CreateInstance(wmpType);
            if (_wmp is null)
            {
                error = "Failed to instantiate Windows Media Player.";
                return false;
            }

            // Configure: continuous loop, volume from saved setting.
            _wmp.settings.autoStart  = true;
            _wmp.settings.setMode("loop", true);
            _wmp.settings.volume     = _volume;
            // Keep UI hidden — we're using WMP purely as an audio engine.
            _wmp.uiMode              = "invisible";
            _wmp.URL                 = filePath;

            _currentPath = filePath;
            _isPlaying   = true;
            return true;
        }
        catch (Exception ex)
        {
            // Common failures: missing codec for a format (FLAC on pre-Win10),
            // corrupt file, permissions on the chosen path.  Surface the message
            // verbatim so the caller can put it in a status bar without guessing.
            error = ex.Message;
            try { _wmp = null; } catch { }
            _isPlaying   = false;
            _currentPath = null;
            return false;
        }
    }

    /// <summary>Stops playback and releases the WMP COM object.  Safe to call multiple times.</summary>
    public void Stop()
    {
        if (_wmp is null) { _isPlaying = false; return; }
        try
        {
            _wmp.controls.stop();
            _wmp.close();
        }
        catch { /* ignore — best-effort shutdown */ }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_wmp); }
            catch { }
            _wmp         = null;
            _isPlaying   = false;
            _currentPath = null;
        }
    }

    /// <summary>
    /// Sets playback volume.  <paramref name="volume"/> is clamped to 0–100.
    /// If a track is currently playing, the change applies immediately.
    /// </summary>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_wmp is not null)
        {
            try { _wmp.settings.volume = _volume; }
            catch { /* WMP can throw if the player is mid-state-change; ignore */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
