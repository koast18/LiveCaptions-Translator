namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>Maps provider ids to factories so the host never hard-codes a backend.</summary>
    public sealed class AsrProviderRegistry
    {
        private readonly Dictionary<string, Func<IAsrProvider>> factories =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> order = [];

        public IReadOnlyList<string> Ids => order;

        public int Count => order.Count;

        public void Register(string id, Func<IAsrProvider> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(factory);

            if (factories.ContainsKey(id))
                throw new InvalidOperationException($"ASR provider '{id}' is already registered.");

            factories[id] = factory;
            order.Add(id);
        }

        public bool IsRegistered(string id) => factories.ContainsKey(id);

        public IAsrProvider Create(string id)
        {
            if (!factories.TryGetValue(id, out var factory))
                throw new KeyNotFoundException($"ASR provider '{id}' is not registered.");

            return factory();
        }

        public bool TryCreate(string id, out IAsrProvider? provider)
        {
            if (factories.TryGetValue(id, out var factory))
            {
                provider = factory();
                return true;
            }

            provider = null;
            return false;
        }

        /// <summary>
        /// Instantiate every registered provider to read its metadata (display name,
        /// language list). Used by the settings UI to build the provider picker.
        /// </summary>
        public IReadOnlyList<IAsrProvider> DescribeAll()
        {
            var providers = new List<IAsrProvider>(order.Count);
            foreach (var id in order)
                providers.Add(Create(id));

            return providers;
        }
    }
}
