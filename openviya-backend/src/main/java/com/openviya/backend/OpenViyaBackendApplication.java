package com.openviya.backend;

import com.openviya.backend.config.OpenViyaProperties;
import com.openviya.backend.integrations.threexui.ThreeXUiProperties;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.scheduling.annotation.EnableScheduling;

@SpringBootApplication
@EnableScheduling
@EnableConfigurationProperties({OpenViyaProperties.class, ThreeXUiProperties.class})
public class OpenViyaBackendApplication {

    public static void main(String[] args) {
        SpringApplication.run(OpenViyaBackendApplication.class, args);
    }
}
