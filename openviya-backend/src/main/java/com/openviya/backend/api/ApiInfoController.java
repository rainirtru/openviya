package com.openviya.backend.api;

import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1")
public class ApiInfoController {

    @GetMapping
    public ApiInfo getApiInfo() {
        return new ApiInfo("OpenViya Backend", "v1");
    }

    public record ApiInfo(String service, String apiVersion) {
    }
}
