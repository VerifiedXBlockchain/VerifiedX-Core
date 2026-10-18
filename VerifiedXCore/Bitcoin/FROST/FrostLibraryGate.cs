using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// A node whose FROST library has not passed its startup check (still running, or failed: missing, or built for
    /// another architecture) cannot do its part in a key ceremony or a signing. It neither leads nor joins one, and it
    /// does not announce itself as an active vBTC validator, so peers do not pick it and stall a withdrawal on it.
    /// </summary>
    public static class FrostLibraryGate
    {
        public const string Unavailable = "FROST library unavailable";

        public static bool Ready => Globals.FrostFFIAvailable;

        private static readonly TaskCompletionSource CheckDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The startup check has finished, passed or not.</summary>
        public static void CheckFinished() => CheckDone.TrySetResult();

        /// <summary>Waits for the startup check to finish, at most <paramref name="timeout"/>; <see cref="Ready"/> then holds its result.</summary>
        public static Task WaitForCheckAsync(TimeSpan timeout) => Task.WhenAny(CheckDone.Task, Task.Delay(timeout));

        /// <summary>Logged once when the startup check fails.</summary>
        public static void ReportCheckFailed(string summary)
        {
            var message = $"[FROST FFI] {Unavailable}: this node will not lead or join vBTC key ceremonies or signings, and sends no vBTC validator heartbeat, until it restarts with a working library. {summary}";
            Console.WriteLine(message);
            ErrorLogUtility.LogError(message, "FrostLibraryGate");
        }
    }
}
