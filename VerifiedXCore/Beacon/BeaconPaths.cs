using VerifiedXCore.Utilities;

namespace VerifiedXCore.Beacon
{
    /// <summary>
    /// NEW-03 (found by the independent review; same class as VX-04): every beacon path built from a network-supplied
    /// asset name or contract UID goes through here. The beacon upload/download endpoints (HTTP, the fast TCP server and
    /// the legacy TCP server) concatenated the caller's file name and UID onto the beacon folder, so a registered asset
    /// named "../../x" wrote outside it (code execution via a startup/profile script) and a download read any file
    /// (e.g. the wallet database on a Windows beacon).
    /// </summary>
    public static class BeaconPaths
    {
        /// <summary>
        /// Resolves <paramref name="fileName"/> inside <paramref name="root"/> (and inside the UID's folder when a UID is
        /// given — the legacy TCP server stores flat). False for any name or UID that is not a plain file/folder name, or
        /// whose full path is not directly inside the expected folder.
        /// </summary>
        public static bool TryResolve(string root, string? scUid, string? fileName, out string fullPath)
        {
            fullPath = "";
            if (string.IsNullOrEmpty(root) || !NFTAssetFileUtility.IsSafeAssetFileName(fileName))
                return false;

            var rootFull = Path.GetFullPath(root);
            var folder = rootFull;
            if (scUid != null)
            {
                var scFolder = scUid.Replace(":", "");
                if (!NFTAssetFileUtility.IsSafeScUidFolder(scFolder))
                    return false;
                folder = Path.GetFullPath(Path.Combine(rootFull, scFolder));
            }

            var candidate = Path.GetFullPath(Path.Combine(folder, fileName!));
            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return false;
            // NEW-03 (follow-up): Windows trims trailing spaces and dots when resolving, so "a.exe " (extension ".exe ",
            // not on the reject list) was written as "a.exe". The resolved name must be exactly the requested name.
            if (!string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal))
                return false;

            fullPath = candidate;
            return true;
        }

        /// <summary>NEW-03: extension reject list, compared case-insensitively (".EXE" passed a list containing ".exe").</summary>
        public static bool ExtensionAllowed(string? fileName)
        {
            // NEW-03 (follow-up): a trailing space or dot hides the real extension from the list (Windows drops it).
            if (fileName == null || fileName.EndsWith(' ') || fileName.EndsWith('.')) return false;
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return false;
            return !Globals.RejectAssetExtensionTypes.Any(r => string.Equals(r, ext, StringComparison.OrdinalIgnoreCase));
        }
    }
}
