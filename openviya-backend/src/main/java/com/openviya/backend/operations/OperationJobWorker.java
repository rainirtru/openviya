package com.openviya.backend.operations;

import com.openviya.backend.config.OpenViyaProperties;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;

/** Polls the durable queue and persists only explicit handler outcomes. */
@Component
@ConditionalOnProperty(prefix = "openviya.queue", name = "worker-enabled", havingValue = "true")
public final class OperationJobWorker {

    private static final Logger log = LoggerFactory.getLogger(OperationJobWorker.class);

    private final OperationJobRepository jobs;
    private final OpenViyaProperties.Queue queue;
    private final String workerId = "openviya-worker-" + UUID.randomUUID();
    private final Map<String, OperationJobHandler> handlers;

    public OperationJobWorker(
            OperationJobRepository jobs,
            OpenViyaProperties properties,
            List<OperationJobHandler> handlers) {
        this.jobs = jobs;
        this.queue = properties.queue();
        this.handlers = indexHandlers(handlers);
    }

    @Scheduled(fixedDelayString = "${openviya.queue.poll-interval:1s}")
    public void poll() {
        for (int index = 0; index < queue.batchSize(); index++) {
            List<ClaimedOperationJob> claimed = jobs.claimReady(workerId, 1, queue.leaseDuration());
            if (claimed.isEmpty()) {
                return;
            }
            process(claimed.getFirst());
        }
    }

    private void process(ClaimedOperationJob job) {
        OperationJobHandler handler = handlers.get(job.kind());
        if (handler == null) {
            persist(job, jobs.requireAttention(job, "NO_HANDLER"), "manual attention");
            return;
        }

        try {
            OperationJobResult result = handler.handle(job);
            if (result instanceof OperationJobResult.Complete) {
                persist(job, jobs.complete(job), "completed");
            } else if (result instanceof OperationJobResult.RetryAt retry) {
                persist(job, jobs.scheduleRetry(job, retry.nextAttemptAt(), retry.errorCode()), "retry scheduled");
            } else if (result instanceof OperationJobResult.NeedsAttention attention) {
                persist(job, jobs.requireAttention(job, attention.errorCode()), "manual attention");
            } else {
                persist(job, jobs.requireAttention(job, "INVALID_HANDLER_RESULT"), "manual attention");
            }
        } catch (RuntimeException exception) {
            boolean updated = jobs.requireAttention(job, "HANDLER_FAILURE");
            if (updated) {
                log.warn("Operation handler failed; moved job {} of kind {} to manual attention ({})",
                        job.id(), job.kind(), exception.getClass().getSimpleName());
            } else {
                log.info("Operation handler failed after its lease expired; ignored stale result for job {} of kind {}",
                        job.id(), job.kind());
            }
        }
    }

    private void persist(ClaimedOperationJob job, boolean updated, String outcome) {
        if (updated) {
            log.debug("Operation job {} of kind {}: {}", job.id(), job.kind(), outcome);
        } else {
            log.info("Operation job {} of kind {} lost its lease before outcome persistence", job.id(), job.kind());
        }
    }

    private static Map<String, OperationJobHandler> indexHandlers(List<OperationJobHandler> handlers) {
        Map<String, OperationJobHandler> byKind = new HashMap<>();
        for (OperationJobHandler handler : handlers) {
            String kind = handler.kind();
            if (kind == null || kind.isBlank()) {
                throw new IllegalArgumentException("Operation handler kind must not be blank.");
            }
            if (byKind.putIfAbsent(kind, handler) != null) {
                throw new IllegalStateException("Duplicate operation handler kind: " + kind);
            }
        }
        return Map.copyOf(byKind);
    }
}
