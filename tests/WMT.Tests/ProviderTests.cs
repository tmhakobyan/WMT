using WMT.Core;
using WMT.Core.Vba;
using Xunit;

namespace WMT.Tests;

public class VbaTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact] public void Decompress_literal_vector_from_MS_OVBA()
        => Assert.Equal("abcdefghijklmnopqrstuv.", System.Text.Encoding.ASCII.GetString(OvbaCompression.Decompress(Hex("01 19 B0 00 61 62 63 64 65 66 67 68 00 69 6A 6B 6C 6D 6E 6F 70 00 71 72 73 74 75 76 2E"))));

    [Fact] public void Decompress_copy_token_vector_from_MS_OVBA()
        => Assert.Equal("#aaabcdefaaaaghijaaaaaklaaamnopqaaaaaaaaaaaarstuvwxyzaaa", System.Text.Encoding.ASCII.GetString(OvbaCompression.Decompress(
            Hex("01 2F B0 00 23 61 61 61 62 63 64 65 82 66 00 70 61 67 68 69 6A 01 38 08 61 6B 6C 00 30 6D 6E 6F 70 06 71 02 70 04 10 72 73 74 75 76 10 77 78 79 7A 00 3C"))));

    [Fact] public void Synthetic_dotm_is_analysed_without_running_macros()
    {
        var a = VbaProjectReader.AnalyzeFile(T.Fixture("synthetic.dotm"));
        Assert.Empty(a.Errors);
        Assert.True(a.HasVbaProject);
        var m = Assert.Single(a.Modules);
        Assert.Equal("HyphenTools", m.Name);
        Assert.Contains("Sub Hyphenate()", m.Source);
        Assert.Equal(3, a.Declares.Count);
        Assert.Contains(a.Declares, d => d.Library == "ArmHyph.dll" && !d.PtrSafe);
        Assert.Contains(a.Declares, d => d.Library == "kernel32" && d.PtrSafe);
        Assert.True(a.UsesConditionalBitness);
        Assert.Equal(2, a.References.Count);
        Assert.Contains(a.References, r => r.Name == "ArmTools" && r.Path == @"C:\Tools\ArmTools\ArmTools.tlb" && r.Guid == "{12345678-1234-1234-1234-1234567890AB}");
        Assert.Contains("Scripting.Dictionary", a.CreateObjectProgIds);
        Assert.Contains(@"C:\Tools\Hyphen\rules-hy.txt", a.ExternalPaths);
        Assert.Contains("{8BD21D40-EC42-11CE-9E0D-00AA006002F3}", a.ActiveXClassIds);
        Assert.Contains(a.ExternalRelationships, r => r.StartsWith("attachedTemplate"));
    }

    [Fact] public void Raw_compound_file_is_readable()
    {
        var a = new VbaAnalysis();
        VbaProjectReader.AnalyzeCompound(File.ReadAllBytes(T.Fixture("vbaProject.bin")), a);
        Assert.True(a.HasVbaProject); Assert.Single(a.Modules);
    }

    [Fact] public void Garbage_is_reported_not_thrown()
    {
        var p = T.Write(Path.Combine(T.Temp(), "bad.dotm"), "not a zip");
        var a = VbaProjectReader.AnalyzeFile(p);
        Assert.NotEmpty(a.Errors); Assert.False(a.HasVbaProject);
    }
}

