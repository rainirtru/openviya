package com.openviya.backend.identity;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.zip.GZIPInputStream;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpRequest;
import org.springframework.http.HttpStatusCode;
import org.springframework.http.client.ClientHttpRequestExecution;
import org.springframework.http.client.ClientHttpRequestInterceptor;
import org.springframework.http.client.ClientHttpResponse;

/** JDK HTTP transports do not transparently decode Telegram's gzip JSON responses. */
final class TelegramGzipInterceptor implements ClientHttpRequestInterceptor {
    private static final int MAX_JSON_BYTES = 1024 * 1024;

    @Override
    public ClientHttpResponse intercept(HttpRequest request, byte[] body, ClientHttpRequestExecution execution)
            throws IOException {
        request.getHeaders().set(HttpHeaders.ACCEPT_ENCODING, "gzip");
        ClientHttpResponse response = execution.execute(request, body);
        String encoding = response.getHeaders().getFirst(HttpHeaders.CONTENT_ENCODING);
        if (encoding == null || "identity".equalsIgnoreCase(encoding.strip())) {
            return response;
        }
        try {
            if (!"gzip".equalsIgnoreCase(encoding.strip())) {
                throw new IOException("Unsupported Telegram response encoding");
            }
            byte[] decoded;
            try (var input = new GZIPInputStream(response.getBody())) {
                decoded = input.readNBytes(MAX_JSON_BYTES + 1);
            }
            if (decoded.length > MAX_JSON_BYTES) {
                throw new IOException("Telegram JSON response exceeds size limit");
            }
            HttpHeaders headers = new HttpHeaders();
            headers.putAll(response.getHeaders());
            headers.remove(HttpHeaders.CONTENT_ENCODING);
            headers.setContentLength(decoded.length);
            return new DecodedResponse(response, headers, decoded);
        } catch (IOException | RuntimeException exception) {
            response.close();
            throw exception;
        }
    }

    private record DecodedResponse(ClientHttpResponse delegate, HttpHeaders headers, byte[] body)
            implements ClientHttpResponse {
        @Override public HttpStatusCode getStatusCode() throws IOException { return delegate.getStatusCode(); }
        @Override public String getStatusText() throws IOException { return delegate.getStatusText(); }
        @Override public HttpHeaders getHeaders() { return headers; }
        @Override public InputStream getBody() { return new ByteArrayInputStream(body); }
        @Override public void close() { delegate.close(); }
    }
}
