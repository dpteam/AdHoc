# DPT.Agent AdHoc Server

Сервер ad-hoc сети для PSP-игр (PPSSPP / PRO Online, протокол `SceNetAdhocctl`) на **.NET Framework 4.0**,
совместимый с Windows XP SP3, **без единой сторонней библиотеки**, с собственной веб-мордой в стиле
VK/Facebook 2008 (светлая / тёмная / OLED темы), мягкими синтезированными звуками и расширенным
логированием протокола.

```
adhoc 27312  <-- PSP / PPSSPP клиенты (TCP, бинарный протокол)
web   8080   <-- браузер (свой HTTP/1.1 поверх TcpListener, без admin-прав)
```

## Что умеет

**Игровой сервер**
- Полная реализация `SceNetAdhocctl`: `LOGIN`, `CONNECT`, `DISCONNECT`, `SCAN`, `CHAT`, `PING`,
  ответы `CONNECT_BSSID`, `CONNECT`, `DISCONNECT`, `SCAN`, `SCAN_COMPLETE`, `CHAT`.
- Группы (комнаты) внутри игры, хост группы (BSSID) = основатель, корректные уведомления пирам.
- Кросс-региональные игры (`crosslink`): EU/JP/US копии одной игры попадают в одно лобби.
- Таймауты сессий, лимит на IP, лимит пользователей, автоочистка пустых игр.
- Дубликаты MAC, «залипшие» сессии, переполнение RX-буфера — всё обрабатывается и логируется.
- Мягкий режим (по умолчанию): неизвестный опкод не убивает сессию, а пропускается с предупреждением.

**Веб-морда**
- Живое лобби: игры → группы → игроки (ник, MAC, IP, время в игре, idle, пинг), раскрывающиеся карточки.
- Недавние игроки с причиной выхода (вышел / таймаут / кик / обрыв).
- Лента событий и хвост лога протокола прямо в браузере (с фильтром по уровню и паузой).
- Каталог игр: поиск по 162 названиям, добавление/правка названия и crosslink прямо из UI.
- Настройки: тема, звук + громкость + выбор событий, интервал опроса, admin token, чат всем игрокам.
- Кик игрока одной кнопкой (с подтверждением).
- Три темы: `light` (VK 2008), `dark`, `oled`; выбор запоминается в браузере.
- Звуки — WebAudio-синтез (без файлов): вход, выход, новая игра, чат, ошибка, потеря/восстановление связи.
- Всё на чистом ES5 + CSS (без фреймворков), Font Awesome подключается с CDN (без него вёрстка не ломается).

**Надёжность**
- Supervisor-поток: следит за обоими листенерами, перезапускает их с экспоненциальной задержкой.
- Fallback-порты, если основной занят (`adhocPortFallbacks`, `web.portFallbacks`).
- Временные баны IP за повторные ошибки протокола, rate limit HTTP, лимит одновременных запросов.
- Логирование через `Trace` в `logs/adhoc-YYYYMMDD.log` (ротация по дню и размеру, ретеншн),
  без дублирования в консоль, с защитой от «мёртвых» listener'ов.

## Быстрый старт

```bat
build.cmd
run.cmd
```

Откройте `http://localhost:8080/` (или `http://<IP-машины>:8080/` из локальной сети).

В PPSSPP: **Сеть → Ad Hoc → Включить**, «Пользовательский сервер» → адрес машины с сервером,
порт **27312**, ник — любой. Игра должна быть запущена, дальше она сама логинится в лобби.

> Требуется .NET Framework 4.0+ (на XP SP3 — именно 4.0). Сборка идёт стоковым `csc.exe`,
> без NuGet, MSBuild и интернета.

## Настройки (`config.ini`)

