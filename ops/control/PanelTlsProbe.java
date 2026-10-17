import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;

/** Deployment probe: reads credentials from container env, prints only endpoint names/status. */
public final class PanelTlsProbe {
    public static void main(String[] args) throws Exception {
        var base = URI.create(System.getenv("OPENVIYA_3XUI_ADMIN_BASE_URI"));
        var token = System.getenv("OPENVIYA_3XUI_BEARER_TOKEN");
        var client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build();
        for (var path : new String[]{"panel/api/server/status", "panel/api/nodes/list", "panel/api/inbounds/list"}) {
            var request = HttpRequest.newBuilder(base.resolve(path)).timeout(Duration.ofSeconds(10))
                .header("Authorization", "Bearer " + token).GET().build();
            var response = client.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200 || !response.body().replaceAll("\\s+", "").contains("\"success\":true")) {
                throw new IllegalStateException("Panel readback failed: " + path + ", HTTP " + response.statusCode());
            }
            System.out.println(path + ": HTTP 200, success=true, Java TLS verified");
        }
    }
}
