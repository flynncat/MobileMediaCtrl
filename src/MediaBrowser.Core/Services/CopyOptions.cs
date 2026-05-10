namespace MediaBrowser.Core.Services;

public enum NameCollisionPolicy
{
    Skip = 0,
    AutoRename = 1,
    Overwrite = 2,
}

public sealed class CopyOptions
{
    public NameCollisionPolicy CollisionPolicy { get; init; } = NameCollisionPolicy.Skip;
}

public sealed class CopyResult
{
    public int SuccessCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
