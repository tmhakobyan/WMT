using System.Text.RegularExpressions;
using WMT.Core.Vba;

namespace WMT.Core;

/// <summary>OFF-002..009: Microsoft Word (Office 2016 and later) user customizations.</summary>
public sealed class WordProvider : IDiscoveryProvider
{
    public const string ProviderId = "word";
    public string Id => ProviderId;
    public string DisplayName => "Microsoft Word";

    /// <summary>OFF-004: examples of known own tools (used to raise confidence, never as the only accepted names).</summary>
    public static readonly string[] KnownToolExamples = { "ConverterBox 3D.dotm", "HT_Doc_template_2.dotm", "HT_Doc_template_2.dotx" };
    static readonly string[] TemplateExts = { ".dotm", ".dotx", ".dot" };
    static readonly string[] SearchExts = { ".dotm", ".dotx", ".dot", ".wll", ".docm" };
    static readonly HashSet<string> SystemDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "user32", "gdi32", "advapi32", "shell32", "ole32", "oleaut32", "comdlg32", "winmm", "version", "shlwapi", "ntdll",
        "wininet", "urlmon", "winspool.drv", "comctl32", "imm32", "mpr", "netapi32", "rpcrt4", "secur32", "ws2_32", "crypt32", "dwmapi", "uxtheme", "psapi"
    };
    static readonly HashSet<string> StandardTypeLibGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{00020430-0000-0000-C000-000000000046}", // stdole
        "{000204EF-0000-0000-C000-000000000046}", // VBA
        "{00020905-0000-0000-C000-000000000046}", // Word
        "{2DF8D04C-5BFA-101B-BDE5-00AA0044DE52}", // Office
        "{0D452EE1-E08F-101A-852E-02608C4D0BB4}", // MSForms
        "{420B2830-E718-11CF-893D-00A0C9054228}", // Scripting
        "{00020813-0000-0000-C000-000000000046}", // Excel
        "{3F4DACA7-160D-11D2-A8E9-00104B365C9F}", // VBScript RegExp
        "{0002E157-0000-0000-C000-000000000046}", // VBIDE
    };

    public bool Applies(DiscoveryContext ctx) => ctx.IsSelected(ProviderId);

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        var office = ctx.Office ?? OfficeFingerprint.Read(ctx.Registry, ctx.AllSoftware);
        ctx.Office = office;
        o.Manifests["Office-Environment"] = office;

        o.Components.Add(new Component
        {
            Id = "office-product", Name = office.Installed ? office.Family : "Microsoft Office", Application = "Microsoft Office", ProviderId = Id,
            Kind = "Office product", Classification = Classification.InstallerManaged, MigrationMethod = MigrationMethod.Reinstall,
            DestinationRule = "Install on the new system from your own licensed media",
            RequiredPrerequisites = { $"Office {office.Release} {office.Architecture} installation media and license" },
            VerificationMethod = "Start Word; File → Account shows the product as activated",
            ManualActions = { "Provide the installation media and license yourself: activation data is never exported (OFF-009).",
                              $"Install the same architecture ({office.Architecture}) to keep VBA Declare and add-ins working." },
            Basis = $"Detected {office.InstallType} {office.Version}", Confidence = office.Installed ? Confidence.Confirmed : Confidence.Unknown
        });

        var mediaRoots = new List<string>();
        foreach (var p in ctx.SelectedProfiles)
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (p.Access != AccessState.Accessible)
            {
                o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"Profile {p.Account} is not accessible; its Word customizations cannot be scanned.", p.ProfilePath, "Run as administrator or sign in as that user."));
                continue;
            }
            var f = ProfileFolders.Resolve(p, ctx);
            string cat = "Microsoft Word";
            void Root(string path, string sub, Func<FileCandidate, FileDecision?> rule, bool recursive = true, string note = "", int depth = 64, params string[] excludeDirs)
            {
                var r = new ScanRoot { Path = path, Category = $"{cat} / {sub}", ProviderId = Id, ProfileSid = p.Sid, Recursive = recursive, Rule = rule, Note = note, MaxDepth = depth };
                foreach (var e in excludeDirs) r.ExcludedDirectoryNames.Add(e);
                o.Roots.Add(r);
            }

            Root(Path.Combine(f.Roaming, @"Microsoft\Templates"), "Templates", TemplateRule, true,
                "User templates incl. Normal.dotm (styles, AutoText, macros, QAT stored in templates)", 64, "LiveContent");
            Root(Path.Combine(f.Roaming, @"Microsoft\Word\STARTUP"), "Startup add-ins (user)", StartupRule, true, "Global templates/add-ins loaded when Word starts");
            Root(Path.Combine(f.Roaming, @"Microsoft\UProof"), "Custom dictionaries", Rules.Extensions(Classification.Recommended, "Office custom dictionary / exclusion list", ".dic", ".lex", ".exc"), true);
            Root(Path.Combine(f.Roaming, @"Microsoft\Proof"), "Custom dictionaries (legacy)", Rules.Extensions(Classification.Recommended, "Legacy custom dictionary", ".dic", ".lex", ".exc"), true);
            Root(Path.Combine(f.Roaming, @"Microsoft\Spelling"), "Windows spelling dictionaries", Rules.Extensions(Classification.Recommended, "Windows spell-checker user word lists (e.g. hy-AM\\default.dic)", ".dic", ".exc", ".acl"), true);
            Root(Path.Combine(f.Roaming, @"Microsoft\Office"), "AutoCorrect", Rules.Extensions(Classification.Recommended, "AutoCorrect list (MSOxxxx.acl)", ".acl"), false);
            Root(Path.Combine(f.Local, @"Microsoft\Office"), "Ribbon and Quick Access Toolbar", Rules.Extensions(Classification.Recommended, "Ribbon/QAT customization (*.officeUI)", ".officeUI"), false,
                "Only *.officeUI directly in this folder; 16.0\\*_Rules, caches and telemetry are never entered");
            Root(Path.Combine(f.Roaming, @"Microsoft\Document Building Blocks"), "Building Blocks", Rules.Extensions(Classification.Review,
                "Created by Office on first start; keep only if you added your own building blocks", ".dotx"), true);

            var hive = ctx.UserHive(p);
            if (hive != null)
            {
                var (h, pre) = hive.Value;
                foreach (var (key, val, label) in new[] {
                    (@"Software\Microsoft\Office\16.0\Word\Options", "STARTUP-PATH", "Startup add-ins (custom location)"),
                    (@"Software\Microsoft\Office\16.0\Common\General", "UserTemplates", "Templates (custom location)"),
                    (@"Software\Microsoft\Office\16.0\Common\General", "SharedTemplates", "Workgroup templates"),
                    (@"Software\Microsoft\Office\16.0\Word\Options", "PersonalTemplates", "Personal templates location") })
                {
                    var v = ctx.Registry.Value(h, pre + key, val)?.AsString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    v = v.Replace("%USERPROFILE%", p.ProfilePath, StringComparison.OrdinalIgnoreCase);
                    if (h == "HKCU") v = Environment.ExpandEnvironmentVariables(v);
                    if (v.Contains('%') || !Path.IsPathRooted(v)) continue;
                    Root(v, label, label.StartsWith("Startup") ? (Func<FileCandidate, FileDecision?>)StartupRule : TemplateRule, true, $"From registry {key}\\{val}");
                }
                // OFF-004: Trusted Locations often hold non-standard own tools.
                var tl = pre + @"Software\Microsoft\Office\16.0\Word\Security\Trusted Locations";
                foreach (var loc in ctx.Registry.SubKeys(h, tl))
                {
                    var path = ctx.Registry.Value(h, tl + "\\" + loc, "Path")?.AsString();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    path = Environment.ExpandEnvironmentVariables(path.Replace("%USERPROFILE%", p.ProfilePath, StringComparison.OrdinalIgnoreCase));
                    if (path.Contains('%') || !Path.IsPathRooted(path)) continue;
                    if (path.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase)) continue;
                    bool sub = (ctx.Registry.Value(h, tl + "\\" + loc, "AllowSubfolders")?.Data is int i && i == 1);
                    Root(path, "Trusted locations", SearchRule, sub, $"Word trusted location ({loc})", sub ? 8 : 0, Rules.HeavyDirectoryNames.ToArray());
                }
                // Custom dictionaries registered anywhere on disk.
                var cd = pre + @"Software\Microsoft\Shared Tools\Proofing Tools\1.0\Custom Dictionaries";
                foreach (var name in ctx.Registry.ValueNames(h, cd))
                {
                    var dic = ctx.Registry.Value(h, cd, name)?.AsString();
                    if (string.IsNullOrWhiteSpace(dic) || !dic.EndsWith(".dic", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Path.IsPathRooted(dic)) dic = Path.Combine(f.Roaming, @"Microsoft\UProof", dic);
                    var dir = Path.GetDirectoryName(dic);
                    if (dir == null) continue;
                    var fileName = Path.GetFileName(dic);
                    Root(dir, "Custom dictionaries", c => c.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ? new FileDecision(Classification.Recommended, "Registered custom dictionary") : null,
                        false, "Registered in Proofing Tools\\Custom Dictionaries");
                }
                ExportRegistry(ctx, p, h, pre, o);
            }
            else
            {
                o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"Registry settings of {p.Account} are not readable (hive not loaded). Custom template/startup paths, trusted locations and Word options were not checked.",
                    null, "Sign in as that user and run the tool again, or review those settings manually."));
            }

            // OFF-004: non-standard tools in the user's own folders (template/add-in extensions only).
            foreach (var d in new[] { f.Documents, f.Desktop, f.Downloads }.Where(x => !string.IsNullOrEmpty(x)).Distinct(PathUtil.Comparer))
                Root(d, "Templates and add-ins found elsewhere", SearchRule, true, "Only template/add-in file types are taken from this folder", 6, Rules.HeavyDirectoryNames.ToArray());
            mediaRoots.Add(f.Documents);
        }
        foreach (var cp in ctx.Options.CustomPaths)
            o.Roots.Add(new ScanRoot { Path = cp, Category = "Microsoft Word / Templates and add-ins found elsewhere", ProviderId = Id, Rule = SearchRule, MaxDepth = 8, ExcludedDirectoryNames = new(Rules.HeavyDirectoryNames, StringComparer.OrdinalIgnoreCase) });

        // System-level Startup (installer-managed unless proven otherwise): inventory only, never copied by default.
        if (office.WordPath != null)
        {
            var sysStartup = Path.Combine(Path.GetDirectoryName(office.WordPath) ?? "", "STARTUP");
            if (Directory.Exists(sysStartup))
                o.Roots.Add(new ScanRoot { Path = sysStartup, Category = "Microsoft Word / Startup add-ins (system)", ProviderId = Id, Recursive = false,
                    Rule = c => Rules.IsTempOrLock(c) ? null : new FileDecision(Classification.Review, "System-level Word Startup: usually installed by a program; copy only if you placed it there"),
                    Note = "Office installation folder; files listed for review only" });
        }

        DiscoverComAddins(ctx, office, o);
        o.FunctionalTests.Add("Word: open Word, File → Options → Add-ins → Manage: Templates → Go: all your global templates/add-ins are listed and checked.");
        o.FunctionalTests.Add("Word: create a new document from each restored template; styles and building blocks appear.");
        o.FunctionalTests.Add("Word: run each macro you use (e.g. hyphenation/line-break scripts) on a test document.");
        o.FunctionalTests.Add("Word: Review → Spelling: Armenian/Russian proofing works; words from your custom dictionaries are accepted.");
        o.FunctionalTests.Add("Word: the Ribbon and Quick Access Toolbar show your custom buttons; AutoCorrect entries work.");
        o.FunctionalTests.Add("Word: File → Options → Trust Center: macro settings and trusted locations allow your templates to run.");
        return o;
    }

    static FileDecision? TemplateRule(FileCandidate f)
    {
        if (Rules.IsTempOrLock(f)) return null;
        if (f.Name.Equals("Normal.dotm", StringComparison.OrdinalIgnoreCase)) return new(Classification.Recommended, "Word global template: styles, AutoText, macros, customizations");
        if (f.Name.Equals("NormalEmail.dotm", StringComparison.OrdinalIgnoreCase)) return new(Classification.Recommended, "Outlook e-mail global template");
        if (TemplateExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return new(Classification.Recommended, "Own Word template");
        if (f.Extension.Equals(".thmx", StringComparison.OrdinalIgnoreCase)) return new(Classification.Review, "Office theme");
        return new(Classification.Review, "Other file in the templates folder");
    }

    static FileDecision? StartupRule(FileCandidate f)
    {
        if (Rules.IsTempOrLock(f)) return null;
        if (f.Extension.Equals(".dotm", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".dot", StringComparison.OrdinalIgnoreCase))
            return new(Classification.Recommended, "Global template loaded at Word start");
        if (f.Extension.Equals(".wll", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return new(Classification.Review, "Binary Word add-in: architecture-specific; may need its installer");
        return new(Classification.Review, "Other file in Word Startup");
    }

    static FileDecision? SearchRule(FileCandidate f)
    {
        if (Rules.IsTempOrLock(f) || !SearchExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return null;
        if (KnownToolExamples.Any(k => k.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
            return new(Classification.Recommended, "Known own Word tool");
        return new(Classification.Review, f.Extension.Equals(".docm", StringComparison.OrdinalIgnoreCase)
            ? "Macro-enabled document outside the standard folders: may contain own tools"
            : "Template/add-in outside the standard folders: confirm it is yours");
    }

    void ExportRegistry(DiscoveryContext ctx, UserProfileInfo p, string hive, string prefix, ProviderOutput o)
    {
        var keys = new[]
        {
            @"Software\Microsoft\Office\16.0\Word\Options",
            @"Software\Microsoft\Office\16.0\Word\Security",
            @"Software\Microsoft\Office\16.0\Common\General",
            @"Software\Microsoft\Office\16.0\Common\LanguageResources",
            @"Software\Microsoft\Office\16.0\Common\AutoCorrect",
            @"Software\Microsoft\Shared Tools\Proofing Tools\1.0\Custom Dictionaries",
        };
        foreach (var k in keys) RegistryExport.Export(ctx, hive, prefix + k, p, "Microsoft Word", o, recurse: k.EndsWith("Security"));
    }

    static void DiscoverComAddins(DiscoveryContext ctx, OfficeEnvironment office, ProviderOutput o)
    {
        var places = new List<(string Hive, string Path, RegView View)>
        {
            ("HKCU", @"Software\Microsoft\Office\Word\Addins", RegView.Default),
            ("HKLM", @"SOFTWARE\Microsoft\Office\Word\Addins", RegView.Reg64),
            ("HKLM", @"SOFTWARE\Microsoft\Office\Word\Addins", RegView.Reg32),
        };
        foreach (var (h, path, view) in places)
        {
            foreach (var progId in ctx.Registry.SubKeys(h, path, view))
            {
                var k = path + "\\" + progId;
                var friendly = ctx.Registry.Value(h, k, "FriendlyName", view)?.AsString() ?? progId;
                var lb = ctx.Registry.Value(h, k, "LoadBehavior", view)?.AsString() ?? "?";
                var manifest = ctx.Registry.Value(h, k, "Manifest", view)?.AsString();
                var comp = new Component
                {
                    Id = "addin-" + PathUtil.StableId(h + view + progId), Name = friendly, Application = "Microsoft Word", ProviderId = ProviderId,
                    Kind = manifest != null ? "VSTO add-in" : "COM add-in", Classification = Classification.InstallerManaged,
                    MigrationMethod = MigrationMethod.Reinstall, SourcePath = manifest,
                    DestinationRule = "Reinstall the add-in with its own installer",
                    VerificationMethod = "Word → File → Options → Add-ins: the add-in is listed as Active",
                    Basis = $"{h}\\{path} ({view}); LoadBehavior={lb}",
                };
                string? dll = null; string arch = "Unknown"; var conf = Confidence.Unknown;
                if (manifest != null)
                {
                    var file = manifest.Split('|')[0].Replace("file:///", "").Replace('/', '\\');
                    conf = File.Exists(file) ? Confidence.Confirmed : Confidence.Unknown;
                    o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = friendly, DependsOn = "Visual Studio Tools for Office Runtime", Type = "Runtime",
                        Confidence = Confidence.Confirmed, RequiredAction = "Install VSTO runtime, then the add-in", ValidationTest = comp.VerificationMethod, Evidence = "Manifest value present" });
                }
                else
                {
                    var clsid = ctx.Registry.Value("HKCR", progId + "\\CLSID", "")?.AsString();
                    if (clsid != null)
                    {
                        dll = ctx.Registry.Value("HKCR", $@"CLSID\{clsid}\InprocServer32", "", RegView.Reg64)?.AsString()
                              ?? ctx.Registry.Value("HKCR", $@"CLSID\{clsid}\InprocServer32", "", RegView.Reg32)?.AsString();
                        if (dll != null)
                        {
                            dll = Environment.ExpandEnvironmentVariables(dll.Trim('"'));
                            if (File.Exists(dll)) { arch = PeInfo.Architecture(dll); conf = Confidence.Confirmed; }
                        }
                    }
                }
                comp.Confidence = conf;
                comp.ManualActions.Add("Find and reinstall this add-in from its vendor/installer; registering copied DLLs by hand is not supported.");
                o.Components.Add(comp);
                o.Dependencies.Add(new Dependency
                {
                    Application = "Microsoft Word", Component = friendly, DependsOn = dll ?? manifest ?? progId, Type = comp.Kind, Confidence = conf,
                    RequiredAction = "Reinstall add-in", ValidationTest = comp.VerificationMethod,
                    Evidence = dll != null ? $"InprocServer32 {dll} ({arch})" : "Registration found; server file not resolved"
                });
                if (arch != "Unknown" && office.Architecture != SoftwareItem.UnknownText && !arch.Equals(office.Architecture, StringComparison.OrdinalIgnoreCase))
                    o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"Add-in '{friendly}' is {arch} but Office is {office.Architecture}; it cannot load in this Office.", dll));
            }
        }
    }

    /// <summary>OFF-005/008: VBA/COM analysis of indexed templates without executing macros.</summary>
    public void AnalyzeIndexed(DiscoveryContext ctx, IReadOnlyList<FileEntry> files, ProviderOutput o)
    {
        var office = ctx.Office;
        foreach (var fe in files)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(fe.SourcePath);
            bool isWll = ext.Equals(".wll", StringComparison.OrdinalIgnoreCase);
            bool isVbaHost = ext.Equals(".dotm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dot", StringComparison.OrdinalIgnoreCase) || ext.Equals(".docm", StringComparison.OrdinalIgnoreCase);
            bool isTemplate = isVbaHost || ext.Equals(".dotx", StringComparison.OrdinalIgnoreCase);
            if (!isTemplate && !isWll) continue;
            var name = Path.GetFileName(fe.SourcePath);
            bool inStartup = fe.Category.Contains("Startup", StringComparison.OrdinalIgnoreCase);
            var comp = new Component
            {
                Id = "file-" + fe.Id, Name = name, Application = "Microsoft Word", ProviderId = Id, Kind = isWll ? "Word add-in (WLL)" : "Word template",
                Classification = fe.Classification, MigrationMethod = MigrationMethod.CopyFile, SourcePath = fe.SourcePath, ProfileSid = fe.ProfileSid,
                DestinationRule = fe.ProfileSid != null ? "Same relative location inside the mapped target profile" : "Same path on the target system",
                VerificationMethod = "SHA-256 after copy; then open in Word",
                Basis = fe.Note ?? fe.Category, Confidence = Confidence.Confirmed,
            };
            if (inStartup) comp.ManualActions.Add("Word loads it from STARTUP automatically; confirm under Templates and Add-ins.");
            if (isWll)
            {
                var arch = PeInfo.Architecture(fe.SourcePath);
                comp.RequiredPrerequisites.Add($"Office {arch}");
                o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = $"Office {arch}", Type = "Architecture",
                    Confidence = arch == "Unknown" ? Confidence.Unknown : Confidence.Confirmed, RequiredAction = "Install Office with the same architecture",
                    ValidationTest = "Add-in loads without error", Evidence = "PE header" });
                if (office != null && arch != "Unknown" && office.Architecture != SoftwareItem.UnknownText && !arch.Equals(office.Architecture, StringComparison.OrdinalIgnoreCase))
                    o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"{name} is {arch}, Office is {office.Architecture}: it cannot load.", fe.SourcePath));
                o.Components.Add(comp);
                continue;
            }
            if (isVbaHost)
            {
                var a = VbaProjectReader.AnalyzeFile(fe.SourcePath);
                o.Manifests["VBA:" + fe.Id] = a;
                if (a.Errors.Count > 0)
                {
                    o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"{name}: VBA project could not be fully analysed ({string.Join("; ", a.Errors)}). Dependencies are Unknown.", fe.SourcePath, "Check its macros manually in the VBA editor."));
                    o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = "Unknown dependency", Type = "VBA", Confidence = Confidence.Unknown,
                        RequiredAction = "Manual check", ValidationTest = "Run its macros", Evidence = string.Join("; ", a.Errors) });
                }
                if (a.HasVbaProject)
                {
                    comp.Kind = "Word template with macros";
                    comp.ManualActions.Add("Allow macros: put it in a trusted location or sign it; check Trust Center settings.");
                    comp.ManualActions.Add($"Modules: {string.Join(", ", a.Modules.Select(m => m.Name))}");
                    foreach (var r in a.References)
                    {
                        bool standard = r.Guid != null && StandardTypeLibGuids.Contains(r.Guid);
                        bool exists = r.Path != null && File.Exists(Environment.ExpandEnvironmentVariables(r.Path));
                        o.Dependencies.Add(new Dependency
                        {
                            Application = "Microsoft Word", Component = name, DependsOn = $"{r.Name} ({r.Path ?? r.Libid})", Type = "VBA reference (" + r.Kind + ")",
                            Confidence = standard ? Confidence.Confirmed : exists ? Confidence.Inferred : Confidence.Unknown,
                            RequiredAction = standard ? "None (part of Windows/Office)" : "Install/register the library that provides this reference",
                            ValidationTest = "VBA editor → Tools → References: no item marked MISSING",
                            Evidence = r.Libid
                        });
                        if (!standard && !exists)
                            o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"{name}: VBA reference '{r.Name}' points to {r.Path ?? r.Libid}, which was not found on this computer.", fe.SourcePath));
                    }
                    foreach (var d in a.Declares)
                    {
                        var lib = Path.GetFileNameWithoutExtension(d.Library);
                        bool sys = SystemDlls.Contains(lib) || SystemDlls.Contains(d.Library);
                        o.Dependencies.Add(new Dependency
                        {
                            Application = "Microsoft Word", Component = name, DependsOn = d.Library, Type = "VBA Declare (" + d.Procedure + ")",
                            Confidence = sys ? Confidence.Confirmed : Confidence.Unknown,
                            RequiredAction = sys ? "None (Windows DLL)" : "Locate and install the DLL this macro calls",
                            ValidationTest = $"Run the macro in module {d.Module}", Evidence = (d.PtrSafe ? "PtrSafe " : "") + "Declare in " + d.Module
                        });
                    }
                    if (a.Declares.Any(d => !d.PtrSafe) && office?.Architecture == "x64" && !a.UsesConditionalBitness)
                        o.Issues.Add(new Issue(IssueSeverity.Warning, "Word", $"{name}: Declare without PtrSafe and without #If VBA7: will not compile in 64-bit Office.", fe.SourcePath, "Update the Declare statements (PtrSafe/LongPtr)."));
                    foreach (var prog in a.CreateObjectProgIds)
                    {
                        var clsid = ctx.Registry.Value("HKCR", prog + "\\CLSID", "")?.AsString();
                        o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = prog, Type = "COM (CreateObject)",
                            Confidence = clsid != null ? Confidence.Inferred : Confidence.Unknown,
                            RequiredAction = clsid != null ? "Ensure the program providing this COM class is installed" : "COM class not registered here: identify and install its provider",
                            ValidationTest = "Run the macro that uses it", Evidence = clsid != null ? "Registered CLSID " + clsid : "Not registered on source" });
                    }
                    foreach (var path in a.ExternalPaths)
                        o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = path, Type = "File path in macro code",
                            Confidence = File.Exists(path) || Directory.Exists(path) ? Confidence.Inferred : Confidence.Unknown,
                            RequiredAction = "Make sure this path exists on the new system (add it to the plan as a custom folder if it is yours)",
                            ValidationTest = "Run the macro", Evidence = "String literal in VBA code" });
                    if (a.UsesShell) comp.ManualActions.Add("Macros start external programs (Shell): check those programs are installed.");
                }
                foreach (var cls in a.ActiveXClassIds)
                    o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = cls, Type = "ActiveX control",
                        Confidence = ctx.Registry.KeyExists("HKCR", "CLSID\\" + cls) ? Confidence.Inferred : Confidence.Unknown,
                        RequiredAction = "Ensure the control is installed", ValidationTest = "Open the template; the control works", Evidence = "word/activeX part" });
                foreach (var rel in a.ExternalRelationships)
                    o.Dependencies.Add(new Dependency { Application = "Microsoft Word", Component = name, DependsOn = rel, Type = "External link",
                        Confidence = Confidence.Inferred, RequiredAction = "Check the linked file/template exists on the new system", ValidationTest = "Open the file", Evidence = "OOXML relationship (TargetMode=External)" });
            }
            o.Components.Add(comp);
        }
    }
}

