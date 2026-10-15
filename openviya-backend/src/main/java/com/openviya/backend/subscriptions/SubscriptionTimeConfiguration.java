package com.openviya.backend.subscriptions;

import java.time.Clock;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
public class SubscriptionTimeConfiguration {

    @Bean
    Clock subscriptionClock() {
        return Clock.systemUTC();
    }
}
