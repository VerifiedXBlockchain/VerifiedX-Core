using System.Security;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Gives a test's encrypted wallet a real keystore record under <paramref name="password"/>, so code that verifies
    /// the wallet password (VX-13 follow-up: sealing Bitcoin keys) sees a genuine wallet.
    /// </summary>
    internal static class TestWalletKeystore
    {
        public static void Ensure(string password)
        {
            WalletEncryptionService.ResetPasswordChecks();
            var prior = Globals.EncryptPassword;
            var pw = new SecureString();
            foreach (var c in password) pw.AppendChar(c);
            Globals.EncryptPassword = pw;
            var account = AccountData.CreateNewAccount(skipSave: true);
            var ks = WalletEncryptionService.EncryptWallet(account).GetAwaiter().GetResult();
            if (ks != null) Keystore.SaveKeystore(ks);
            Globals.EncryptPassword = prior;
        }
    }
}
