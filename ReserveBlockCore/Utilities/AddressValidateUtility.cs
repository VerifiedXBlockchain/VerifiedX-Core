
using ReserveBlockCore.Models;
using ReserveBlockCore.Privacy;
using System.Security.Cryptography;

namespace ReserveBlockCore.Utilities
{
    internal class AddressValidateUtility
    {
        public static bool ValidateAddress(string addr)
        {
            var result = false;

            if (!string.IsNullOrEmpty(addr) &&
                addr.StartsWith(ShieldedAddressConstants.Prefix, StringComparison.Ordinal))
                return ShieldedAddressCodec.IsWellFormed(addr);

			var adnrCheck = (addr.ToLower().Contains(".rbx") || addr.ToLower().Contains(".vfx")) ? true : false;

            if (adnrCheck)
            {
				var adnr = Adnr.GetAdnr();
				var adnrExist = adnr.FindOne(x => x.Name == addr.ToLower());
				if(adnrExist != null)
                {
					addr = adnrExist.Address;
					result = ValidateRBXAddress(addr);
				}
            }
            else
            {
				if(addr.StartsWith("xRBX"))
				{
                    result = ValidateRBXAddress(addr);
                }
				else
				{
                    result = ValidateRBXAddress(addr);
                }
			}
			
			return result;
        }

		const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
		const int Size = 25;

		public static bool ValidateRBXAddress(string address)
		{
			if (address.Length < 26 || address.Length > 35) return false; // wrong length
			
			byte[] decoded;
			try
			{
				decoded = DecodeBase58(address);
			}
			catch
			{
				// FIND-010 FIX: Reject invalid Base58 or overflow
				return false;
			}
			
			// FIND-010 FIX: Enforce canonical encoding - re-encode and compare
			// This prevents alias addresses that decode to same bytes but aren't canonical
			var reEncoded = EncodeBase58(decoded);
			if (address != reEncoded)
			{
				return false; // Non-canonical encoding rejected
			}
			
			var d1 = Hash(decoded.SubArray(0, 21));
			var d2 = Hash(d1);
			if (!decoded.SubArray(21, 4).SequenceEqual(d2.SubArray(0, 4))) return false; //bad digest
			return true;
		}

		private static byte[] DecodeBase58(string input)
		{
			var output = new byte[Size];
			foreach (var t in input)
			{
				var p = Alphabet.IndexOf(t);
				if (p == -1) throw new Exception("invalid character found");
				var j = Size;
				while (--j >= 0)
				{
					p += 58 * output[j];
					output[j] = (byte)(p % 256);
					p /= 256;
				}
				// FIND-010 FIX: Restore overflow check to reject non-canonical encodings
				if (p != 0) throw new Exception("address too long");
			}
			return output;
		}

		private static string EncodeBase58(byte[] input)
		{
			// FIND-010 FIX: Canonical Base58 encoder for validation
			// Convert byte array to big integer
			System.Numerics.BigInteger value = 0;
			for (int i = 0; i < input.Length; i++)
			{
				value = value * 256 + input[i];
			}

			// Encode to Base58
			var result = new System.Text.StringBuilder();
			while (value > 0)
			{
				var remainder = (int)(value % 58);
				value /= 58;
				result.Insert(0, Alphabet[remainder]);
			}

			// Handle leading zeros
			for (int i = 0; i < input.Length && input[i] == 0; i++)
			{
				result.Insert(0, Alphabet[0]);
			}

			return result.ToString();
		}

		private static byte[] Hash(byte[] bytes)
		{
			var hasher = new SHA256Managed();
			return hasher.ComputeHash(bytes);
		}

		
	}
	public static class ArrayExtensions
	{
		public static T[] SubArray<T>(this T[] data, int index, int length)
		{
			var result = new T[length];
			Array.Copy(data, index, result, 0, length);
			return result;
		}
	}
}
