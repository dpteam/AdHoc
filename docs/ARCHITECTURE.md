# Архитектура

Один процесс, один exe, BCL-only. Ниже — как он устроен внутри, почему именно так и где
что менять.

## Слои

```
Program (bootstrap, аргументы, shutdown)
 ├── Config            config.ini -> объект, генерация admin token/salt
 ├── Log               Trace -> файл + ring-буфер для UI
 ├── ApiCrypto         PBKDF2 / HMAC / HMAC-CTR конверт / NonceCache
 ├── GameDatabase      data/games.json  (product id -> название, crosslink)
 ├── ServerState       игры/группы/сессии/история/события   [lock Sync]
 ├── AdhocServer       TCP 27312, протокол, группы, баны, таймауты
 ├── RelayServer       TCP 27313, PPSSPP-релей (aemu_postoffice)
 ├── WebServer(Public) TCP 44501, read-only, вычищенные данные
 ├── WebServer(Admin)  TCP 44502, подпись+шифрование, полный доступ
 ├── WebApi            разветвление public/admin, JSON/XML
 ├── AppSupervisor     watchdog всех трёх листенеров + backoff
 └── ConsoleUi         статус-строка и команды
```

`ServerApp` — общий контекст (Config, Db, State, Adhoc, WebPublic, WebAdmin, Version).
Обе доски читают один и тот же `ServerState` под одним локом, поэтому «синхронизация» —
это просто один источник правды, без репликации и таймеров.

## Потоки

| Поток | Кто | Зачем |
|---|---|---|
| main | Program | ждёт `StopEvent`, затем graceful shutdown |
| `adhoc-accept` | AdhocServer | `AcceptTcpClient`, по клиенту — свой поток |
| `adhoc-<id>` | AdhocServer | блокирующий `Read` сессии, разбор пакетов |
| `adhoc-maintenance` | AdhocServer | раз в 2 c: пустые игры, баны, зависшие сессии |
| `relay-accept` | RelayServer | TCP 27313, по соединению — свой поток |
| `relay-<id>` | RelayServer | сессия релея: INIT, PDP/PTP, пересылка байт |
| `relay-maintenance` | RelayServer | раз в 5 c: idle/lifetime сессий |
| `web-public-accept` | WebServer | TCP 44501 → ThreadPool |
| `web-admin-accept` | WebServer | TCP 44502 → ThreadPool |
| `supervisor` | AppSupervisor | раз в 2 c проверяет все листенеры, перезапускает |
| `console-ui` | ConsoleUi | статус-строка + команды |

Все фоновые потоки `IsBackground = true`.

## Блокировки

- `ServerState.Sync` — модель (игры ↔ группы ↔ сессии, история, события). Никогда не делать
  socket I/O под ней: обработчики собирают `Outbound{Target, Data}`, выходят из `lock` и отправляют.
- `ClientSession._sendLock` — сериализует запись в сокет.
- `Log` — файл и ring-буфер; listener не бросает исключений.
- `AdhocServer._banLock` — баны и счётчики ошибок по IP.
- `WebServer._rateLock` — окна rate limit и счётчики неудачных подписей.
- `GameDatabase._sync` — словари + сохранение файла.
- `NonceCache` — свой lock, окно 10 минут, ограничение размера.

Порядок: `State.Sync` → `Db` (только чтение), `State.Sync` → ничего сетевого. Обратных
зависимостей нет.

## Обработка сессии

```
accept -> ClientSession (socket options, timeouts)
       -> State.SessionOpened
       -> поток: while(running && open)
             проверка lifetime / idle
             Read (ReceiveTimeout = readTimeoutMs, IOException = таймаут -> continue)
             байты -> Rx-буфер (растёт до maxPacketBuffer)
             ProcessBuffer: сколько целых пакетов есть — столько и обработать
                  Waiting: только LOGIN(144)
                  LoggedIn: PING(1) / CONNECT(9) / DISCONNECT(1) / SCAN(1) / CHAT(65)
                            неизвестный -> Consume(1) + warn (или разрыв в strict)
       -> finally: CloseSession(reason) -> уведомить пиров, история, счётчики
```

## Две веб-морды

```
                 ┌──────────────────────────────┐
   internet ──▶  │ 44501 public (WebServer)     │  GET /api/status  -> redacted JSON
   (NPM/nginx)   │  role = Public               │  GET /api/games   -> каталог
                 └──────────────┬───────────────┘  GET /status.xml, /data.json (PPSSPP feed)
                                │
                        ServerApp / ServerState (один на процесс)
                                │
                 ┌──────────────┴───────────────┐
   LAN only ──▶  │ 44502 admin (WebServer)      │  /api/auth (salt)
                 │  role = Admin                │  всё остальное: подпись + конверт
                 └──────────────────────────────┘
```

