package com.openviya.backend.integrations.threexui;

import java.net.URI;
import tools.jackson.databind.JsonNode;
import org.springframework.http.HttpStatusCode;
import org.springframework.web.client.RestClient;
import org.springframework.web.client.RestClientException;

/** Read-only access to the documented 3x-ui health and inventory endpoints. */
public final class ThreeXUiClient {

    private final RestClient restClient;
    private final URI adminBaseUri;

    ThreeXUiClient(RestClient restClient, URI adminBaseUri) {
        this.restClient = restClient;
        this.adminBaseUri = adminBaseUri;
    }

    public JsonNode serverStatus() {
        return getObject("/panel/api/server/status");
    }

    public JsonNode nodes() {
        return getObject("/panel/api/nodes/list");
    }

    public JsonNode inbounds() {
        return getObject("/panel/api/inbounds/list");
    }

    private JsonNode getObject(String path) {
        try {
            ApiEnvelope response = restClient.get()
                    .uri(adminBaseUri.resolve(path.substring(1)))
                    .retrieve()
                    .onStatus(HttpStatusCode::isError,
                            (request, result) -> { throw new ThreeXUiApiException("3x-ui returned an HTTP error"); })
                    .body(ApiEnvelope.class);
            if (response == null || !Boolean.TRUE.equals(response.success()) || response.obj() == null) {
                throw new ThreeXUiApiException("3x-ui did not confirm a successful read response");
            }
            return response.obj();
        } catch (ThreeXUiApiException exception) {
            throw exception;
        } catch (RestClientException exception) {
            throw new ThreeXUiApiException("3x-ui read request failed", exception);
        }
    }

    private record ApiEnvelope(Boolean success, String msg, JsonNode obj) {
    }
}
