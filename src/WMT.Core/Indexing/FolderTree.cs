namespace WMT.Core;

public sealed class FolderNode
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? FullPath { get; init; }
    public FolderNode? Parent { get; init; }
    public bool IsCategory { get; init; }
    public SortedDictionary<string, FolderNode> Children { get; } = new(StringComparer.CurrentCultureIgnoreCase);
    public List<int> Files { get; } = new();
    public FolderState State { get; set; } = FolderState.Normal;
    public string? Note { get; set; }
    public bool IncludedEmpty { get; set; }
    public int EligibleCount { get; internal set; }
    public long EligibleBytes { get; internal set; }
    public int SelectedCount { get; internal set; }
    public long SelectedBytes { get; internal set; }
    /// <summary>IDX-005 tri-state: true = all, false = none, null = partial.</summary>
    public bool? CheckState => EligibleCount == 0 ? false : SelectedCount == 0 ? false : SelectedCount == EligibleCount ? true : null;
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
    public override string ToString() => Name;
}

/// <summary>IDX-002/005/006: hierarchical selection model independent of the GUI. Selecting a parent affects
/// every descendant whether or not the GUI ever opened it; counts are maintained incrementally.</summary>
public sealed class FolderTree
{
    readonly FolderNode[] fileNode;
    public IReadOnlyList<FileEntry> Files { get; }
    public List<FolderNode> Categories { get; } = new();
    readonly Dictionary<string, FolderNode> byKey = new(StringComparer.OrdinalIgnoreCase);

    public FolderTree(FileIndex index)
    {
        Files = index.Files;
        fileNode = new FolderNode[index.Files.Count];
        for (int i = 0; i < index.Files.Count; i++)
        {
            var f = index.Files[i];
            var node = EnsureFolder(f.Category, f.RootPath, Path.GetDirectoryName(f.SourcePath) ?? f.RootPath);
            node.Files.Add(i);
            fileNode[i] = node;
            for (var n = node; n != null; n = n.Parent)
            {
                n.EligibleCount++; n.EligibleBytes += f.Size;
                if (f.Selected) { n.SelectedCount++; n.SelectedBytes += f.Size; }
            }
        }
        foreach (var d in index.Directories)
        {
            if (d.State is FolderState.Inaccessible or FolderState.SkippedReparsePoint || (d.State == FolderState.Empty && d.Included))
            {
                if (string.IsNullOrEmpty(d.RootPath) || !PathUtil.IsInsideOrSame(d.Path, d.RootPath)) continue;
                var node = EnsureFolder(d.Category, d.RootPath, d.Path);
                node.State = d.State; node.Note = d.Note; node.IncludedEmpty = d.State == FolderState.Empty;
            }
        }
    }

    FolderNode EnsureFolder(string category, string rootPath, string dirPath)
    {
        if (!byKey.TryGetValue(category, out var cat))
        {
            cat = new FolderNode { Key = category, Name = category, IsCategory = true };
            byKey[category] = cat; Categories.Add(cat);
        }
        var rootKey = category + "|" + rootPath;
        if (!byKey.TryGetValue(rootKey, out var rootNode))
        {
            rootNode = new FolderNode { Key = rootKey, Name = rootPath, FullPath = rootPath, Parent = cat };
            byKey[rootKey] = rootNode; cat.Children[rootPath] = rootNode;
        }
        var rel = Path.GetRelativePath(rootPath, dirPath);
        if (rel == "." || rel.StartsWith("..")) return rootNode;
        var node = rootNode; var path = rootPath;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, seg);
            var key = category + "|" + path;
            if (!byKey.TryGetValue(key, out var child))
            {
                child = new FolderNode { Key = key, Name = seg, FullPath = path, Parent = node };
                byKey[key] = child; node.Children[seg] = child;
            }
            node = child;
        }
        return node;
    }

    public FolderNode? Find(string key) => byKey.TryGetValue(key, out var n) ? n : null;
    public FolderNode NodeOfFile(int fileIndex) => fileNode[fileIndex];

    public int TotalEligible => Categories.Sum(c => c.EligibleCount);
    public long TotalEligibleBytes => Categories.Sum(c => c.EligibleBytes);
    public int TotalSelected => Categories.Sum(c => c.SelectedCount);
    public long TotalSelectedBytes => Categories.Sum(c => c.SelectedBytes);

    public event Action<FolderNode>? NodeChanged;

    public bool SetFileSelected(int i, bool selected)
    {
        var f = Files[i];
        if (f.Selected == selected) return false;
        f.Selected = selected;
        int dc = selected ? 1 : -1; long db = selected ? f.Size : -f.Size;
        for (var n = fileNode[i]; n != null; n = n.Parent) { n.SelectedCount += dc; n.SelectedBytes += db; }
        return true;
    }

    /// <summary>Selects/deselects every file below <paramref name="node"/> (including never-expanded folders).</summary>
    public int SetSelected(FolderNode node, bool selected)
    {
        int changed = 0;
        foreach (var i in DescendantFiles(node)) if (SetFileSelected(i, selected)) changed++;
        NodeChanged?.Invoke(node);
        return changed;
    }

    public IEnumerable<int> DescendantFiles(FolderNode node)
    {
        var stack = new Stack<FolderNode>(); stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var i in n.Files) yield return i;
            foreach (var c in n.Children.Values) stack.Push(c);
        }
    }

    public IEnumerable<FolderNode> DescendantFolders(FolderNode node)
    {
        var stack = new Stack<FolderNode>(); stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children.Values) stack.Push(c);
        }
    }

    /// <summary>Selects all files of a given classification (e.g. all Recommended).</summary>
    public void SelectByClassification(Classification c, bool selected)
    {
        for (int i = 0; i < Files.Count; i++) if (Files[i].Classification == c) SetFileSelected(i, selected);
        foreach (var cat in Categories) NodeChanged?.Invoke(cat);
    }
}

