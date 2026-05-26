namespace FabricationAssistant.App.Android;

public sealed class RecentFileEntry
{
    public string Uri { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long LastOpenedUnixMs { get; set; }
}
