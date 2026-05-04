using System;
using System.Collections.Generic;
using System.IO;

namespace PKDen;

/// <summary>
/// Persistent per-save-file thumbnail override.  When the user picks a custom thumbnail
/// for a particular save (via right-click → Change Thumbnail… on a card), the choice is
/// stored here as <c>save file path → embedded resource name</c> and persisted to
/// <c>PKDen.thumbnails</c> next to the executable.  Survives application restarts.
///
/// === Why a separate file (not PKDen.cfg)? ===
/// PKDen.cfg is a flat key=value file consumed by MainForm.  Thumbnail overrides can
/// number in the dozens (one per save file the user has customized) and using long
/// file paths as keys would clutter the cfg.  Separate file also makes it trivial for
/// power users to back up or share their thumbnail choices independently.
///
/// === File format ===
/// Plain UTF-8 text, one record per line:
///     <savePath>|<resourceName>
/// We use '|' as separator because Windows save paths legitimately contain '=' less
/// often than they contain ':' or '\'.  Lines beginning with '#' are comments.
/// Reads silently skip malformed lines so a corrupted file degrades gracefully.
/// </summary>
public static class ThumbnailOverrides
{
    private static readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static readonly object _gate = new();

    /// <summary>Path to the persisted overrides file (next to the exe).</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "PKDen.thumbnails");

    /// <summary>
    /// Returns the user's chosen thumbnail resource name for the given save path,
    /// or null if no override is set (in which case the auto-detected artwork is used).
    /// </summary>
    public static string? Get(string savePath)
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _overrides.TryGetValue(savePath, out var resourceName) ? resourceName : null;
        }
    }

    /// <summary>
    /// Sets a custom thumbnail for a given save file.  Pass null/empty resourceName
    /// to remove the override (revert to auto-detected artwork).  Persists immediately
    /// so the choice survives a crash before normal exit.
    /// </summary>
    public static void Set(string savePath, string? resourceName)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(resourceName)) _overrides.Remove(savePath);
            else _overrides[savePath] = resourceName;
        }
        SaveToDisk();
    }

    /// <summary>Loads overrides from disk on first use.  No-op if already loaded.</summary>
    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(FilePath)) return;
            try
            {
                foreach (var rawLine in File.ReadAllLines(FilePath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int sep = line.IndexOf('|');
                    if (sep <= 0 || sep == line.Length - 1) continue; // malformed — silently skip
                    var path = line[..sep].Trim();
                    var resource = line[(sep + 1)..].Trim();
                    if (path.Length > 0 && resource.Length > 0)
                        _overrides[path] = resource;
                }
            }
            catch { /* unreadable file → just use empty overrides */ }
        }
    }

    /// <summary>Writes the in-memory overrides to disk.  Best-effort — failures are silent.</summary>
    private static void SaveToDisk()
    {
        try
        {
            var lines = new List<string> { "# PKDen thumbnail overrides — <savePath>|<resourceName>" };
            lock (_gate)
            {
                foreach (var (path, resource) in _overrides)
                    lines.Add($"{path}|{resource}");
            }
            File.WriteAllLines(FilePath, lines);
        }
        catch { /* best-effort; loss of one save's preference is non-fatal */ }
    }
}