Один и тот же класс `WebServer` с полем `Role`:

- порт/bind/fallback берутся из своей пары настроек;
- `ServeStatic` отдаёт `admin.html` на админском порту и `index.html` на публичном;
  `/admin.html` на публичном → `404`;
- `WebApi.Handle` разветвляется на `HandlePublic` / `HandleAdmin`;
- админский порт при `adminPrivateOnly` отклоняет не-приватные адреса до разбора запроса;
- публичный порт вообще не обслуживает `POST` и админские пути.

## Авторизация админского API

```
браузер                                    сервер
  | GET /api/auth -----------------------> |  { salt, iterations, ts, encrypt }
  |  K = PBKDF2(token, salt, iter)         |  K = PBKDF2(configToken, configSalt, iter)
  |  на каждый запрос:                     |
  |    nonce = random(16)                  |
  |    ts    = now + skew                  |
  |    sig   = HMAC(kmac, canonical)       |
  | --- headers + (sealed) body ---------->|  проверка ts/nonce/подписи/тега
  | <--- (sealed) JSON --------------------|  SealResponse, если adminEncrypt
```

- `canonical = METHOD \n path?query \n ts \n nonce \n hex(SHA256(wireBody))`;
  `path?query` — относительный, поэтому reverse proxy с префиксом не ломает подпись.
- nonce кэшируется на 10 минут (`NonceCache`), повтор → `401`.
- тело и ответ — `base64(iv16 | HMAC-CTR(enc) XOR data | tag16)`; тег проверяется
  побайтово без раннего выхода.
- 20 неудач за 5 минут с адреса → блок на 5 минут (`_authFails`).
- Если тело перед шифрованием было gzip — `SealResponse` сначала распаковывает,
  чтобы клиент получил чистый JSON.

Клиентская реализация (`public/assets/js/crypto.js`, `auth.js`) повторяет C# бит в бит:
свой SHA-256/HMAC/PBKDF2 на ES5, потому что `crypto.subtle` недоступен на `http://` в LAN.

## Reverse proxy

`WebServer.EffectiveClientIp`:

1. если `trustProxyHeaders=false` — адрес сокета;
2. если пир не входит в `trustedProxies` (loopback + приватные по умолчанию) — адрес сокета;
3. иначе берём `X-Forwarded-For` (или `X-Real-IP`), идём **справа налево** и берём первый
   недоверенный адрес — так внешний клиент не подделает свой адрес, чтобы обойти лимит.

`MapPath` срезает `web.publicBasePath` / `web.adminBasePath` и `X-Forwarded-Prefix`, поэтому
доска работает и на поддомене, и на `example.com/adhoc/`. Вся статика и API-вызовы в HTML/JS
относительные — префикс не нужно нигде прописывать.

## Модель данных

```
GameEntry (id = канонический product code)
 ├── groups: GroupEntry[]      имя группы = 8 символов из игры
 │    └── sessions: ClientSession[]   + Founder (BSSID)
 └── sessions: ClientSession[]        все игроки игры (включая тех, кто без группы)
```

- Ключ игры — **resolved** product code (после crosslink).
- `Founder` — первый вошедший в группу; при выходе передаётся следующему.
- `History` и `Events` — кольцевые списки с обрезкой.
- Пустая игра живёт `emptyGameTtlMin` минут (maintenance-поток).

## HTTP-сервер

`HttpCore.cs` — минимальный, но честный HTTP/1.1:

- разбор request line + заголовков (8 КБ строка, 64 КБ заголовки, 2 МБ тело);
- `Content-Length` и `Transfer-Encoding: chunked`, `Expect: 100-continue`;
- keep-alive (до 100 запросов на соединение), иначе `Connection: close`;
- ответ всегда с `Content-Length`, `Date`, `Server`, `Connection`; `HEAD` без тела;
- для `304` тело не отправляется.

`WebServer`:

- `TcpListener` на `publicBind`/`adminBind` — **не нужен URL ACL/netsh**;
- fallback-порты для обеих досок;
- `ThreadPool` + лимит одновременных соединений;
- rate limit по реальному клиенту (скользящая минута);
- статика: белый список MIME, защита от `..`, `ETag`/`Last-Modified`/`304`, gzip;
- заголовки безопасности: CSP, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`,
  `Cross-Origin-Resource-Policy`, `Permissions-Policy`.

## Логирование

```
Log.Info("NET", "...")  ->  LogEntry (ring, seq)  ->  Trace.WriteLine(formatted)
                                                     └── LogFileListener (один!)
                                                          logs/adhoc-YYYYMMDD[.NNN].log
