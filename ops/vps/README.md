# Свежий VPS: защита и установка 3x-ui

[bootstrap-vps.sh](bootstrap-vps.sh) предназначен для нового VPS с Ubuntu **26.04 LTS**; также поддерживает Ubuntu 24.04 и Debian 12/13, amd64/arm64, systemd. Нужны root/sudo, исходящий интернет и аварийная консоль провайдера. Для существующей установки этого скрипта используйте раздел перехода ниже; повторный `prepare` запрещён.

Закреплён официальный релиз **3x-ui v3.10.2**. SHA-256 архивов записаны в скрипте, структура архива проверяется до распаковки. Изменяемый upstream `install.sh` не исполняется.

Общая [сетевая схема OpenViya](../NETWORK-PLAN.md) связывает эту установку с бэкендом, основным центром управления, VPN-нодами и gateway подписок. В ней приведены правила доступа для CONTROL и нод и отдельные команды prepare для этих ролей.

## Доступ, порты и сертификаты

| Назначение | `--panel-access tunnel` (по умолчанию) | `--panel-access both` |
| --- | --- | --- |
| SSH | Текущий порт, обычно 22/tcp; после finalize только ключ | То же |
| Панель | HTTPS, 127.0.0.1:38471, SSH-туннель | HTTPS, 0.0.0.0:38471 и SSH-туннель |
| Источники доступа к панели | Только loopback | `--panel-cidr CIDR`, либо `any` |
| Let’s Encrypt | **80/tcp открыт всем** для HTTP-01 | То же |
| VPN | 443/tcp разрешён; inbound ещё не создан | То же |
| UDP | Закрыт; нужные порты задаются через `--vpn-udp` | То же |
| Подписки | Отключены, 2096 закрыт | То же |
| Остальные входящие соединения | UFW deny, IPv4 и IPv6 | То же |

Панель сама обслуживает HTTPS. Nginx/Caddy не устанавливаются. Certbot standalone слушает порт 80 только при выпуске или продлении; правило UFW для 80 сохраняется постоянно. Закрытие порта у провайдера или запуск другого HTTP-сервера на 80 нарушит продление. Порт 443 остаётся под VPN.

Для каждого VPS параметр `--domain` задаёт **поддомен панели**, например `panel-usa-1.openviya.online`; `--acme-email` — email аккаунта Let’s Encrypt. VPN-клиенты используют другое имя: `usa-1.openviya.online`. Скрипт принимает условия Let’s Encrypt от вашего имени при выпуске. IP, localhost и wildcard вместо домена не принимаются. Секреты DNS API не нужны.

Пример DNS-записей:

| Тип | Имя | Значение | Назначение |
| --- | --- | --- | --- |
| A | usa-1.openviya.online | Публичный IPv4 VPS в США | Подключение VPN-клиентов, например TCP 443 |
| A | panel-usa-1.openviya.online | Тот же IPv4 VPS в США | Панель/API, TCP 38471 |
| A | ca-1.openviya.online | Публичный IPv4 VPS в Канаде | Подключение VPN-клиентов, например TCP 443 |
| A | panel-ca-1.openviya.online | Тот же IPv4 VPS в Канаде | Панель/API, TCP 38471 |

В Timeweb для каждой ноды создайте два поддомена: например `usa-1` и `panel-usa-1`, оба с IP её VPS. В A-записи указывается только IPv4: без схемы, порта и пути. DNS не ограничивает доступ по портам: правила firewall применяются к IP/порту, а имена здесь помогают разделить назначение адресов.

Скрипт выпускает сертификат только для имени панели из `--domain`. VPN inbound и адрес в выдаваемых клиентских конфигурациях настраиваются отдельно в 3x-ui: для США указывайте `usa-1.openviya.online` как адрес сервера. DNS-запись сама по себе не создаёт inbound. Если VPN-транспорт использует обычный TLS, для него отдельно нужен сертификат, соответствующий его TLS-имени; сертификат панели автоматически на VPN не распространяется.

Перед запуском:

