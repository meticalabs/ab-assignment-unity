namespace AB.Assignment {
    /// <summary>
    /// Settings for <see cref="ABAssignment.Initialize"/>: the game's Metica app id and API key (the same values the
    /// game gives the Metica SDK).
    /// </summary>
    public sealed class ABAssignmentOptions {
        public ABAssignmentOptions(string appId, string apiKey) {
            AppId = appId;
            ApiKey = apiKey;
        }

        /// <summary>The game's Metica app id, e.g. "myGame".</summary>
        public string AppId { get; }

        /// <summary>The game's Metica API key, sent as the X-API-KEY header.</summary>
        public string ApiKey { get; }

        /// <summary>
        /// Optional: the domain of a dedicated Metica cluster, the same value the game gives the Metica SDK. A host
        /// with an optional scheme (https when omitted) and port, e.g. "api.example.com" or
        /// "https://api.example.com:8443"; no path, query or credentials. Null or blank sends to Metica production.
        /// An invalid value makes <see cref="ABAssignment.Initialize"/> throw <see cref="System.ArgumentException"/>.
        /// </summary>
        public string CustomDomain { get; set; }
    }
}
