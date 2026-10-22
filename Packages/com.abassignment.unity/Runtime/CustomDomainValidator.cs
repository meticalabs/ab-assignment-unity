using System;
using System.Linq;

namespace AB.Assignment {
    /// <summary>
    /// Normalises and validates <see cref="ABAssignmentOptions.CustomDomain"/> with the Metica SDK's rules
    /// (Android CustomDomain.kt, iOS CustomDomain.swift), so a domain the SDK accepts is accepted here and resolves to
    /// the same base URL. Accepted input is a host, optionally with a scheme and a port: "api.example.com",
    /// "https://api.example.com", "api.example.com:8443". Anything after the authority is rejected, because
    /// "/ingest/v1/events" is appended to the result.
    /// </summary>
    internal static class CustomDomainValidator {
        private const string SchemeSeparator = "://";
        private const string Https = "https";
        private const string Http = "http";

        private const int MinPort = 1;
        private const int MaxPort = 65535;

        // Checked explicitly because .NET's Uri, Java's URI and Swift's URLComponents disagree on characters such as
        // '_'. Covers hostnames and IPv4 literals; excludes IPv6 literals.
        private const string AllowedHostCharacters = "abcdefghijklmnopqrstuvwxyz0123456789-.";

        /// <summary>
        /// The base URL for <paramref name="raw"/>: the normalised custom domain, or <paramref name="defaultBaseUrl"/>
        /// when it is null or blank (as the SDK's Unity bridges drop blank values). An invalid value throws
        /// <see cref="ArgumentException"/> instead of falling back as the SDK does, so a wrong domain is noticed at
        /// once rather than sending events to Metica production.
        /// </summary>
        public static string ResolveBaseUrl(string raw, string defaultBaseUrl) {
            if (string.IsNullOrWhiteSpace(raw)) {
                return defaultBaseUrl;
            }
            string normalized = Normalize(raw);
            AssignmentLog.Info("Using custom Metica domain: " + normalized);
            if (normalized.StartsWith(Http + SchemeSeparator, StringComparison.Ordinal)) {
                AssignmentLog.Warning("customDomain uses cleartext http; the platform's network security settings may block it: " + normalized);
            }
            return normalized;
        }

        /// <summary>
        /// "scheme://host[:port]", lowercased and without a trailing slash, or an <see cref="ArgumentException"/> if
        /// <paramref name="raw"/> is not a usable host. Normalising a normalised value returns it unchanged.
        /// </summary>
        public static string Normalize(string raw) {
            string trimmed = raw.Trim();

            if (trimmed.Length == 0) {
                throw new ArgumentException("customDomain must not be blank");
            }
            if (trimmed.Any(char.IsWhiteSpace)) {
                throw new ArgumentException("customDomain must not contain whitespace: '" + raw + "'");
            }
            // Checked on the raw input: Foundation maps some non-ASCII characters (U+3002 becomes '.') where Java
            // rejects them, so all platforms reject non-ASCII before parsing.
            if (trimmed.Any(c => c > 0x7F)) {
                throw new ArgumentException("customDomain must be ASCII only: '" + raw + "'");
            }

            // The scheme is optional. Detected by "://" because "api.example.com:8080" is itself a valid URI scheme
            // followed by "8080".
            string withScheme = trimmed;
            if (!trimmed.Contains(SchemeSeparator)) {
                withScheme = Https + SchemeSeparator + trimmed;
            }

            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out Uri uri)) {
                throw new ArgumentException("customDomain is not a valid host: '" + raw + "'");
            }

            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != Https && scheme != Http) {
                throw new ArgumentException("customDomain scheme must be https or http, was '" + uri.Scheme + "': '" + raw + "'");
            }
            // Path, credentials and host are read from the text, not from Uri: Mono collapses "/.." to "/", reports an
            // empty user info as "" and rewrites IPv4 shorthand ("127.1" to "127.0.0.1"), where the SDK does not.
            string authority = Authority(withScheme, out string rest);
            if (authority.Contains("@")) {
                throw new ArgumentException("customDomain must not contain credentials: '" + raw + "'");
            }
            bool hasExplicitPort = HasExplicitPort(authority);
            string hostText = authority;
            int colon = authority.LastIndexOf(':');
            if (colon >= 0) {
                hostText = authority.Substring(0, colon);
            }
            string host = hostText.ToLowerInvariant().TrimEnd('.');
            if (host.Length == 0) {
                throw new ArgumentException("customDomain has no valid host: '" + raw + "'");
            }
            if (host.Any(c => AllowedHostCharacters.IndexOf(c) < 0)) {
                throw new ArgumentException("customDomain host contains invalid characters: '" + raw + "'");
            }
            if (host.StartsWith(".", StringComparison.Ordinal)) {
                throw new ArgumentException("customDomain host must not start with a dot: '" + raw + "'");
            }
            if (host.Contains("..")) {
                throw new ArgumentException("customDomain host must not contain an empty label: '" + raw + "'");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo)) {
                throw new ArgumentException("customDomain must not contain credentials: '" + raw + "'");
            }
            if (!string.IsNullOrEmpty(uri.Query)) {
                throw new ArgumentException("customDomain must not contain a query string: '" + raw + "'");
            }
            if (!string.IsNullOrEmpty(uri.Fragment)) {
                throw new ArgumentException("customDomain must not contain a fragment: '" + raw + "'");
            }

            // "host" and "host/" mean the same thing; anything else is a path prefix, which is not supported.
            if (rest.Length > 0 && rest != "/") {
                throw new ArgumentException("customDomain must not contain a path, was '" + rest + "': '" + raw + "'");
            }

            // Uri reports 443 or 80 when no port was written, so the text decides whether there is one.
            if (hasExplicitPort && (uri.Port < MinPort || uri.Port > MaxPort)) {
                throw new ArgumentException("customDomain port must be between " + MinPort + " and " + MaxPort + ", was " + uri.Port + ": '" + raw + "'");
            }

            if (hasExplicitPort) {
                return scheme + SchemeSeparator + host + ":" + uri.Port;
            }
            return scheme + SchemeSeparator + host;
        }

        // The text between "://" and the first '/', '?' or '#'; rest is everything from that character on.
        private static string Authority(string withScheme, out string rest) {
            string afterScheme = withScheme.Substring(withScheme.IndexOf(SchemeSeparator, StringComparison.Ordinal) + SchemeSeparator.Length);
            int authorityEnd = afterScheme.IndexOfAny(new[] { '/', '?', '#' });
            if (authorityEnd < 0) {
                rest = "";
                return afterScheme;
            }
            rest = afterScheme.Substring(authorityEnd);
            return afterScheme.Substring(0, authorityEnd);
        }

        // A ":digits" at the end of the authority. A bare trailing colon is an empty port, so "host:" gives "https://host"
        // as in the SDK, not "https://host:443".
        private static bool HasExplicitPort(string authority) {
            int colon = authority.LastIndexOf(':');
            if (colon < 0 || colon == authority.Length - 1) {
                return false;
            }
            return authority.Substring(colon + 1).All(char.IsDigit);
        }
    }
}