- Создайте запись A и дождитесь распространения DNS. Если есть AAAA, она должна вести на IPv6 **того же VPS** с доступным TCP 80; ошибочную AAAA удалите.
- Используйте прямую DNS-запись без CDN/HTTP-прокси. Если есть ограничивающая CAA, она должна разрешать `letsencrypt.org`.
- В firewall провайдера разрешите TCP 80 из интернета, SSH и нужные VPN-порты. Порт панели разрешайте только для нужных источников в режиме `both`.
- TCP 80 должен быть свободен. Скрипт не останавливает чужие службы ради проверки.

Предварительная проверка DNS исключает локальные адреса, но не доказывает соответствие публичного IP VPS: окончательную HTTP-проверку выполняет Let’s Encrypt. DNS и firewall провайдера скрипт не изменяет.

Сертификат и приватный ключ Certbot хранятся в `/etc/letsencrypt/live/openviya-panel/`. Проверенная полная цепочка копируется в `/etc/x-ui/tls/panel.crt`, ключ — в `/etc/x-ui/tls/panel.key` (root:root, 0600, каталог 0700). Пути в 3x-ui остаются прежними. Самоподписанный сертификат больше не создаётся. Проверяются системное доверие CA, имя домена, срок и совпадение ключа.

Порт панели по умолчанию **38471**; другой задаётся через `--panel-port`. Он сохраняется в состоянии установки и выводится в `status`. SSH, VPN, панель, ACME TCP 80 и подписки 2096 не должны конфликтовать. Установленный ранее порт, например 2053, при переходе на Let’s Encrypt **сохраняется**:

```bash
sudo grep '^PANEL_PORT=' /var/lib/openviya-vps-bootstrap/config
```

## Переход уже установленной панели на Let’s Encrypt

Этот путь предназначен для установки, созданной предыдущей версией **этого** скрипта. Сначала настройте DNS и TCP 80 по разделу выше. Логин, пароль, inbound, путь панели, сохранённый порт и режим доступа не меняются. Смена сертификата перезапускает панель вместе с Xray и может кратковременно прервать VPN-соединения.

На компьютере, PowerShell из корня проекта:

```powershell
scp -i "$env:USERPROFILE/.ssh/vpsadmin" ./ops/vps/bootstrap-vps.sh vpsadmin@SERVER_IP:bootstrap-vps.sh
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" vpsadmin@SERVER_IP
```

На VPS замените поддомен и email своими значениями:

```bash
sudo cp -a /usr/local/sbin/openviya-vps /usr/local/sbin/openviya-vps.before-letsencrypt
sudo install -o root -g root -m 700 ~/bootstrap-vps.sh /usr/local/sbin/openviya-vps
sudo /usr/local/sbin/openviya-vps setup-letsencrypt \
  --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL
sudo /usr/local/sbin/openviya-vps status
sudo certbot renew --cert-name openviya-panel --dry-run
```

Если установка ещё `pending`, сначала продлите таймер `sudo systemctl restart openviya-access-rollback.timer`, выполните переход и затем обычный `finalize` из нового SSH-сеанса. Для `rolled-back` требуется восстановление доступа по разделу ниже.

При ошибке выпуска действующие файлы панели и сохранённая конфигурация остаются прежними; правило 80/tcp остаётся разрешённым для повторной попытки. При ошибке перезапуска или проверки HTTPS восстанавливается прежняя пара сертификат/ключ. Порт панели из закрытого в публичный эта команда не переводит. Для режима `tunnel` используйте доменное имя через hosts, как описано ниже.

## Подготовка свежего VPS

### Машинный интерфейс для OpenViya Ops

Для Desktop-приложения скрипт предоставляет JSON-команды, не зависящие от интерактивного вывода `status`. Они рассчитаны на свежий VPS и используют те же `prepare`/`finalize`, проверки SSH и таймер отката. Ручной сценарий выше остаётся доступен.

Сначала прочитайте систему без изменений (эта команда работает до установки Python и не создаёт каталоги, не сбрасывает UFW и не меняет SSH):

```bash
sudo /var/lib/openviya-desktop/GUIDN/bootstrap.sh desktop-inspect --admin vpsadmin --ssh-port 22
```