public class WordProviderTests
{
    static (string Profile, MigrationPlan Plan) Build()
    {
        var p = T.Temp("profile");
        string R(string rel) => Path.Combine(p, rel);
        Directory.CreateDirectory(R(@"AppData\Roaming\Microsoft\Templates"));
        File.Copy(T.Fixture("synthetic.dotm"), R(@"AppData\Roaming\Microsoft\Templates\Normal.dotm"));
        T.Write(R(@"AppData\Roaming\Microsoft\Templates\My Letter.dotx"));
        T.Write(R(@"AppData\Roaming\Microsoft\Templates\LiveContent\16\Managed\x.dotx"));
        T.Write(R(@"AppData\Roaming\Microsoft\Templates\~$Normal.dotm"));
        T.Write(R(@"AppData\Roaming\Microsoft\Word\STARTUP\HT_Doc_template_2.dotm"));
        T.Write(R(@"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC"), "բառ");
        T.Write(R(@"AppData\Roaming\Microsoft\Spelling\hy-AM\default.dic"), "բառ");
        T.Write(R(@"AppData\Roaming\Microsoft\Office\MSO1033.acl"));
        T.Write(R(@"AppData\Local\Microsoft\Office\Word.officeUI"), "<customUI/>");
        for (int i = 0; i < 50; i++) T.Write(R($@"AppData\Local\Microsoft\Office\16.0\Word{i}_Rules\rule.xml"));
        T.Write(R(@"AppData\Local\Microsoft\Office\16.0\OfficeFileCache\cache.dat"));
        T.Write(R(@"Documents\Tools\ConverterBox 3D.dotm"));
        T.Write(R(@"Documents\letter.docx"));
        T.Write(R(@"Documents\node_modules\pkg\x.dotm"));
        T.Write(R(@"MyTrusted\tool.dotm"));
        var reg = new MemoryRegistry();
        reg.Set("HKCU", @"Software\Microsoft\Office\16.0\Word\Security\Trusted Locations\Location9", "Path", "String", R("MyTrusted"));
        reg.Set("HKCU", @"Software\Microsoft\Office\16.0\Word\Options", "AUTOFORMAT", "DWord", 1);
        reg.Set("HKCU", @"Software\Microsoft\Office\16.0\Word\Options", "DOC-PATH", "ExpandString", R("Documents"));
        reg.Set("HKCU", @"Software\Microsoft\Office\16.0\Word\Options", "ProductKeyCache", "String", "SECRET-SHOULD-NOT-EXPORT");
        var office = new SoftwareItem { Name = "Microsoft Office Professional Plus 2016", Category = "Office", Providers = { WordProvider.ProviderId }, Selected = true };
        var ctx = T.Ctx(new[] { office }, new[] { T.Profile(p) }, reg: reg);
        ctx.Office = new OfficeEnvironment { Installed = true, Family = office.Name, Release = "2016", Version = "16.0.20326.20318", Build = "20326.20318", Architecture = "x64" };
        return (p, new PlanBuilder(new IDiscoveryProvider[] { new WordProvider() }).Build(ctx));
    }

    [Fact] public void Finds_own_word_customizations_and_skips_office_caches()
    {
        var (p, plan) = Build();
        var names = plan.Index.Files.Select(f => Path.GetRelativePath(p, f.SourcePath)).ToList();
        foreach (var expect in new[] { @"AppData\Roaming\Microsoft\Templates\Normal.dotm", @"AppData\Roaming\Microsoft\Templates\My Letter.dotx",
            @"AppData\Roaming\Microsoft\Word\STARTUP\HT_Doc_template_2.dotm", @"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC",
            @"AppData\Roaming\Microsoft\Spelling\hy-AM\default.dic", @"AppData\Roaming\Microsoft\Office\MSO1033.acl",
            @"AppData\Local\Microsoft\Office\Word.officeUI", @"Documents\Tools\ConverterBox 3D.dotm", @"MyTrusted\tool.dotm" })
            Assert.Contains(expect, names);
        Assert.DoesNotContain(names, n => n.Contains("_Rules") || n.Contains("LiveContent") || n.Contains("OfficeFileCache") || n.Contains("node_modules") || n.EndsWith("letter.docx") || n.Contains("~$"));
        Assert.Equal(Classification.Recommended, plan.Index.Files.Single(f => f.SourcePath.EndsWith("ConverterBox 3D.dotm")).Classification);
        Assert.Equal(Classification.Review, plan.Index.Files.Single(f => f.SourcePath.EndsWith("tool.dotm")).Classification);
        Assert.All(plan.Index.Files.Where(f => f.Classification == Classification.Recommended), f => Assert.True(f.Selected));
    }

