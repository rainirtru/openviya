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
    }
}