Ответ содержит `schemaVersion`, `os`, `version`, `architecture`, `supported`, `fresh`, `phase`, `rollbackActive` и безопасное `message`. `fresh=true` выдаётся только если ОС поддерживается и проверки не нашли существующего состояния, пользователя, панели, активного/настроенного firewall, Docker, `SSH Match` или слушателя на указанном SSH-порту. Существующая фаза `pending`, `complete` или `rolled-back` возвращается для диагностики и не считается свежей.

Приложение устанавливает копию скрипта с правами root:root 0700 в `/var/lib/openviya-desktop/GUIDN/bootstrap.sh` (каталог GUIDN — root:root 0700) до запуска машины. `GUIDN` — 32 шестнадцатеричных символа UUID; интерфейс также принимает UUID с дефисами. Файл ключа `admin.pub` в том же каталоге должен быть root:root 0600. После подтверждения свежего сервера:

```bash
sudo /var/lib/openviya-desktop/GUIDN/bootstrap.sh desktop-prepare --run-id GUIDN \
  --admin vpsadmin --ssh-port 22 --key-file /var/lib/openviya-desktop/GUIDN/admin.pub \
  --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL \
  --panel-access tunnel --panel-port 38471 --vpn-tcp 443 --vpn-udp ''
```

Команда ставит detached systemd job `openviya-desktop-<GUIDN>-prepare` и сразу возвращает JSON-состояние. Работа переживает отключение SSH-клиента. `desktop-job-status --run-id GUIDN --stage prepare` возвращает только whitelist-сообщение, фазу, состояние `running`/`succeeded`/`failed`/`missing`, exit code и состояние таймера; журналы systemd в машинный ответ не попадают. Повторная отправка того же UUID не создаёт второй job. Скрипт не возобновляет подготовку после перезагрузки автоматически. Не публикуйте журналы systemd: в них могут быть системные пути и вывод подготовки.

Как и в ручном сценарии, оставьте исходное SSH-подключение открытым. Новый вход под созданным администратором должен пройти SSH-проверку. В новом сеансе:

```bash
sudo --preserve-env=SSH_CONNECTION /var/lib/openviya-desktop/GUIDN/bootstrap.sh desktop-finalize --run-id GUIDN
```

Перед созданием detached finalize job команда проверяет `SUDO_USER`, отличие `SSH_CONNECTION` от соединения prepare, SSH-порт, фазу `pending` и активный rollback timer. Внутри job запускается существующий finalize, который повторно выполняет SSH-проверку и отключает таймер только после успешной проверки панели. `desktop-credentials` возвращает JSON с логином, паролем, путём, доменом, портом, режимом доступа и TLS только для root при фазе `complete`. Этот JSON содержит секрет панели; не включайте его в обычные логи или историю команд.

Не запускайте `prepare` на сервере с существующими приложениями, контейнерами, панелью или собственными правилами UFW. Скрипт создаёт пользователя `vpsadmin`; если он уже существует, требуется другой `--admin`.

На компьютере, PowerShell из корня проекта. Если ключ уже есть, пропустите `ssh-keygen`; существующий приватный ключ не перезаписывайте:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE/.ssh" | Out-Null
ssh-keygen -t ed25519 -a 64 -f "$env:USERPROFILE/.ssh/vpsadmin"
scp ./ops/vps/bootstrap-vps.sh root@SERVER_IP:/root/bootstrap-vps.sh
scp "$env:USERPROFILE/.ssh/vpsadmin.pub" root@SERVER_IP:/root/vpsadmin.pub
ssh root@SERVER_IP
```

Сверьте fingerprint SSH-сервера с данными провайдера. Приватный ключ остаётся на компьютере, используйте passphrase. Для нестандартного SSH-порта у `ssh` применяется `-p PORT`, у `scp` — `-P PORT`, а у скрипта `--ssh-port PORT`: скрипт не меняет текущий SSH-порт.

### Вариант 1: панель через SSH-туннель

На VPS:

```bash
ssh-keygen -lf /root/vpsadmin.pub
bash /root/bootstrap-vps.sh prepare \
  --admin vpsadmin --key-file /root/vpsadmin.pub \
  --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL \
  --panel-access tunnel --panel-port 38471 --vpn-tcp 443 --vpn-udp ''
