using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.ElectrumX.Request;
using VerifiedXCore.Bitcoin.ElectrumX.Response;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace VerifiedXCore.Bitcoin.ElectrumX
{
    public class Client : IDisposable
    {
        #region Variables
        public event Action<Client, int, string> OnError;
        protected readonly string Host;
        protected readonly int Port;
        protected readonly bool UseSsl;
        protected TcpClient? TcpClient;
        protected Stream? Stream;
        private const int MaxResponseSize = 10 * 1024 * 1024; // 10 MB max response size
        public static Version CurrentVersion;
        protected bool IsConnected;
        protected static NBitcoin.Network Network;

        /// <summary>
        /// Default bound on TCP connect plus TLS handshake. The connect used to have no bound at all: a server that
        /// never answered (packets dropped, not refused) hung every call routed to it, and since the attempt never
        /// finished its FailCount never rose, so it was picked again (tester report MTI#11).
        /// </summary>
        public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Default bound on one request/response once connected.</summary>
        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

        public TimeSpan ConnectTimeout { get; set; } = DefaultConnectTimeout;
        public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;

        /// <summary>
        /// The last command got a well-formed answer without a JSON-RPC error. Commands still return an empty
        /// result on failure (existing callers rely on that), so a caller that must not mistake a failure for a
        /// zero balance or an empty list checks this.
        /// </summary>
        public bool LastCallSucceeded { get; private set; }

        /// <summary>
        /// The server answered the last command, possibly with a JSON-RPC error (an unknown transaction, say).
        /// False means it could not be reached or did not answer in time: the server-health signal.
        /// </summary>
        public bool LastCallAnswered { get; private set; }

        /// <summary>Why the last command did not succeed, for logs.</summary>
        public string? LastFailure { get; private set; }

        public string ServerLabel => $"{Host}:{Port}";

        public Client(string host, int port, bool useSsl = false)
        {
            Host = host;
            Port = port;
            UseSsl = useSsl;
            Network = Globals.BTCNetwork;
        }

        public Client(ClientSettings settings) : this(settings.Host, settings.Port, settings.UseSsl) { }

        #endregion

        #region Connect/Disconnect/Exchange

        private async Task Connect()
        {
            if (IsConnected)
                return;

            using var cts = new CancellationTokenSource(ConnectTimeout);
            // WaitAsync is the backstop: whether the DNS step inside ConnectAsync honors the token is platform-dependent.
            await Observe(ConnectCore(cts.Token)).WaitAsync(ConnectTimeout);
            IsConnected = true;
        }

        private async Task ConnectCore(CancellationToken token)
        {
            // Locals, not the fields: after a timeout Disconnect() clears the fields while this may still be running.
            var tcp = new TcpClient();
            TcpClient = tcp;
            await tcp.ConnectAsync(Host, Port, token);
            if (UseSsl)
            {
                var ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chan, sslPolicy) => true);
                Stream = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, token);
            }
            else
            {
                Stream = tcp.GetStream();
            }
        }

        /// <summary>One request, one answer, then the connection is closed. Empty string on any failure (LastFailure says why).</summary>
        private async Task<string> Exchange(byte[] requestData)
        {
            try
            {
                await Connect();
                using var cts = new CancellationTokenSource(RequestTimeout);
                return await Observe(ReadAnswer(requestData, cts.Token)).WaitAsync(RequestTimeout);
            }
            catch (Exception ex)
            {
                LastFailure = ex is TimeoutException || ex is OperationCanceledException ? "timed out" : ex.Message;
                return string.Empty;
            }
            finally
            {
                Disconnect();
            }
        }

        private async Task<string> ReadAnswer(byte[] requestData, CancellationToken token)
        {
            var stream = Stream ?? throw new IOException("not connected");
            await stream.WriteAsync(requestData, 0, requestData.Length, token);

            var buffer = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            var decoder = Encoding.UTF8.GetDecoder(); // keeps a multi-byte character split across reads intact
            var response = new StringBuilder();
            var totalBytesRead = 0;

            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (read == 0)
                    throw new IOException("connection closed before a complete answer");

                totalBytesRead += read;
                if (totalBytesRead > MaxResponseSize)
                    throw new IOException("answer exceeded the size limit");

                response.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));

                var text = response.ToString();
                if (IsCompleteJson(text))
                    return text;
            }
        }

        /// <summary>A task abandoned by WaitAsync still faults when Disconnect tears its socket down; observe that fault.</summary>
        private static Task<T> Observe<T>(Task<T> task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }

        private static Task Observe(Task task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }

        private bool IsCompleteJson(string response)
        {
            response = response.Trim();
            if ((response.StartsWith("{") && response.EndsWith("}")) || // Object
                (response.StartsWith("[") && response.EndsWith("]")))   // Array
            {
                try
                {
                    JToken.Parse(response);
                    return true;
                }
                catch (JsonReaderException)
                {
                    // Not a complete JSON
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Sends one request and parses the answer, recording LastCallAnswered / LastCallSucceeded / LastFailure.
        /// Null when nothing usable came back; a JSON-RPC error still returns the response (LastCallSucceeded false).
        /// </summary>
        private async Task<T?> Call<T>(byte[] requestData) where T : class, IElectrumResponse
        {
            LastCallSucceeded = false;
            LastCallAnswered = false;
            LastFailure = null;

            var buff = await Exchange(requestData);
            if (string.IsNullOrEmpty(buff))
                return null;

            T? response;
            try
            {
                response = JsonConvert.DeserializeObject<T>(buff);
            }
            catch (Exception ex)
            {
                LastFailure = $"malformed answer: {ex.Message}";
                return null;
            }

            if (response == null)
            {
                LastFailure = "empty answer";
                return null;
            }

            LastCallAnswered = true;
            if (response.Error != null)
            {
                LastFailure = $"server error {response.Error.Code}: {response.Error.Message}";
                OnError?.Invoke(this, response.Error.Code, response.Error.Message);
                return response;
            }

            LastCallSucceeded = true;
            return response;
        }

        /// <summary>A successful call whose answer carried no result is not a success.</summary>
        private T? Succeeded<T>(T? result) where T : class
        {
            if (result == null && LastCallSucceeded)
            {
                LastCallSucceeded = false;
                LastFailure = "answer carried no result";
            }
            return result;
        }

        protected void Disconnect()
        {
            IsConnected = false;
            Stream?.Dispose();
            TcpClient?.Dispose();
            Stream = null;
            TcpClient = null;
        }
        protected void Dispose(bool disposing)
        {
            if (!disposing) return;
            Disconnect();
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        public static string GetScriptHash(IDestination addr)
        {
            var witBytes = addr.ScriptPubKey.WitHash.ToBytes();
            Array.Reverse(witBytes);
            return witBytes.Aggregate("", (current, c) => current + $"{c:x2}");
        }

        #endregion

        #region Commands

        #region Broadcast
        public async Task<BlockchainTransactionBroadcastResult> Broadcast(string hexTx)
        {
            if (string.IsNullOrEmpty(hexTx))
                return new BlockchainTransactionBroadcastResult();

            var response = await Call<BlockchainTransactionBroadcastResponse>(new BlockchainTransactionBroadcastRequest(hexTx).GetRequestData());
            if (response == null)
            {
                VerifiedXCore.Utilities.ErrorLogUtility.LogError(
                    $"Electrum broadcast: no answer from {Host}:{Port} ({LastFailure})",
                    "Client.Broadcast");
                return new BlockchainTransactionBroadcastResult();
            }
            if (response.Error != null)
            {
                VerifiedXCore.Utilities.ErrorLogUtility.LogError(
                    $"Electrum broadcast REJECTED: code={response.Error.Code}, message={response.Error.Message}, host={Host}:{Port}",
                    "Client.Broadcast");
                return new BlockchainTransactionBroadcastResult { ErrorMessage = response.Error.Message };
            }
            return response.GetResultModel();
        }
        #endregion

        #region Create Address
        private static BitcoinAddress CreateBitcoinAddress(string address)
        {
            var prefix = "";

            return string.IsNullOrEmpty(address) ? null : BitcoinAddress.Create(prefix + address, Globals.BTCNetwork);
        }
        #endregion

        #region Get History
        public async Task<List<BlockchainScripthashGetHistoryResult>> GetHistory(IDestination address)
        {
            if (address == null)
                return new List<BlockchainScripthashGetHistoryResult>();
            return await GetHistoryByScriptHash(GetScriptHash(address));
        }

        public async Task<List<BlockchainScripthashGetHistoryResult>> GetHistory(string baseAddr)
        {
            if (string.IsNullOrEmpty(baseAddr))
                return new List<BlockchainScripthashGetHistoryResult>();
            return await GetHistoryByScriptHash(GetScriptHash(BitcoinAddress.Create(baseAddr, Globals.BTCNetwork)));
        }

        private async Task<List<BlockchainScripthashGetHistoryResult>> GetHistoryByScriptHash(string scriptHash)
        {
            var response = await Call<BlockchainScripthashGetHistoryResponse>(new BlockchainScripthashGetHistoryRequest(scriptHash).GetRequestData());
            return Succeeded(LastCallSucceeded ? response!.GetResultModel() : null) ?? new List<BlockchainScripthashGetHistoryResult>();
        }

        #endregion

        #region Get Confirms
        public async Task<int> GetConfirms(string txHash)
        {
            if (string.IsNullOrEmpty(txHash))
                return -1;
            var response = await Call<BlockchainTransactionGetConfirmsResponse>(new BlockchainTransactionGetRequest(txHash, true).GetRequestData());
            return response?.GetResultModel() ?? -1;
        }
        #endregion

        #region Get Balance
        public async Task<BlockchainScripthashGetBalanceResult> GetBalance(string address, bool isLegacy)
        {
            return await GetBalance(BitcoinAddress.Create(address, Globals.BTCNetwork));
        }

        public async Task<BlockchainScripthashGetBalanceResult> GetBalance(IDestination address)
        {
            var response = await Call<BlockchainScripthashGetBalanceResponse>(new BlockchainScripthashGetBalance(GetScriptHash(address)).GetRequestData());
            return Succeeded(LastCallSucceeded ? response!.GetResultModel() : null) ?? new BlockchainScripthashGetBalanceResult();
        }
        #endregion

        #region Get List Unspent
        public async Task<List<BlockchainScripthashListunspentResult>> GetListUnspent(string address, bool isLegacy)
        {

            var bAddr = CreateBitcoinAddress(address);
            return await GetListUnspent(bAddr);
        }
        public async Task<List<BlockchainScripthashListunspentResult>> GetListUnspent(IDestination address)
        {
            var scriptHash = GetScriptHash(address);
            return await GetListUnspent(scriptHash);
        }

        public async Task<List<BlockchainScripthashListunspentResult>> GetListUnspent(string scriptHash)
        {
            var response = await Call<BlockchainScripthashListunspentResponse>(new BlockchainScripthashListunspentRequest(scriptHash).GetRequestData());
            return Succeeded(LastCallSucceeded ? response!.GetResultModel() : null) ?? new List<BlockchainScripthashListunspentResult>();
        }
        #endregion

        #region Get Server Version
        public async Task<ServerVersionResult> GetServerVersion()
        {
            var response = await Call<ServerVersionResponse>(new ServerVersionRequest().GetRequestData());
            var valid = LastCallSucceeded && response!.Result?.Length >= 2;
            return Succeeded(valid ? response!.GetResultModel() : null) ?? new ServerVersionResult();
        }
        #endregion

        #region Get Tip Height
        /// <summary>The server's current block height (blockchain.headers.subscribe), or null when it did not answer.</summary>
        public async Task<int?> GetTipHeight()
        {
            var response = await Call<BlockchainHeadersTipResponse>(new BlockchainHeadersSubscribeRequest().GetRequestData());
            var result = Succeeded(LastCallSucceeded ? response!.Result : null);
            return result?.Height;
        }
        #endregion

        #region Get Raw Tx
        public async Task<BlockchainTransactionGetResult> GetRawTx(string txHash)
        {
            if (string.IsNullOrEmpty(txHash))
                return new BlockchainTransactionGetResult();
            var response = await Call<BlockchainTransactionGetResponse>(new BlockchainTransactionGetRequest(txHash).GetRequestData());
            return Succeeded(LastCallSucceeded && response!.Result != null ? response.GetResultModel() : null) ?? new BlockchainTransactionGetResult();
        }
        #endregion

        #region Get Block Header Hex
        public async Task<BlockchainBlockHeaderGetResult> GetBlockHeaderHex(int height)
        {
            var response = await Call<BlockchainBlockHeaderGetResponse>(new BlockchainBlockHeaderGetRequest(height, 1).GetRequestData());
            return Succeeded(LastCallSucceeded ? response!.Result : null);
        }

        #endregion

        #region Get Block Transaction  Merkle
        public async Task<BlockchainTransactionGetMerkleResult> GetBlockchainTransactionGetMerkle(string txId, int height)
        {
            var response = await Call<BlockchainTransactionGetMerkleResponse>(new BlockchainTransactionGetMerkleRequest(txId, height).GetRequestData());
            return Succeeded(LastCallSucceeded ? response!.GetResultModel() : null) ?? new BlockchainTransactionGetMerkleResult();
        }
        #endregion

        #endregion
    }
}
