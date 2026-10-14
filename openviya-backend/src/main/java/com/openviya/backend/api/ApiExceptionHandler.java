package com.openviya.backend.api;

import jakarta.servlet.http.HttpServletRequest;
import com.openviya.backend.security.StaffAuthException;
import com.openviya.backend.security.StaffAdminException;
import java.time.Instant;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;

@RestControllerAdvice
public class ApiExceptionHandler {

    @ExceptionHandler({MethodArgumentNotValidException.class, HttpMessageNotReadableException.class})
    public ResponseEntity<ApiError> handleInvalidRequest(Exception exception, HttpServletRequest request) {
        return response(HttpStatus.BAD_REQUEST, "INVALID_REQUEST", "Проверьте параметры запроса.", request);
    }

    @ExceptionHandler(StaffAuthException.class)
    public ResponseEntity<ApiError> handleStaffAuth(StaffAuthException exception, HttpServletRequest request) {
        return response(
                exception.status(),
                exception.code(),
                "Не удалось выполнить вход сотрудника.",
                request);
    }

    @ExceptionHandler(StaffAdminException.class)
    public ResponseEntity<ApiError> handleStaffAdmin(StaffAdminException exception, HttpServletRequest request) {
        return response(
                exception.status(), exception.code(), "Не удалось выполнить операцию с учетной записью.", request);
    }

    @ExceptionHandler(Exception.class)
    public ResponseEntity<ApiError> handleUnexpectedError(Exception exception, HttpServletRequest request) {
        return response(
                HttpStatus.INTERNAL_SERVER_ERROR,
                "INTERNAL_ERROR",
                "Внутренняя ошибка сервера.",
                request);
    }

    private ResponseEntity<ApiError> response(
            HttpStatus status,
            String code,
            String message,
            HttpServletRequest request) {
        Object correlationId = request.getAttribute(CorrelationIdFilter.ATTRIBUTE_NAME);
        ApiError body = new ApiError(
                Instant.now(),
                status.value(),
                code,
                message,
                correlationId instanceof String value ? value : null);
        return ResponseEntity.status(status).body(body);
    }
}
