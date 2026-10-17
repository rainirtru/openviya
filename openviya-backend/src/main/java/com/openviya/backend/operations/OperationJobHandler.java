package com.openviya.backend.operations;

/** Handles one durable operation kind after its lease has been acquired. */
public interface OperationJobHandler {

    String kind();

    OperationJobResult handle(ClaimedOperationJob job);
}
