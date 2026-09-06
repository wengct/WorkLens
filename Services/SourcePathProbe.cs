namespace WorkLens.Services;

internal enum SourcePathStatus
{
    Missing,
    Readable,
    AccessDenied,
    Unreadable
}

internal static class SourcePathProbe
{
    public static SourcePathStatus CheckDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return SourcePathStatus.Missing;
            }

            using var entries = new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                .GetEnumerator();
            _ = entries.MoveNext();
            return SourcePathStatus.Readable;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SourcePathStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return SourcePathStatus.AccessDenied;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return SourcePathStatus.Unreadable;
        }
    }

    public static string Describe(string path, SourcePathStatus status) => status switch
    {
        SourcePathStatus.Readable => $"可讀取：{path}",
        SourcePathStatus.AccessDenied => $"存取遭拒：{path}",
        SourcePathStatus.Unreadable => $"無法讀取：{path}",
        _ => $"找不到：{path}"
    };
}
