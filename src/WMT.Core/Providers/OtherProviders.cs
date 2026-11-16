namespace WMT.Core;

/// <summary>USR-004: user-data folders, only those explicitly chosen per profile.</summary>
public sealed class UserDataProvider : IDiscoveryProvider
{
    public const string ProviderId = "userdata";
    public string Id => ProviderId;
    public string DisplayName => "User data folders";
    public bool Applies(DiscoveryContext ctx) => ctx.Options.UserData.Any(kv => kv.Value.Count > 0);

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        foreach (var p in ctx.SelectedProfiles)
        {
            if (!ctx.Options.UserData.TryGetValue(p.Sid, out var cats) || cats.Count == 0) continue;
            if (p.Access != AccessState.Accessible)
            {
                o.Issues.Add(new Issue(IssueSeverity.Warning, "User data", $"Profile {p.Account} is not accessible; its folders were not scanned.", p.ProfilePath));
                continue;
            }
            var f = ProfileFolders.Resolve(p, ctx);
            if (!f.Verified) o.Issues.Add(new Issue(IssueSeverity.Info, "User data", $"Folder locations of {p.Account} are defaults (their registry is not loaded); redirected folders may be missed."));
            foreach (var c in cats)
            {
                var path = f.For(c);
                if (string.IsNullOrEmpty(path)) continue;
                if (!Directory.Exists(path)) { o.Issues.Add(new Issue(IssueSeverity.Info, "User data", $"{c} of {p.Account} does not exist.", path)); continue; }
                o.Roots.Add(new ScanRoot
                {
                    Path = path, Category = $"User data / {ShortAccount(p)} / {c}", ProviderId = Id, ProfileSid = p.Sid, IsUserData = true,
                    Rule = fc => Rules.IsTempOrLock(fc) ? null
                        : fc.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ? new FileDecision(Classification.Review, "Folder display settings")
                        : fc.Attributes.HasFlag((FileAttributes)0x400000) || fc.Attributes.HasFlag(FileAttributes.Offline)
                            ? new FileDecision(Classification.Review, "Cloud-only file (not downloaded): copying will download it")
                            : new FileDecision(Classification.Recommended, null),
                    Note = "User-data folder chosen explicitly"
                });
            }
        }
        return o;
    }

    public static string ShortAccount(UserProfileInfo p) => p.Account.Contains('\\') ? p.Account[(p.Account.LastIndexOf('\\') + 1)..] : p.Account;
}

/// <summary>USR-005: own folders on any accessible drive.</summary>
public sealed class CustomPathsProvider : IDiscoveryProvider
{
    public const string ProviderId = "custom";
    public string Id => ProviderId;
    public string DisplayName => "Custom folders";
    public bool Applies(DiscoveryContext ctx) => ctx.Options.CustomPaths.Count > 0;

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        foreach (var p in ctx.Options.CustomPaths.Distinct(PathUtil.Comparer))
        {
            if (!Directory.Exists(p)) { o.Issues.Add(new Issue(IssueSeverity.Warning, "Custom folders", "Folder not found or not accessible.", p)); continue; }
            var owner = ctx.SelectedProfiles.FirstOrDefault(pr => !string.IsNullOrEmpty(pr.ProfilePath) && PathUtil.IsInsideOrSame(p, pr.ProfilePath));
            o.Roots.Add(new ScanRoot { Path = p, Category = "Custom folders / " + p, ProviderId = Id, ProfileSid = owner?.Sid, IsUserData = true, Rule = Rules.AllUserFiles(), Note = "Added by you" });
        }
        return o;
    }
}

/// <summary>SW-006: portable applications: own config/data recommended, whole folder only by review.</summary>
public sealed class PortableProvider : IDiscoveryProvider
{
    public const string ProviderId = "portable";
    public string Id => ProviderId;
    public string DisplayName => "Portable applications";
    static readonly string[] ConfigExts = { ".ini", ".cfg", ".conf", ".config", ".json", ".xml", ".yaml", ".yml", ".toml", ".db", ".sqlite", ".dat", ".reg" };
    public bool Applies(DiscoveryContext ctx) => ctx.IsSelected(ProviderId);

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        foreach (var s in ctx.SelectedFor(ProviderId))
        {
            if (s.InstallLocation == SoftwareItem.UnknownText || !Directory.Exists(s.InstallLocation)) continue;
            o.Roots.Add(new ScanRoot
            {
                Path = s.InstallLocation, Category = "Portable / " + s.Name, ProviderId = Id, MaxDepth = 12,
                Rule = f => Rules.IsTempOrLock(f) ? null
                    : ConfigExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) || f.RelativeToRoot.Contains(@"\Data\", StringComparison.OrdinalIgnoreCase) || f.RelativeToRoot.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase)
                        ? new FileDecision(Classification.Recommended, "Portable app configuration/data")
                        : new FileDecision(Classification.Review, "Program file of a portable app: select to copy the whole application"),
                Note = "Portable application folder"
            });
            o.Components.Add(new Component
            {
                Id = "portable-" + s.Id, Name = s.Name, Application = s.Name, ProviderId = Id, Kind = "Portable application", Classification = Classification.Review,
                MigrationMethod = MigrationMethod.CopyFile, SourcePath = s.InstallLocation, DestinationRule = "Same path (or remapped drive)",
                VerificationMethod = "Start the application from its folder", Basis = "Detected without installer", Confidence = Confidence.Inferred,
                ManualActions = { "Copy the whole folder only if you cannot download the application again.", "Recreate shortcuts manually." }
            });
        }
        return o;
    }
}

