# Архитектура

Один процесс, один exe, BCL-only. Ниже — как он устроен внутри, почему именно так и где
что менять.

## Слои

```
Program (bootstrap, аргументы, shutdown)
 ├── Config            config.ini -> объект, нормализация
 ├── Log               Trace -> файл + ring-буфер для UI
 ├── GameDatabase      data/games.json  (product id -> название, crosslink)
 ├── ServerState       игры/группы/сессии/история/события   [lock Sync]
 ├── AdhocServer       TCP 27312, протокол, группы, баны, таймауты
 ├── WebServer         TCP 8080, HTTP/1.1, статика, gzip, ETag, rate limit
 ├── WebApi            /api/* и /status.xml
 ├── AppSupervisor     watchdog + backoff
 └── ConsoleUi         статус-строка и команды
```

`ServerApp` — общий контекст (Config, Db, State, Adhoc, Web, Version), чтобы не тащить
зависимости через конструкторы.

## Потоки

| Поток | Кто | Зачем |
|---|---|---|
| main | Program | ждёт `StopEvent`, затем graceful shutdown |
| `adhoc-accept` | AdhocServer | `AcceptTcpClient`, по клиенту — свой поток |
| `adhoc-<id>` | AdhocServer | блокирующий `Read` сессии, разбор пакетов |
| `adhoc-maintenance` | AdhocServer | раз в 2 c: очистка пустых игр, банов, зависших сессий |
| `web-accept` | WebServer | `AcceptTcpClient`, соединение → `ThreadPool` |
| ThreadPool | WebServer | разбор HTTP, до 100 keep-alive запросов на соединение |
| `supervisor` | AppSupervisor | раз в 2 c проверяет листенеры, перезапускает |
| `console-ui` | ConsoleUi | статус-строка + `ReadLine` команды |

Все фоновые потоки `IsBackground = true`: процесс не висит после закрытия окна/`quit`.

## Блокировки

- `ServerState.Sync` — единственная блокировка на модель (игры ↔ группы ↔ сессии, история, события).
  Правило: **никогда не делать socket I/O под ней**. Обработчики сначала собирают список
  `Outbound{Target, Data}`, выходят из `lock` и только потом отправляют.
- `ClientSession._sendLock` — сериализует запись в сокет (пиры пишут в чужую сессию из разных потоков).
- `Log` — свой lock на файл и ring-буфер; listener никогда не бросает исключения.
- `AdhocServer._banLock` — баны и счётчики ошибок по IP.
- `WebServer._rateLock` — окна rate limit.
- `GameDatabase._sync` — словари + сохранение файла.
- Порядок захвата: `State.Sync` → `Db` (только чтение), `State.Sync` → ничего сетевого. Обратных
  зависимостей нет, дедлоков не возникает.

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

`ProcessBuffer` корректно склеивает и дробит TCP-сегменты: один `Read` может принести
полтора пакета, и наоборот — пакет может прийти по частям.

## Модель данных

```
GameEntry (id = канонический product code)
 ├── groups: GroupEntry[]      имя группы = 8 символов из игры
 │    └── sessions: ClientSession[]   + Founder (BSSID)
 └── sessions: ClientSession[]        все игроки игры (включая тех, кто без группы)
```

- Ключ игры — **resolved** product code (после crosslink), поэтому EU-копия и US-копия
  попадают в одну `GameEntry`, а `rawId` хранит оригинал для UI.
- `Founder` — первый вошедший в группу; при выходе передаётся следующему. Именно его MAC
  уходит в `CONNECT_BSSID`.
- `History` и `Events` — кольцевые списки с обрезкой (`limits.historySize`, `limits.eventSize`).
- Пустая игра живёт `emptyGameTtlMin` минут (maintenance-поток), чтобы доска не зарастала.

## HTTP-сервер

`HttpCore.cs` — минимальный, но честный HTTP/1.1:

- разбор request line + заголовков (лимиты: 8 KB строка, 64 KB заголовки, 2 MB тело);
- `Content-Length` и `Transfer-Encoding: chunked`, `Expect: 100-continue`;
- keep-alive (до 100 запросов на соединение), иначе `Connection: close`;
- ответ всегда с `Content-Length`, `Date`, `Server`, `Connection`; для `HEAD` — без тела;
- для `304` тело не отправляется вообще.

