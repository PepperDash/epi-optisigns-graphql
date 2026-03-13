using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using Serilog.Events;

namespace PepperDash.Essentials.Plugins.Optisigns.GraphQL
{
    /// <summary>
    /// Thin HTTP wrapper for the OptiSigns GraphQL API.
    ///
    /// All requests are HTTP POST to the single GraphQL gateway endpoint.
    /// Authentication is Bearer token on every request.
    ///
    /// All public methods return null (or false) on any error — network failure, HTTP error,
    /// or GraphQL error — so callers in the polling path never need to catch exceptions.
    ///
    /// The static HttpClient is intentional: reusing a single instance avoids socket
    /// exhaustion from repeated connection teardown, which matters on embedded processors.
    /// </summary>
    public class OptiSignsGraphQLClient : IKeyed
    {
        private readonly string _separator = new string('-', 50);
        public string Key { get; private set; }
        private const string GraphQlEndpoint =
            "https://graphql-gateway.optisigns.com/graphql";

        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly string _apiKey;

        // ──────────────────────────────────────────────
        // GraphQL query / mutation strings
        // Stored as static fields so they are allocated once, not on every poll cycle.
        // ──────────────────────────────────────────────

        private static readonly string ListAllDevicesQuery =
            @"query {
                devices(query: {}) {
                    page {
                        edges {
                            node {
                                _id
                                deviceName
                                UUID
                                pairingCode
                                currentType
                                currentAssetId
                                currentPlaylistId
                                localAppVersion
                                status
                                lastHeartBeat
                            }
                        }
                    }
                }
            }";

