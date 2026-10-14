package com.openviya.backend.audit;

import java.util.UUID;

public record AuditActor(Kind kind, UUID id) {

    public AuditActor {
        if (kind == null) {
            throw new IllegalArgumentException("Audit actor kind is required");
        }
        if ((kind == Kind.SYSTEM) != (id == null)) {
            throw new IllegalArgumentException("System actors have no id; user and staff actors require one");
        }
    }

    public enum Kind {
        USER,
        STAFF,
        SYSTEM
    }
}
