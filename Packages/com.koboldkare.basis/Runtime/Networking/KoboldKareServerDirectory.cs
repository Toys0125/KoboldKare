using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Basis.Network.Core;
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
            var result = new List<KoboldKareServerEntry>();
            IReadOnlyList<IServerDirectorySource> sources = BasisServerDirectoryRegistry.Sources;

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IServerDirectorySource source = sources[sourceIndex];
                if (source == null)
                {
                    continue;
                }

                try
                {
                    await source.RefreshAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"Server directory source '{source.SourceId}' refresh failed: {ex.Message}");
                }

                IReadOnlyList<ServerDirectoryEntry> entries;
                try
                {
                    entries = await source.ListAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"Server directory source '{source.SourceId}' list failed: {ex.Message}");
                    continue;
                }

                if (entries == null)
                {
                    continue;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    ServerDirectoryEntry entry = entries[i];
                    ConnectionTarget target = entry?.Target;
                    string address = target?.Get(ConnectionTarget.Keys.Address, string.Empty) ?? string.Empty;
                    string portText = target?.Get(ConnectionTarget.Keys.Port, string.Empty) ?? string.Empty;
                    if (entry == null || string.IsNullOrWhiteSpace(address) ||
                        !ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) ||
                        port == 0)
                    {
                        continue;
                    }

                    string sourceName = string.IsNullOrWhiteSpace(source.DisplayName)
                        ? entry.SourceId
                        : source.DisplayName;
                    result.Add(new KoboldKareServerEntry(
                        entry.Id,
                        entry.DisplayName,
                        entry.Description,
                        address,
                        port,
                        sourceName));
                }
            }

            result.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
