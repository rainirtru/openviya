package com.openviya.backend.identity;

import static org.junit.jupiter.api.Assertions.*;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.List;
import org.junit.jupiter.api.Test;

class TelegramProfileTest {
    @Test
    void countsProfileTextByUnicodeCharactersLikePostgresVarchar() {
        String validName = "\uD83D\uDE00".repeat(255);
        assertEquals(validName, new TelegramProfile("123", null, validName).displayName());
        assertThrows(IllegalArgumentException.class,
                () -> new TelegramProfile("123", null, "\uD83D\uDE00".repeat(256)));
    }

    @Test
    void acceptsExactNumericAndCanonicalStringRepresentations() {
        for (Object claim : List.of(712345678L, new BigInteger("712345678"), new BigDecimal("712345678"), "712345678")) {
            assertEquals("712345678", TelegramProfile.numericUserIdFromOidcClaim(claim));
        }
        assertEquals("4503599627370495", TelegramProfile.numericUserIdFromOidcClaim("4503599627370495"));
    }

    @Test
    void rejectsAmbiguousOutOfRangeAndNonNumericRepresentations() {
        for (Object claim : List.of("", "0", "-1", "+1", "01", " 1", "1 ", "1.0", "1e3", "１２", "4503599627370496",
                0, -1, 1.5, Double.NaN, Double.POSITIVE_INFINITY, new BigInteger("4503599627370496"), true, List.of("123"))) {
            assertThrows(IllegalArgumentException.class, () -> TelegramProfile.numericUserIdFromOidcClaim(claim));
        }
        assertThrows(IllegalArgumentException.class, () -> TelegramProfile.numericUserIdFromOidcClaim(null));
    }
}
