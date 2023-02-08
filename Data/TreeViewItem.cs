namespace VideoHome.Data;
using System.IO;

public class FileTreeViewItem
{
    public enum NodeType
    {
        Folder,
        File
    }

    public NodeType ItemType { get; set; }
    public string? Text { get; set; }
    public string? Path { get; set; }
    public List<FileTreeViewItem>? Children { get; set; }

    public static List<FileTreeViewItem> EnumerateFilesWithRootMapping(string root, string rootmapping, IEnumerable<string> fileEndings)
        => EnumerateFiles(new DirectoryInfo(root), root, rootmapping, fileEndings);

    private static List<FileTreeViewItem> EnumerateFiles(DirectoryInfo path, string root, string rootmapping, IEnumerable<string> fileEndings)
    {
        FileTreeViewItem item = new FileTreeViewItem
        {
            ItemType = NodeType.Folder,
            Text = path.Name,
            Path = path.FullName.Replace(root, rootmapping),
            Children = new()
        };

        foreach (DirectoryInfo dirInfo in path.GetDirectories().OrderBy(d => d.Name))
        {
            item.Children.AddRange(EnumerateFiles(dirInfo, root, rootmapping, fileEndings));
        }

        foreach (FileInfo fi in path.GetFiles("*.*").OrderBy(fi => fi.Name)
                                    .Where(f => fileEndings == null || fileEndings.Any(e => f.Name.EndsWith(e))))
        {
            FileTreeViewItem file = new FileTreeViewItem
            {
                ItemType = NodeType.File,
                Text = fi.Name,
                Path = fi.FullName.Replace(root, rootmapping),
            };
            item.Children.Add(file);
        }

        return new() { item };
    }
}
