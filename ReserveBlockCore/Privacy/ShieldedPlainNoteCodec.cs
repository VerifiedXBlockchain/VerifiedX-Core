using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    public static class ShieldedPlainNoteCodec
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public static byte[] SerializeToUtf8Bytes(ShieldedPlainNote note)
        {
            var json = JsonConvert.SerializeObject(note, Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public static bool TryDeserializeUtf8(ReadOnlySpan<byte> utf8, out ShieldedPlainNote? note, out string? error)
        {
            note = null;
            error = null;
            try
            {
                var text = Encoding.UTF8.GetString(utf8);
                note = JsonConvert.DeserializeObject<ShieldedPlainNote>(text, Settings);
                if (note == null || note.Version != 1)
                {
                    error = "Invalid shielded plain note.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(note.RandomnessB64) || string.IsNullOrWhiteSpace(note.AssetType))
                {
                    error = "Shielded plain note missing required fields.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