public enum ExplorerSort { Name, Type, Size, Date }

public sealed record ExplorerItem(bool IsFolder, string Name, string? FullPath, FolderNode? Folder, int FileIndex, long Size, DateTime? Modified, string TypeText);

/// <summary>UI-005/006: listing of one folder (or recursive search) with sort and paging — the GUI only renders one page.</summary>
public static class ExplorerQuery
{
    public static List<ExplorerItem> List(FolderTree tree, FolderNode folder, string? search, ExplorerSort sort, bool descending)
    {
        var items = new List<ExplorerItem>();
        if (string.IsNullOrWhiteSpace(search))
        {
            foreach (var c in folder.Children.Values)
                items.Add(new ExplorerItem(true, c.Name, c.FullPath, c, -1, c.EligibleBytes, null, "Folder"));
            foreach (var i in folder.Files) items.Add(FileItem(tree, i));
        }
        else
        {
            var s = search.Trim();
            foreach (var n in tree.DescendantFolders(folder))
            {
                if (n != folder && n.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase))
                    items.Add(new ExplorerItem(true, n.Name, n.FullPath, n, -1, n.EligibleBytes, null, "Folder"));
                foreach (var i in n.Files)
                    if (Path.GetFileName(tree.Files[i].SourcePath).Contains(s, StringComparison.CurrentCultureIgnoreCase)) items.Add(FileItem(tree, i));
            }
        }
        IEnumerable<ExplorerItem> q = sort switch
        {
            ExplorerSort.Type => items.OrderBy(x => x.IsFolder ? 0 : 1).ThenBy(x => x.TypeText, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            ExplorerSort.Size => items.OrderBy(x => x.IsFolder ? 0 : 1).ThenBy(x => x.Size),
            ExplorerSort.Date => items.OrderBy(x => x.IsFolder ? 0 : 1).ThenBy(x => x.Modified ?? DateTime.MinValue),
            _ => items.OrderBy(x => x.IsFolder ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var list = q.ToList();
        if (descending)
        {
            var folders = list.Where(x => x.IsFolder).Reverse();
            var files = list.Where(x => !x.IsFolder).Reverse();
            list = folders.Concat(files).ToList();
        }
        return list;
    }

    static ExplorerItem FileItem(FolderTree tree, int i)
    {
        var f = tree.Files[i];
        var ext = Path.GetExtension(f.SourcePath);
        return new ExplorerItem(false, Path.GetFileName(f.SourcePath), f.SourcePath, null, i, f.Size, f.LastWriteUtc, string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file");
    }

    public static (List<ExplorerItem> Page, int PageCount) Page(List<ExplorerItem> all, int pageIndex, int pageSize)
    {
        int pages = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        pageIndex = Math.Clamp(pageIndex, 0, pages - 1);
        return (all.Skip(pageIndex * pageSize).Take(pageSize).ToList(), pages);
    }
}