```

Домен и публичный TCP 80 нужны даже в этом режиме. Сама панель остаётся на loopback; выдача сертификата не открывает её порт.

### Вариант 2: публичная панель и SSH-туннель

```bash
bash /root/bootstrap-vps.sh prepare \
  --admin vpsadmin --key-file /root/vpsadmin.pub \
  --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL \
  --panel-access both --panel-port 38471 \
  --panel-cidr YOUR_REAL_IPV4/32 --vpn-tcp 443 --vpn-udp ''
```

`--panel-cidr` ограничивает панель одной административной сетью; `any` разрешает все источники. Для IPv6 используйте /128 или административную подсеть. Этот CIDR **не ограничивает HTTP-01**: порт 80 должен быть доступен валидаторам Let’s Encrypt. Для доступа бэкенда и администратора с разных сетей добавьте необходимые правила UFW и firewall провайдера отдельно либо используйте частную сеть.

Оба варианта поддерживают `--ssh-cidr YOUR_REAL_IPV4/32`. CIDR должен включать адрес, который сервер видит как источник SSH. Для динамического IP можно оставить `any`.

### Проверка нового входа и finalize

**Не закрывайте первый SSH-сеанс.** В течение 20 минут подключитесь из нового окна PowerShell:

```powershell
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" -o IdentitiesOnly=yes vpsadmin@SERVER_IP
```

В новом сеансе:

```bash
sudo --preserve-env=SSH_CONNECTION /usr/local/sbin/openviya-vps finalize
sudo /usr/local/sbin/openviya-vps status
sudo cat /var/lib/openviya-vps-bootstrap/credentials
```

Дождитесь `Complete`. Файл credentials содержит логин, пароль и `PANEL_PATH`; его не публикуйте. `finalize` требует новое соединение от созданного пользователя и снова даёт 20 минут на установку. Проверка SSH_CONNECTION защищает от случайного запуска в старом сеансе; это не граница безопасности против root.

### Открытие панели и несколько серверов

В режиме `both` при `PANEL_PATH=/abc/` используйте `https://panel-usa-1.openviya.online:38471/abc/`. Домен в сертификате совпадает с доменом URL; импортировать сертификат в Windows не нужно.

Для SSH-туннеля **сохраните доменное имя в URL**. На компьютере откройте от администратора файл `C:\Windows\System32\drivers\etc\hosts` и добавьте строки:

```text
127.0.0.1 panel-usa-1.openviya.online
127.0.0.1 panel-ca-1.openviya.online
```

Это локальное переопределение только для **имён панелей** на компьютере администратора; публичный DNS продолжает указывать на VPS. VPN-имена `usa-1.openviya.online` и `ca-1.openviya.online` в hosts не добавляйте: клиенты должны разрешать их в публичные IP VPS. Изменение hosts также направляет другие обращения компьютера к именам панелей на loopback. SSH подключайте по **SERVER_IP**.

На компьютере оставьте работающими отдельные окна PowerShell:

```powershell
# США: локальный порт 20530
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes -N -L 127.0.0.1:20530:127.0.0.1:38471 vpsadmin@USA_SERVER_IP
# Канада: локальный порт 20531
ssh -i "$env:USERPROFILE/.ssh/vpsadmin" -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes -N -L 127.0.0.1:20531:127.0.0.1:38471 vpsadmin@CANADA_SERVER_IP
```

Открывайте `https://panel-usa-1.openviya.online:20530/ПУТЬ_ПАНЕЛИ/` и `https://panel-ca-1.openviya.online:20531/ПУТЬ_ПАНЕЛИ/`. Сертификат не привязан к порту. Старый адрес `https://127.0.0.1:20531` **не совпадает** с именем сертификата Let’s Encrypt. Если старый сервер использует 2053, замените только правый порт в `-L` на 2053.

Получить данные панели и сертификата на VPS:

```bash
sudo cat /var/lib/openviya-vps-bootstrap/credentials
sudo grep -E '^(PANEL_DOMAIN|PANEL_PORT|PANEL_ACCESS)=' /var/lib/openviya-vps-bootstrap/config
sudo openssl x509 -noout -issuer -subject -dates -ext subjectAltName -in /etc/x-ui/tls/panel.crt
sudo certbot certificates
```

