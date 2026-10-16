package com.openviya.backend.operations;

import java.time.Instant;
import java.util.Objects;
import java.util.regex.Pattern;

/** Explicit durable outcome; handlers decide whether retrying is safe for their operation. */
public sealed interface OperationJobResult {

    Pattern ERROR_CODE = Pattern.compile("[A-Z0-9_]{1,100}");

    record Complete() implements OperationJobResult {
    }

    record RetryAt(Instant nextAttemptAt, String errorCode) implements OperationJobResult {
        public RetryAt {
            Objects.requireNonNull(nextAttemptAt, "nextAttemptAt");
            requireErrorCode(errorCode);
        }
    }

    record NeedsAttention(String errorCode) implements OperationJobResult {
        public NeedsAttention {
            requireErrorCode(errorCode);
        }
    }

    static OperationJobResult complete() {
        return new Complete();
    }

    static OperationJobResult retryAt(Instant nextAttemptAt, String errorCode) {
        return new RetryAt(nextAttemptAt, errorCode);
    }

    static OperationJobResult needsAttention(String errorCode) {
        return new NeedsAttention(errorCode);
    }

    private static void requireErrorCode(String errorCode) {
        if (errorCode == null || !ERROR_CODE.matcher(errorCode).matches()) {
            throw new IllegalArgumentException("Operation error code must use 1-100 uppercase letters, digits, or underscores.");
        }
    }
}
