using System;
using System.Runtime.InteropServices;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.FROST
{
    /// <summary>
    /// P/Invoke bindings for FROST FFI library
    /// Provides C# interface to Rust-based FROST threshold signatures
    /// </summary>
    public static class FrostNative
    {
        private const string DllName = "frost_ffi";

        // Error codes
        public const int SUCCESS = 0;
        public const int ERROR_NULL_POINTER = -1;
        public const int ERROR_INVALID_UTF8 = -2;
        public const int ERROR_SERIALIZATION = -3;
        public const int ERROR_CRYPTO_ERROR = -4;
        public const int ERROR_INVALID_PARAMETER = -5;

        /// <summary>
        /// Free a string allocated by Rust
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void frost_free_string(IntPtr ptr);

        /// <summary>
        /// DKG Round 1: Generate commitment
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int frost_dkg_round1_generate(
            ushort participantId,
            ushort maxSigners,
            ushort minSigners,
            out IntPtr outCommitment,
            out IntPtr outSecretPackage);

        /// <summary>
        /// DKG Round 2: Generate shares
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int frost_dkg_round2_generate_shares(
            [MarshalAs(UnmanagedType.LPStr)] string secretPackage,
            [MarshalAs(UnmanagedType.LPStr)] string commitmentsJson,
            out IntPtr outSharesJson,
            out IntPtr outRound2Secret);

        /// <summary>
        /// DKG Round 3: Finalize and get group public key
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int frost_dkg_round3_finalize(
            [MarshalAs(UnmanagedType.LPStr)] string round2SecretPackage,
            [MarshalAs(UnmanagedType.LPStr)] string round1PackagesJson,
            [MarshalAs(UnmanagedType.LPStr)] string round2PackagesJson,
            out IntPtr outGroupPubkey,
            out IntPtr outKeyPackage,
            out IntPtr outPubkeyPackage);

        /// <summary>
        /// Signing Round 1: Generate nonce commitments
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int frost_sign_round1_nonces(
            [MarshalAs(UnmanagedType.LPStr)] string keyPackageJson,
            out IntPtr outNonceCommitment,
            out IntPtr outNonceSecret);

        /// <summary>
        /// Signing Round 2: Generate signature share
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int frost_sign_round2_signature(
            [MarshalAs(UnmanagedType.LPStr)] string keyPackageJson,
            [MarshalAs(UnmanagedType.LPStr)] string nonceSecret,
            [MarshalAs(UnmanagedType.LPStr)] string nonceCommitmentsJson,
            [MarshalAs(UnmanagedType.LPStr)] string messageHashHex,
            out IntPtr outSignatureShare);

        /// <summary>
        /// Aggregate signature shares into final Schnorr signature
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int frost_sign_aggregate(
            [MarshalAs(UnmanagedType.LPStr)] string signatureSharesJson,
            [MarshalAs(UnmanagedType.LPStr)] string nonceCommitmentsJson,
            [MarshalAs(UnmanagedType.LPStr)] string messageHashHex,
            [MarshalAs(UnmanagedType.LPStr)] string pubkeyPackageJson,
            out IntPtr outSchnorrSignature);

        /// <summary>
        /// Get library version
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int frost_get_version(out IntPtr outVersion);

        /// <summary>
        /// Helper to convert IntPtr to string and free the memory
        /// </summary>
        public static string PtrToStringAndFree(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;

            try
            {
                var str = Marshal.PtrToStringAnsi(ptr);
                return str ?? string.Empty;
            }
            finally
            {
                frost_free_string(ptr);
            }
        }

        /// <summary>
        /// Wrapper for DKG Round 1 with automatic memory management
        /// </summary>
        public static (string commitment, string secretPackage, int errorCode) DKGRound1Generate(
            ushort participantId, 
            ushort maxSigners, 
            ushort minSigners)
        {
            IntPtr commitmentPtr = IntPtr.Zero;
            IntPtr secretPtr = IntPtr.Zero;

            try
            {
                int result = frost_dkg_round1_generate(
                    participantId, 
                    maxSigners, 
                    minSigners, 
                    out commitmentPtr, 
                    out secretPtr);
                
                if (result != SUCCESS)
                    return (string.Empty, string.Empty, result);

                var commitment = PtrToStringAndFree(commitmentPtr);
                var secret = PtrToStringAndFree(secretPtr);
                
                return (commitment, secret, result);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKGRound1Generate error: {ex.Message}", "FrostNative.DKGRound1Generate");
                return (string.Empty, string.Empty, ERROR_CRYPTO_ERROR);
            }
        }

        /// <summary>
        /// Wrapper for DKG Round 3 with automatic memory management
        /// </summary>
        public static (string groupPubkey, string keyPackage, string pubkeyPackage, int errorCode) DKGRound3Finalize(
            string round2SecretPackage, 
            string round1PackagesJson, 
            string round2PackagesJson)
        {
            IntPtr groupPubkeyPtr = IntPtr.Zero;
            IntPtr keyPackagePtr = IntPtr.Zero;
            IntPtr pubkeyPackagePtr = IntPtr.Zero;

            try
            {
                int result = frost_dkg_round3_finalize(
                    round2SecretPackage, 
                    round1PackagesJson, 
                    round2PackagesJson, 
                    out groupPubkeyPtr, 
                    out keyPackagePtr,
                    out pubkeyPackagePtr);
                
                if (result != SUCCESS)
                    return (string.Empty, string.Empty, string.Empty, result);

                var groupPubkey = PtrToStringAndFree(groupPubkeyPtr);
                var keyPackage = PtrToStringAndFree(keyPackagePtr);
                var pubkeyPackage = PtrToStringAndFree(pubkeyPackagePtr);
                
                return (groupPubkey, keyPackage, pubkeyPackage, result);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKGRound3Finalize error: {ex.Message}", "FrostNative.DKGRound3Finalize");
                return (string.Empty, string.Empty, string.Empty, ERROR_CRYPTO_ERROR);
            }
        }

        /// <summary>
        /// Wrapper for Signing Round 1 with automatic memory management
        /// </summary>
        public static (string nonceCommitment, string nonceSecret, int errorCode) SignRound1Nonces(string signingShare)
        {
            IntPtr commitmentPtr = IntPtr.Zero;
            IntPtr secretPtr = IntPtr.Zero;

            try
            {
                int result = frost_sign_round1_nonces(signingShare, out commitmentPtr, out secretPtr);
                
                if (result != SUCCESS)
                    return (string.Empty, string.Empty, result);

                var commitment = PtrToStringAndFree(commitmentPtr);
                var secret = PtrToStringAndFree(secretPtr);
                
                return (commitment, secret, result);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"SignRound1Nonces error: {ex.Message}", "FrostNative.SignRound1Nonces");
                return (string.Empty, string.Empty, ERROR_CRYPTO_ERROR);
            }
        }

        /// <summary>
        /// Wrapper for signature aggregation with automatic memory management
        /// </summary>
        public static (string schnorrSignature, int errorCode) SignAggregate(
            string signatureSharesJson,
            string nonceCommitmentsJson,
            string messageHash,
            string groupPubkey)
        {
            IntPtr signaturePtr = IntPtr.Zero;

            try
            {
                int result = frost_sign_aggregate(
                    signatureSharesJson,
                    nonceCommitmentsJson,
                    messageHash,
                    groupPubkey,
                    out signaturePtr);
                
                if (result != SUCCESS)
                    return (string.Empty, result);

                var signature = PtrToStringAndFree(signaturePtr);
                
                return (signature, result);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"SignAggregate error: {ex.Message}", "FrostNative.SignAggregate");
                return (string.Empty, ERROR_CRYPTO_ERROR);
            }
        }

        /// <summary>
        /// Get FROST library version
        /// </summary>
        public static string GetVersion()
        {
            IntPtr versionPtr = IntPtr.Zero;

            try
            {
                int result = frost_get_version(out versionPtr);
                
                if (result != SUCCESS)
                    return "Unknown";

                return PtrToStringAndFree(versionPtr);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"GetVersion error: {ex.Message}", "FrostNative.GetVersion");
                return "Error";
            }
        }
    }
}
