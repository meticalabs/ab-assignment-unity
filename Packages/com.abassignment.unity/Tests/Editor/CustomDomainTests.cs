using System;
using NUnit.Framework;

namespace AB.Assignment.Tests {
    /// <summary>
    /// Custom domain normalisation and validation. The cases are the Metica SDK's (Android CustomDomainTest, iOS
    /// CustomDomainTests and the Unity SDK's former C# copy), so the package accepts and rejects the same values.
    /// </summary>
    public class CustomDomainTests {
        // ---- accepted ----

        [Test]
        public void BareHostGetsHttps() {
            Assert.AreEqual("https://api.customer.com", CustomDomainValidator.Normalize("api.customer.com"));
        }

        [Test]
        public void SchemeIsKeptAndHttpIsAllowed() {
            Assert.AreEqual("https://api.customer.com", CustomDomainValidator.Normalize("https://api.customer.com"));
            Assert.AreEqual("http://api.customer.com", CustomDomainValidator.Normalize("http://api.customer.com"));
        }

        [Test]
        public void PortIsKeptWithOrWithoutScheme() {
            Assert.AreEqual("https://api.customer.com:8080", CustomDomainValidator.Normalize("api.customer.com:8080"));
            Assert.AreEqual("http://localhost:8080", CustomDomainValidator.Normalize("http://localhost:8080"));
        }

        [Test]
        public void NoDefaultPortIsAdded() {
            Assert.AreEqual("https://api.customer.com", CustomDomainValidator.Normalize("https://api.customer.com"));
            Assert.AreEqual("http://api.customer.com", CustomDomainValidator.Normalize("http://api.customer.com"));
        }

        [Test]
        public void TrailingColonMeansNoPort() {
            Assert.AreEqual("https://api.customer.com", CustomDomainValidator.Normalize("api.customer.com:"));
            Assert.AreEqual("http://api.customer.com", CustomDomainValidator.Normalize("http://api.customer.com:"));
        }

        [Test]
        public void EquivalentInputsGiveTheSameBaseUrl() {
            string[] inputs = {
                "api.customer.com", "https://api.customer.com", "https://api.customer.com/", " API.CUSTOMER.COM ",
                "HTTPS://API.Customer.COM", "api.customer.com.", "  api.customer.com \n"
            };
            foreach (string raw in inputs) {
                Assert.AreEqual("https://api.customer.com", CustomDomainValidator.Normalize(raw), "raw: '" + raw + "'");
            }
        }

        [Test]
        public void NumericHostsAreKeptAsWritten() {
            Assert.AreEqual("https://127.0.0.1", CustomDomainValidator.Normalize("127.0.0.1"));
            Assert.AreEqual("https://012.1.1.1", CustomDomainValidator.Normalize("012.1.1.1"));
            Assert.AreEqual("https://127.1", CustomDomainValidator.Normalize("127.1"));
        }

        [Test]
        public void NormalisationIsIdempotent() {
            foreach (string raw in new[] { "api.customer.com", "api.customer.com:8080", "http://localhost:3000" }) {
                string once = CustomDomainValidator.Normalize(raw);
                Assert.AreEqual(once, CustomDomainValidator.Normalize(once), "raw: '" + raw + "'");
            }
        }

        [Test]
        public void AcceptedInputIsRebuiltNeverEchoed() {
            string[,] cases = {
                { "API.Metica.COM.", "https://api.metica.com" },
                { "https://api.metica.com/", "https://api.metica.com" },
                { "api.metica.com:8443", "https://api.metica.com:8443" }
            };
            for (int i = 0; i < cases.GetLength(0); i++) {
                string result = CustomDomainValidator.Normalize(cases[i, 0]);
                Assert.AreEqual(cases[i, 1], result);
                string afterScheme = result.Substring(result.IndexOf("://", StringComparison.Ordinal) + 3);
                Assert.IsFalse(afterScheme.Contains("/"), "path leaked: " + result);
                foreach (string ch in new[] { "?", "#", "@" }) {
                    Assert.IsFalse(result.Contains(ch), "structural character leaked: " + result);
                }
            }
        }

        // ---- rejected ----

        [Test]
        public void InvalidValuesAreRejected() {
            string[] invalid = {
                "",
                "   ",
                "api customer.com",
                "https://api.customer.com/metica",
                "https://api.customer.com?env=test",
                "https://api.customer.com#frag",
                "https://user:pass@api.customer.com",
                "ftp://api.customer.com",
                "file://api.customer.com",
                "https://",
                "api.customer.com:0",
                "api.customer.com:99999",
                "api_customer.com",
                "api.customer.com?",
                "api.customer.com#",
                ".api.customer.com",
                "..api.customer.com",
                "api..customer.com",
                "api.customer..com",
                "@api.customer.com",
                "https://@api.customer.com",
                "api.customer.com/.",
                "api.customer.com/..",
                "api.customer.com/./",
                "api.customer.com/%2e"
            };
            foreach (string raw in invalid) {
                Assert.Throws<ArgumentException>(() => CustomDomainValidator.Normalize(raw), "raw: '" + raw + "'");
            }
        }

        [Test]
        public void UrlInjectionPayloadsAreRejected() {
            string[] payloads = {
                "user:pass@evil.example.com",
                "api.metica.com@evil.example.com",
                "api.metica.com\\@evil.example.com",
                "//evil.example.com",
                "api.metica.com/evil",
                "api.metica.com/../../admin",
                "api.metica.com/smartfloors/v1/apps/other/init",
                "api.metica.com%2F..%2Fadmin",
                "api.metica.com%00.evil.example.com",
                "api.metica.com?appId=attacker",
                "api.metica.com#@evil.example.com",
                "api.metica.com\r\nHost: evil.example.com",
                "api.metica.com\nX-Injected: 1",
                "аpi.metica.com",
                "api.metica。com",
                "javascript:alert(1)",
                "file:///etc/passwd",
                "data:text/html,<script>alert(1)</script>"
            };
            foreach (string payload in payloads) {
                Assert.Throws<ArgumentException>(() => CustomDomainValidator.Normalize(payload),
                    "payload should have been rejected: '" + payload + "'");
            }
        }

        // ---- resolution ----

        [Test]
        public void ResolveKeepsTheDefaultForBlankAndThrowsOnInvalid() {
            Assert.AreEqual("https://default", CustomDomainValidator.ResolveBaseUrl(null, "https://default"));
            Assert.AreEqual("https://default", CustomDomainValidator.ResolveBaseUrl(" \t", "https://default"));
            Assert.Throws<ArgumentException>(() => CustomDomainValidator.ResolveBaseUrl("api.customer.com/path", "https://default"));
            Assert.AreEqual("https://api.customer.com", CustomDomainValidator.ResolveBaseUrl("API.customer.com", "https://default"));
        }
    }
}