```

- `Trace.Listeners.Clear()` один раз в `Log.Configure`, затем ровно один listener.
- Listener ловит все исключения внутри себя, проверяет флаг shutdown, не пишет в консоль.
- `AutoFlush` у файла, `FileShare.ReadWrite` (лог можно тейлить).
- Ротация по дате и размеру, удаление старых файлов.
- `Log.Proto(...)` дополнительно управляется `logging.protocolTrace`.
- Ring-буфер для `/api/logs?since=…` — без чтения файла.
- Запросы к API логируются на уровне `Verbose`, чтобы панель не тонула в `GET /api/status`.

## Отказоустойчивость

1. **Accept-loop** каждого листенера считает подряд идущие ошибки; после 25 — `Failed`
   и выход, supervisor перезапускает (backoff 1,2,4,8,16,30 c).
2. **Порты**: основной + fallback-список для adhoc, public и admin.
3. **Сессии**: idle-таймаут, жёсткий lifetime, лимит RX-буфера, `SendTimeout`,
   идемпотентное закрытие.
4. **Протокол**: мягкий режим (по умолчанию), строгий включается флагом.
5. **Абуз**: счётчик ошибок на IP, временный бан, лимиты пользователей.
6. **HTTP**: лимиты тела/заголовков, rate limit, лимит одновременных, 503 при перегрузе,
   троттлинг неудачных подписей.
7. **Процесс**: `UnhandledException`, `CancelKeyPress`, `ProcessExit`, финальный `Log.Shutdown`,
   `ExitCode` 2 если не удалось занять порт протокола.

## Где что менять

| Задача | Файл |
|---|---|
| новый опкод / поле пакета | `Net/Protocol.cs`, `Net/AdhocServer.cs`, `docs/PROTOCOL.md` |
| правила групп/хоста | `AdhocServer.HandleConnect`, `LeaveGroupLocked` |
| таймауты и лимиты | `Core/Config.cs` (+ `config.example.ini`) |
| база игр / crosslink | `Core/GameDatabase.cs`, `data/games.json` |
| публичный JSON (что скрыть) | `Web/WebApi.cs` → `redact`-ветки `BuildStatus`/`WritePlayer` |
| подпись/шифрование | `Core/ApiCrypto.cs`, `Web/WebServer.cs` (`Authenticate`, `SealResponse`) |
| пороги прокси/лимитов | `Core/Config.cs` (`trustedProxies`, `requestsPerMinute`) |
| статика/HTTP | `Web/WebServer.cs`, `Web/HttpCore.cs` |
| UI (обе доски) | `public/index.html`, `public/admin.html`, `public/assets/js/app.js` |
| вход в админку | `public/assets/js/auth.js`, `public/admin.html` (auth gate) |
| звуки | `public/assets/js/sound.js` (пресеты — `PRESETS`) |
| релей (PDP/PTP, лимиты) | `Net/RelayServer.cs`, `docs/RELAY.md` |
| детект NAT hairpin | `Core/LocalNetwork.cs`, `AdhocServer.HandleLogin` |
| статистика за всё время | `Core/StatsStore.cs`, `data/stats.json`, `AdhocServer` (Note* вызовы) |
| посещаемость доски | `Core/VisitStore.cs`, `data/visits.json`, `POST /api/visit` |
| публичный чат | `Core/ChatStore.cs`, `Core/Captcha.cs`, `WebApi.HandleChatSend` |

## Тесты

- `tools/test-client.ps1` — протокол (LOGIN/CONNECT/SCAN/CHAT/DISCONNECT, BSSID, crosslink).
- `tools/test-relay.ps1` — релей: PDP-пересылка с переписыванием MAC/порта, полный
  PTP handshake (listen → connect → accept), байты в обе стороны, закрытие пары, мусорный INIT.
- `tools/test-web.ps1` — обе доски: вычистка публичного JSON, скрытые эндпоинты,
  gzip/ETag/304, подпись, sealed-ответы, replay, битая подпись, кик, чат, каталог.
- `tools/admin-api.ps1` — клиент админского API (грузит `ApiCrypto` прямо из exe,
  поэтому тест не может разойтись с сервером).
- `tools/hold-client.ps1` — держит сессию с пингами (имитация PPSSPP).