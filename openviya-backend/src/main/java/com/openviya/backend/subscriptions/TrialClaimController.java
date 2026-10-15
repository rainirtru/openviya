package com.openviya.backend.subscriptions;

import com.openviya.backend.identity.UserPrincipal;
import java.time.Instant;
import java.util.UUID;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class TrialClaimController {

    private final TrialClaimService trials;

    public TrialClaimController(TrialClaimService trials) {
        this.trials = trials;
    }

    @PostMapping("/api/v1/trials/claim")
    public ResponseEntity<OperationResponse> claim(@AuthenticationPrincipal UserPrincipal principal) {
        TrialClaim claim = trials.claim(principal.userId(), principal.telegramUserId());
        OperationResponse response = OperationResponse.from(claim);
        return claim.state() == TrialClaimState.REQUESTED || claim.state() == TrialClaimState.ISSUING
                ? ResponseEntity.accepted().body(response)
                : ResponseEntity.ok(response);
    }

    @GetMapping("/api/v1/access-operations/{operationId}")
    public OperationResponse get(
            @PathVariable UUID operationId,
            @AuthenticationPrincipal UserPrincipal principal) {
        return OperationResponse.from(trials.get(principal.userId(), operationId));
    }

    public record OperationResponse(UUID operationId, String state, Instant activatedAt) {
        static OperationResponse from(TrialClaim claim) {
            return new OperationResponse(claim.id(), claim.state().name(), claim.activatedAt());
        }
    }
}
