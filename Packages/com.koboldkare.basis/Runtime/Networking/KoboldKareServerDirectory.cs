using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.Networking;

namespace KoboldKare.Basis.Networking
{
    public readonly struct KoboldKareServerEntry
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string Description;
        public readonly string Address;
        public readonly ushort Port;
        public readonly string SourceName;

        public KoboldKareServerEntry(
            string id,
            string displayName,
            string description,
            string address,
            ushort port,
            string sourceName)
        {
            Id = id ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id ?? "Basis Server" : displayName;
            Description = description ?? string.Empty;
            Address = address ?? string.Empty;
            Port = port;
            SourceName = sourceName ?? string.Empty;
        }

        public string Endpoint
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Address))
                {
                    return string.Empty;
                }
                if (Address.IndexOf(':') >= 0 && !Address.StartsWith("[", StringComparison.Ordinal))
                {
                    return $"[{Address}]:{Port}";
                }
                return $"{Address}:{Port}";
            }
        }
    }

    public static class KoboldKareServerDirectory
    {
        public static async Task<IReadOnlyList<KoboldKareServerEntry>> QueryAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ServerDirectoryEntry> entries =
                await BasisServerDirectoryRegistry.QueryAllAsync(cancellationToken);
            var result = new List<KoboldKareServerEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                ServerDirectoryEntry entry = entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Address) || entry.Port == 0)
                {
                    continue;
                }
                result.Add(new KoboldKareServerEntry(
                    entry.Id,
                    entry.DisplayName,
                    entry.Description,
                    entry.Address,
                    entry.Port,
                    entry.SourceName));
            }

            result.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
