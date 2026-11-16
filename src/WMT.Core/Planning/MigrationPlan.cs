using System.Text.Json.Serialization;

namespace WMT.Core;

/// <summary>Migration plan: selected inputs + everything providers found (Plan.json and companion manifests).</summary>
public sealed class MigrationPlan
{
    public const int SchemaVersion = 1;
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceMachine { get; set; } = Environment.MachineName;
    public List<SoftwareItem> SelectedSoftware { get; set; } = new();
    public List<UserProfileInfo> SelectedProfiles { get; set; } = new();
    public DiscoveryOptions Options { get; set; } = new();
    public List<Component> Components { get; set; } = new();
    public List<Dependency> Dependencies { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
    public List<RegistryValueRecord> Registry { get; set; } = new();
    public List<string> FunctionalTests { get; set; } = new();
    public OfficeEnvironment? Office { get; set; }
    public PythonInventory? Python { get; set; }
    public object? WindowsSettings { get; set; }
    public Dictionary<string, object> VbaAnalyses { get; set; } = new();
    public List<string> ProvidersRun { get; set; } = new();

    [JsonIgnore] public FileIndex Index { get; set; } = new();
    [JsonIgnore] public FolderTree? Tree { get; set; }

    public FolderTree EnsureTree() => Tree ??= new FolderTree(Index);

    /// <summary>WF-006 preview figures.</summary>
    public (int Files, long Bytes, int Folders) SelectedTotals()
    {
        var sel = Index.Files.Where(f => f.Selected).ToList();
        return (sel.Count, sel.Sum(f => f.Size), Index.Directories.Count(d => d.Included));
    }
}

public sealed class PlanBuilder
{
    public static IReadOnlyList<IDiscoveryProvider> DefaultProviders() => new IDiscoveryProvider[]
    {
        new WordProvider(), new PythonProvider(), new PortableProvider(), new RuntimeProvider(), new WindowsSettingsProvider(),
        new CustomPathsProvider(), new UserDataProvider()
    };

    readonly IReadOnlyList<IDiscoveryProvider> providers;
    public PlanBuilder(IReadOnlyList<IDiscoveryProvider>? providers = null) { this.providers = providers ?? DefaultProviders(); }

    /// <summary>WF-004/005: targeted discovery for the selection only, then one index pass over provider roots.</summary>
    public MigrationPlan Build(DiscoveryContext ctx)
    {
        var plan = new MigrationPlan
        {
            SelectedSoftware = ctx.SelectedSoftware.ToList(),
            SelectedProfiles = ctx.SelectedProfiles.ToList(),
            Options = ctx.Options,
        };
        if (ctx.SelectedProfiles.Count == 0)
            plan.Issues.Add(new Issue(IssueSeverity.Warning, "Plan", "No user profile is selected: per-user customizations (templates, dictionaries, projects) cannot be found.", null, "Select at least one profile in User Profiles."));
        if (ctx.SelectedSoftware.Count == 0 && ctx.Options.CustomPaths.Count == 0 && !ctx.Options.UserData.Any(kv => kv.Value.Count > 0))
            plan.Issues.Add(new Issue(IssueSeverity.Warning, "Plan", "Nothing is selected: choose programs, user-data folders or custom folders."));

        var outputs = new List<(IDiscoveryProvider P, ProviderOutput O)>();
        var all = new ProviderOutput();
        foreach (var p in providers)
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (!p.Applies(ctx)) continue;
            ctx.Progress?.Report(new ProgressInfo("Targeted discovery: " + p.DisplayName, outputs.Count, providers.Count));
            ctx.Log.Info("plan.provider", "Running provider " + p.Id);
            try
            {
                var o = p.Discover(ctx);
                outputs.Add((p, o));
                all.Merge(o);
                plan.ProvidersRun.Add(p.Id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                all.Issues.Add(new Issue(IssueSeverity.Error, p.DisplayName, "Provider failed: " + ex.Message, null, "Report this error; the rest of the plan is still valid but incomplete."));
                ctx.Log.Error("plan.provider_failed", p.Id + ": " + ex);
            }
        }

        var indexer = new Indexer(ctx.Options.ExcludedPaths, ctx.Progress, ctx.Token);
        plan.Index = indexer.Build(all.Roots, ctx.Options.EmptyFolders);

        foreach (var (p, o) in outputs)
        {
            var files = plan.Index.Files.Where(f => f.ProviderId == p.Id || (p.Id == PythonProvider.ProviderId && f.ProviderId == PythonProvider.OcrProviderId)).ToList();
            if (files.Count == 0) continue;
            try
            {
                var extra = new ProviderOutput();
                p.AnalyzeIndexed(ctx, files, extra);
                all.Merge(extra);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { all.Issues.Add(new Issue(IssueSeverity.Warning, p.DisplayName, "File analysis failed: " + ex.Message)); }
        }

        plan.Components = all.Components;
        plan.Dependencies = all.Dependencies;
        plan.Issues.AddRange(all.Issues);
        plan.Issues.AddRange(plan.Index.Issues);
        plan.Registry = all.Registry.GroupBy(r => r.Id).Select(g => g.First()).ToList();
        plan.FunctionalTests = all.FunctionalTests.Distinct().ToList();
        plan.Office = ctx.Office ?? (all.Manifests.TryGetValue("Office-Environment", out var off) ? off as OfficeEnvironment : null);
        plan.Python = all.Manifests.TryGetValue("Python-Environments", out var py) ? py as PythonInventory : null;
        plan.WindowsSettings = all.Manifests.TryGetValue("Windows-Settings", out var ws) ? ws : null;
        foreach (var kv in all.Manifests.Where(k => k.Key.StartsWith("VBA:"))) plan.VbaAnalyses[kv.Key[4..]] = kv.Value;
        foreach (var c in plan.Components) c.State = LifecycleState.Planned;

        // SW-002 Dependency Status per selected program
        foreach (var s in plan.SelectedSoftware)
        {
            var deps = plan.Dependencies.Where(d => d.Application.Equals(s.Name, StringComparison.OrdinalIgnoreCase)
                || (s.Providers.Contains(WordProvider.ProviderId) && d.Application == "Microsoft Word")
                || (s.Providers.Contains(PythonProvider.ProviderId) && d.Application == "Python")).ToList();
            s.DependencyStatus = deps.Count == 0 ? (s.Providers.Count == 0 ? "No rules for this program (reinstall only)" : "No dependencies found")
                : $"{deps.Count(d => d.Confidence == Confidence.Confirmed)} confirmed, {deps.Count(d => d.Confidence == Confidence.Inferred)} inferred, {deps.Count(d => d.Confidence == Confidence.Unknown)} unknown";
            if (s.Providers.Count == 0)
                plan.Components.Add(new Component
                {
                    Id = "sw-" + s.Id, Name = s.Name, Application = s.Name, ProviderId = "software", Kind = "Program", Classification = Classification.InstallerManaged,
                    MigrationMethod = MigrationMethod.Reinstall, DestinationRule = "Reinstall on the new system", OfficialSource = s.OfficialSource,
                    VerificationMethod = "Program starts and opens your files", Basis = "Selected in Software; no specific discovery rules", Confidence = s.Confidence,
                    ManualActions = { "Settings of this program were not analysed automatically (SW-005): add its data folders as custom folders if needed." }, State = LifecycleState.Planned
                });
        }
        ctx.Progress?.Report(new ProgressInfo("Plan ready", plan.Index.Files.Count, plan.Index.Files.Count));
        return plan;
    }
}