Для диагностической копии публичного сертификата:

```bash
sudo install -o vpsadmin -g vpsadmin -m 644 /etc/x-ui/tls/panel.crt /home/vpsadmin/panel.crt
```

На компьютере: `scp -i "$env:USERPROFILE/.ssh/vpsadmin" vpsadmin@SERVER_IP:panel.crt .`. Приватный `panel.key` не скачивайте для настройки браузера.

## Автоматическое продление

Штатный `certbot.timer` периодически запускает `certbot renew`. После успешного продления hook `/etc/letsencrypt/renewal-hooks/deploy/50-openviya-panel` проверяет именно lineage `openviya-panel`, валидирует цепочку/домен/ключ, копирует пару в каталог панели и перезапускает x-ui. Если файлы не изменились, перезапуска нет. После rollback hook не запускает панель.

Перезапуск x-ui затрагивает также Xray: возможен краткий разрыв клиентских соединений. При ошибке применения новая копия остаётся у Certbot; прежняя рабочая пара восстанавливается в каталоге панели. Drop-in `certbot.service.d/90-openviya-deploy.conf` повторяет применение после следующего успешного запуска службы, даже когда выпуск ещё не требуется. Для немедленной повторной попытки после устранения причины используйте `renew-cert`.

```bash
sudo systemctl status certbot.timer --no-pager
sudo systemctl list-timers certbot.timer
sudo certbot renew --cert-name openviya-panel --dry-run
sudo /usr/local/sbin/openviya-vps renew-cert
sudo journalctl -u certbot.service -n 80 --no-pager
```

`--dry-run` проверяет выпуск через тестовый CA и обычно не исполняет deploy hook. Проверка реального применения выполнена скриптом при установке/переходе. Не используйте `--force-renewal` для обычных проверок: у CA есть лимиты. При ошибках таймера или продления нужны внешние оповещения; локальный журнал не заменяет мониторинг. Не отключайте проверку TLS через `-k`.

## Что выполняется и что проверить после установки

1. Проверяются параметры и ОС, устанавливаются инструменты и обновления без автоматического reboot. Новый sudo-пользователь получает один публичный Ed25519-ключ; пароль аккаунта заблокирован, sudo NOPASSWD.
2. Исходный SSH-конфиг сохраняется. Таймер на 20 минут восстанавливает доступ, если установка не завершена. До finalize прежний root/password-вход сохраняется; новый пользователь сразу обязан входить по ключу.
3. UFW разрешает явно выбранные порты, запрещает остальные входящие/маршрутизируемые соединения; исходящие разрешены. IPv6 не отключается. Чужие правила UFW вызывают отказ до сброса.
4. Finalize запрещает root/password/keyboard-interactive, X11 и agent forwarding; разрешает только local TCP forwarding к панели. Проверяются sshd -t и эффективные настройки.
5. Fail2ban защищает SSH: пять ошибок за 10 минут, бан на час, текущий IP администратора исключён. Включаются security updates ОС, ограничиваются journald и небезопасные сетевые redirects/source routing. Маски двух штатных APT-служб снимаются, отсутствующие units восстанавливаются.
6. Выпускается Let’s Encrypt, устанавливается закреплённый 3x-ui, создаются случайные логин, пароль и URL-путь. HTTPS настраивается до запуска; подписки отключаются; настройки БД проверяются.
7. x-ui/Xray работают от root с NoNewPrivileges, PrivateTmp, ProtectHome и ProtectSystem=full. Запись разрешена в /etc/x-ui и /usr/local/x-ui/bin; основной бинарник и остальная /usr read-only.
8. Ежедневно около 03:30–04:00 UTC создаётся согласованная SQLite backup с хранением около 14 дней. HTTPS, listener, firewall, службы и Xray через авторизованный API проверяются до отмены таймера возврата доступа.

После входа включите 2FA, проверьте время `timedatectl status` и сохраните резервный доступ. Создайте выбранный VPN inbound и отдельного тестового клиента: firewall разрешает TCP 443, но сам inbound скрипт не создаёт. Для VLESS + REALITY нужны собственные параметры и ключи; сертификат панели к нему не относится.