/// <summary>SYS-001/002: runtimes as reinstall manifest entries (never DLL copies).</summary>
public sealed class RuntimeProvider : IDiscoveryProvider
{
    public const string ProviderId = "runtime";
    public string Id => ProviderId;
    public string DisplayName => "Runtimes";
    public bool Applies(DiscoveryContext ctx) => ctx.Options.IncludeRuntimes;

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        foreach (var s in ctx.AllSoftware.Where(s => s.Category == "Runtime"))
        {
            o.Components.Add(new Component
            {
                Id = "rt-" + s.Id, Name = $"{s.Name}", Application = "Runtimes", ProviderId = Id, Kind = "Runtime",
                Classification = Classification.InstallerManaged, MigrationMethod = MigrationMethod.Reinstall,
                DestinationRule = "Install from the official installer (with your confirmation)", OfficialSource = s.OfficialSource,
                RequiredPrerequisites = { $"{s.Architecture} installer" }, VerificationMethod = "Listed in Apps & features with the same or newer version",
                Basis = $"Installed on the source ({s.DetectedSource}); relevance to your programs is inferred",
                Confidence = s.Version != SoftwareItem.UnknownText ? Confidence.Confirmed : Confidence.Inferred,
            });
        }
        return o;
    }
}

/// <summary>SYS-003: the selected Windows settings of the signed-in user (Windows Settings tab), plus the files they need.</summary>
public sealed class WindowsSettingsProvider : IDiscoveryProvider
{
    public const string ProviderId = "windows";
    public string Id => ProviderId;
    public string DisplayName => "Windows settings";
    public bool Applies(DiscoveryContext ctx) => ctx.Options.IncludeWindowsSettings && (ctx.Options.WindowsSettings != null || ctx.SelectedProfiles.Any(p => p.IsCurrentUser));

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        var snap = ctx.Options.WindowsSettings ?? new WindowsSettingsCapture(new SettingsCaptureEnvironment
        { Registry = ctx.Registry, Processes = ctx.Processes, Token = ctx.Token }).Capture(ctx.Progress);
        o.Manifests["Windows-Settings"] = snap;
        o.Issues.AddRange(snap.Issues);
        foreach (var i in snap.Items.Where(i => i.Selected))
        {
            var cat = "Windows settings / " + i.Group;
            if (i.Kind is SettingKinds.Font or SettingKinds.Wallpaper && i.SourcePath != null && File.Exists(i.SourcePath))
                o.Roots.Add(new ScanRoot { Path = Path.GetDirectoryName(i.SourcePath)!, OnlyFiles = new() { Path.GetFileName(i.SourcePath) }, Category = cat, ProviderId = Id,
                    ProfileSid = snap.ProfileSid, Recursive = false, Rule = _ => new FileDecision(Classification.Recommended, i.Name), Note = i.Name });
            if (i.Kind == SettingKinds.StartupFolder && i.SourcePath != null)
                o.Roots.Add(new ScanRoot { Path = i.SourcePath, Category = cat + " / Startup folder", ProviderId = Id, ProfileSid = snap.ProfileSid, Recursive = false,
                    Rule = f => f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ? null : new FileDecision(Classification.Recommended, "Startup shortcut"), Note = "Startup folder" });
            o.Components.Add(new Component
            {
                Id = "win-" + i.Id, Name = i.Name, Application = "Windows", ProviderId = Id, Kind = "Windows setting: " + i.Group, Classification = i.Classification,
                MigrationMethod = i.Kind == SettingKinds.Manual ? MigrationMethod.ManualCheck : MigrationMethod.ApplySetting, ProfileSid = snap.ProfileSid,
                DestinationRule = i.RestoreMethod, VerificationMethod = "Check the setting in Windows Settings after sign-in", Basis = i.Summary, Confidence = Confidence.Confirmed,
                ManualActions = i.Kind == SettingKinds.Manual ? new() { i.RestoreMethod } : new()
            });
        }
        o.FunctionalTests.Add("Windows: languages and keyboard order, date/number formats, Explorer options, Wi-Fi, mapped drives, printers and fonts look as before.");
        return o;
    }
}
