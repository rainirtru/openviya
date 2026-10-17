# OpenViya: backend и центральный 3x-ui на одном VPS

Этот вариант размещения выбран пользователем 17.10.2026: backend, PostgreSQL и центральная панель 3x-ui на **31.44.7.93**, пользовательский VPN-трафик — только на отдельных нодах. Он уточняет прежнее предложение разнести APP и CONTROL из `../NETWORK-PLAN.md`. Скрипт `../vps/bootstrap-vps.sh` сюда не применяется: у него другая сетевая роль и процедура TLS.

Каталог на сервере: `/opt/openviya-control`. ОС: Ubuntu 26.04 LTS, amd64. Для небольшого тестового стенда настроены лимиты контейнеров и 2 ГБ swap; swap не заменяет RAM, поэтому при росте нагрузки понадобится пересмотр ресурсов.

## Сервисы и доступ

| Сервис | Адрес | Доступ |
| --- | --- | --- |
| Backend | `127.0.0.1:8080` на VPS | SSH-туннель; после DNS — публичный HTTPS через Caddy |
| Панель 3x-ui v3.10.2 | `127.0.0.1:38471` на VPS | Только SSH-туннель, с индивидуальным секретным base path |
| PostgreSQL 18.6 | Сеть Docker `database` | Порт на хост не опубликован |
| Сайт | `https://openviya.online` | Статические публичные страницы; `www` перенаправляет на apex |
| Кабинет | `https://api.openviya.online/account/` | Статические файлы на API-origin; сессия остаётся host-only для API |
| Backend | `https://api.openviya.online/api/v1/*` | Caddy проксирует в backend; admin/staff/internal закрыты |
| Caddy | TCP 80/443 | До DNS — HTTP 503, после DNS — HTTPS для API, apex и `www` |

Backend обращается к панели через закрытую Docker-сеть по HTTPS. Внутренний сертификат подписан отдельным CA, его публичный сертификат включён в доверенное хранилище Java с сохранением стандартных CA. Проверки TLS и имени сервера не отключаются. У панели нет VPN-inbound, серверы подписок выключены, контейнер не получает `NET_ADMIN`, `NET_RAW`, host networking или Docker socket. `openviya-panel-tls.timer` ежедневно проверяет срок и продлевает сертификат, когда осталось меньше 30 дней.

На закреплённой версии `monitor` возвращает HTTP 403 для `/panel/api/nodes/list`. Поэтому для backend создан отдельный токен `admin` с именем `openviya-backend-control`. Backend пока выполняет только три операции чтения. Токен хранится в серверном `.env`, не передаётся клиентам. Для будущего перехода на менее привилегированный токен сначала проверить, разрешает ли новая версия чтение всех необходимых endpoint.

Подключение с настроенным на компьютере SSH-ключом:

```powershell
ssh -N -o ExitOnForwardFailure=yes -L 38471:127.0.0.1:38471 -L 8080:127.0.0.1:8080 openviya@31.44.7.93
```

Пользователь `openviya` входит существующим ключом `openviya-vps`, имеет обычную оболочку и домашний каталог. Он не входит в группы sudo/docker/adm, не читает серверный `.env` и root-only файлы. SSH разрешает ему локальные туннели только к `127.0.0.1:38471` и `127.0.0.1:8080`; обратные туннели, agent/X11 и Unix socket forwarding отключены. Это обычный непривилегированный пользователь ОС, а не изолированная сетевая песочница. Сервисные команды из раздела эксплуатации выполняются root.

Панель открывается по `https://localhost:38471/<секретный-путь>/`. Браузер самостоятельно не доверяет внутреннему CA: для работы без предупреждения нужно отдельно установить публичный `panel-ca.crt` в доверенные сертификаты своего устройства, предварительно сверив fingerprint через SSH. Изменения доверия Windows автоматически не выполняются. Данные именно этой панели сохранены локально в `private/orchestrator-3x-ui.md` с ограниченными Windows ACL и исключением из Git. У непривилегированного пользователя есть их копия в домашнем каталоге:

```powershell
ssh openviya@31.44.7.93 "cat ~/PANEL-ACCESS.md"
```

Пароль первоначального владельца backend хранится отдельно в root-only файле:

```powershell
ssh root@31.44.7.93 "cat /opt/openviya-control/private/access.json"
```

Этот файл содержит пароль панели и пароль первоначального владельца backend. Не отправлять вывод в чат и не добавлять в Git. Владелец backend создаётся однократно, после чего bootstrap выключается, а его пароль удаляется из runtime `.env`. Учётная запись остаётся `PENDING` до подтверждения TOTP по `../../openviya-backend/docs/api/staff-auth.md`. Регистрация аутентификатора владельца и 2FA панели требует участия администратора.

## DNS и HTTPS

Создать у авторитетного DNS-провайдера:

| Тип | Имя | Значение |
| --- | --- | --- |
| A | `api.openviya.online` | `31.44.7.93` |
| A | `openviya.online` | `31.44.7.93` |
| A | `www.openviya.online` | `31.44.7.93` |

Точные поля для формы Timeweb, существующие записи нод и условная запись канадской ноды приведены в [DNS-RECORDS.md](DNS-RECORDS.md).

Проксирование DNS через CDN для первой проверки не использовать. Не добавлять AAAA: IPv6 для веб-сервиса ещё не настроен. SSH-панели отдельная публичная DNS-запись не требуется.