Файл создаётся рядом с `AdHocServer.exe` при первом запуске (пример — `config.example.ini`).
Ключевые параметры:

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `server.adhocPort` | `27312` | порт игрового протокола |
| `server.adhocPortFallbacks` | `27313,27314` | запасные порты |
| `server.maxUsers` / `maxUsersPerIp` | `256` / `16` | лимиты |
| `server.userTimeoutSec` | `25` | простой до разрыва (PPSSPP пингует каждые ~2 c) |
| `server.sessionMaxHours` | `12` | жёсткий лимит жизни сессии, `0` = без лимита |
| `server.emptyGameTtlMin` | `15` | сколько пустая игра висит на доске |
| `server.ignoreUnknownOpcodes` | `true` | мягкий режим протокола |
| `server.kickDuplicateMac` | `false` | кикать старую сессию при дубле MAC |
| `web.port` / `web.portFallbacks` | `8080` / `8081…8085` | веб-порт |
| `web.bindAddress` | `0.0.0.0` | `127.0.0.1` — только локально |
| `web.adminToken` | пусто | токен для POST-ручек и (опционально) логов |
| `web.exposeLogs` | `true` | отдавать `/api/logs` |
| `logging.level` | `Info` | `Error/Warn/Info/Debug/Verbose` |
| `logging.protocolTrace` | `true` | подробный лог пакетов |
| `limits.*` | — | баны, размеры истории/событий/буфера |

После правки `config.ini` нужен перезапуск сервера.

## Веб-интерфейс

- **Лобби** — карточки игр. Клик раскрывает группы: хост с короной, игроки, MAC, IP, время, idle, кик.
- **Поиск** — по названию, product ID, нику, MAC или IP. `/` — фокус, `R` — обновить сейчас.
- **События** — вход/выход/группы/чат/ошибки; галочка «оповещать» выключает звуки и тосты.
- **Протокол** — живой хвост лога (`/api/logs`), уровень и пауза; видно сырые пакеты и их расшифровку.
- **Каталог игр** — таблица Product ID ↔ название, crosslink, кнопка «Обновить из файла».
- **Настройки** — тема, звук (громкость, тест, какие события озвучивать), период опроса,
  уровень лога, admin token, отправка сообщения всем игрокам через игровой чат.
- **Справка** — что вводить в PPSSPP, как устроен протокол, горячие клавиши.

## API

`/api/status`, `/api/games` (GET/POST), `/api/logs`, `/api/history`, `/api/bans`, `/api/bans/clear`,
`/api/kick`, `/api/chat`, `/api/reload`, `/api/health`, плюс легаси `/status.xml`
(XML в формате PRO Online для старых мониторингов). Подробности и примеры — [docs/API.md](docs/API.md).

## Логи

```
logs/adhoc-20261102.log          # текущий файл
logs/adhoc-20261102.001.log      # после 8 MB
```

Пишется через `Trace` одним собственным listener'ом: `timestamp [level] category message`.
Категории: `APP`, `NET`, `PROTO`, `WEB`, `DB`, `CONFIG`, `LOG`, `UI`.
`PROTO` содержит hex + расшифровку каждого пакета (`LOGIN nic=… game=…`, `CONNECT_BSSID …`),
причину закрытия сессии, длительность и объём трафика. Пинги не засоряют лог (только `Verbose`).

## Устройство проекта

```
build.cmd / run.cmd                сборка стоковым csc.exe / запуск
src/AdHocServer/
  Program.cs                       bootstrap, аргументы, graceful shutdown
  AppSupervisor.cs                  watchdog листенеров
  ConsoleUi.cs                     консоль: статус + команды
  Core/Config.cs                   парсер config.ini (+ port.txt)
  Core/Log.cs                      Trace-listener, ротация, ring-буфер для UI
  Core/Json.cs                     свой JSON (парсер + writer), без зависимостей
  Core/GameDatabase.cs             product ID ↔ игра, crosslinks, data/games.json
  Core/ServerState.cs              игры/группы/сессии/история/события
  Core/ServerApp.cs                общий контекст
  Core/Util.cs                     MAC/IP/hex/время/строки
  Net/Protocol.cs                  опкоды и сборка пакетов S2C
  Net/ClientSession.cs             TCP-сессия, буфер, send-lock
  Net/AdhocServer.cs               состояние протокола, группы, чат, баны, таймауты
  Web/HttpCore.cs                  HTTP/1.1: запрос, ответ, keep-alive, chunked
  Web/WebServer.cs                 листенер, статика, gzip, ETag/304, rate limit
  Web/WebApi.cs                    JSON/XML API
public/                            веб-морда (html/css/js, без сборки)
data/games.json                     база названий (создаётся/правится автоматически)
tools/test-client.ps1              тест протокола (LOGIN/CONNECT/SCAN/CHAT)
tools/test-web.ps1                 тест API, статики и кика
tools/hold-client.ps1              «держатель» сессии для ручных проверок
docs/PROTOCOL.md                   байтовый формат пакетов
docs/ARCHITECTURE.md               потоки, блокировки, отказоустойчивость
docs/API.md                        HTTP API
```

