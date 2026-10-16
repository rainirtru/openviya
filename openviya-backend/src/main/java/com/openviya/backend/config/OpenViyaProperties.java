package com.openviya.backend.config;

import jakarta.validation.Valid;
import jakarta.validation.constraints.NotNull;
import java.net.URI;
import java.time.Duration;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

@Validated
@ConfigurationProperties("openviya")
public record OpenViyaProperties(
        @NotNull URI publicBaseUrl,
        @Valid @NotNull Queue queue) {

    public record Queue(
            @NotNull Duration pollInterval,
            int batchSize,
            @NotNull Duration leaseDuration) {

        public Queue {
            if (pollInterval == null || pollInterval.isZero() || pollInterval.isNegative()) {
                throw new IllegalArgumentException("Queue poll interval must be positive.");
            }
            if (batchSize < 1) {
                throw new IllegalArgumentException("Queue batch size must be positive.");
            }
            if (leaseDuration == null || leaseDuration.isZero() || leaseDuration.isNegative()) {
                throw new IllegalArgumentException("Queue lease duration must be positive.");
            }
        }
    }
}
