using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;

namespace MediaBrowser.Tests;

public class FileSystemCopyServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "MediaBrowserTests_" + Guid.NewGuid().ToString("N"));

    public FileSystemCopyServiceTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void CopyFileSystemItems_Copies_To_Target()
    {
        var srcDir = Path.Combine(_temp, "src");
        Directory.CreateDirectory(srcDir);
        var file = Path.Combine(srcDir, "test.txt");
        File.WriteAllText(file, "x");

        var destDir = Path.Combine(_temp, "dest");
        var item = new MediaItem
        {
            Id = file,
            SourceKind = MediaSourceKind.FileSystem,
            FileSystemPath = file,
            DisplayName = "test.txt",
            SortTimeUtc = DateTime.UtcNow,
        };

        var result = FileSystemCopyService.CopyFileSystemItems(new[] { item }, destDir, new CopyOptions());

        Assert.Equal(1, result.SuccessCount);
        Assert.True(File.Exists(Path.Combine(destDir, "test.txt")));
    }

    [Fact]
    public void CopyFileSystemItems_Skip_On_Collision()
    {
        var srcDir = Path.Combine(_temp, "src2");
        Directory.CreateDirectory(srcDir);
        var file = Path.Combine(srcDir, "dup.txt");
        File.WriteAllText(file, "x");

        var destDir = Path.Combine(_temp, "dest2");
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, "dup.txt"), "existing");

        var item = new MediaItem
        {
            Id = file,
            SourceKind = MediaSourceKind.FileSystem,
            FileSystemPath = file,
            DisplayName = "dup.txt",
            SortTimeUtc = DateTime.UtcNow,
        };

        var result = FileSystemCopyService.CopyFileSystemItems(
            new[] { item },
            destDir,
            new CopyOptions { CollisionPolicy = NameCollisionPolicy.Skip });

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.SkippedCount);
    }
}
