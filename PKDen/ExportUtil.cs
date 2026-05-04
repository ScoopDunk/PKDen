using System;
using System.IO;
using System.Windows.Forms;
using PKHeX.Core;

namespace PKDen;

/// <summary>
/// Utilities for exporting Pokémon from Den storage as individual .pk files or database folders.
/// </summary>
public static class ExportUtil
{
    /// <summary>
    /// Builds a PKHeX-style filename for a PKM using PKHeX's own naming logic.
    /// Format: "{Species:0000}{-form}{★ if shiny} - {Nickname} - {Checksum:X4}{EncryptionConstant:X8}.pkN"
    /// (Gen 1-2 variants omit the encryption constant.)
    /// </summary>
    public static string BuildPKHeXFileName(PKM pk)
    {
        // Use PKHeX's built-in FileName property (from EntityFileNamer.DefaultEntityNamer)
        string fileName = pk.FileName;

        // Sanitize illegal filename characters (nicknames may contain them for non-Latin games)
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        return fileName;
    }

    /// <summary>
    /// Exports a single PKM to a file chosen by the user.
    /// </summary>
    public static void ExportSinglePKM(PKM pk, IWin32Window? owner = null)
    {
        using var sfd = new SaveFileDialog
        {
            FileName = BuildPKHeXFileName(pk),
            Filter = $"Pokémon File|*.{pk.Extension}|All Files|*.*",
        };
        if (sfd.ShowDialog(owner) != DialogResult.OK) return;

        WritePKMToFile(pk, sfd.FileName);
    }

    /// <summary>
    /// Exports all Pokémon from Den storage to a folder as individual .pk files.
    /// This creates a PKHeX-compatible database folder.
    /// </summary>
    public static int ExportDatabase(DenStorageManager den, IWin32Window? owner = null)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Select a folder to export the Pokémon database to.\nEach Pokémon will be saved as an individual .pk file.\nThis folder can be used as a PKHeX database.",
        };
        if (fbd.ShowDialog(owner) != DialogResult.OK)
            return -1;

        var path = fbd.SelectedPath;
        int count = 0;

        for (int b = 0; b < den.BoxCount; b++)
        {
            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = den.GetSlot(b, s);
                if (pk is not { Species: > 0 })
                    continue;

                string fileName = BuildPKHeXFileName(pk);
                string fullPath = ResolveUniquePath(path, fileName);
                WritePKMToFile(pk, fullPath);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Exports all Pokémon from Den storage organized into box sub-folders.
    /// </summary>
    public static int ExportDatabaseByBox(DenStorageManager den, IWin32Window? owner = null)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Select a root folder. Sub-folders will be created for each box.",
        };
        if (fbd.ShowDialog(owner) != DialogResult.OK)
            return -1;

        var rootPath = fbd.SelectedPath;
        int count = 0;

        for (int b = 0; b < den.BoxCount; b++)
        {
            if (den.GetBoxCount(b) == 0)
                continue;

            string boxName = den.GetBoxName(b);
            foreach (char c in Path.GetInvalidFileNameChars())
                boxName = boxName.Replace(c, '_');

            string boxPath = Path.Combine(rootPath, boxName);
            Directory.CreateDirectory(boxPath);

            for (int s = 0; s < DenStorageManager.SlotsPerBox; s++)
            {
                var pk = den.GetSlot(b, s);
                if (pk is not { Species: > 0 })
                    continue;

                string fileName = BuildPKHeXFileName(pk);
                string fullPath = ResolveUniquePath(boxPath, fileName);
                WritePKMToFile(pk, fullPath);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Writes a single PKM to a folder with a PKHeX-style filename.
    /// The boxName/slot parameters are ignored — retained for backward compatibility.
    /// </summary>
    public static void WritePKMToFolder(PKM pk, string folderPath, string boxName, int slot)
    {
        string fileName = BuildPKHeXFileName(pk);
        string fullPath = ResolveUniquePath(folderPath, fileName);
        WritePKMToFile(pk, fullPath);
    }

    /// <summary>Appends (2), (3), ... to filename if it already exists in the folder.</summary>
    private static string ResolveUniquePath(string folderPath, string fileName)
    {
        string fullPath = Path.Combine(folderPath, fileName);
        if (!File.Exists(fullPath)) return fullPath;

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int n = 2;
        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(folderPath, $"{baseName} ({n}){ext}");
            n++;
        }
        return fullPath;
    }

    public static void WritePKMToFile(PKM pk, string path)
    {
        pk.ForcePartyData();
        var data = new byte[pk.SIZE_PARTY];
        pk.WriteDecryptedDataParty(data);
        File.WriteAllBytes(path, data);
    }
}
