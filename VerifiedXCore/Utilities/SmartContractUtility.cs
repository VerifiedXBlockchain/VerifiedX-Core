using System.IO.Compression;

namespace VerifiedXCore.Utilities
{
    public static class SmartContractUtility
    {
		public static byte[] Compress(byte[] bytes)
		{
			using (var memoryStream = new MemoryStream())
			{
				using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
				{
					gzipStream.Write(bytes, 0, bytes.Length);
				}
				return memoryStream.ToArray();
			}
		}

		/// <summary>
		/// VX-20 (adjacent): smart contract code arrives inside transactions and is decompressed by every node during
		/// validation (deploy binding, state updates). It was unbounded, so one transaction carrying a GZip bomb could
		/// exhaust memory on every validator. The bound is deliberately generous (contract source is text, far smaller)
		/// and identical on every node, so a rejection is deterministic.
		/// </summary>
		public const int MaxDecompressedContractBytes = 64 * 1024 * 1024;

		public static byte[] Decompress(byte[] bytes)
		{
			using (var memoryStream = new MemoryStream(bytes))
			using (var decompressStream = new GZipStream(memoryStream, CompressionMode.Decompress))
			{
				return VerifiedXCore.Extensions.GenericExtensions.ReadBounded(decompressStream, MaxDecompressedContractBytes);
			}
		}

		public static IEnumerable<string> Split(string str, int chunkSize)
		{
			return Enumerable.Range(0, str.Length / chunkSize)
				.Select(i => str.Substring(i * chunkSize, chunkSize));
		}

		public static string Unsplit(IEnumerable<string> split)
        {
			var output = "";

			var textUnsplit = "";
			split.ToList().ForEach(x => {
				textUnsplit += x;
			});

			output = textUnsplit;

			return output;
        }
	}
}
