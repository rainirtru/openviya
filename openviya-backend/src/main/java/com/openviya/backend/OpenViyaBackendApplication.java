package com.openviya.backend;

import com.openviya.backend.config.OpenViyaProperties;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.scheduling.annotation.EnableScheduling;

@SpringBootApplication
@EnableScheduling
@EnableConfigurationProperties(OpenViyaProperties.class)
public class OpenViyaBackendApplication {

    public static void main(String[] args) {
        SpringApplication.run(OpenViyaBackendApplication.class, args);
    }
}
