namespace MyAgents.Core
{
    /// <summary>
    /// Type-safe, mutable bag for storing arbitrary objects keyed by their type.
    /// Used by <see cref="AgentTurnContext.Capabilities"/> to allow middleware and
    /// features to communicate per-turn information without polluting the core
    /// context object with lots of strongly typed properties.
    /// </summary>
    public sealed class TurnCapabilities
    {
        private readonly Dictionary<Type, object> _backing = new();

        /// <summary>
        /// Stores a capability object of type <typeparamref name="T"/>. Any existing
        /// value for the same type will be overwritten.
        /// </summary>
        /// <typeparam name="T">The type of the capability (typically a record or
        /// immutable class).</typeparam>
        /// <param name="value">The capability instance.</param>
        public void Set<T>(T value) where T : class
            => _backing[typeof(T)] = value!;

        /// <summary>
        /// Attempts to retrieve a previously stored capability.
        /// </summary>
        /// <typeparam name="T">The requested type.</typeparam>
        /// <param name="value">Receives the stored value if present.</param>
        /// <returns><see langword="true"/> if a value of the requested type was
        /// found; otherwise <see langword="false"/>.</returns>
        public bool TryGet<T>([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value) where T : class
        {
            if (_backing.TryGetValue(typeof(T), out var obj) && obj is T typed)
            {
                value = typed;
                return true;
            }

            value = null;
            return false;
        }
    }
}
