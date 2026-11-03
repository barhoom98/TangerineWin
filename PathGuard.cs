using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TangerineWin;

/// <summary>
/// Restricts the app to files on local drives that are explicitly allowed (default: C: only).
/// Anything else - other drive letters (e.g. a mapped Z: server drive), UNC paths (\\server\share) -
/// is rejected by looking at the path string only, before the app touches the file in any way.
/// </summary>
internal static class PathGuard
{
    /// <summary>Drive letters the app may work with. Change this to allow more local drives.</summary>
    public static readonly string[] AllowedDrives = { "C" };

    private static readonly Lazy<bool> AllowedDrivesAreLocal = new(() =>
    {
        try
        {
            // every allowed drive must be a local fixed disk (not a mapped network drive)
            return AllowedDrives.All(d => new DriveInfo(d).DriveType == DriveType.Fixed);
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false; // UNC and \\?\ paths

        string full;
        try { full = Path.GetFullPath(path); }   // pure string work, no disk or network access
        catch { return false; }

        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (full.Length < 3 || full[1] != ':' || (full[2] != '\\' && full[2] != '/')) return false;

        char letter = char.ToUpperInvariant(full[0]);
        if (!AllowedDrives.Any(d => char.ToUpperInvariant(d[0]) == letter)) return false;

        return AllowedDrivesAreLocal.Value;
    }

    /// <summary>True if the item itself is a symlink / junction whose target is outside the allowed drives.</summary>
    private static bool LinksOutside(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget == null) return false;           // not a symlink / junction
            string? target = info.ResolveLinkTarget(true)?.FullName;
            return target == null || !IsAllowed(target);
        }
        catch
        {
            return true; // when in doubt, refuse
        }
    }

    /// <summary>
    /// Returns the paths unchanged if all of them are allowed; if even one is not,
    /// returns an empty array and reports how many were blocked (the whole drop is refused).
    /// </summary>
    public static string[] FilterAll(IEnumerable<string> paths, out int blocked)
    {
        string[] list = paths.ToArray();
        blocked = list.Count(p => !IsAllowed(p) || LinksOutside(p));
        return blocked == 0 ? list : Array.Empty<string>();
    }
}
