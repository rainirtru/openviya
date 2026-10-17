import com.openviya.backend.identity.UserPrincipal;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.datasource.DataSourceTransactionManager;
import org.springframework.jdbc.datasource.DriverManagerDataSource;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.authority.SimpleGrantedAuthority;
import org.springframework.security.core.context.SecurityContext;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.session.Session;
import org.springframework.session.SessionRepository;
import org.springframework.session.jdbc.JdbcIndexedSessionRepository;
import org.springframework.transaction.support.TransactionTemplate;

/** Exercise real JDBC session persistence inside a transaction which is always rolled back. */
public final class SessionPrincipalProbe {
    @SuppressWarnings("unchecked")
    public static void main(String[] args) {
        var dataSource = new DriverManagerDataSource(System.getenv("OPENVIYA_DB_URL"),
                System.getenv("OPENVIYA_DB_USERNAME"), System.getenv("OPENVIYA_DB_PASSWORD"));
        var jdbc = new JdbcTemplate(dataSource);
        var transactions = new TransactionTemplate(new DataSourceTransactionManager(dataSource));
        var repository = (SessionRepository<Session>) (SessionRepository<?>) new JdbcIndexedSessionRepository(jdbc, transactions);
        UUID userId = UUID.randomUUID();
        var principal = new UserPrincipal(userId, "712345678", "diagnostic", "Long display name ".repeat(12));
        var context = SecurityContextHolder.createEmptyContext();
        context.setAuthentication(UsernamePasswordAuthenticationToken.authenticated(principal, null,
                List.of(new SimpleGrantedAuthority("ROLE_USER"))));
        var session = repository.createSession();
        session.setAttribute("SPRING_SECURITY_CONTEXT", context);
        transactions.executeWithoutResult(status -> {
            status.setRollbackOnly();
            repository.save(session);
            String index = jdbc.queryForObject("SELECT principal_name FROM spring_session WHERE session_id = ?", String.class, session.getId());
            if (!userId.toString().equals(index)) throw new IllegalStateException("Wrong principal index");
            Session restored = repository.findById(session.getId());
            SecurityContext saved = restored.getAttribute("SPRING_SECURITY_CONTEXT");
            if (!principal.equals(saved.getAuthentication().getPrincipal())) throw new IllegalStateException("Session did not deserialize");
            System.out.println("PASS: PostgreSQL session principal index is UUID (36 characters), full user context round-trips");
        });
        if (repository.findById(session.getId()) != null) throw new IllegalStateException("Diagnostic session was not rolled back");
        System.out.println("PASS: diagnostic transaction rolled back; no test authentication session remains");
    }
}