        private static readonly string DeviceStatusQuery =
            @"query GetDevice($id: String!) {
                devices(query: { _id: $id }) {
                    page {
                        edges {
                            node {
                                _id
                                deviceName
                                currentType
                                currentPlaylistId
                                currentAssetId
                                currentScheduleId
                                status
                                lastHeartBeat
                            }
                        }
                    }
                }
            }";

        // NOTE: The playlists query is not in the official TypeScript SDK (Phase 2).
        // We attempt it directly. GetPlaylistsAsync returns null if the endpoint
        // is not yet live — the caller falls back to the config-provided list.
        private static readonly string PlaylistsQuery =
            @"query {
                playlists(query: {}) {
                    page {
                        edges {
                            node {
                                _id
                                name
                                totalDuration
                                tags
                            }
                        }
                    }
                }
            }";

        private static readonly string UpdateDeviceMutation =
            @"mutation UpdateDevice($id: String!, $payload: UpdateDeviceInput!, $teamId: String!) {
                updateDevice(_id: $id, payload: $payload, teamId: $teamId) {
                    _id
                    currentType
                    currentPlaylistId
                }
            }";

        // pushToScreens is defined in the GraphQL schema but is Phase 2 in the SDK.
        // We call it here directly via raw HTTP.
        private static readonly string PushToScreensMutation =
            @"mutation PushToScreens($force: Boolean, $payload: PushToScreensInput!, $teamId: String!) {
                pushToScreens(force: $force, payload: $payload, teamId: $teamId) {
                    status
                }
            }";

        // ──────────────────────────────────────────────

        public OptiSignsGraphQLClient(string key,string apiKey)
        {
            Key = key + "-client";
            _apiKey = apiKey;
        }

        // ──────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────

        /// <summary>
        /// Fetches the current status of the configured device.
        /// Returns null on any error (network, API error, empty result).
        /// </summary>
        public async Task<DeviceNode> GetDeviceStatusAsync(string deviceId)
        {
            var variables = new { id = deviceId };
            var data = await ExecuteAsync<DevicesQueryData>(DeviceStatusQuery, variables)
                .ConfigureAwait(false);

            if (data?.Devices?.Page?.Edges == null || data.Devices.Page.Edges.Count == 0)
                return null;

            return data.Devices.Page.Edges[0].Node;
        }

        /// <summary>
        /// Fetches all devices (players/screens) available in the OptiSigns account.
        /// Returns null on any error (network, API error).
        /// </summary>
        public async Task<List<DeviceNode>> ListAllDevicesAsync()
        {
            var data = await ExecuteAsync<DevicesQueryData>(ListAllDevicesQuery, null)
                .ConfigureAwait(false);

            if (data?.Devices?.Page?.Edges == null)
                return null;

            var result = new List<DeviceNode>();
            foreach (var edge in data.Devices.Page.Edges)
            {
                if (edge?.Node != null)
                    result.Add(edge.Node);
            }
            return result;
        }

        /// <summary>
        /// Fetches the full playlist list for the account.
        /// Returns null if the endpoint is unavailable or returns an error.
        /// The caller should fall back to the config-provided list when null is returned.
        /// </summary>
        public async Task<List<PlaylistNode>> GetPlaylistsAsync()
        {
            var data = await ExecuteAsync<PlaylistsQueryData>(PlaylistsQuery, null)
                .ConfigureAwait(false);

            if (data?.Playlists?.Page?.Edges == null)
                return null;

            var result = new List<PlaylistNode>();
            foreach (var edge in data.Playlists.Page.Edges)
            {
                if (edge?.Node != null)
                    result.Add(edge.Node);
            }
            return result;
        }

        /// <summary>
        /// Updates device properties via the updateDevice mutation.
        /// Called for power off: payload.CurrentType = "NONE".
        /// Returns true if the API accepted the change (_id is present in response).
        /// </summary>
        public async Task<bool> UpdateDeviceAsync(
            string deviceId,
            string teamId,
            UpdateDeviceInput payload)
        {
            var variables = new UpdateDeviceVariables
            {
                Id = deviceId,
                TeamId = teamId,
                Payload = payload
            };

            var data = await ExecuteAsync<UpdateDeviceMutationData>(UpdateDeviceMutation, variables)
                .ConfigureAwait(false);

            return !string.IsNullOrEmpty(data?.UpdateDevice?.Id);
        }

        /// <summary>
        /// Pushes content to the screen via the pushToScreens mutation.
        /// Called for power on (restore playlist) and playlist selection.
        /// Returns true if the mutation returned a boolean true.
        /// </summary>
        public async Task<bool> PushToScreensAsync(
            string teamId,
            PushToScreensInput payload,
            bool force = false)
        {
            var variables = new PushToScreensVariables
            {
                TeamId = teamId,
                Payload = payload,
                Force = force
            };

            var data = await ExecuteAsync<PushToScreensMutationData>(PushToScreensMutation, variables)
                .ConfigureAwait(false);

            return data?.PushToScreens?.Status == true;
        }

        // ──────────────────────────────────────────────
        // Private HTTP execution core
        // ──────────────────────────────────────────────        

        private string MaskedApiKey
        {
            get
            {
                return _apiKey != null && _apiKey.Length > 8
                    ? _apiKey.Substring(0, 8) + "..."
                    : "(empty)";
            }
        }

        private void LogRequest(HttpMethod method = null, string body = null)
        {
            // this.LogVerbose(">>> Sending GraphQL Request\r\nmethod: {method}\r\nendpoint: {endpoint}\r\nheaders: Bearer {apiKey}\r\nrequestBody: {@requestBody}",
            //     method, GraphQlEndpoint, MaskedApiKey, body);

//             this.LogVerbose(@"\n
// >>> Sending GraphQL Request\n
// method: {method}\n
// endpoint: {endpoint}\n
// headers: Bearer {apiKey}\n
// requestBody: {@requestBody}",
//                 method, GraphQlEndpoint, MaskedApiKey, body);

            this.LogVerbose(">>> Sending GraphQL Request");
            this.LogVerbose("method: {method}", method);
            this.LogVerbose("endpoint: {endpoint}", GraphQlEndpoint);
            this.LogVerbose("headers: Bearer {apiKey}", MaskedApiKey);
            this.LogVerbose("requestBody: {@requestBody}", body.Trim());
        }

        private void LogResponse(HttpMethod method = null, HttpResponseMessage httpResponse = null, string body = null)
        {
            // this.LogVerbose(">>> Received GraphQL Response\r\nmethod: {method}\r\nendpoint: {endpoint}\r\nstatus: {status} ({reasonPhrase})\r\nresponseBody: {@responseBody}",
            //     method, GraphQlEndpoint, (int)httpResponse.StatusCode, httpResponse.ReasonPhrase, body);

//             this.LogVerbose(@"\n
// >>> Received GraphQL Response\n
// method: {method}\n
// endpoint: {endpoint}\n
// status: {status} ({reasonPhrase})\n
// responseBody: {@responseBody}",
//                 method, GraphQlEndpoint, (int)httpResponse.StatusCode, httpResponse.ReasonPhrase, body);

            this.LogVerbose(">>> Received GraphQL Response");
            this.LogVerbose("method: {method}", method);
            this.LogVerbose("endpoint: {endpoint}", GraphQlEndpoint);
            this.LogVerbose("status: {status} ({reasonPhrase})", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
            this.LogVerbose("responseBody: {@responseBody}", body.Trim());
        }

        private void LogGraphQlError(string errorMessage, string requestBody, string responseBody)
        {
            // this.LogError(">>> GraphQL Error: {errorMessage}\r\nendpoint: {endpoint}\r\napiKey: {apiKey}\r\nrequestBody: {@requestBody}\r\nresponseBody: {@responseBody}",
            //     errorMessage, GraphQlEndpoint, MaskedApiKey, requestBody, responseBody);

//             this.LogError(@"\n
// >>> GraphQL Error: {errorMessage}\n
// endpoint: {endpoint}\n
// apiKey: {apiKey}\n
// requestBody: {@requestBody}\n
// responseBody: {@responseBody}",
//                 errorMessage, GraphQlEndpoint, MaskedApiKey, requestBody, responseBody);

            this.LogError(">>> GraphQL Error");
            this.LogError("errorMessage: {errorMessage}", errorMessage);
            this.LogError("endpoint: {endpoint}", GraphQlEndpoint);
            this.LogError("apiKey: {apiKey}", MaskedApiKey);
            this.LogError("requestBody: {@requestBody}", requestBody.Trim());
            this.LogError("responseBody: {@responseBody}", responseBody.Trim());
        }

        /// <summary>
        /// Serializes a GraphQL request, sends it as HTTP POST with Bearer auth,
        /// deserializes the response envelope, and returns the typed data object.
        /// Returns null on any error; all failures are logged but never rethrown.
        /// ConfigureAwait(false) throughout to avoid deadlocks on Crestron's
        /// synchronization context.
        /// </summary>
        private async Task<T> ExecuteAsync<T>(string query, object variables)
            where T : class
        {
            try
            {
                var requestBody = new GraphQlRequest
                {
                    Query = query,
                    Variables = variables
                };

                var requestBodyJson = JsonConvert.SerializeObject(requestBody);
                
                LogRequest(HttpMethod.Post, requestBodyJson);
                
                var content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
                {
                    Content = content
                };
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _apiKey);

                var httpResponse = await HttpClient.SendAsync(request).ConfigureAwait(false);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                LogResponse(HttpMethod.Post, httpResponse, responseBody);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    this.LogError("HTTP {0}: {1}", (int)httpResponse.StatusCode, responseBody);
                    return null;
                }

                var envelope = JsonConvert.DeserializeObject<GraphQlResponse<T>>(responseBody);

                if (envelope?.Errors != null && envelope.Errors.Count > 0)
                {
                    foreach (var error in envelope.Errors)
                        LogGraphQlError(error.Message, requestBodyJson, responseBody);
                    // Return data anyway; partial results are valid in GraphQL
                }

                return envelope?.Data;
            }
            catch (TaskCanceledException)
            {
                this.LogError("Request timed out");
                return null;
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Exception during GraphQL request");
                this.LogError("HTTP exception: {0}", ex.Message);
                return null;
            }
        }
    }
}
