namespace Metica.Assignment {
    /// <summary>
    /// The Metica ingestion environment the events go to.
    /// </summary>
    public enum MeticaEnvironment {
        /// <summary>Metica production ingestion, with the relay host as fallback.</summary>
        Production,
        /// <summary>Metica DEV ingestion. Only for tests with a DEV app id and key.</summary>
        Development
    }

    /// <summary>
    /// Settings for <see cref="MeticaAssignment.Initialize"/>: the game's Metica app id and API key (the same values the
    /// game gives the Metica SDK).
    /// </summary>
    public sealed class MeticaAssignmentOptions {
        public MeticaAssignmentOptions(string appId, string apiKey) {
            AppId = appId;
            ApiKey = apiKey;
        }

        /// <summary>The game's Metica app id, e.g. "myGame".</summary>
        public string AppId { get; }

        /// <summary>The game's Metica API key, sent as the X-API-KEY header.</summary>
        public string ApiKey { get; }

        /// <summary>Production by default.</summary>
        public MeticaEnvironment Environment { get; set; } = MeticaEnvironment.Production;

        /// <summary>
        /// Optional ingestion base URL (scheme and host, no path) that replaces the environment's host, e.g. a custom
        /// domain. Null uses <see cref="Environment"/>. With a custom URL there is no fallback host.
        /// </summary>
        public string BaseUrl { get; set; }
    }
}