    [Fact] public void Vba_dependencies_are_classified_and_unknown_ones_flagged()
    {
        var (_, plan) = Build();
        Assert.Contains(plan.Dependencies, d => d.DependsOn == "ArmHyph.dll" && d.Confidence == Confidence.Unknown);
        Assert.Contains(plan.Dependencies, d => d.DependsOn == "kernel32" && d.Confidence == Confidence.Confirmed);
        Assert.Contains(plan.Dependencies, d => d.DependsOn.StartsWith("stdole") && d.Confidence == Confidence.Confirmed);
        Assert.Contains(plan.Dependencies, d => d.DependsOn.StartsWith("ArmTools") && d.Confidence == Confidence.Unknown);
        Assert.Contains(plan.Dependencies, d => d.Type == "ActiveX control");
        Assert.Contains(plan.Dependencies, d => d.DependsOn == @"C:\Tools\Hyphen\rules-hy.txt");
        Assert.Contains(plan.Components, c => c.Name == "Normal.dotm" && c.Kind == "Word template with macros" && c.ManualActions.Any(a => a.Contains("HyphenTools")));
        Assert.Contains(plan.Components, c => c.Id == "office-product" && c.MigrationMethod == MigrationMethod.Reinstall);
        Assert.NotEmpty(plan.FunctionalTests);
    }

    [Fact] public void Registry_export_is_review_only_and_skips_secrets()
    {
        var (p, plan) = Build();
        Assert.Contains(plan.Registry, r => r.ValueName == "AUTOFORMAT" && r.Type == "DWord" && r.DefaultAction == "ReviewOnly");
        Assert.DoesNotContain(plan.Registry, r => r.ValueName == "ProductKeyCache" || (r.Data ?? "").Contains("SECRET"));
        Assert.True(plan.Registry.Single(r => r.ValueName == "DOC-PATH").MachineSpecific);
    }

    [Fact] public void Unloaded_other_user_hive_is_not_mounted_and_reported()
    {
        var p = T.Temp("other");
        var prof = T.Profile(p, "S-1-5-21-7-7-7-1009"); prof.Loaded = false; prof.IsCurrentUser = false;
        var office = new SoftwareItem { Name = "Microsoft Office Professional Plus 2016", Category = "Office", Providers = { WordProvider.ProviderId } };
        var ctx = T.Ctx(new[] { office }, new[] { prof });
        ctx.Office = OfficeEnvironment.Absent();
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new WordProvider() }).Build(ctx);
        Assert.Contains(plan.Issues, i => i.Message.Contains("hive not loaded"));
    }
}

public class PythonProviderTests
{
    static (string Project, MigrationPlan Plan, FakeProcess Proc) Build(bool probeFails = false)
    {
        var p = T.Temp("pyprofile");
        var proj = Path.Combine(p, "OCR", "calfa");
        T.Write(Path.Combine(proj, ".venv", "pyvenv.cfg"), "home = C:\\Python311\nversion = 3.11.9\n");
        Directory.CreateDirectory(Path.Combine(proj, ".venv", "Scripts"));
        File.Copy(Environment.ProcessPath!, Path.Combine(proj, ".venv", "Scripts", "python.exe"));
        T.Write(Path.Combine(proj, ".venv", "Lib", "site-packages", "paddle", "big.py"));
        T.Write(Path.Combine(proj, "main.py"), "import paddleocr");
        T.Write(Path.Combine(proj, "requirements.txt"), "paddleocr==2.7.0");
        T.Write(Path.Combine(proj, "models", "hye.traineddata"), "model");
        T.Write(Path.Combine(proj, "models", "det.onnx"), "onnx");
        T.Write(Path.Combine(proj, "__pycache__", "main.cpython-311.pyc"));
        T.Write(Path.Combine(proj, "run.bat"), "@python main.py");
        var proc = new FakeProcess
        {
            Handler = (exe, a) =>
            {
                if (exe == "py" || exe == "nvidia-smi") return FakeProcess.Fail(1, "not available");
                if (a.Count > 0 && a[0] == "-I") return probeFails ? FakeProcess.Fail(1, "Fatal Python error") : FakeProcess.Ok("{\"v\":\"3.11.9\",\"bits\":64,\"exe\":\"x\",\"base\":\"C:\\\\Python311\",\"prefix\":\"y\",\"machine\":\"AMD64\"}");
                if (a.Contains("--version")) return FakeProcess.Ok("pip 24.0 from x (python 3.11)");
                if (a.Contains("list")) return FakeProcess.Ok("[{\"name\":\"paddleocr\",\"version\":\"2.7.0\"},{\"name\":\"pytesseract\",\"version\":\"0.3.10\"},{\"name\":\"paddlepaddle\",\"version\":\"2.6.0\"}]");
                if (a.Contains("freeze")) return FakeProcess.Fail(1, "ERROR: freeze crashed");
                if (a.Contains("inspect")) return FakeProcess.Ok("{}");
                return FakeProcess.Fail(1, "unexpected");
            }
        };
        var py = new SoftwareItem { Name = "Python 3.11.9 (64-bit)", Category = "Python", Providers = { PythonProvider.ProviderId } };
        var ctx = T.Ctx(new[] { py }, new[] { T.Profile(p) }, proc: proc);
        return (proj, new PlanBuilder(new IDiscoveryProvider[] { new PythonProvider() }).Build(ctx), proc);
    }

