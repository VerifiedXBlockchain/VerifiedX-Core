namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// Values of <c>key=value</c> start arguments that are credentials (the API token, the wallet password). The key is
    /// matched without regard to case or leading dashes; the value is taken exactly as given, including any further
    /// '=' characters. The general argument loop lowercases every argument and splits at each '=', which changed a
    /// mixed-case token or password, and cut one containing '=' short.
    /// </summary>
    public static class StartArgs
    {
        public static bool TryGetValue(string? rawArg, string key, out string value)
        {
            value = "";
            if (string.IsNullOrEmpty(rawArg))
                return false;
            var arg = rawArg.TrimStart('-');
            var prefix = key + "=";
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            value = arg[prefix.Length..];
            return true;
        }
    }
}
