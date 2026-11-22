using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace LolMatchAssistant;

internal sealed class ChampionPortraits(LeagueTransport client, CancellationToken cancellationToken)
{
    private readonly Dictionary<string, ChampionPortrait> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requests = new(4);

    public ChampionPortrait? Get(int id)
    {
        return id <= 0 ? null : GetIcon($"champion:{id}", async () =>
        {
            var bytes = await client.ChampionIcon(id, cancellationToken);
            return bytes is null && id is > 60000 and < 61000
                ? await client.ChampionIcon(id - 60000, cancellationToken) : bytes;
        });
    }

    public ChampionPortrait? Asset(string path) => path.Length == 0 ? null : GetIcon(path, () => client.ReadIcon(path, cancellationToken));
    public ChampionPortrait? Profile(int id) => id < 0 ? null : GetIcon($"profile:{id}",
        async () => await client.ReadIcon($"/lol-game-data/assets/v1/profile-icons/{id}.jpg", cancellationToken) ??
            await client.ReadIcon($"/lol-game-data/assets/v1/profile-icons/{id}.png", cancellationToken));

    private ChampionPortrait GetIcon(string key, Func<Task<byte[]?>> fetch)
    {
        if (_cache.TryGetValue(key, out var existing)) return existing;
        var portrait = new ChampionPortrait();
        _cache.Add(key, portrait);
        _ = Load(key, portrait, fetch);
        return portrait;
    }

    private async Task Load(string key, ChampionPortrait portrait, Func<Task<byte[]?>> fetch)
    {
        try
        {
            await _requests.WaitAsync(cancellationToken);
            try
            {
                var bytes = await fetch();
                if (bytes is not null) portrait.SetImage(Decode(bytes));
            }
            finally { _requests.Release(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            NotSupportedException or FileFormatException)
        {
            if (!cancellationToken.IsCancellationRequested) _cache.Remove(key);
        }
    }

    internal static BitmapSource Decode(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 112;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

internal sealed class ChampionPortrait : INotifyPropertyChanged
{
    public BitmapSource? Image { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetImage(BitmapSource source)
    {
        Image = source;
        PropertyChanged?.Invoke(this, new(nameof(Image)));
    }
}
