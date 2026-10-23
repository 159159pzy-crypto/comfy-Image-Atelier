using Anima.Launcher;
using Anima.Launcher.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

internal static class ModelLibraryTests
{
    public static async Task RunAsync(ModelManifest manifest, string work, Action<bool, string> check)
    {
        var optionalIds = new[] { "krea2-turbo", "qwen3vl-krea2", "seedvr2-model", "seedvr2-vae" };
        var optional = optionalIds.Select(id => manifest.Models.Single(model => model.Id == id)).ToArray();
        check(optional.All(model => model.CanDownload && Regex.IsMatch(model.Revision, "^[a-f0-9]{40}$")
            && model.Urls.All(url => url.Contains("/resolve/" + model.Revision + "/", StringComparison.Ordinal))
            && model.SourceUrl.Contains("/blob/" + model.Revision + "/", StringComparison.Ordinal)),
            "all formerly manual optional models have verified immutable download metadata and source links");
        check(manifest.Models.All(model => model.CanDownload), "merged model catalog has no unresolved manual downloads");
        check(optional.Where(model => model.Id is "krea2-turbo" or "qwen3vl-krea2").All(model =>
            model.License == "Krea 2 Community License" && model.LicenseUrl.EndsWith("/LICENSE.pdf", StringComparison.Ordinal))
            && optional.Where(model => model.Id.StartsWith("seedvr2-", StringComparison.Ordinal)).All(model =>
                model.License == "Apache-2.0" && model.LicenseUrl.Length > 0),
            "Krea community license and SeedVR2 Apache license remain explicit in the catalog");

        var directory = Path.Combine(work, "model-library");
        Directory.CreateDirectory(directory);
        var scan = await Inventory.Scan(new(directory, "", new()), manifest, false, default);
        check(scan.Select(item => item.Model.Id).SequenceEqual(manifest.Models.Select(model => model.Id)),
            "rescan retains every manifest item including missing optional models and unresolved tagger directories");

        var rows = new ObservableCollection<ModelRow>();
        var groups = new ObservableCollection<ModelGroup>();
        var visibleIds = new List<string>();
        var publishedEmptyGroup = false;
        groups.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Reset) visibleIds.Clear();
            if (change.NewItems is null) return;
            foreach (ModelGroup group in change.NewItems)
            {
                publishedEmptyGroup |= group.Items.Count == 0;
                visibleIds.AddRange(group.Items.Select(row => row.Status.Model.Id));
            }
        };
        for (var pass = 0; pass < 3; pass++)
        {
            ModelListProjection.Refresh(rows, groups, scan);
            check(!publishedEmptyGroup && rows.Count == scan.Count && visibleIds.Count == scan.Count
                && visibleIds.ToHashSet().SetEquals(scan.Select(item => item.Model.Id)),
                $"already-bound model view receives every populated group on scan {pass + 1}");
        }
        var childNotified = false;
        groups[0].Items.CollectionChanged += (_, change) => childNotified |= change.Action == NotifyCollectionChangedAction.Add;
        groups[0].Add(new ModelRow(scan[0]));
        check(childNotified, "model group child changes notify an existing grouped view");

        foreach (var row in rows.Where(row => optionalIds.Contains(row.Status.Model.Id))) row.Selected = true;
        ModelListProjection.Refresh(rows, groups, scan);
        check(rows.Where(row => row.Selected).Select(row => row.Status.Model.Id).ToHashSet().SetEquals(optionalIds),
            "rescan preserves selected optional downloads by model identity");
        var updated = scan.Select(item => item.Model.Id switch
        {
            "krea2-turbo" => item with { State = "已校验", FoundPath = item.Destination },
            "qwen3vl-krea2" => item with { State = "大小异常", FoundPath = item.Destination },
            "seedvr2-model" => item with { State = "目录待确认", Destination = "" },
            _ => item,
        }).ToList();
        ModelListProjection.Refresh(rows, groups, updated);
        check(rows.Single(row => row.Selected).Status.Model.Id == "seedvr2-vae",
            "rescan deselects completed, abnormal existing and unresolved downloads while retaining remaining choices");
        check(rows.All(row => !row.Selected || row.CanSelect), "rescan never leaves an unselectable model checked");

        var beforeRows = rows.ToArray();
        var beforeGroups = groups.ToArray();
        static IEnumerable<ModelStatus> FailedScan(ModelStatus first)
        {
            yield return first;
            throw new IOException("scan fixture failed before publishing");
        }
        try { ModelListProjection.Refresh(rows, groups, FailedScan(scan[0])); }
        catch (IOException) { }
        check(rows.SequenceEqual(beforeRows) && groups.SequenceEqual(beforeGroups),
            "failed replacement keeps the previous model list and selection visible");

        var vae = optional.Single(model => model.Id == "seedvr2-vae");
        check(vae.Aliases.Contains("seedvr2_ema_vae_fp16.safetensors"), "SeedVR2 VAE recognizes the upstream alternate filename");
        var vaeDirectory = Path.Combine(directory, "models", "vae");
        Directory.CreateDirectory(vaeDirectory);
        byte[] data = [1, 2, 3, 4];
        var alias = Path.Combine(vaeDirectory, "seedvr2_ema_vae_fp16.safetensors");
        await File.WriteAllBytesAsync(alias, data);
        var fixture = vae with { Size = data.Length, Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() };
        var reused = (await Inventory.Scan(new(directory, "", new() { ["vae"] = [vaeDirectory] }),
            new(manifest.SchemaVersion, [fixture]), true, default)).Single();
        check(!reused.Missing && reused.FoundPath == alias && reused.State == "已校验",
            "existing SeedVR2 VAE alternate filename is verified and reused without duplicate download");
    }
}
