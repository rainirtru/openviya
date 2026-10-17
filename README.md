# OpenViya

OpenViya — проект коммерческого VPN-сервиса с собственным кабинетом и управлением подписками. Репозиторий содержит backend на Java, веб-интерфейс, локальное приложение для инфраструктуры и документацию.

## Состав репозитория

- [`openviya-backend/`](openviya-backend/) — модульный монолит на Java 25 и Spring Boot 4.1.1; использует PostgreSQL и Flyway.
- [`openviya-web/`](openviya-web/README.md) — публичный сайт и пользовательский кабинет на русском: Telegram-вход, профиль, состояние подписки, история начислений и выход.
- [`openviya-backend/docs/`](openviya-backend/docs/) — требования, архитектура, планы реализации, контракты API и документация интеграций.
- [`CONTEXT.md`](CONTEXT.md) — словарь понятий предметной области.
- [`openviya-ops/`](openviya-ops/README.md) — Windows-приложение для реестра VPS, автоматической первичной настройки с 3x-ui, DNS, SSH-туннелей и диагностики панелей; поддерживает локальный режим и общий LAN-сервер в Docker.

Основные интеграции и продуктовые сценарии вводятся по мере согласования контрактов. Текущий статус и границы этапов описаны в [плане реализации](openviya-backend/docs/implementation-plan.md) и [техническом плане](openviya-backend/docs/technical-implementation-plan.md).

Сверка планов с кодом от 17.10.2026, границы выполненного и упорядоченные следующие шаги: [статус разработки](DEVELOPMENT-STATUS.md). LAN-комплект выпущен как предварительный релиз; сквозная выдача доступа и платежи ещё не реализованы.

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

## Подготовка VPS и установка 3x-ui

Единая [сетевая схема OpenViya](ops/NETWORK-PLAN.md) описывает роли APP/CONTROL/нод, все домены, порты, firewall, TLS, доступ к панелям, подписки и порядок внедрения. Это план настройки; фактическое состояние VPS проверяется отдельно.

Первичную установку можно запустить из мастера Windows-приложения: IP, начальный пароль или SSH-ключ, DNS и email Let’s Encrypt. Мастер проверяет новый административный вход перед завершением защиты SSH и сохраняет данные панели зашифрованными. Порядок работы и продолжение после прерывания — в [руководстве полной настройки VPS](openviya-ops/VPS-PROVISIONING.md).

Для свежего VPS с Ubuntu 26.04 используется [bootstrap-vps.sh](ops/vps/bootstrap-vps.sh). HTTPS — **Let’s Encrypt с автоматическим продлением**, порт панели по умолчанию **38471** (`--panel-port`). Нужны поддомен с DNS на VPS, email и публично доступный TCP 80 для HTTP-01. Полная [инструкция](ops/vps/README.md) содержит оба режима HTTPS, переход существующей установки, порты, резервное копирование и [исправления ошибок установки](ops/vps/README.md#устранение-ошибок).

**Если панель уже установлена этим скриптом, не запускайте prepare заново.** Используйте [переход на Let’s Encrypt](ops/vps/README.md#переход-уже-установленной-панели-на-lets-encrypt): команда `setup-letsencrypt` сохраняет порт, логин, пароль, путь и настройки VPN.

На каждом VPS используются разные имена: `usa-1.openviya.online` — для VPN-клиентов, `panel-usa-1.openviya.online` — для панели/API. Обе A-записи указывают на IP этого VPS. В параметре `--domain` передавайте **имя панели**; VPN inbound и адрес в клиентской конфигурации задаются отдельно.

**На компьютере, в PowerShell из корня проекта:** замените `SERVER_IP` адресом VPS. Команду `ssh-keygen` выполняйте только при отсутствии этого ключа; существующий ключ не перезаписывайте.

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE/.ssh" | Out-Null
ssh-keygen -t ed25519 -a 64 -f "$env:USERPROFILE/.ssh/vpsadmin"
scp ./ops/vps/bootstrap-vps.sh root@SERVER_IP:/root/bootstrap-vps.sh
scp "$env:USERPROFILE/.ssh/vpsadmin.pub" root@SERVER_IP:/root/vpsadmin.pub
ssh root@SERVER_IP
```

**На VPS, в первом SSH-сеансе:** проверьте публичный ключ и запустите подготовку. Этот пример включает HTTPS через SSH-туннель; публичный HTTPS настраивается отдельным вариантом из полной инструкции.

```bash
ssh-keygen -lf /root/vpsadmin.pub
bash /root/bootstrap-vps.sh prepare --key-file /root/vpsadmin.pub \
  --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL \
  --panel-port 38471 --panel-access tunnel
```

**Не закрывая первый сеанс**, в течение 20 минут подключитесь из нового окна PowerShell:

```powershell
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" -o IdentitiesOnly=yes vpsadmin@SERVER_IP
```

**В новом SSH-сеансе на VPS:**

```bash
sudo --preserve-env=SSH_CONNECTION /usr/local/sbin/openviya-vps finalize
sudo /usr/local/sbin/openviya-vps status
sudo cat /var/lib/openviya-vps-bootstrap/credentials
```

После `Complete` откройте туннель в отдельном окне PowerShell и оставьте его работающим:

```powershell
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes -N -L 127.0.0.1:20530:127.0.0.1:38471 vpsadmin@SERVER_IP
```

Для туннеля добавьте на компьютере строку `127.0.0.1 panel-usa-1.openviya.online` в `C:\Windows\System32\drivers\etc\hosts` от администратора. Адрес панели — `https://panel-usa-1.openviya.online:20530` + значение `PANEL_PATH` из `credentials`; там же логин и пароль. Сохраняйте доменное имя в URL для проверки сертификата. Для нескольких VPS используйте разные поддомены и локальные порты — [примеры](ops/vps/README.md#открытие-панели-и-несколько-серверов).

Файл `credentials` и приватный SSH-ключ содержат секреты. После входа включите 2FA и создайте VPN inbound: скрипт разрешает `443/tcp`, но inbound не создаёт. Для автопродления оставьте TCP 80 открытым в UFW и firewall провайдера и свободным от других HTTP-серверов. Проверка: `sudo certbot renew --cert-name openviya-panel --dry-run`.

Для нестандартного SSH-порта используйте `ssh -p PORT`, `scp -P PORT` и `prepare --ssh-port PORT`. Если подготовка остановилась, продолжайте по [разделу диагностики](ops/vps/README.md#устранение-ошибок); после создания пользователя повторный `prepare` не запускается.

## Документация

- [Требования](openviya-backend/docs/requirements.md)
- [Архитектура](openviya-backend/docs/architecture.md)
- [План реализации](openviya-backend/docs/implementation-plan.md)
- [Технический план](openviya-backend/docs/technical-implementation-plan.md)
- [Документация API](openviya-backend/docs/api/README.md)
- [Подготовка свежего VPS и установка 3x-ui](ops/vps/README.md)
- [Домены, серверы, порты и доступ](ops/NETWORK-PLAN.md)
- [Архитектура локального приложения OpenViya Ops](ops/LOCAL-OPS-APP-ARCHITECTURE.md), [первая реализация и запуск](openviya-ops/README.md).
