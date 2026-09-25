using System.Runtime.CompilerServices;
using VerifiedXCore.Utilities;

// Root namespace on purpose: resolves unqualified from VerifiedXCore.Controllers and VerifiedXCore.Bitcoin.Controllers.
namespace VerifiedXCore
{
    /// <summary>
    /// VX-23: the only way an exception reaches an API response. Routes used to return ex.ToString() (type, message,
    /// stack trace with source paths and line numbers — the release build ships portable PDBs) or ex.Message straight to
    /// the caller. The full exception now goes to the node's error log; the caller gets a short, stack-free text.
    /// </summary>
    public static class ApiErrorText
    {
        /// <summary>
        /// Operator-facing API (wallet, local routes behind the API token / origin guard): exception type and the first
        /// line of its message, never the stack trace. The full exception is logged with the calling route.
        /// </summary>
        public static string For(Exception? ex, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            Log(ex, caller, file);
            if (ex == null) return "Unexpected error.";
            return $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
        }

        /// <summary>Network-facing API (validator/consensus host): no exception detail at all.</summary>
        public static string Generic(Exception? ex, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            Log(ex, caller, file);
            return "Request failed";
        }

        private static void Log(Exception? ex, string caller, string file)
        {
            try { ErrorLogUtility.LogError(ex?.ToString() ?? "(null exception)", $"{Path.GetFileNameWithoutExtension(file)}.{caller}"); }
            catch { /* logging must never break the response */ }
        }

        private static string FirstLine(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var line = s.Split('\n')[0].Trim();
            return line.Length > 200 ? line.Substring(0, 200) : line;
        }
    }
}
