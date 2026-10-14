package com.openviya.backend.operations;

import java.time.Instant;
import java.util.UUID;

public record ClaimedOperationJob(
        UUID id,
        String kind,
        String businessKey,
        int payloadVersion,
        String payload,
        int attemptCount,
        String leaseOwner,
        Instant leaseUntil,
        long leaseGeneration) {
}
