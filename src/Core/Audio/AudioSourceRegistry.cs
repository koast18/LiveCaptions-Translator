namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>
    /// Maps audio source ids to factories, mirroring <c>AsrProviderRegistry</c> and
    /// <c>TranslationProviderRegistry</c> so all three plugin seams look the same to the host.
    /// </summary>
    public sealed class AudioSourceRegistry
    {
        private readonly Dictionary<string, Func<IAudioSource>> factories =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> order = [];

        public IReadOnlyList<string> Ids => order;

        public int Count => order.Count;

        public void Register(string id, Func<IAudioSource> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(factory);

            if (factories.ContainsKey(id))
                throw new InvalidOperationException($"Audio source '{id}' is already registered.");

            factories[id] = factory;
            order.Add(id);
        }

        public bool IsRegistered(string id) => factories.ContainsKey(id);

        public IAudioSource Create(string id)
        {
            if (!factories.TryGetValue(id, out var factory))
                throw new KeyNotFoundException($"Audio source '{id}' is not registered.");

            return factory();
        }

        public bool TryCreate(string id, out IAudioSource? source)
        {
            if (factories.TryGetValue(id, out var factory))
            {
                source = factory();
                return true;
            }

            source = null;
            return false;
        }
    }
}
