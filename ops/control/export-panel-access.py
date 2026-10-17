#!/usr/bin/env python3
"""Write panel credentials to permission-restricted files, without printing their values."""
import json
import os
import pathlib
import pwd
import subprocess

os.umask(0o077)
root = pathlib.Path('/opt/openviya-control')
access = json.loads((root / 'private/access.json').read_text())
fingerprint = subprocess.check_output(['openssl', 'x509', '-in', str(root / 'panel-ca.crt'), '-noout', '-fingerprint', '-sha256'], text=True).strip()
text = f'''# Доступ к панели-оркестратору OpenViya

Этот файл содержит пароль. Хранить приватно, не добавлять в Git и не пересылать в чат.

VPS: 31.44.7.93
SSH: openviya@31.44.7.93, порт 22
Авторизация SSH: существующий ключ openviya-vps на компьютере администратора; пароль SSH отсутствует.
Права SSH-пользователя: обычная оболочка, без sudo, Docker и доступа к серверному .env.
Версия панели: 3x-ui v3.10.2
Назначение: только управление отдельными VPN-нодами; локальные VPN-inbound отсутствуют.

## Открыть панель

В PowerShell запустить и оставить работающим:

```powershell
ssh -N -o ExitOnForwardFailure=yes -L 38471:127.0.0.1:38471 openviya@31.44.7.93
```

Затем открыть в браузере:

{ 'https://localhost:38471' + access['panel_path'] }

Логин: {access['panel_username']}
Пароль: {access['panel_password']}

## Сертификат

HTTPS панели использует внутренний CA, поэтому обычный браузер изначально не доверяет ему.
Публичный сертификат CA: файл panel-ca.crt рядом с этой инструкцией; также /home/openviya/panel-ca.crt на VPS.
Отпечаток CA: {fingerprint}
Приватный ключ CA остаётся root-only на сервере. Доверие сертификатам Windows автоматически не изменялось.

## Проверка backend через SSH

```powershell
ssh -N -o ExitOnForwardFailure=yes -L 8080:127.0.0.1:8080 openviya@31.44.7.93
```

Readiness: http://127.0.0.1:8080/actuator/health/readiness
Публичное API после DNS и выпуска сертификата: https://api.openviya.online
OIDC callback: https://api.openviya.online/login/oauth2/code/telegram

Служебный API-токен backend хранится в /opt/openviya-control/.env и в этот файл не включён.
Для входа человека он не нужен. Обслуживание ОС и контейнеров выполняется отдельным входом root по ключу.
'''
target = root / 'private/orchestrator-3x-ui.md'
target.write_text(text)
target.chmod(0o600)
account = pwd.getpwnam('openviya')
for destination, contents in [(pathlib.Path(account.pw_dir)/'PANEL-ACCESS.md', text.encode()),
                              (pathlib.Path(account.pw_dir)/'panel-ca.crt', (root/'panel-ca.crt').read_bytes())]:
    destination.write_bytes(contents)
    destination.chmod(0o600)
    os.chown(destination, account.pw_uid, account.pw_gid)
print('Panel access files saved with mode 600; credentials not printed')