Сохраните штатные запреты `geoip:private` и `freedom.settings.finalRules`: они ограничивают доступ VPN-клиентов к панели, RFC1918/link-local/metadata-адресам. Проверьте отказ реальным клиентом. Подписки выключены; публичную выдачу подписок проектируйте отдельно от административного API. Для бэкенда на другом VPS предпочтительны частная сеть и отдельный сервисный токен.

## Проверка и восстановление

```bash
sudo sshd -t
sudo ufw status verbose
sudo ss -lntup
sudo fail2ban-client status sshd
sudo systemctl status x-ui --no-pager
sudo journalctl -u x-ui -n 50 --no-pager
sudo systemctl list-timers openviya-xui-backup.timer certbot.timer
sudo unattended-upgrade --dry-run --debug
```

Проверьте с другого компьютера IPv4/IPv6, SSH-вход ключом и отказ root/password, доступность панели согласно режиму/CIDR и реальную работу VPN. Порт подписок 2096 закрыт. Reboot после обновления ОС выполняйте вручную после проверки нового входа и консоли провайдера.

Если установка не завершилась, активный таймер восстановит исходный SSH-конфиг, отключит UFW и остановит панель. Это возврат доступа: пакеты, пользователи и данные сохраняются. Ручной возврат:

```bash
sudo /usr/local/sbin/openviya-vps rollback
```

В состоянии `pending` повторяйте finalize из проверенного нового SSH-сеанса, пока таймер активен. После `rolled-back` автоматический prepare запрещён; используйте консоль либо пересоздайте ещё пустой VPS. После `complete` не используйте provisioning для обновления. Certbot может продолжить обновлять свою копию сертификата после rollback, но hook откажется запускать панель.

Копии `/var/backups/3x-ui` содержат БД, SSL-ключи, конфигурацию Certbot **с ключом ACME-аккаунта**, версию и часть конфигурации хоста. Настройте зашифрованное копирование вне VPS и пробное восстановление. Локальный backup не защищает от удаления VPS или компрометации root.

БД восстанавливайте при остановленной панели: сохраните текущие данные, восстановите согласованную SQLite и SSL-пару, обеспечьте root:root и 0600 для БД/ключа, уберите старые -wal/-shm только у остановленной базы. После восстановления Certbot восстановите его каталог с правами/ссылками и проверьте renewal dry-run. Сначала отработайте восстановление на отдельном сервере.

Обновления 3x-ui/Xray не автоматизированы. Нужны release notes, backup, новый SHA-256 и проверка перед обновлением. Upstream installer может заменить systemd unit этой установки. Управление — через systemctl; бинарник панели `/usr/local/x-ui/x-ui`, меню `/usr/bin/x-ui` не устанавливается.

## Ошибки Let’s Encrypt

- `unauthorized`, timeout или connection refused: проверьте A/AAAA, TCP 80 в UFW и firewall провайдера, прямой доступ без CDN; CAA должна разрешать Let’s Encrypt.
- `TCP 80 is occupied`: посмотрите `sudo ss -ltnp 'sport = :80'`; устраните конфликт. Скрипт не останавливает службу автоматически.
- Выпуск не завершился: читайте `sudo tail -n 80 /var/log/letsencrypt/letsencrypt.log`. При pending держите SSH открытым и следите за таймером возврата доступа.
- Браузер пишет о несовпадении имени: открывайте **домен**, указанный в сертификате. Для туннеля используйте локальную запись hosts и доменный URL.
- `Legacy self-signed installation`: выполните setup-letsencrypt из раздела перехода. Самоподписанный сертификат автоматически не заменяется при простом копировании скрипта.

## Устранение ошибок

Команды ниже выполняются **на VPS**, кроме явно отмеченных команд PowerShell. Исходный SSH-сеанс держите открытым до успешной проверки нового входа и завершения установки.

### Нет файла публичного SSH-ключа

Ошибка `Provide --key-file with one PUBLIC Ed25519 key` на ранней проверке означает, что переданный путь не указывает на обычный файл либо является символической ссылкой. Загрузите `.pub` с компьютера:

