// TEMPORARY, NOT COMMITTED: which message did a historical TransferCoinMulti input sign?
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempSigProbe
    {
        [Fact]
        public void Probe()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            var list = Environment.GetEnvironmentVariable("SCAN_TXS");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(list)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;
            Globals.CustomPath = root;
            DbContext.Initialize();
            using var w = new StreamWriter(Path.Combine(root, "sigprobe.txt"));
            foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':');
                var h = long.Parse(parts[0]);
                var tx = BlockchainData.GetBlocks().Query().Where(b => b.Height == h).First().Transactions.First(t => t.Hash == parts[1]);
                var j = JObject.Parse(tx.Data);
                var sigInput = j["SignatureInput"]?.ToString() ?? "";
                w.WriteLine($"=== {h} {tx.Hash} from={tx.FromAddress} to={tx.ToAddress} sigInput={sigInput} amount={j["Amount"]}");
                w.WriteLine($"outer signature valid: {SignatureService.VerifySignature(tx.FromAddress, tx.Hash, tx.Signature)}");
                try
                {
                    var tail = tx.Signature.Split('.', 2)[1];
                    var dec = VerifiedXCore.Utilities.Base58Utility.Base58Decode(tail);
                    w.WriteLine($"pubkey bytes={dec.Length}");
                    var hex = Convert.ToHexString(dec).ToLower();
                    if (dec.Length == 63) hex = "00" + hex;
                    var pk = VerifiedXCore.EllipticCurve.PublicKey.fromString(Convert.FromHexString(hex));
                    var addr = AccountData.GetHumanAddress("04" + Convert.ToHexString(pk.toString()).ToLower());
                    w.WriteLine($"derived address={addr} matches={addr == tx.FromAddress}");
                    w.WriteLine($"ecdsa over stored hash={VerifiedXCore.EllipticCurve.Ecdsa.verify(tx.Hash, VerifiedXCore.EllipticCurve.Signature.fromBase64(tx.Signature.Split('.')[0]), pk)}");
                    var recomputed = tx.GetHash();
                    w.WriteLine($"recomputed hash={recomputed} equalsStored={recomputed == tx.Hash}");
                    w.WriteLine($"ecdsa over recomputed={VerifiedXCore.EllipticCurve.Ecdsa.verify(recomputed, VerifiedXCore.EllipticCurve.Signature.fromBase64(tx.Signature.Split('.')[0]), pk)}");
                }
                catch (Exception ex) { w.WriteLine("probe error: " + ex.GetType().Name + " " + ex.Message); }
                foreach (var input in (JArray)j["Inputs"]!)
                {
                    var from = input["FromAddress"]!.ToString();
                    var sig = input["Signature"]!.ToString();
                    var candidates = new Dictionary<string, string>
                    {
                        ["sig+to+from"] = sigInput + tx.ToAddress + tx.FromAddress,
                        ["sig"] = sigInput,
                        ["sig+from+to"] = sigInput + tx.FromAddress + tx.ToAddress,
                        ["sig+to+inputFrom"] = sigInput + tx.ToAddress + from,
                        ["sig+to"] = sigInput + tx.ToAddress,
                        ["sig+from"] = sigInput + tx.FromAddress,
                    };
                    var hits = candidates.Where(c => { try { return SignatureService.VerifySignature(from, c.Value, sig); } catch { return false; } }).Select(c => c.Key).ToList();
                    w.WriteLine($"input scuid={input["SCUID"]} from={from} amount={input["Amount"]} verifies-with=[{string.Join(",", hits)}] sigTail={sig.Substring(sig.IndexOf('.') + 1)}");
                }
            }
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
