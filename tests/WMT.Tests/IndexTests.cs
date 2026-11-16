using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class IndexerTests
{
    static string Make(int count, int perDir = 1000)
    {
        var root = T.Temp("n" + count);
        for (int i = 0; i < count; i++)
        {
            var d = Path.Combine(root, "d" + (i / perDir));
            if (i % perDir == 0) Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, $"f{i}.txt"), new byte[] { (byte)(i % 251) });
        }
        return root;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(100)]
    public void Small_counts(int n)
    {
        var plan = T.PlanFor(Make(n));
        Assert.Equal(n, plan.Index.Files.Count);
        Assert.Equal(n, plan.EnsureTree().TotalSelected);
    }

    [Fact] public void Large_35000_and_100000_files_index_without_gui_objects()
    {
        foreach (var n in new[] { 35_000, 100_000 })
        {
            var root = Make(n);
            var sw = Stopwatch.StartNew();
            var plan = T.PlanFor(root);
            var tree = plan.EnsureTree();
            Assert.Equal(n, plan.Index.Files.Count);
            Assert.Equal(n, tree.TotalEligible);
            tree.SetSelected(tree.Categories[0], false);
            Assert.Equal(0, tree.TotalSelected);
            tree.SetSelected(tree.Categories[0], true);
            Assert.Equal(n, tree.TotalSelected);
            var (page, pages) = ExplorerQuery.Page(ExplorerQuery.List(tree, tree.Find(tree.Categories[0].Children.Values.First().Key)!.Children.Values.First(), null, ExplorerSort.Name, false), 0, 500);
            Assert.Equal(500, page.Count); Assert.Equal(2, pages);
            Assert.True(sw.Elapsed < TimeSpan.FromMinutes(3), $"{n} files took {sw.Elapsed}");
            Directory.Delete(root, true);
        }
    }

    [Fact] public void Empty_only_excluded_and_inaccessible_are_distinguished()
    {
        var root = T.Temp("states");
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        T.Write(Path.Combine(root, "OnlyTemp", "~$lock.docx"));
        T.Write(Path.Combine(root, "Normal", "a.txt"));
        var locked = Directory.CreateDirectory(Path.Combine(root, "Locked"));
        T.Write(Path.Combine(locked.FullName, "secret.txt"));
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var sec = locked.GetAccessControl(); sec.AddAccessRule(rule); locked.SetAccessControl(sec);
        try
        {
            var plan = T.PlanFor(root);
            Assert.Single(plan.Index.Files);
            Assert.Contains(plan.Index.Directories, d => d.Path.EndsWith("Empty") && d.State == FolderState.Empty);
            Assert.Contains(plan.Index.Directories, d => d.Path.EndsWith("OnlyTemp") && d.State == FolderState.OnlyExcluded);
            Assert.Contains(plan.Index.Directories, d => d.Path.EndsWith("Locked") && d.State == FolderState.Inaccessible);
            Assert.DoesNotContain(plan.Index.Directories, d => d.Path.EndsWith("Locked") && d.State == FolderState.Empty);
            var tree = plan.EnsureTree();
            Assert.Contains(tree.DescendantFolders(tree.Categories[0]), n => n.Name == "Locked" && n.State == FolderState.Inaccessible);
            Assert.DoesNotContain(tree.DescendantFolders(tree.Categories[0]), n => n.Name == "Empty" || n.Name == "OnlyTemp");
        }
        finally { sec.RemoveAccessRule(rule); locked.SetAccessControl(sec); }
    }

    [Theory]
    [InlineData(EmptyFolderMode.ExcludeEmpty, 0)]
    [InlineData(EmptyFolderMode.KeepAllEmpty, 1)]
    public void Empty_folder_modes(EmptyFolderMode mode, int kept)
    {
        var root = T.Temp("emptymode");
        Directory.CreateDirectory(Path.Combine(root, "Keep", "Me"));
        T.Write(Path.Combine(root, "a.txt"));
        var ctx = T.Ctx(opts: new DiscoveryOptions { CustomPaths = { root }, EmptyFolders = mode, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new CustomPathsProvider() }).Build(ctx);
        Assert.Equal(kept, plan.Index.Directories.Count(d => d.Included));
    }

    [Fact] public void Junction_loop_is_not_followed()
    {
        var root = T.Temp("loop");
        T.Write(Path.Combine(root, "sub", "a.txt"));
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "sub", "loop")}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(root, "sub", "loop", "sub")), "junction was not created");
        var plan = T.PlanFor(root);
        Assert.Single(plan.Index.Files);
        Assert.True(plan.Index.ReparsePointsSkipped >= 1);
        Assert.Contains(plan.Index.Directories, d => d.State == FolderState.SkippedReparsePoint);
    }

    [Fact] public void Duplicate_and_nested_roots_do_not_duplicate_files()
    {
        var root = T.Temp("nested");
        T.Write(Path.Combine(root, "a.txt")); T.Write(Path.Combine(root, "sub", "b.txt"));
        var ctx = T.Ctx(opts: new DiscoveryOptions { CustomPaths = { root, root + "\\", Path.Combine(root, "sub") }, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new CustomPathsProvider() }).Build(ctx);
        Assert.Equal(2, plan.Index.Files.Count);
        Assert.Equal(2, plan.Index.Files.Select(f => f.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact] public void Long_unicode_armenian_paths()
    {
        var root = T.Temp("long");
        var seg = new string('Ա', 40) + "_" + "Թղթապանակ";
        var dir = Path.Combine(root, seg, seg, seg, seg, seg, seg);
        Assert.True(dir.Length > 260);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Ֆայլ_բառարան.dic");
        File.WriteAllText(file, "բառ");
        var plan = T.PlanFor(root);
        Assert.Equal(file, Assert.Single(plan.Index.Files).SourcePath);
        var dest = T.Temp("longdest");
        var r = T.Backup(plan, dest);
        Assert.Equal("Complete", r.Status);
        Assert.True(Verifier.Verify(r.PackagePath, null, CancellationToken.None).Passed);
    }

    [Fact] public void Backup_destination_inside_a_scanned_tree_is_skipped()
    {
        var root = T.Temp("withpkg");
        T.Write(Path.Combine(root, "a.txt"));
        var dest = Directory.CreateDirectory(Path.Combine(root, "Backups")).FullName;
        T.Write(Path.Combine(dest, "old.txt"));
        var ctx = T.Ctx(opts: new DiscoveryOptions { CustomPaths = { root }, ExcludedPaths = { dest }, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new CustomPathsProvider() }).Build(ctx);
        Assert.Single(plan.Index.Files);
        Assert.Contains(plan.Index.Directories, d => d.State == FolderState.SkippedPackage);
    }
}

public class FolderTreeTests
{
    static (MigrationPlan, FolderTree) Make()
    {
        var root = T.Temp("tree");
        T.Write(Path.Combine(root, "A", "1.txt"), "11"); T.Write(Path.Combine(root, "A", "2.txt"), "22");
        T.Write(Path.Combine(root, "A", "Deep", "Deeper", "3.txt"), "333");
        T.Write(Path.Combine(root, "B", "4.png"), "4444");
        var plan = T.PlanFor(root);
        return (plan, plan.EnsureTree());
    }

    [Fact] public void Tri_state_category_folder_file()
    {
        var (plan, t) = Make();
        var cat = t.Categories.Single();
        Assert.True(cat.CheckState);
        var a = t.DescendantFolders(cat).Single(n => n.Name == "A");
        var file1 = plan.Index.Files.FindIndex(f => f.SourcePath.EndsWith("1.txt"));
        t.SetFileSelected(file1, false);
        Assert.Null(a.CheckState); Assert.Null(cat.CheckState);
        t.SetSelected(a, false);
        Assert.False(a.CheckState); Assert.Null(cat.CheckState);
        Assert.False(plan.Index.Files.Single(f => f.SourcePath.EndsWith("3.txt")).Selected); // never "opened" deep folder follows parent
        t.SetSelected(cat, true);
        Assert.True(cat.CheckState);
        Assert.Equal(4, t.TotalSelected); Assert.Equal(11, t.TotalSelectedBytes);
    }

    [Fact] public void Selection_survives_search_paging_and_sorting()
    {
        var (plan, t) = Make();
        var cat = t.Categories.Single();
        var idx = plan.Index.Files.FindIndex(f => f.SourcePath.EndsWith("4.png"));
        t.SetFileSelected(idx, false);
        var found = ExplorerQuery.List(t, cat, "4.png", ExplorerSort.Name, false);
        Assert.Single(found);
        var sorted = ExplorerQuery.List(t, t.DescendantFolders(cat).Single(n => n.Name == "A"), null, ExplorerSort.Size, true);
        Assert.Equal("Deep", sorted[0].Name); Assert.Equal("2.txt", sorted[1].Name);
        var (p1, pages) = ExplorerQuery.Page(sorted, 0, 2);
        Assert.Equal(2, pages); Assert.Equal(2, p1.Count);
        Assert.False(plan.Index.Files[idx].Selected);
    }
}