/// <summary>REG-001/004: review-only export of named keys; secrets and MRU/privacy data are skipped.</summary>
public static class RegistryExport
{
    static readonly Regex Skip = new(@"MRU|Recent|Place|Identity|License|Registration|ProductKey|DigitalProductId|Token|Credential|Password|Secret|DPAPI|Cache|Telemetry", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MachineSpecific = new(@"S-1-5-21-[\d\-]+|\{[0-9A-Fa-f\-]{36}\}|^[A-Za-z]:\\|\\\\", RegexOptions.Compiled);

    public static void Export(DiscoveryContext ctx, string hive, string key, UserProfileInfo p, string app, ProviderOutput o, bool recurse = false, int depth = 0)
    {
        if (depth > 3 || !ctx.Registry.KeyExists(hive, key)) return;
        foreach (var name in ctx.Registry.ValueNames(hive, key))
        {
            if (Skip.IsMatch(name) || Redaction.LooksSecretName(name)) continue;
            var v = ctx.Registry.Value(hive, key, name);
            if (v == null) continue;
            if (v.Data is byte[] b && b.Length > 4096) continue;
            var rec = new RegistryValueRecord
            {
                Id = PathUtil.StableId($"{p.Sid}|{key}|{name}"),
                Hive = "HKCU", KeyPath = hive == "HKU" && key.StartsWith(p.Sid + "\\") ? key[(p.Sid.Length + 1)..] : key,
                ValueName = name, Type = v.Kind, ProfileSid = p.Sid, Application = app,
            };
            if (v.Data is string[] arr) rec.MultiData = arr.ToList(); else rec.Data = v.AsString();
            var text = rec.Data ?? string.Join(";", rec.MultiData ?? new());
            if (MachineSpecific.IsMatch(text))
            {
                rec.MachineSpecific = true;
                rec.Note = "Contains a machine-specific path/SID/GUID: review before restoring.";
                if (!string.IsNullOrEmpty(p.ProfilePath) && text.Contains(p.ProfilePath, StringComparison.OrdinalIgnoreCase))
                    rec.Note += " The source profile path will be replaced by the mapped target profile path.";
            }
            o.Registry.Add(rec);
        }
        if (recurse)
            foreach (var sub in ctx.Registry.SubKeys(hive, key))
                if (!Skip.IsMatch(sub)) Export(ctx, hive, key + "\\" + sub, p, app, o, true, depth + 1);
    }
}
