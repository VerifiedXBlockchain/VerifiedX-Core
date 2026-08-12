using System;
using LiteDB;

namespace VerifiedXCore.Data
{
    /// <summary>
    /// LiteDB type-name binder that resolves legacy "_type" metadata written by pre-rename
    /// (ReserveBlockCore) builds. LiteDB stamps polymorphic members (e.g. the object-typed
    /// SmartContractFeatures.FeatureFeatures) with "_type": "Type.FullName, AssemblyName";
    /// records written before the ReserveBlockCore → VerifiedXCore rename can no longer be
    /// resolved by the default binder. This binder rewrites those names on read (namespace
    /// prefix and assembly suffix, generic forms included) and retries. Writes are unchanged —
    /// new records always get current VerifiedXCore type names.
    /// </summary>
    public class LegacyTypeNameBinder : ITypeNameBinder
    {
        public static readonly LegacyTypeNameBinder Instance = new LegacyTypeNameBinder();

        public string GetName(Type type) => DefaultTypeNameBinder.Instance.GetName(type);

        public Type GetType(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var type = Type.GetType(name);
            if (type != null)
                return type;

            if (name.Contains("ReserveBlockCore"))
            {
                // "ReserveBlockCore.Models...., ReserveBlockCore" → "VerifiedXCore.Models...., VerifiedXCore".
                // The two targeted replaces also cover generic forms like
                // "System.Collections.Generic.List`1[[ReserveBlockCore...., ReserveBlockCore]], ...".
                var rewritten = name.Replace("ReserveBlockCore.", "VerifiedXCore.")
                                    .Replace(", ReserveBlockCore", ", VerifiedXCore");
                type = Type.GetType(rewritten);
            }

            return type; // null → LiteDB throws its normal "type not found" error
        }
    }
}
