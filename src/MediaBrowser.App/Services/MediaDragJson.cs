using System.Text.Json;

namespace MediaBrowser.App.Services;

public static class MediaDragJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}