    [Fact] public void Venv_versions_and_failed_freeze_are_recorded_honestly()
    {
        var (proj, plan, proc) = Build();
        var env = plan.Python!.Environments.Single(e => e.Kind == "Virtual environment");
        Assert.Equal("3.11.9", env.Version); Assert.Equal("x64", env.Architecture);
        Assert.Equal(CaptureStatus.Failed, env.FreezeStatus); Assert.Equal(CaptureStatus.Captured, env.ListStatus);
        Assert.Equal(CaptureStatus.Partial, env.Overall);
        Assert.Contains(env.Errors, e => e.Contains("freeze"));
        Assert.Contains(env.Commands, c => c.Command.Contains("freeze") && c.ExitCode == 1 && c.StdErr.Contains("crashed"));
        Assert.Equal(proj, env.ProjectPath);
        Assert.Contains(plan.Issues, i => i.Area == "Python" && i.Message.Contains("Partial"));
        Assert.Contains(plan.Dependencies, d => d.DependsOn.StartsWith("Tesseract") && d.Confidence == Confidence.Unknown);
    }

    [Fact] public void Project_files_and_models_kept_venv_and_caches_not()
    {
        var (proj, plan, _) = Build();
        var rel = plan.Index.Files.Where(f => f.SourcePath.StartsWith(proj)).Select(f => Path.GetRelativePath(proj, f.SourcePath)).ToList();
        Assert.Contains("main.py", rel); Assert.Contains("requirements.txt", rel); Assert.Contains("run.bat", rel);
        Assert.Contains(@"models\hye.traineddata", rel); Assert.Contains(@"models\det.onnx", rel);
        Assert.DoesNotContain(rel, r => r.StartsWith(".venv") || r.Contains("__pycache__") || r.Contains("site-packages"));
        Assert.Equal(Classification.Recommended, plan.Index.Files.Single(f => f.SourcePath.EndsWith("hye.traineddata")).Classification);
    }

    [Fact] public void Broken_interpreter_is_Failed_and_a_blocker()
    {
        var (_, plan, _) = Build(probeFails: true);
        var env = plan.Python!.Environments.Single(e => e.Kind == "Virtual environment");
        Assert.Equal(CaptureStatus.Failed, env.Overall);
        Assert.Contains(plan.Issues, i => i.Severity == IssueSeverity.Blocker && i.Area == "Python");
    }

    [Fact] public void Installer_url_is_exact_or_absent()
    {
        Assert.Equal("https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe", PythonProvider.PythonInstallerUrl("3.11.9", "x64"));
        Assert.Equal("https://www.python.org/ftp/python/3.8.10/python-3.8.10.exe", PythonProvider.PythonInstallerUrl("3.8.10", "x86"));
        Assert.Null(PythonProvider.PythonInstallerUrl("Unknown", "x64"));
    }
}
