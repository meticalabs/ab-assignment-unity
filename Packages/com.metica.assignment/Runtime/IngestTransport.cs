using System;
using System.Text;
using UnityEngine.Networking;

namespace Metica.Assignment {
    /// <summary>Outcome of one POST to Metica ingestion.</summary>
    internal struct IngestResponse {
        /// <summary>HTTP status, or 0 when there was no HTTP answer.</summary>
        public long Status;
        /// <summary>No HTTP answer because the request timed out.</summary>
        public bool TimedOut;
        /// <summary>Transport error text when there was no HTTP answer.</summary>
        public string Error;
    }

    /// <summary>Sends one request body. UnityWebRequest in production, a fake in tests.</summary>
    internal interface IIngestTransport {
        /// <summary>Starts the POST. <paramref name="onComplete"/> runs once, on the main thread.</summary>
        void Post(string url, string apiKey, string body, Action<IngestResponse> onComplete);
    }

    internal sealed class UnityWebRequestTransport : IIngestTransport {
        // The SDK's request timeout.
        internal const int TimeoutSeconds = 20;

        public void Post(string url, string apiKey, string body, Action<IngestResponse> onComplete) {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-KEY", apiKey);
            request.timeout = TimeoutSeconds;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            operation.completed += _ => {
                var response = new IngestResponse();
                if (request.result == UnityWebRequest.Result.Success ||
                    request.result == UnityWebRequest.Result.ProtocolError) {
                    response.Status = request.responseCode;
                } else {
                    response.Error = request.error;
                    // UnityWebRequest reports its own timeout with this text.
                    response.TimedOut = string.Equals(request.error, "Request timeout", StringComparison.OrdinalIgnoreCase);
                }
                request.Dispose();
                onComplete(response);
            };
        }
    }
}
