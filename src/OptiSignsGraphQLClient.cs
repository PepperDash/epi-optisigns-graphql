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
            @"query GetPlaylists($limit: Int) {
                playlists(query: { limit: $limit }) {
                    totalCount
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
                    currentAssetId
                }
            }";

        // pushToScreens is defined in the GraphQL schema but is Phase 2 in the SDK.
        // Returns JSONObject! which is a scalar - no subfields can be selected.
        private static readonly string PushToScreensMutation =
            @"mutation PushToScreens($force: Boolean, $payload: PushToScreensInput!, $teamId: String!) {
                pushToScreens(force: $force, payload: $payload, teamId: $teamId)
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
        /// 
        /// If the API returns totalCount greater than the initial limit, a second request
        /// is made with the actual totalCount to fetch all playlists.
        /// </summary>
        /// <param name="limit">Initial limit for playlists to fetch. Default 100.</param>
        public async Task<List<PlaylistNode>> GetPlaylistsAsync(int limit = 100)
        {
            var variables = new PlaylistsQueryVariables { Limit = limit };
            var data = await ExecuteAsync<PlaylistsQueryData>(PlaylistsQuery, variables)
                .ConfigureAwait(false);

            if (data?.Playlists?.Page?.Edges == null)
                return null;

            var totalCount = data.Playlists.TotalCount;
            var fetchedCount = data.Playlists.Page.Edges.Count;

            // If there are more playlists than we fetched, re-fetch with the full count
            if (totalCount > fetchedCount)
            {
                this.LogDebug("Re-fetching playlists: {0} available, {1} fetched", 
                    totalCount, fetchedCount);

                variables = new PlaylistsQueryVariables { Limit = totalCount };
                data = await ExecuteAsync<PlaylistsQueryData>(PlaylistsQuery, variables)
                    .ConfigureAwait(false);

                if (data?.Playlists?.Page?.Edges == null)
                    return null;
            }

            var result = new List<PlaylistNode>();
            foreach (var edge in data.Playlists.Page.Edges)
            {
                if (edge?.Node != null)
                    result.Add(edge.Node);
            }
            return result;
        }

        /// <summary>
        /// Gets the total number of playlists available in the account.
        /// Returns -1 if the endpoint is unavailable or returns an error.
        /// </summary>
        public async Task<int> GetPlaylistCountAsync()
        {
            // Fetch with limit=1 just to get totalCount efficiently
            var variables = new PlaylistsQueryVariables { Limit = 1 };
            var data = await ExecuteAsync<PlaylistsQueryData>(PlaylistsQuery, variables)
                .ConfigureAwait(false);

            if (data?.Playlists == null)
                return -1;

            return data.Playlists.TotalCount;
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
        /// Assigns a playlist to a device using the updateDevice mutation.
        /// Per API docs: sets currentType=PLAYLIST and currentAssetId=playlistId.
        /// This is the documented method for assigning playlist content to a screen.
        /// </summary>
        public async Task<bool> AssignPlaylistAsync(
            string deviceId,
            string teamId,
            string playlistId)
        {
            var payload = new UpdateDeviceInput
            {
                CurrentType = "PLAYLIST",
                CurrentAssetId = playlistId
            };

            return await UpdateDeviceAsync(deviceId, teamId, payload).ConfigureAwait(false);
        }

        /// <summary>
        /// Pushes content to the screen via the pushToScreens mutation.
        /// Called for power on (restore playlist) and playlist selection.
        /// Returns true if the mutation executes successfully (data is returned).
        /// The mutation returns JSONObject! which may contain a status field.
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

            // JSONObject! returns a JObject - check if it has a status field
            if (data?.PushToScreens == null)
                return false;

            // Try to get status from the returned JSON object
            var statusToken = data.PushToScreens["status"];
            if (statusToken != null)
                return statusToken.ToObject<bool>();

            // If no status field, consider success if we got any response
            return true;
        }

        // ──────────────────────────────────────────────
        // Private HTTP execution core
        // ──────────────────────────────────────────────        

        private void LogRequest(HttpMethod method, string body)
        {
            this.LogVerbose("GraphQL >> {0} {1} | Body: {2}", 
                method, GraphQlEndpoint, body?.Trim());
        }

        private void LogResponse(HttpMethod method, HttpResponseMessage httpResponse, string body)
        {
            this.LogVerbose("GraphQL << {0} {1} | Body: {2}", 
                (int)httpResponse.StatusCode, httpResponse.ReasonPhrase, body?.Trim());
        }

        private void LogGraphQlError(GraphQlError error, string requestBody, string responseBody)
        {
            var errorCode = error.Extensions?.Code ?? "UNKNOWN";
            var errorPath = error.Path != null ? string.Join(".", error.Path) : "(root)";

            this.LogError("GraphQL error [{0}] at {1}: {2}", errorCode, errorPath, error.Message);
            this.LogVerbose("Request: {0}", requestBody?.Trim());
            this.LogVerbose("Response: {0}", responseBody?.Trim());
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
                    this.LogError("HTTP {0} {1}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                    return null;
                }

                var envelope = JsonConvert.DeserializeObject<GraphQlResponse<T>>(responseBody);

                if (envelope?.Errors != null && envelope.Errors.Count > 0)
                {
                    foreach (var error in envelope.Errors)
                        LogGraphQlError(error, requestBodyJson, responseBody);
                    // Return data anyway; partial results are valid in GraphQL
                }

                return envelope?.Data;
            }
            catch (TaskCanceledException)
            {
                this.LogError("GraphQL request timed out");
                return null;
            }
            catch (Exception ex)
            {
                this.LogError("GraphQL request failed: {0}", ex.Message);
                return null;
            }
        }
    }
}
