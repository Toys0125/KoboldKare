using System;
using System.Globalization;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Compatibility identifier for KoboldKare code that historically passed PUN ViewID values
    /// through RPCs and save/runtime references. The value is derived from the first 31 bits of
    /// the canonical 32-hex-character instance id. The world registry rejects collisions.
    /// </summary>
    public static class KoboldKareLegacyViewId
    {
        public static bool TryFromInstanceId(string instanceId, out int viewId)
        {
            viewId = 0;
            if (string.IsNullOrEmpty(instanceId) || instanceId.Length < 8)
            {
                return false;
            }

            if (!uint.TryParse(
                    instanceId.Substring(0, 8),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint raw))
            {
                return false;
            }

            viewId = (int)(raw & 0x7fffffffU);
            return viewId != 0;
        }

        public static bool TryFromInstanceId(string instanceId, byte subViewIndex, out int viewId)
        {
            if (subViewIndex == 0)
            {
                return TryFromInstanceId(instanceId, out viewId);
            }
            if (string.IsNullOrEmpty(instanceId))
            {
                viewId = 0;
                return false;
            }

            // FNV-1a over the canonical instance id plus sub-view index. Multi-view prefabs are rare,
            // but the deterministic hash keeps their legacy integer references stable on every client.
            uint hash = 2166136261U;
            for (int i = 0; i < instanceId.Length; i++)
            {
                char c = instanceId[i];
                hash ^= (byte)c;
                hash *= 16777619U;
                hash ^= (byte)(c >> 8);
                hash *= 16777619U;
            }
            hash ^= subViewIndex;
            hash *= 16777619U;

            viewId = (int)(hash & 0x7fffffffU);
            if (viewId == 0)
            {
                viewId = subViewIndex;
            }
            return true;
        }
    }
}