```powershell
scp "$env:USERPROFILE/.ssh/vpsadmin.pub" root@SERVER_IP:/root/vpsadmin.pub
```

На VPS проверьте файл и повторите подготовку:

```bash
ssh-keygen -lf /root/vpsadmin.pub
bash /root/bootstrap-vps.sh prepare --key-file /root/vpsadmin.pub --panel-access tunnel --domain panel-usa-1.openviya.online --acme-email YOUR_EMAIL
```

Приватный файл `vpsadmin` остаётся на компьютере. Если нужен режим `both` или нестандартные порты, повторите `prepare` с выбранными ранее параметрами.

### Missing privilege separation directory: /run/sshd

OpenSSH требует временный каталог `/run/sshd` даже для проверки `sshd -t`. На свежем образе или после перезапуска службы при обновлении пакетов каталог может отсутствовать. Текущий скрипт создаёт его перед каждой проверкой с владельцем root:root и правами `0755`.

Для продолжения после ошибки раннего варианта выполните на VPS:

```bash
sudo install -d -o root -g root -m 755 /run/sshd
sudo /usr/sbin/sshd -t
```

При успешной проверке `sshd -t` ничего не выводит. Если ошибка возникла во время `prepare` сразу после установки пакетов, до создания пользователя и состояния `pending`, повторите исходную команду `prepare` с теми же параметрами. Если установка уже имеет состояние `pending`, продолжайте по разделу ниже про повторный `finalize`; существующее состояние не удаляйте. Для новых серверов используйте текущий скрипт, поскольку содержимое `/run` временное.

### Пустой UFW принят за существующие правила

Ранний вариант скрипта ошибочно выдавал `Existing UFW rules refused`, когда `ufw show added` выводил `(None)`. Исправленная версия принимает пустой список. Проверьте:

```bash
LC_ALL=C ufw show added
```

Если показано `(None)`, загрузите текущий скрипт из PowerShell и повторите `prepare` с выбранными параметрами:

```powershell
scp ./ops/vps/bootstrap-vps.sh root@SERVER_IP:/root/bootstrap-vps.sh
```

Эта ошибка возникает до создания пользователя и изменения firewall. Если вывод содержит реальные правила, сначала разберите их назначение; вручную сбрасывать UFW для обхода проверки не нужно.

### Ошибка finalize и таймер возврата доступа

Перед повторной попыткой проверьте состояние:

```bash
sudo cat /var/lib/openviya-vps-bootstrap/phase
sudo systemctl list-timers --all openviya-access-rollback.timer
```

Если состояние `pending` и новый вход по ключу работает, продлите окно на 20 минут:

```bash
sudo systemctl restart openviya-access-rollback.timer
```

После устранения причины повторите **из нового сеанса пользователя `vpsadmin`**:

```bash
sudo --preserve-env=SSH_CONNECTION /usr/local/sbin/openviya-vps finalize
```

При `rolled-back` сначала разберите сохранённые изменения через консоль провайдера. При `complete` установка завершена: используйте команды обслуживания, а не повторный `prepare`/`finalize`.

### APT timers: служба не загружена или masked

Если `apt-daily.timer` сообщает `unit apt-daily.service to trigger not loaded`, проверьте **сами службы**:

```bash
sudo systemctl show apt-daily.service apt-daily-upgrade.service \
  -p Id -p LoadState -p LoadError -p FragmentPath -p UnitFileState
sudo systemctl cat apt-daily.service apt-daily-upgrade.service
```

Текущая версия скрипта автоматически снимает постоянные и временные маски только с этих двух штатных служб, затем повторно проверяет загрузку. Для уже загруженной на VPS ранней версии при `LoadState=masked` выполните:

```bash
sudo systemctl unmask apt-daily.service apt-daily-upgrade.service
sudo systemctl unmask --runtime apt-daily.service apt-daily-upgrade.service
sudo systemctl daemon-reload
sudo systemctl enable --now apt-daily.timer apt-daily-upgrade.timer
sudo systemctl list-timers --all apt-daily.timer apt-daily-upgrade.timer
```

У таймеров должно появиться время следующего запуска. Переустановка пакета `apt` сама по себе не убирает маски в `/etc/systemd/system`.

