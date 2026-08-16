using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Basis.Setup;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace KoboldKare.Basis.Editor
{
    /// <summary>
    /// Installs the fixed Assets-level pieces required by Basis without replacing KoboldKare's
    /// existing Addressables catalog. Basis' stock Addressables setup owns the entire catalog,
    /// which is appropriate for the Basis application but not for an existing game.
    /// </summary>
    [InitializeOnLoad]
    internal static class KoboldKareBasisProjectSetup
    {
        private static readonly string[] SafeSetupModules =
        {
            "project.structure",
            "xr.management",
            "input.actions",
            "highlight.settings",
            "linker.linkxml",
            "steamaudio.settings",
            "rendering.defaults",
        };

        private static bool applying;
        private static bool scheduled;

        static KoboldKareBasisProjectSetup()
        {
            ScheduleEnsureIntegrated();
        }

        [MenuItem("KoboldKare/Basis/Apply Framework Project Setup")]
        private static void ApplyFromMenu()
        {
            EnsureIntegrated(logSuccess: true);
        }

        [MenuItem("KoboldKare/Basis/Validate Framework Project Setup")]
        private static void ValidateFromMenu()
        {
            List<string> problems = CollectProblems();
            if (problems.Count == 0)
            {
                Debug.Log("KoboldKare Basis integration: required project setup and core Addressables entries are present.");
                return;
            }

            Debug.LogWarning("KoboldKare Basis integration still needs attention:\n - " + string.Join("\n - ", problems));
        }

        private static void ScheduleEnsureIntegrated()
        {
            if (scheduled)
            {
                return;
            }

            scheduled = true;
            EditorApplication.delayCall += () =>
            {
                scheduled = false;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    ScheduleEnsureIntegrated();
                    return;
                }

                EnsureIntegrated(logSuccess: false);
            };
        }

        private static void EnsureIntegrated(bool logSuccess)
        {
            if (applying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            applying = true;
            try
            {
                ApplySafeBasisSetupModules();
                int mergedEntries = MergeBasisAddressables();
                if (logSuccess)
                {
                    Debug.Log($"KoboldKare Basis integration applied. Addressables entries updated: {mergedEntries}.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"KoboldKare Basis project integration failed: {exception}");
            }
            finally
            {
                applying = false;
            }
        }

        private static void ApplySafeBasisSetupModules()
        {
            IReadOnlyList<IBasisSetupModule> modules = BasisSetupRegistry.Modules;
            for (int i = 0; i < SafeSetupModules.Length; i++)
            {
                string key = SafeSetupModules[i];
                IBasisSetupModule module = modules.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, key, StringComparison.Ordinal));
                if (module == null || !module.IsAvailable)
                {
                    continue;
                }

                BasisSetupStatus status = module.GetStatus();
                if (status == BasisSetupStatus.UpToDate || status == BasisSetupStatus.NotApplicable)
                {
                    continue;
                }

                BasisSetupReport report = module.Apply(BasisSetupMode.EnsureExists);
                if (report.Status == BasisSetupStatus.Error)
                {
                    Debug.LogWarning($"Basis setup module '{key}' reported an error: {report.Message}");
                }
            }
        }

        private static int MergeBasisAddressables()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("KoboldKare Basis integration cannot merge Addressables because the project has no default AddressableAssetSettings.");
                return 0;
            }

            string templateGroupDirectory = Path.Combine(
                BasisSetupTemplates.TemplatesRoot(),
                "Assets",
                "AddressableAssetsData",
                "AssetGroups");
            if (!Directory.Exists(templateGroupDirectory))
            {
                Debug.LogWarning($"KoboldKare Basis integration could not locate Basis Addressables templates at '{templateGroupDirectory}'.");
                return 0;
            }

            int changed = 0;
            string[] groupFiles = Directory.GetFiles(templateGroupDirectory, "*.asset", SearchOption.TopDirectoryOnly);
            Array.Sort(groupFiles, StringComparer.Ordinal);
            for (int i = 0; i < groupFiles.Length; i++)
            {
                string yaml = File.ReadAllText(groupFiles[i]);
                string groupName = ParseGroupName(yaml);
                if (string.IsNullOrWhiteSpace(groupName) ||
                    string.Equals(groupName, "Built In Data", StringComparison.Ordinal))
                {
                    continue;
                }

                AddressableAssetGroup group = GetOrCreateBasisGroup(settings, groupName);
                foreach (TemplateAddressableEntry templateEntry in ParseEntries(yaml))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(templateEntry.Guid);
                    if (string.IsNullOrWhiteSpace(assetPath))
                    {
                        // Basis' application catalog also contains optional/test/application-only assets.
                        // A framework consumer intentionally imports only entries that resolve here.
                        continue;
                    }

                    AddressableAssetEntry existingEntry = settings.FindAssetEntry(templateEntry.Guid);
                    string previousAddress = existingEntry?.address;
                    AddressableAssetEntry entry = settings.CreateOrMoveEntry(
                        templateEntry.Guid,
                        group,
                        false,
                        false);
                    if (entry == null)
                    {
                        continue;
                    }

                    string desiredAddress = string.IsNullOrWhiteSpace(templateEntry.Address)
                        ? assetPath
                        : templateEntry.Address;
                    if (!string.Equals(previousAddress, desiredAddress, StringComparison.Ordinal))
                    {
                        entry.address = desiredAddress;
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
            return changed;
        }

        private static AddressableAssetGroup GetOrCreateBasisGroup(
            AddressableAssetSettings settings,
            string groupName)
        {
            AddressableAssetGroup group = settings.FindGroup(groupName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    groupName,
                    false,
                    false,
                    false,
                    null,
                    typeof(BundledAssetGroupSchema),
                    typeof(ContentUpdateGroupSchema));
            }

            BundledAssetGroupSchema bundled = group.GetSchema<BundledAssetGroupSchema>() ??
                                                group.AddSchema<BundledAssetGroupSchema>();
            if (group.GetSchema<ContentUpdateGroupSchema>() == null)
            {
                group.AddSchema<ContentUpdateGroupSchema>();
            }

            bundled.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bundled.IncludeInBuild = true;
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            return group;
        }

        private static string ParseGroupName(string yaml)
        {
            Match match = Regex.Match(yaml, @"(?m)^\s*m_GroupName:\s*(?<name>.+?)\s*$", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["name"].Value.Trim() : null;
        }

        private static IEnumerable<TemplateAddressableEntry> ParseEntries(string yaml)
        {
            MatchCollection matches = Regex.Matches(
                yaml,
                @"(?ms)^\s*- m_GUID:\s*(?<guid>[0-9a-fA-F]{32})\s*\r?\n\s*m_Address:\s*(?<address>.*?)(?=\r?\n\s*m_ReadOnly:)",
                RegexOptions.CultureInvariant);

            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                string address = Regex.Replace(
                    match.Groups["address"].Value,
                    @"\s*\r?\n\s*",
                    " ").Trim();
                yield return new TemplateAddressableEntry(
                    match.Groups["guid"].Value.ToLowerInvariant(),
                    address);
            }
        }

        private static List<string> CollectProblems()
        {
            var problems = new List<string>();
            IReadOnlyList<IBasisSetupModule> modules = BasisSetupRegistry.Modules;
            for (int i = 0; i < SafeSetupModules.Length; i++)
            {
                string key = SafeSetupModules[i];
                IBasisSetupModule module = modules.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, key, StringComparison.Ordinal));
                if (module == null)
                {
                    problems.Add($"Basis setup module '{key}' is unavailable");
                    continue;
                }
                if (!module.IsAvailable)
                {
                    continue;
                }

                BasisSetupStatus status = module.GetStatus();
                if (status != BasisSetupStatus.UpToDate && status != BasisSetupStatus.NotApplicable)
                {
                    problems.Add($"Basis setup module '{key}' is {status}");
                }
            }

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                problems.Add("No default Addressables settings asset is configured");
                return problems;
            }

            RequireAddress(settings, "BasisFramework", problems);
            RequireAddress(settings, "LocalPlayer", problems);
            RequireAddress(settings, "Locomotion", problems);
            return problems;
        }

        private static void RequireAddress(
            AddressableAssetSettings settings,
            string address,
            ICollection<string> problems)
        {
            bool found = settings.groups
                .Where(group => group != null)
                .SelectMany(group => group.entries)
                .Any(entry => string.Equals(entry.address, address, StringComparison.Ordinal));
            if (!found)
            {
                problems.Add($"Required Basis Addressables address '{address}' is missing");
            }
        }

        private readonly struct TemplateAddressableEntry
        {
            public readonly string Guid;
            public readonly string Address;

            public TemplateAddressableEntry(string guid, string address)
            {
                Guid = guid;
                Address = address;
            }
        }
    }
}