Подробности: [docs/PROTOCOL.md](docs/PROTOCOL.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Сборка и проверка

```bat
build.cmd                     :: csc.exe /langversion:4 /define:TRACE -> bin\AdHocServer.exe
powershell -File tools\test-client.ps1     :: протокол
powershell -File tools\test-web.ps1 -Base http://127.0.0.1:8080
```

`build.cmd` копирует `public/` и `config.example.ini` в `bin/`, данные и логи создаются при запуске.
Проект также открывается в Visual Studio 2010+ (`src/AdHocServer/AdHocServer.csproj`, TargetFramework 4.0).

> Правя фронтенд, не забудьте `build.cmd` (или скопировать `public/` в `bin/public/`) и поднять `?v=`
> в `public/index.html`, иначе браузер отдаст закешированный CSS/JS.

## Отказоустойчивость в деталях

1. **Supervisor** каждые 2 c проверяет `IsRunning` обоих листенеров, при падении перезапускает
   (backoff 1→2→4…30 c), событие `Failed` из accept-loop ускоряет реакцию.
2. **Fallback-порты**: занятый порт — не ошибка, а повод взять следующий; всё пишется в лог и на доску.
3. **Мягкий протокол**: неизвестные опкоды, скан из группы, повторный `CONNECT` — не рвут сессию
   (строгий режим включается `ignoreUnknownOpcodes=false`).
4. **Таймауты**: idle-сессия, жёсткий лимит жизни, переполнение буфера, зависшая запись — всё
   закрывается с причиной, пиры получают `DISCONNECT`.
5. **Баны**: N ошибок протокола с одного IP → временный бан (`maxFailedLoginsPerIp`, `banSeconds`).
6. **HTTP**: rate limit на IP, лимит параллельных запросов, таймауты сокета, обрезка тела запроса,
   запрет path traversal, `Connection: close` при перегрузке.
7. **Процесс**: глобальные обработчики `UnhandledException` / `ProcessExit`, финальный `Trace.Flush`,
   остановка при Ctrl+C и `quit` без «зависших» потоков (все `IsBackground`).

## Совместимость

- Windows XP SP3 + .NET Framework 4.0 — целевая среда (только API .NET 4.0, C# 4).
- Windows 7/10/11 — работает и на .NET 4.8 (тот же CLR 4.0, сборка не меняется).
- Linux/macOS через Mono >= 4.x — теоретически да (используется только BCL), не тестировалось.
- Браузер: любой современный; вёрстка на CSS-переменных, ES5-скрипты.

## Решение проблем

| Симптом | Что делать |
|---|---|
| В логе `port … is busy`, веб на другом порту | порт занят другой программой; смотрите лог/консоль — сервер уже взял fallback-порт |
| `http://IP:8080` не открывается | фаервол Windows: разрешить `AdHocServer.exe` (частные сети); либо `web.bindAddress=127.0.0.1` и заходить локально |
| PPSSPP не видит лобби | проверить порт 27312, IP сервера, и что игра запущена; в логе будет `LOGIN` |
| Игроки одной игры не видят друг друга | разные региональные ID — добавьте crosslink в каталоге |
| Нет иконок | нет интернета/CDN; положите Font Awesome локально в `public/assets/vendor/` и поправьте `<link>` |
| Нет звука | браузер блокирует autoplay до первого клика — кликните по странице; громкость в настройках |

## Атрибуция

Таблица Product ID → название и crosslink'и взяты из открытого исходного кода PPSSPP
(`Core/HLE/proAdhocServer.cpp`, проект PRO Online, GPLv3) и дополняются через веб-интерфейс
в `data/games.json`. Сам сервер написан с нуля и не содержит кода PPSSPP.