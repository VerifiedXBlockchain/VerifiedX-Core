using System;
using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    public static class PrivateTxPayloadCodec
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Decodes <paramref name="data"/> as UTF-8 JSON, or Base64-wrapped UTF-8 JSON when the trimmed value looks like Base64 and decodes to JSON.
        /// </summary>
        public static bool TryDecode(string? data, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PrivateTxPayload? payload, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
        {
            payload = null;
            error = null;
            if (string.IsNullOrWhiteSpace(data))
            {
                error = "Private transaction Data is empty.";
                return false;
            }

            var trimmed = data.Trim();
            string json;

            if (LooksLikeBase64Payload(trimmed))
            {
                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(trimmed);
                }
                catch
                {
                    error = "Private transaction Data is not valid Base64.";
                    return false;
                }
                try
                {
                    json = Encoding.UTF8.GetString(raw);
                }
                catch
                {
                    error = "Private transaction Data Base64 payload is not valid UTF-8.";
                    return false;
                }
            }
            else
            {
                json = trimmed;
            }

            try
            {
                payload = JsonConvert.DeserializeObject<PrivateTxPayload>(json, JsonSettings);
            }
            catch (Exception ex)
            {
                error = $"Private transaction Data JSON parse failed: {ex.Message}";
                return false;
            }

            if (payload == null)
            {
                error = "Private transaction Data deserialized to null.";
                return false;
            }

            return true;
        }

        /// <summary>JSON for <see cref="Transaction.Data"/> (UTF-8 text; validators accept Base64-wrapped JSON too).</summary>
        public static string SerializeToJson(PrivateTxPayload payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            return JsonConvert.SerializeObject(payload, JsonSettings);
        }

        private static bool LooksLikeBase64Payload(string s)
        {
            if (s.Length < 8 || (s.Length % 4 != 0))
                return false;
            if (s[0] == '{' || s[0] == '[')
                return false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c))
                    return false;
                if (char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=')
                    continue;
                return false;
            }
            return true;
        }
    }
}