`WebServer`:

- `TcpListener` на `web.bindAddress` (`0.0.0.0` по умолчанию) — **не нужен URL ACL/netsh**;
- fallback-порты;
- `ThreadPool` + лимит одновременных соединений (`maxConcurrentRequests`);
- rate limit по IP (скользящее окно 60 c);
- статика: белый список MIME, защита от `..`, `ETag`/`Last-Modified`/`304`, gzip для текста;
- `Cache-Control`: `no-cache` для HTML, `max-age=300` для `/assets/`.

Выбор `TcpListener` вместо `HttpListener` сделан осознанно: `HttpListener` на Windows требует
`netsh http add urlacl` для не-localhost префиксов (то есть admin-прав) и по-разному ведёт себя
на XP. Для игрового сервера, который запускают двойным кликом, это неприемлемо.

## Логирование

```
Log.Info("NET", "...")  ->  LogEntry (ring, seq)  ->  Trace.WriteLine(formatted)
                                                     └── LogFileListener (один!)
                                                          logs/adhoc-YYYYMMDD[.NNN].log
```

- `Trace.Listeners.Clear()` один раз в `Log.Configure`, затем добавляется ровно один listener.
- Listener ловит **все** исключения внутри себя, проверяет флаг shutdown и не пишет в консоль —
  это защита от классического бага «листенер сдох и всё приложение падает на Trace».
- `Trace.AutoFlush = false`, запись идёт через `StreamWriter` с `AutoFlush = true` и
  `FileShare.ReadWrite` (лог можно читать/тейлить на ходу).
- Ротация: по дате и по размеру (`maxFileMB`), старые файлы удаляются (`retentionDays`).
- `Log.Proto(...)` дополнительно управляется `logging.protocolTrace`.
- Для веб-панели есть ring-буфер в памяти: `/api/logs?since=…` — без чтения файла и без
  парсинга строк.

## Отказоустойчивость

1. **Accept-loop** считает подряд идущие ошибки; после 25 — генерирует `Failed` и выходит,
   supervisor перезапускает листенер (backoff 1,2,4,8,16,30 c).
2. **Порты**: основной + fallback-список для adhoc и web.
3. **Сессии**: idle-таймаут, жёсткий lifetime, лимит RX-буфера, защита от «зависшей» записи
   (`SendTimeout`), идемпотентное закрытие (`ClientSession.CloseHandled`).
4. **Протокол**: мягкий режим (по умолчанию), строгий включается флагом.
5. **Абуз**: счётчик ошибок на IP, временный бан, лимиты пользователей на IP и всего.
6. **HTTP**: лимиты тела/заголовков, rate limit, лимит параллельных запросов, 503 при перегрузе.
7. **Процесс**: `AppDomain.UnhandledException`, `Console.CancelKeyPress`, `ProcessExit`,
   финальный `Log.Shutdown()`, `ExitCode` 2 если не удалось занять порт протокола.

## Где что менять

| Задача | Файл |
|---|---|
| новый опкод / поле пакета | `Net/Protocol.cs`, `Net/AdhocServer.cs`, `docs/PROTOCOL.md` |
| правила групп/хоста | `AdhocServer.HandleConnect`, `LeaveGroupLocked` |
| таймауты и лимиты | `Core/Config.cs` (+ `config.example.ini`) |
| база игр / crosslink | `Core/GameDatabase.cs`, `data/games.json` |
| JSON API | `Web/WebApi.cs` |
| статика/HTTP | `Web/WebServer.cs`, `Web/HttpCore.cs` |
| новая плитка/панель UI | `public/index.html`, `public/assets/js/app.js`, `app.css` |
| звуки | `public/assets/js/sound.js` (пресеты — `PRESETS`) |

## Тесты

- `tools/test-client.ps1` — протокол (LOGIN/CONNECT/SCAN/CHAT/DISCONNECT, BSSID, crosslink).
- `tools/test-web.ps1` — API, каталог, кик, `/status.xml`, gzip, ETag/304, раздача index.html.
  Скрипт сам поднимает «держателей» сессий и восстанавливает `data/games.json` после себя.
- `tools/hold-client.ps1` — держит сессию с пингами (имитация PPSSPP).