namespace AppRemote;

internal interface IRemoteVideoPeer : IDisposable
{
    event Action<bool>? ActiveChanged;
    event Action<string>? Failed;
    VideoPreference CodecMode { get; }
    Task<string> AcceptOfferAsync(string offer);
}
