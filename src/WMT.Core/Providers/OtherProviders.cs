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

/// <summary>SYS-003: fonts, non-secret environment variables, PATH additions, file associations (current user only).</summary>
public sealed class WindowsSettingsProvider : IDiscoveryProvider
{
    public const string ProviderId = "windows";
    public string Id => ProviderId;
    public string DisplayName => "Windows settings";
    static readonly string[] AssocExts = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf", ".txt", ".pdf", ".py", ".ipynb", ".csv", ".xlsx", ".tif", ".tiff", ".png", ".jpg" };
    public bool Applies(DiscoveryContext ctx) => ctx.Options.IncludeWindowsSettings && ctx.SelectedProfiles.Count > 0;

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        var settings = new List<object>();
        foreach (var p in ctx.SelectedProfiles)
        {
            if (p.Access == AccessState.Accessible)
            {
                var f = ProfileFolders.Resolve(p, ctx);
                var fonts = Path.Combine(f.Local, @"Microsoft\Windows\Fonts");
                if (Directory.Exists(fonts))
                    o.Roots.Add(new ScanRoot { Path = fonts, Category = $"Windows / {UserDataProvider.ShortAccount(p)} / Fonts installed for this user", ProviderId = Id, ProfileSid = p.Sid, Recursive = false,
                        Rule = Rules.Extensions(Classification.Review, "User-installed font: check that its license allows installing it again", ".ttf", ".otf", ".ttc", ".fon", ".fnt"),
                        Note = "Per-user fonts" });
            }
            var hive = ctx.UserHive(p);
            if (hive == null) { o.Issues.Add(new Issue(IssueSeverity.Info, "Windows settings", $"Environment variables and associations of {p.Account} were not read (hive not loaded).")); continue; }
            var (h, pre) = hive.Value;
            var envVars = new List<object>();
            foreach (var name in ctx.Registry.ValueNames(h, pre + "Environment"))
            {
                var v = ctx.Registry.Value(h, pre + "Environment", name);
                if (v == null) continue;
                if (Redaction.LooksSecretName(name)) { envVars.Add(new { Name = name, Value = "[not exported: name suggests a secret]", Kind = v.Kind }); continue; }
                envVars.Add(new { Name = name, Value = v.AsString(), Kind = v.Kind });
                if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var entry in (v.AsString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                        o.Components.Add(new Component
                        {
                            Id = "path-" + PathUtil.StableId(p.Sid + entry), Name = "PATH entry: " + entry, Application = "Windows", ProviderId = Id, Kind = "User PATH entry",
                            Classification = Classification.Review, MigrationMethod = MigrationMethod.ApplySetting, ProfileSid = p.Sid,
                            DestinationRule = "Add to the target user's PATH if the folder exists there",
                            VerificationMethod = "Open a new terminal; the program in this folder starts by name", Basis = "HKCU\\Environment\\Path", Confidence = Confidence.Confirmed,
                            ManualActions = { "Settings → System → About → Advanced system settings → Environment Variables" }
                        });
                }
            }
            var assoc = new List<object>();
            foreach (var ext in AssocExts)
            {
                var prog = ctx.Registry.Value(h, pre + $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice", "ProgId")?.AsString();
                if (prog != null) assoc.Add(new { Extension = ext, ProgId = prog });
            }
            if (assoc.Count > 0)
                o.Components.Add(new Component
                {
                    Id = "assoc-" + PathUtil.StableId(p.Sid), Name = $"Default apps of {UserDataProvider.ShortAccount(p)} ({assoc.Count} file types)", Application = "Windows", ProviderId = Id,
                    Kind = "File associations", Classification = Classification.Unsupported, MigrationMethod = MigrationMethod.ManualCheck, ProfileSid = p.Sid,
                    DestinationRule = "Set manually: Windows protects default-app choices", VerificationMethod = "Double-click a file of each type",
                    Basis = "UserChoice registry values", Confidence = Confidence.Confirmed,
                    ManualActions = { "Settings → Apps → Default apps: set the programs listed in Windows-Settings.json" }
                });
            settings.Add(new { Profile = p.Sid, Account = p.Account, EnvironmentVariables = envVars, FileAssociations = assoc });
        }
        o.Manifests["Windows-Settings"] = settings;
        o.FunctionalTests.Add("Windows: your fonts appear in Word's font list; your environment variables/PATH entries exist; file types open in the right programs.");
        return o;
    }
}
