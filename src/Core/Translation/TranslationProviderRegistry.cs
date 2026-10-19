namespace LiveCaptionsTranslator.Core.Translation
{
    /// <summary>Maps provider ids to factories so the host never hard-codes an engine.</summary>
    public sealed class TranslationProviderRegistry
    {
        private readonly Dictionary<string, Func<ITranslationProvider>> factories =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> order = [];

        public IReadOnlyList<string> Ids => order;

        public int Count => order.Count;

        public void Register(string id, Func<ITranslationProvider> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(factory);

            if (factories.ContainsKey(id))
                throw new InvalidOperationException($"Translation provider '{id}' is already registered.");

            factories[id] = factory;
            order.Add(id);
        }

        public bool IsRegistered(string id) => factories.ContainsKey(id);

        public ITranslationProvider Create(string id)
        {
            if (!factories.TryGetValue(id, out var factory))
                throw new KeyNotFoundException($"Translation provider '{id}' is not registered.");

            return factory();
        }

        public bool TryCreate(string id, out ITranslationProvider? provider)
        {
            if (factories.TryGetValue(id, out var factory))
            {
                provider = factory();
                return true;
            }

            provider = null;
            return false;
        }
    }
}
