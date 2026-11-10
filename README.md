# DPT.PSP AdHoc Server

[![build](https://github.com/dpteam/AdHoc/actions/workflows/build.yml/badge.svg)](https://github.com/dpteam/AdHoc/actions/workflows/build.yml)

Сервер ad-hoc сети для PSP-игр (PPSSPP / PRO Online, протокол `SceNetAdhocctl`) на **.NET Framework 4.0**,
совместимый с Windows XP SP3, **без единой сторонней библиотеки**.

Две веб-морды на разных портах, одно общее состояние:

```
adhoc  27312   <-- PSP / PPSSPP (TCP, бинарный протокол)
relay  27313   <-- PPSSPP packet relay (TCP, для NAT hairpin / строгих NAT)
public 44501   <-- публичная доска: read-only, без MAC/IP/логов/модерации (это порт для проброса)
admin  44502   <-- админка для локалки: кик, чат, баны, каталог, лог протокола
```

Публичная и админская доски живут в одном процессе, обновляются из одного `ServerState`,
публичная просто не содержит чувствительных полей (сервер их не отдаёт, а не прячет в CSS).

## Что умеет

**Игровой сервер**
- Полная реализация `SceNetAdhocctl`: `LOGIN`, `CONNECT`, `DISCONNECT`, `SCAN`, `CHAT`, `PING`
  и ответы `CONNECT_BSSID`, `CONNECT`, `DISCONNECT`, `SCAN`, `SCAN_COMPLETE`, `CHAT`.
- Группы (комнаты), хост группы (BSSID) = основатель, уведомления пирам, чат внутри группы.
- Кросс-региональные игры (`crosslink`): EU/JP/US копии попадают в одно лобби.
- База имён: **3900+ product ID** (UMD и PSN-издания), см. `data/games.json`.
- Таймауты сессий, лимиты на IP и на всех, автоочистка пустых игр, дубли MAC.
- Мягкий режим: неизвестный опкод не убивает сессию, а логируется.
- **Ретранслятор** (`aemu_postoffice`, TCP 27313): игровой трафик идёт через сервер —
  лечит NAT hairpin (игра через свой внешний IP), CGNAT и «два игрока за одним роутером».
  Подробности и настройка PPSSPP — [docs/RELAY.md](docs/RELAY.md).

**Публичная доска (`44501`)**
- Игры → группы → ники, кто во что играет, сколько времени в игре, кто хост.
- Статистика, лента событий, недавние выходы с причиной, каталог игр (только просмотр).
- В каталоге видно все 3900+ ID: страницы по 300 строк, «Показать все», поиск по всей базе.
- **Зелёным цветом — игры с подтверждённым Ad Hoc** (1170 ID) и галочка «Только с Ad Hoc».
  Источники: поле `multiplayer` в pspudb, описания магазина, ручной список проверенных
  игр. Нет данных — серый цвет, а не ложный зелёный.
- Темы light/dark/OLED, мягкие звуки, поиск, автообновление.
- **16 языков** (EN/RU/UK/LT/PL/PT-BR/ES/DE/FR/IT/JA/KO/ZH/ID/TR/VI), выбор в верхней
  панели и в настройках; язык берётся из браузера, запоминается в `localStorage`.
  Как добавить свой — [docs/I18N.md](docs/I18N.md).
- **Посещаемость**: сколько человек читает доску сейчас, за сегодня и за всё время,
  мини-график за неделю. Считается по солёному хэшу IP+UA — ни IP, ни cookie не хранятся.
- **Публичный чат** (гости доски ↔ игроки в игре) с капчей на 7-сегментных путях,
  кулдауном и антиспамом; сообщения с доски уходят в игровой чат PPSSPP.
- Никаких MAC, IP, session id, логов и кнопок модерации.
- **Статус для списка серверов PPSSPP**: `/data.json` (и `/status.xml`) — игры, комнаты, ники,
  `game_ids` со всеми региональными алиасами. Формат и готовый JSON для заявки в PPSSPP —
  [docs/SERVER_LIST.md](docs/SERVER_LIST.md).

**Админская доска (`44502`)**
- Всё то же + MAC, IP, idle, трафик, кик, сообщение всем игрокам, баны, правка каталога,
  живой лог протокола (hex + расшифровка пакетов).
- Вход по `admin token` из `config.ini`; токен **не уходит по сети** — браузер выводит из него
  ключ (PBKDF2 + соль) и подписывает каждый запрос, тела и ответы шифруются.
- По умолчанию принимает только приватные/LAN-адреса (`web.adminPrivateOnly`).

**Надёжность**
- Supervisor перезапускает adhoc и оба веб-листенера с экспоненциальной задержкой.
- Fallback-порты для всех трёх сервисов.
- Временные баны IP за ошибки протокола, rate limit HTTP, лимиты тела/заголовков/соединений.
- Логирование через `Trace` в `logs/adhoc-YYYYMMDD.log` (ротация по дню и размеру, ретеншн),
  один listener, без дублирования в консоль, пинги не засоряют файл.
- **Статистика переживает перезапуск**: `data/stats.json` — пик онлайна, суммарное время
  в игре, число сессий и запусков, по каждой игре пик/время/последний запуск и последние
  500 завершённых сессий. Файл пишется атомарно (раз в 15 c при изменениях и при выходе).

## Быстрый старт

```bat
build.cmd
run.cmd
```

- Публичная доска: `http://localhost:44501/` (или `http://<IP>:44501/` для LAN).
- Админка: `http://localhost:44502/` → токен из `bin/config.ini` (`web.adminToken`).
  При первом запуске токен и соль генерируются автоматически и печатаются в консоль.

В PPSSPP: **Сеть → Ad Hoc → Включить**, «Пользовательский сервер» → адрес машины, порт **27312**.
Если играете через свой внешний адрес и лобби «видит, но не коннектит» — включите
**Настройки → Сеть → «Try to use server-provided packet relay» = Да** (см. [docs/RELAY.md](docs/RELAY.md)).

> .NET Framework 4.0+ (на XP SP3 — именно 4.0). Сборка стоковым `csc.exe`, без NuGet/MSBuild.

### Фаервол Windows

Один раз запустите (появится UAC — «Да»):

```bat
tools\firewall.cmd
```

Скрипт читает порты из `bin\config.ini` и создаёт входящие TCP-правила, привязанные к
`AdHocServer.exe`:

| Правило | Порт | Источники |
|---|---|---|
| `DPT.PSP AdHoc` | 27312 | любые (игроки из интернета) |
| `DPT.PSP Relay` | 27313 | любые (игроки из интернета, если играют через релей) |
| `DPT.PSP Public board` | 44501 | LAN/приватные (NPM рядом) |
| `DPT.PSP Admin board` | 44502 | только LAN/приватные |

Опции: `-PublicAny` (публичный порт с любых адресов), `-AdhocLanOnly` (adhoc только LAN),
`-Remove` (удалить правила). Скрипт печатает локальные IPv4, состояние сокетов и проверку
`/api/health` — удобно сразу увидеть, что и куда прокидывать в NPM.

## Reverse proxy (Nginx Proxy Manager / nginx / NAT hairpin)

Пробрасывайте наружу **только** публичный порт `44501`; админский `44502` держите в LAN.

- Все клиенты придут с одного адреса (NPM/роутер). Сервер умеет `X-Forwarded-For` и `X-Real-IP`,
  но доверяет им только от приватных/loopback-пиров (`web.trustedProxies`), а из цепочки берёт
  первый недоверенный адрес. Rate limit считается по реальному клиенту.
- Поддомен (`adhoc.example.com`) и под-путь (`example.com/adhoc/`) поддерживаются:
  ссылки в UI относительные, `X-Forwarded-Prefix` и `web.publicBasePath` срезаются.
- `GET /api/health` — для health-check.
- `netsh http add urlacl` не нужен: HTTP-сервер свой (TcpListener), достаточно разрешить
  `AdHocServer.exe` в фаерволе Windows.
- TLS терминирует NPM. Подробности и чек-лист — [docs/SECURITY.md](docs/SECURITY.md).

## Безопасность (кратко)

| | Public | Admin |
|---|---|---|
| Данные | ники, игры, группы, время | + MAC, IP, id, трафик, логи, баны |
| Запись | нет | да, только с подписью |
| Доступ | всем | LAN + `admin token` |

Админский API: `K = PBKDF2-HMAC-SHA256(token, salt, 12000)`, подпись
`HMAC-SHA256(K, method|target|ts|nonce|sha256(body))`, анти-replay по nonce (±180 c),
шифрование тел HMAC-CTR + encrypt-then-MAC, сравнение подписей без утечек по времени,
троттлинг неудачных попыток, CSP/XFO/nosniff на всех ответах.
Это прикладная защита, **не замена TLS** — публичную доску всё равно публикуйте через HTTPS.
Полное описание и границы: [docs/SECURITY.md](docs/SECURITY.md).

Отдельно про ретранслятор: он пересылает **открытый** игровой трафик (как оригинальный
`aemu_postoffice`), порт 27313 открыт для любых адресов, аутентификация — по MAC из
adhoc-логина. Это осознанный компромисс ради совместимости с PPSSPP; подробности и лимиты —
[docs/RELAY.md](docs/RELAY.md).

## Настройки (`config.ini`)

Файл создаётся рядом с `AdHocServer.exe` при первом запуске (пример — `config.example.ini`).

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `server.adhocPort` / `adhocPortFallbacks` | `27312` / `27313,27314` | игровой протокол |
| `server.maxUsers` / `maxUsersPerIp` | `256` / `16` | лимиты |
| `server.userTimeoutSec` | `25` | простой до разрыва (PPSSPP пингует ~2 c) |
| `server.sessionMaxHours` | `12` | жёсткий лимит жизни сессии, `0` = нет |
| `server.emptyGameTtlMin` | `15` | сколько пустая игра висит на доске |
| `server.ignoreUnknownOpcodes` | `true` | мягкий режим протокола |
| `server.kickDuplicateMac` | `false` | кикать старую сессию при дубле MAC |
| `relay.enabled` / `relay.port` | `true` / `27313` | ретранслятор игрового трафика |
| `relay.maxSessions` / `maxPerIp` | `512` / `128` | лимиты релея (у игрока несколько сокетов) |
| `relay.idleSeconds` / `lifetimeSeconds` | `600` / `43200` | таймауты релея |
| `web.publicPort` / `publicPortFallbacks` | `44501` / `44503,44505` | публичная доска |
| `web.adminPort` / `adminPortFallbacks` | `44502` / `44504,44506` | админка |
| `web.publicBind` / `adminBind` | `0.0.0.0` | `127.0.0.1` — только локально |
| `web.adminPrivateOnly` | `true` | админка только для приватных адресов |
| `web.publicBasePath` / `adminBasePath` | пусто | под-путь за reverse proxy |
| `web.trustProxyHeaders` / `trustedProxies` | `true` / приватные | доверие `X-Forwarded-*` |
| `web.adminToken` / `adminSalt` | генерируются | доступ в админку (KDF-соль) |
| `web.adminKdfIterations` | `12000` | PBKDF2-итерации |
| `web.adminEncrypt` | `true` | шифровать тела админского API |
| `web.exposeLogs` | `true` | отдавать лог (только админский порт) |
| `logging.level` / `protocolTrace` | `Info` / `true` | подробность лога |
| `limits.*` | — | баны, размеры истории/событий/буфера |

После правки `config.ini` нужен перезапуск.

## Веб-интерфейс

- **Лобби** — карточки игр, клик раскрывает группы: хост с короной, игроки, время, idle.
- **Поиск** — по названию, product ID, нику, MAC или IP (MAC/IP только в админке).
- **События** — вход/выход/группы/чат/ошибки; «оповещать» выключает звуки и тосты.
- **Протокол** (админ) — живой хвост лога, уровень, пауза, hex + расшифровка пакетов.
- **Каталог игр** — таблица ID ↔ название и crosslink'и; правка только в админке.
- **Настройки** — тема, звук/громкость/события, период опроса; в админке ещё чат и выход.
- **Справка** — настройка PPSSPP, протокол, горячие клавиши (`R`, `/`, `Esc`).

## API

Публично: `/api/status`, `/api/games`, `/api/history`, `/api/health`, `/status.xml`.
Админ: то же без вычистки + `/api/auth`, `/api/logs`, `/api/bans`, `/api/bans/clear`,
`/api/kick`, `/api/chat`, `/api/reload` (с подписью и шифрованием).
Схема подписи, примеры и коды ответов — [docs/API.md](docs/API.md).

## Логи

```
logs/adhoc-20261102.log          # текущий
logs/adhoc-20261102.001.log      # после 8 MB
```

Формат: `timestamp [level] category message`. Категории `APP`, `NET`, `PROTO`, `WEB`, `DB`,
`CONFIG`, `LOG`, `UI`. В `PROTO` — hex и расшифровка пакетов, причина закрытия сессии,
длительность и трафик. Пинги — только на уровне `Verbose`.

## Устройство проекта

```
build.cmd / run.cmd                сборка стоковым csc.exe / запуск
src/AdHocServer/
  Program.cs                       bootstrap, аргументы, graceful shutdown
  AppSupervisor.cs                 watchdog (adhoc + public + admin)
  ConsoleUi.cs                     консоль: статус + команды
  Core/Config.cs                   config.ini, генерация токена/соли, нормализация
  Core/ApiCrypto.cs                PBKDF2 / HMAC / HMAC-CTR конверт / nonce-кэш
  Core/Log.cs                      Trace-listener, ротация, ring-буфер для UI
  Core/Json.cs                     свой JSON (парсер + writer)
  Core/GameDatabase.cs             product ID ↔ игра, crosslinks, data/games.json
  Core/ServerState.cs              игры/группы/сессии/история/события
  Core/StatsStore.cs               data/stats.json: пик/время/сессии, переживает перезапуск
  Core/VisitStore.cs               data/visits.json: посещаемость доски (без IP)
  Core/ChatStore.cs                чат доски: капча, кулдаун, история в data/chat.json
  Core/Captcha.cs                  SVG-капча на 7-сегментных путях
  Core/Util.cs                     MAC/IP/hex/время/строки
  Net/Protocol.cs                  опкоды и сборка пакетов S2C
  Net/ClientSession.cs             TCP-сессия, буфер, send-lock
  Net/AdhocServer.cs               протокол, группы, чат, баны, таймауты
  Net/RelayServer.cs               aemu_postoffice relay (TCP 27313)
  Core/LocalNetwork.cs             адреса/шлюзы (детект NAT hairpin)
  Web/HttpCore.cs                  HTTP/1.1: запрос, ответ, keep-alive, chunked
  Web/WebServer.cs                 листенер, роль, прокси, статика, gzip, auth
  Web/WebApi.cs                    публичный (redacted) и админский API
public/index.html                  публичная доска (data-role="public")
public/admin.html                  админка (data-role="admin") + экран входа
public/assets/js/{ui,sound,app}.js общий UI
public/assets/js/i18n.js        16 языков, автодетект, селектор
public/assets/js/crypto.js         SHA-256/HMAC/PBKDF2/CTR на ES5
public/assets/js/auth.js           клиент админского API (подпись + шифрование)
data/games.json                    база названий (создаётся/правится автоматически)
data/adhoc.json                    какие ID точно поддерживают Ad Hoc (tools/build_adhoc.py)
data/stats.json                    статистика за всё время (создаётся автоматически, не в git)
data/visits.json                   посещаемость доски (счётчики, не в git)
data/chat.json                     последние сообщения чата (не в git)
tools/test-client.ps1              тест протокола
tools/test-relay.ps1               тест релея (PDP + PTP handshake, байты в обе стороны)
tools/hold-relay.ps1               «держатель» PDP-сессии релея (ручные проверки)
tools/build_games_db.py            пересборка data/games.json из pspudb
tools/build_adhoc.py               пересборка data/adhoc.json (какие игры точно с Ad Hoc)
tools/test-web.ps1                 тест обеих досок (вычистка, подпись, replay, шифрование)
tools/ci-test.ps1                  всё сразу: собрать, поднять сервер, прогнать три набора
tools/admin-api.ps1                клиент админского API для PowerShell
tools/firewall.cmd / .ps1          правила Windows Firewall для трёх портов
tools/hold-client.ps1              «держатель» сессии для ручных проверок
docs/PROTOCOL.md                   байтовый формат пакетов
docs/ARCHITECTURE.md               потоки, блокировки, отказоустойчивость
docs/API.md                        HTTP API
docs/I18N.md                       языки интерфейса, как добавить свой
docs/RELAY.md                      ретранслятор: зачем, как включить, протокол
docs/SERVER_LIST.md                /data.json и заявка в список серверов PPSSPP
docs/SECURITY.md                   модель угроз, подпись/шифрование, reverse proxy
.github/workflows/build.yml        CI: сборка + тесты + артефакт (релиз по тегу v*)
```

## Сборка и проверка

```bat
build.cmd
powershell -File tools\test-client.ps1
powershell -File tools\test-relay.ps1
powershell -File tools\test-web.ps1
```

Всё одной командой (то же, что делает CI — собирает, поднимает сервер, гоняет тесты,
гасит сервер и возвращает код выхода):

```bat
powershell -File tools\ci-test.ps1
```

`build.cmd` собирает `bin\AdHocServer.exe`, копирует `public/` и `config.example.ini`.
Тест API сам поднимает «держателей» сессий и восстанавливает `data/games.json` после себя.
Проект открывается в Visual Studio 2010+ (`src/AdHocServer/AdHocServer.csproj`).

База игр пересобирается скриптом `tools/build_games_db.py` (нужен дамп
[pspudb](https://github.com/georgemoralis/pspudb) — `pspudb/data/database.db`):

```bash
python tools/build_games_db.py --pspudb database.db --games data/games.json --out data/games.json
```

> Правя фронтенд, запустите `build.cmd` (или скопируйте `public/` в `bin/public/`) и поднимите
> `?v=` у CSS/JS в HTML, иначе браузер отдаст кеш.

## CI и релизы

`.github/workflows/build.yml` — GitHub Actions, `windows-latest`:

1. проверяет наличие `csc.exe` (.NET Framework 4.x) и печатает версию;
2. `tools/ci-test.ps1`: сборка → старт сервера → протокол + релей + веб → стоп;
3. пакует `bin\*` в `AdHocServer-win.zip` (без `config.ini` и логов) и кладёт в артефакты;
4. на теге `v*` — создаёт GitHub Release и прикладывает zip туда же.

Локально то же самое: `powershell -File tools\ci-test.ps1`. Артефакт самодостаточен:
распаковать, запустить `AdHocServer.exe` — `config.ini` с новым токеном создастся при первом старте.

## Совместимость

- Windows XP SP3 + .NET Framework 4.0 — целевая среда (только API .NET 4.0, C# 4).
- Windows 7/10/11 — работает и на .NET 4.8 (тот же CLR 4.0).
- Linux/macOS через Mono ≥ 4.x — теоретически да, не тестировалось.
- Браузер: любой современный; вёрстка на CSS-переменных, ES5-скрипты, CSP без inline-скриптов.

## Решение проблем

| Симптом | Что делать |
|---|---|
| `port … is busy` в логе | порт занят, сервер уже взял fallback-порт — смотрите лог/консоль |
| Публичная доска не открывается по IP | фаервол Windows: `tools\firewall.cmd`; в NPM указывайте LAN-IP машины, а не `127.0.0.1` (если NPM в Docker — это контейнер) |
| «нужен admin token» / 401 | токен из `config.ini` → `web.adminToken`; проверьте часы (окно ±180 с) |
| 403 на админке | вы не в приватной сети (`web.adminPrivateOnly`) |
| PPSSPP не видит лобби | порт 27312, IP сервера, игра запущена; в логе будет `LOGIN` |
| Играю через свой внешний IP, лобби видит, но не коннектит | NAT hairpin: включите в PPSSPP «Try to use server-provided packet relay» (порт 27313), см. [docs/RELAY.md](docs/RELAY.md) |
| Игроки одной игры не видят друг друга | разные региональные ID → добавьте crosslink в каталоге; **или смешанный транспорт** (один через relay, другой напрямую) — включите relay у всех, см. [docs/RELAY.md](docs/RELAY.md) |
| Игра висит на «Доступ в сеть…» / «Соединение…» | почти всегда смешанный транспорт: один игрок с релеем, другой без. В логе будет `PDP frame (dst port N) for Nick (mac) dropped: no relay session` и `Mixed transports … (no relay: Nick)`. Включите «Try to use server-provided packet relay» **у всех** и перезапустите ad hoc в игре |
| Пустая карточка сервера в списке PPSSPP | статус качается по **HTTP** (клиент переписывает `https` → `http`): `http://host/data.json` должен отвечать без редиректа на https; см. [docs/SERVER_LIST.md](docs/SERVER_LIST.md) |
| Лобби видно, а **сессии в игре** — нет | разные `Network → Port offset` (десктоп `0`, Android `10000`): сервер переводит порты автоматически, но лучше выставить одинаковый offset у всех; см. [docs/RELAY.md](docs/RELAY.md#разные-port-offset--главная-причина-сессии-не-находятся-в-игре) |
| Игра **вылетает через ~10 минут** матча (Killzone Liberation) | было до 1.1: ретранслятор закрывал простаивающий `PTP_LISTEN`-сокет. Исправлено — слушающие сокеты больше не трогаются; см. [docs/RELAY.md](docs/RELAY.md#таймауты-сессий) |
| `requested invalid group name ""` в логе (Phantasy Star Portable 2) | было в 1.0.0, исправлено: пустое имя группы (8 нулей от `sceNetAdhocctlCreate(NULL)`) теперь валидно, как в референсном сервере PPSSPP. Обновите билд |
| Игра показывает ID `000000001` (Sega Rally, Air Conflicts и др.) | это **Game Share** («Обмен игрой»): игра сама подменяет код на `000000001` и создаёт комнату `GameShare`. Сервер показывает её как «Game Share (demo mode)». Оба игрока должны быть в одном режиме — либо оба Game Share, либо оба запускают полную игру; хост Game Share и игрок полной игры никогда не встретятся. Плюс в Game Share гость скачивает данные от хоста, поэтому без ретранслятора это почти не работает |
| Нет иконок | нет CDN; положите Font Awesome локально в `public/assets/vendor/` |
| Нет звука | autoplay блокируется до первого клика по странице |

## Атрибуция

- Названия и crosslink'и изначально взяты из PPSSPP (`Core/HLE/proAdhocServer.cpp`, проект
  PRO Online, GPLv3); crosslink'и правятся в веб-интерфейсе и лежат в `data/games.json`.
- Массовая база Product ID → название собрана из [pspudb](https://github.com/georgemoralis/pspudb)
  (`pspudb/data/database.db`, GPL-2.0): только пары «серийник/название», без кода проекта.
- Сам сервер написан с нуля и не содержит кода PPSSPP или pspudb.