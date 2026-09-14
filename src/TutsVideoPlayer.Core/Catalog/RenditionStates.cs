namespace TutsVideoPlayer.Core.Catalog;

public enum RenditionPurpose
{
    Source = 0,
    Compatibility = 1,
    Quality = 2
}

public enum RenditionRetention
{
    Source = 0,
    Permanent = 1,
    Quality = 2
}

public enum RenditionStatus
{
    Pending = 0,
    Ready = 1,
    Stale = 2,
    Missing = 3,
    Evicting = 4,
    Evicted = 5
}

public enum SubtitleAssociationOrigin
{
    Manual = 0,
    Automatic = 1
}
