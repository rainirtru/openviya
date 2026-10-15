# OpenViya

OpenViya — проект коммерческого VPN-сервиса с собственным кабинетом и управлением подписками. Этот репозиторий содержит backend на Java и документацию по продукту и интеграциям.

## Состав репозитория

- [`openviya-backend/`](openviya-backend/) — модульный монолит на Java 25 и Spring Boot 4.1.1; использует PostgreSQL и Flyway.
- [`openviya-backend/docs/`](openviya-backend/docs/) — требования, архитектура, планы реализации, контракты API и документация интеграций.
- [`CONTEXT.md`](CONTEXT.md) — словарь понятий предметной области.

Основные интеграции и продуктовые сценарии вводятся по мере согласования контрактов. Текущий статус и границы этапов описаны в [плане реализации](openviya-backend/docs/implementation-plan.md) и [техническом плане](openviya-backend/docs/technical-implementation-plan.md).

## Быстрый старт

Требуются JDK 25 и Docker Desktop с Linux containers. Из каталога backend скопируйте шаблон окружения, задайте локальный пароль PostgreSQL и запустите приложение:

```powershell
cd openviya-backend
Copy-Item .env.example .env
# Укажите собственный OPENVIYA_DB_PASSWORD в .env
docker compose up --build
```

API будет доступен на `http://localhost:8080`. Справочная информация API: `GET /api/v1`; readiness: `/actuator/health/readiness`.

Для сборки и запуска тестов без Docker:

```powershell
cd openviya-backend
.\mvnw.cmd --batch-mode verify
```

Подробные инструкции по переменным окружения, первому входу владельца и локальному запуску находятся в [README backend](openviya-backend/README.md).

## Непрерывная интеграция

GitHub Actions запускает Maven Wrapper с JDK 25 на каждый push и pull request, а также вручную через `workflow_dispatch`. Workflow собирает backend и выполняет Maven-проверки и тесты командой `verify`.

## Документация

- [Требования](openviya-backend/docs/requirements.md)
- [Архитектура](openviya-backend/docs/architecture.md)
- [План реализации](openviya-backend/docs/implementation-plan.md)
- [Технический план](openviya-backend/docs/technical-implementation-plan.md)
- [Документация API](openviya-backend/docs/api/README.md)
