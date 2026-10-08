namespace Cuelight.Core;

/// <summary>The API keys the user has saved, one per provider, each in its own encrypted file.</summary>
public sealed class ProviderKeys
{
    /// <summary>Saved for a service that needs no key (a model running on this PC, say), so "has a key" stays simple.
    /// It is never sent anywhere.</summary>
    public const string NoKey = "no-key";

    private readonly Dictionary<Provider, ApiKeyStore> _stores = new();
    private readonly Dictionary<Provider, string?> _loaded = new();
    private readonly object _lock = new();

    public ProviderKeys(string? directory = null, IKeyProtector? protector = null)
    {
        foreach (var p in Enum.GetValues<Provider>())
            _stores[p] = new ApiKeyStore(directory, protector, ApiKeyStore.FileNameFor(p));
    }

    public string? Get(Provider provider)
    {
        lock (_lock)
        {
            if (!_loaded.TryGetValue(provider, out var key)) _loaded[provider] = key = _stores[provider].Load();
            return key;
        }
    }

    public bool Has(Provider provider) => Get(provider) is not null;

    /// <summary>Remember the key for this session and save it. If saving fails the key still works until the app closes,
    /// and the exception says why it can't be remembered.</summary>
    public void Set(Provider provider, string key)
    {
        key = key.Trim();
        lock (_lock) _loaded[provider] = key;
        _stores[provider].Save(key);
    }

    public void Remove(Provider provider)
    {
        lock (_lock) _loaded[provider] = null;
        _stores[provider].Delete();
    }
}