Если состояние `not-found`, восстановите штатные файлы пакета и перечитайте конфигурацию:

```bash
sudo apt-get -o Dpkg::Options::=--force-confold install --reinstall -y apt
sudo systemctl daemon-reload
sudo systemctl enable --now apt-daily.timer apt-daily-upgrade.timer
```

Текущий скрипт выполняет восстановление `not-found` автоматически. Для иных ошибок получите журнал:

```bash
sudo journalctl -b -u apt-daily.timer -u apt-daily-upgrade.timer -n 80 --no-pager
```

После успешного запуска таймеров продолжайте `finalize`, если состояние установки остаётся `pending`.

### Xray: failed to write configuration file, read-only file system

Если ошибка указывает на `/usr/local/x-ui/bin/.config-…tmp`, в старом unit отсутствует разрешение записи для рабочей конфигурации Xray. В текущем скрипте оно уже добавлено. Для сервера, установленного ранним вариантом, сохраните unit и исправьте строку:

```bash
sudo cp -a /etc/systemd/system/x-ui.service /root/x-ui.service.before-write-fix
sudo sed -i 's|^ReadWritePaths=/etc/x-ui$|ReadWritePaths=/etc/x-ui /usr/local/x-ui/bin|' /etc/systemd/system/x-ui.service
sudo systemctl daemon-reload
sudo systemctl restart x-ui
sudo systemctl show x-ui -p ReadWritePaths
```

Ожидаемый вывод:

```text
ReadWritePaths=/etc/x-ui /usr/local/x-ui/bin
```

Через 5–10 секунд обновите страницу панели и проверьте состояние Xray. Перезапуск панели может прервать соединения VPN-клиентов. Если ошибка осталась:

```bash
sudo journalctl -u x-ui -n 50 --no-pager
```

### Сообщения needrestart после обновления пакетов

`User sessions running outdated binaries` и список отложенных перезапусков — сведения об обновлённых пакетах. Они сами по себе не означают ошибку подготовки. Сначала завершите установку и проверьте новый вход по ключу; затем запланируйте перезапуски или reboot с доступной консолью провайдера.

## Изолированные проверки

Из корня проекта:

```powershell
docker build -t openviya-vps-test -f ops/vps/tests/Dockerfile .
docker run --rm --mount "type=bind,source=$($PWD.Path),target=/work,readonly" openviya-vps-test
```

Одноразовый Ubuntu 26.04 проверяет Bash/ShellCheck, порты/домены, настоящий SSH-вход ключом и отказ root, Fail2ban/systemd-конфиги, закреплённый 3x-ui, его БД, CSRF-авторизацию, состояние Xray, HTTPS с проверкой CA/имени и отключённые подписки. В контейнере создаётся только тестовый CA: запросы к Let’s Encrypt и изменения реального DNS не выполняются.

TLS-проверки включают несовпадение имени/ключа, сохранение рабочей пары при ошибках перезапуска и HTTPS, отсутствие перезапуска при неизменном сертификате, неуспешный выпуск, сохранение credentials/порта при переходе, старую конфигурацию без TLS_MODE и запрет запуска панели после rollback. Проверяются backup и возврат исходного SSH-конфига. Службы systemd подменяются в контейнере; реальный выпуск HTTP-01, работа таймера, firewall и сеть IPv6 требуют проверки на VPS. После настройки выполните renewal dry-run. Debian и arm64 отдельно не проверены.

## Источники

- [Let’s Encrypt: HTTP-01 и DNS-01](https://letsencrypt.org/docs/challenge-types/).
- [Certbot: standalone, продление и deploy hooks](https://eff-certbot.readthedocs.io/en/stable/using.html).
- [3x-ui v3.10.2](https://github.com/MHSanaei/3x-ui/releases/tag/v3.10.2), [настройки HTTPS](https://docs.sanaei.dev/docs/config/panel/).
- [Ubuntu: OpenSSH](https://documentation.ubuntu.com/server/how-to/security/openssh-server/), [firewall](https://documentation.ubuntu.com/server/how-to/security/firewalls/), [security updates](https://documentation.ubuntu.com/server/how-to/software/automatic-updates/).
