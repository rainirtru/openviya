import java.lang.reflect.InvocationTargetException;
import java.nio.charset.StandardCharsets;
import java.util.Base64;

/** Probe the configured decoder against real Telegram keys using an intentionally forged token. */
public final class JwksProbe {
    public static void main(String[] args) throws Exception {
        var configurationType = Class.forName("com.openviya.backend.identity.TelegramOidcConfiguration");
        var configuration = configurationType.getConstructor().newInstance();
        var register = configurationType.getDeclaredMethod("telegramClientRegistrationRepository", String.class, String.class, String.class);
        register.setAccessible(true);
        var repository = register.invoke(configuration, "diagnostic", "not-a-real-secret", "https://api.openviya.online/login/oauth2/code/telegram");
        var registration = Class.forName("org.springframework.security.oauth2.client.registration.ClientRegistrationRepository")
                .getMethod("findByRegistrationId", String.class).invoke(repository, "telegram");
        var factoryMethod = configurationType.getDeclaredMethod("telegramJwtDecoderFactory");
        factoryMethod.setAccessible(true);
        var factory = factoryMethod.invoke(configuration);
        var decoder = Class.forName("org.springframework.security.oauth2.jwt.JwtDecoderFactory")
                .getMethod("createDecoder", Object.class).invoke(factory, registration);
        var base64 = Base64.getUrlEncoder().withoutPadding();
        var header = base64.encodeToString("{\"alg\":\"RS256\",\"kid\":\"intentional-unknown-probe-key\"}".getBytes(StandardCharsets.UTF_8));
        var claims = base64.encodeToString("{\"iss\":\"https://oauth.telegram.org\",\"sub\":\"diagnostic\",\"aud\":\"diagnostic\"}".getBytes(StandardCharsets.UTF_8));
        var fake = header + "." + claims + "." + base64.encodeToString(new byte[256]);
        try {
            Class.forName("org.springframework.security.oauth2.jwt.JwtDecoder").getMethod("decode", String.class).invoke(decoder, fake);
            throw new IllegalStateException("Forged token was unexpectedly accepted");
        } catch (InvocationTargetException failure) {
            String message = failure.getCause().getMessage();
            if (message == null || !message.contains("no matching key(s) found")) {
                throw new IllegalStateException("Configured decoder failed before signature rejection: " + failure.getCause().getClass().getSimpleName());
            }
            System.out.println("PASS: configured OIDC decoder fetched and parsed real Telegram JWKS; forged token rejected");
        }
    }
}
