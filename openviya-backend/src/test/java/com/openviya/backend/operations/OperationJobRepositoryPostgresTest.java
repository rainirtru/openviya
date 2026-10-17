package com.openviya.backend.operations;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.core.io.ClassPathResource;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.namedparam.NamedParameterJdbcTemplate;
import org.springframework.jdbc.datasource.DriverManagerDataSource;
import org.springframework.jdbc.datasource.init.ResourceDatabasePopulator;
import org.testcontainers.containers.PostgreSQLContainer;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;

@Testcontainers(disabledWithoutDocker = true)
class OperationJobRepositoryPostgresTest {

    @Container
    private static final PostgreSQLContainer<?> postgres = new PostgreSQLContainer<>("postgres:18.6-alpine3.23");

    private JdbcTemplate jdbc;
    private OperationJobRepository jobs;

    @BeforeEach
    void setUp() {
        DriverManagerDataSource dataSource = new DriverManagerDataSource(
                postgres.getJdbcUrl(), postgres.getUsername(), postgres.getPassword());
        jdbc = new JdbcTemplate(dataSource);
        jdbc.execute("DROP TABLE IF EXISTS operation_job CASCADE");
        new ResourceDatabasePopulator(
                new ClassPathResource("db/migration/V1__create_operation_queue.sql"))
                .execute(dataSource);
        jdbc.update("TRUNCATE operation_job");
        jobs = new OperationJobRepository(new NamedParameterJdbcTemplate(dataSource));
    }

    @Test
    void enqueueIsIdempotentAndRejectsChangedPayloadForSameBusinessKey() {
        var original = jobs.enqueue("TRIAL_ISSUE", "claim-1", 1, "{\"grantId\":\"a\"}");

        assertEquals(original, jobs.enqueue("TRIAL_ISSUE", "claim-1", 1, "{ \"grantId\" : \"a\" }"));
        assertThrows(IllegalStateException.class,
                () -> jobs.enqueue("TRIAL_ISSUE", "claim-1", 1, "{\"grantId\":\"b\"}"));
        assertEquals(1, jdbc.queryForObject("SELECT count(*) FROM operation_job", Integer.class));
    }

    @Test
    void skipLockedAllowsOnlyOneOfTwoConcurrentWorkersToClaimTheSameJob() throws Exception {
        jobs.enqueue("TRIAL_ISSUE", "claim-2", 1, "{}");
        CountDownLatch ready = new CountDownLatch(2);
        CountDownLatch start = new CountDownLatch(1);
        try (var executor = Executors.newFixedThreadPool(2)) {
            var first = executor.submit(() -> claimAfterBarrier("worker-a", ready, start));
            var second = executor.submit(() -> claimAfterBarrier("worker-b", ready, start));
            assertTrue(ready.await(5, TimeUnit.SECONDS));
            start.countDown();

            int acquired = first.get(5, TimeUnit.SECONDS).size() + second.get(5, TimeUnit.SECONDS).size();
            assertEquals(1, acquired);
            assertEquals(1, jdbc.queryForObject(
                    "SELECT count(*) FROM operation_job WHERE status = 'RUNNING'", Integer.class));
        }
    }

    @Test
    void expiredLeaseCanBeRecoveredButStaleWorkerCannotCompleteIt() throws Exception {
        jobs.enqueue("TRIAL_ISSUE", "claim-3", 1, "{}");
        var firstLease = jobs.claimReady("worker-old", 1, Duration.ofMillis(80)).getFirst();
        Thread.sleep(180);

        // A new repository instance models a process restart while the PostgreSQL job remains durable.
        var restarted = new OperationJobRepository(new NamedParameterJdbcTemplate(jdbc.getDataSource()));
        var recovered = restarted.claimReady("worker-new", 1, Duration.ofSeconds(5)).getFirst();

        assertNotEquals(firstLease.leaseGeneration(), recovered.leaseGeneration());
        assertFalse(jobs.complete(firstLease));
        assertTrue(restarted.complete(recovered));
        assertEquals("DONE", jdbc.queryForObject(
                "SELECT status FROM operation_job WHERE business_key = 'claim-3'", String.class));
    }

    @Test
    void retryMovesJobOutOfReadyQueueUntilScheduledTime() {
        jobs.enqueue("TRIAL_ISSUE", "claim-4", 1, "{}");
        var claim = jobs.claimReady("worker-a", 1, Duration.ofMinutes(1)).getFirst();
        Instant later = Instant.now().plus(Duration.ofHours(1));

        assertTrue(jobs.scheduleRetry(claim, later, "PANEL_UNAVAILABLE"));
        assertTrue(jobs.claimReady("worker-b", 1, Duration.ofMinutes(1)).isEmpty());
        assertEquals("RETRY_WAIT", jdbc.queryForObject(
                "SELECT status FROM operation_job WHERE business_key = 'claim-4'", String.class));
    }

    private List<ClaimedOperationJob> claimAfterBarrier(
            String worker, CountDownLatch ready, CountDownLatch start) {
        ready.countDown();
        try {
            if (!start.await(5, TimeUnit.SECONDS)) {
                throw new IllegalStateException("Worker start barrier timed out");
            }
            return jobs.claimReady(worker, 1, Duration.ofSeconds(5));
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Worker interrupted", exception);
        }
    }
}
