namespace VideoHome.Data;
using System.IO;
using BootstrapBlazor.Components;

public class FileTreeViewItem
{
    public enum NodeType
    {
        Folder,
        File
    }

    public NodeType ItemType { get; set; }
    public string? Path { get; set; }

    public static List<TreeViewItem<FileTreeViewItem>> EnumerateFilesWithRootMapping(string root, string rootmapping, IEnumerable<string> fileEndings)
        => EnumerateFiles(new DirectoryInfo(root), root, rootmapping, fileEndings);

    private static List<TreeViewItem<FileTreeViewItem>> EnumerateFiles(DirectoryInfo path, string root, string rootmapping, IEnumerable<string> fileEndings)
    {
        TreeViewItem<FileTreeViewItem> item = new (new FileTreeViewItem
        {
            ItemType = NodeType.Folder,
            Path = path.FullName.Replace(root, rootmapping),
        })
        {
            Text = path.Name,
            Icon = "fa-solid fa-folder-open",
            Items = new()
        };

        foreach (DirectoryInfo dirInfo in path.GetDirectories().OrderBy(d => d.Name))
        {
            item.Items.AddRange(EnumerateFiles(dirInfo, root, rootmapping, fileEndings));
        }

        foreach (FileInfo fi in path.GetFiles("*.*").OrderBy(fi => fi.Name)
                                    .Where(f => fileEndings == null || fileEndings.Any(e => f.Name.EndsWith(e))))
        {
            TreeViewItem<FileTreeViewItem> file = new (new FileTreeViewItem
            {
                ItemType = NodeType.File,
                Path = fi.FullName.Replace(root, rootmapping),
            })
            {
                Text = fi.Name,
                Icon = "fa-solid fa-file-video"
            };
            item.Items.Add(file);
        }

        return new() { item };
    }
}