Таймер `openviya-https.timer` каждые пять минут проверяет системное разрешение API, apex и `www`. Когда все их A/AAAA адреса соответствуют этому VPS, он переключает Caddy на `Caddyfile.https`. Caddy затем выпускает и продлевает сертификаты автоматически. На стороне провайдера должны быть открыты входящие TCP 80/443. Маркер `private/https-activated` означает переключение конфигурации, а не подтверждённую выдачу сертификата; итог нужно проверить HTTPS-запросом для каждого имени.

Публичный сайт обслуживается из `web/current/public`. Кабинет доступен на API-origin под `/account/`, чтобы браузерная сессия оставалась host-only и CORS не требовался. `https://openviya.online/account` и все пути под `/account/` перенаправляются на API-origin с сохранением URI. `www` перенаправляется на apex. На apex пути `/api` и `/api/*` возвращают 404; API работает только на `api.openviya.online`. Caddy закрывает административные, staff и internal endpoint.

В этой установке Telegram OIDC включён после ввода пользователем Client ID/Secret в серверный `.env` и регистрации callback `https://api.openviya.online/login/oauth2/code/telegram` в BotFather. Backend пересоздан, публичный HTTPS readiness возвращает UP. Проверка `python3 verify-telegram-oidc.py` подтверждает redirect на Telegram, точный callback, state/nonce, PKCE S256 и защищённую cookie с Path=/; значения секретов в вывод не попадают. Для первого развёртывания шаблон по-прежнему оставляет OIDC выключенным до настройки credentials.

Начать ручной вход можно из кабинета либо по адресу `https://api.openviya.online/api/v1/auth/telegram/login`. После подтверждения в Telegram браузер возвращается на `https://api.openviya.online/account/`. OIDC callback остаётся `https://api.openviya.online/login/oauth2/code/telegram`; cookie сессии хранится только для API-host.

## Эксплуатация

```bash
cd /opt/openviya-control
docker compose ps
curl --fail http://127.0.0.1:8080/actuator/health/readiness
docker compose logs --tail 100 app
systemctl status openviya-control.service
systemctl list-timers openviya-backup.timer openviya-https.timer
```

Образы PostgreSQL, Caddy, Java и 3x-ui фиксируются digest в серверном `.env`. Установка Docker из репозитория Ubuntu и автоматические обновления безопасности ОС включены. SSH принимает ключи, вход по паролю отключён. UFW разрешает TCP 22/80/443; SSH дополнительно защищён Fail2ban. Порты backend и панели опубликованы Docker только на IPv4 loopback. Объёмы журналов ограничены.

`openviya-control.service` поднимает Compose после загрузки ОС. Контейнеры имеют `restart: unless-stopped`. После изменения `.env` выполнять `docker compose up -d app`: обычный `restart` не перечитывает окружение.

Ежедневно около 03:30 UTC (06:30 Москвы), со случайной задержкой до 15 минут, `openviya-backup.timer` создаёт локальную копию PostgreSQL, SQLite панели, `.env`, учётных данных и внутреннего CA/сертификата панели в `/var/backups/openviya-control`. Права каталога — root-only; хранение семь дней. Проверяются gzip и целостность SQLite. Это локальные копии на том же диске, удалённое хранение и пробное восстановление пока не настроены.

Запуск вручную:

```bash
systemctl start openviya-backup.service
journalctl -u openviya-backup.service --no-pager -n 20
```

## Первое развёртывание и обновление

`deploy.sh` предназначен для первого развёртывания, его нельзя повторно выполнять на рабочей базе. JAR и его SHA-256 передаются отдельно; скрипт проверяет их соответствие до сборки runtime-образа. Он не переносит локальные `.env` или базы разработчика.

Фактический VPS использует уже развёрнутый Docker Compose stack. Эта web-процедура обновляет только текущий runtime и не задаёт архитектуру будущего production-развёртывания.

Для статической поставки создать ZIP с каталогом `public/` в корне архива. В нём обязательны `public/index.html`, `public/account/index.html` и `public/assets/`. Передать ZIP и каталог с обновлёнными `compose.yml` и `Caddyfile.https` в закрытый staging каталог на VPS. Не копировать `.env`, `private/` или базы данных. Запустить helper от root:

```bash
cd /opt/openviya-control
bash deploy-web.sh /opt/openviya-control/incoming/site.zip /opt/openviya-control/incoming/config web-20261017
```

Helper проверяет состав и размер ZIP, запрещает traversal и symlinks, валидирует Compose и Caddyfile, сохраняет предыдущие Compose/Caddy и ссылку на web release в `private/web-deploy-backups/`, затем переключает `web/current` атомарной ссылкой. После запуска app/proxy выполняется HTTPS-проверка `/account/`; при ошибке скрипт автоматически возвращает конфиги и release. Для ручного отката передать имя backup-каталога:

```bash
bash rollback-web.sh 20261017T120000Z.ABCDEFGH
```

Старые релизы не удаляются автоматически. Backup содержит только Compose/Caddy и ссылку на предыдущую статику, не копию базы данных.

Для последующего обновления сначала сделать резервную копию, затем передать новый JAR и checksum, проверить `sha256sum -c app.jar.sha256`, выполнить `docker compose build app && docker compose up -d app` и проверить readiness. При несовместимых миграциях одного отката образа недостаточно: отдельно требуется план восстановления базы.
