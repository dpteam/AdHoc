# HTTP API

Две доски — два порта, один процесс и одно состояние:

| | Public | Admin |
|---|---|---|
| По умолчанию | `http://<host>:44501` | `http://<host>:44502` |
| Роль | read-only, данные вычищены | полный доступ, подпись + шифрование |
| `data-role` в HTML | `public` | `admin` |

Публичная доска **не содержит** полей `mac`, `ip`, `idleSec`, `bytesIn/Out`, `pings`, `id`,
`rawId` и событий `error/ban/kick/chat`. Админские эндпоинты на публичном порту отвечают `404`.

## Публичный API

| Метод | Путь | Что отдаёт |
|---|---|---|
| GET | `/api/status` | игры → группы → ники, статистика, последние выходы, события |
| GET | `/api/games` | каталог: 3900+ названий, crosslink'и, `unknownIds` |
| GET | `/api/history?limit=50` | завершённые сессии (без MAC/IP) |
| GET | `/api/health` | `{"ok":true,"role":"public","uptimeSec":…}` |
| GET | `/status.xml` | легаси PRO Online XML |
| GET | `/data.json` | статус для списка серверов PPSSPP (`/status.json` — алиас), CORS `*`, см. [SERVER_LIST.md](SERVER_LIST.md) |
| POST | `/api/visit` | «пинг» открытой доски: считает посещение, отдаёт счётчики |
| GET | `/api/chat?since=0&limit=60` | публичный чат доски (ники, текст, источник) |
| GET | `/api/chat/captcha` | капча: `{ id, svg, ttl }`, одноразовая |
| POST | `/api/chat/send` | отправить сообщение: `{ nick, text, captcha, answer }` |
| GET | `/` | публичная доска (`index.html`) |

Пример ответа `/api/status` (сокращён):

```json
{
  "ok": true, "role": "public", "nowUtc": "2026-11-02T20:02:28.120Z",
  "server": {
    "name": "DPT.PSP AdHoc Server", "version": "1.0.0", "build": "2026-11-02 22:32",
    "startedUtc": "…", "uptimeSec": 82, "adhocPort": 27312, "webPort": 44501,
    "role": "public", "protocol": "PRO Online / PPSSPP adhocctl"
  },
  "games": [{
    "id": "ULUS10084", "name": "Monster Hunter Freedom", "known": true,
    "players": 2, "peak": 3, "totalPlayers": 9, "lastActivityUtc": "…",
    "groups": [{
      "name": "ROOM1234", "players": 2,
      "members": [
        { "name": "Kai", "connectUtc": "…", "connectedSec": 61, "isHost": true },
        { "name": "Ryo", "connectUtc": "…", "connectedSec": 12, "isHost": false }
      ]
    }],
    "idle": []
  }],
  "stats": { "online": 2, "peak": 3, "games": 2, "groups": 1, "connections": 11,
             "chats": 2, "timeouts": 0, "events": 36, "eventSeq": 36 },
  "recent": [{ "name": "HolderThree", "gameId": "ULUS10391",
               "gameName": "Monster Hunter Freedom Unite", "group": "ABCD1234",
               "connectUtc": "…", "disconnectUtc": "…", "durationSec": 25.4,
               "reason": "timeout" }],
  "events": [{ "seq": 36, "timeUtc": "…", "type": "join",
               "gameName": "Monster Hunter Freedom", "player": "Kai",
               "text": "Kai started playing Monster Hunter Freedom" }],
  "unknownIds": [],
  "relay": { "enabled": true, "sessions": 0 },
  "lifetime": { "starts": 4, "firstStartUtc": "…", "peakOnline": 7, "connections": 132,
                "sessions": 128, "playSeconds": 41250, "games": 23 },
  "topGames": [{ "id": "ULUS10391", "name": "Monster Hunter Freedom Unite", "peak": 4,
                 "sessions": 51, "playSeconds": 19800, "lastSeenUtc": "…" }],
  "config": { "theme": "light", "sound": true, "refreshMs": 2000, "timeoutSec": 25,
              "maxUsers": 256, "dbGames": 3912, "dbCrosslinks": 115,
              "role": "public", "auth": false }
}
```

`lifetime` и `topGames` берутся из `data/stats.json` и **переживают перезапуск сервера**:
пик онлайна, число сессий, суммарное время в игре, по каждой игре пик и время. В публичном
ответе нет `logins`, `chats`, `timeouts` и трафика — они только в админском.

### Посещаемость доски

`POST /api/visit` (раз в ~45 с с каждой открытой вкладки) и блок `visits` в `/api/status`:

```json
{ "ok": true, "enabled": true, "online": 3, "peakOnline": 11,
  "today": 27, "todayVisitors": 14, "total": 4312, "totalVisitors": 908,
  "days": [{ "d": "2026-11-07", "v": 27, "u": 14 }] }
```

- `online` — сколько вкладок открыто прямо сейчас (окно 90 с без пинга);
- `today` / `total` — просмотры (один посетитель = один просмотр раз в 30 минут);
- `todayVisitors` / `totalVisitors` — уникальные за день / за всё время;
- `days` — последние 14 дней для мини-графика.

**Приватность:** посетитель = усечённый SHA-256 от `IP + User-Agent + соль сервера`.
Хэш живёт только в памяти, на диск не пишется и наружу не отдаётся — публикуются
только счётчики. Ни IP, ни UA, ни cookie. Хранится в `data/visits.json`
(`web.visits=false` выключает).

### Публичный чат доски

```
GET  /api/chat?since=<seq>&limit=60   -> { ok, enabled, cooldown, maxLength, messages[], lastSeq }
GET  /api/chat/captcha                -> { ok, id, svg, ttl }
POST /api/chat/send  {nick,text,captcha,answer}
     -> { ok:true, sentToGame:N, message:{seq,ts,nick,text,src} }
     -> { ok:false, error:"captcha"|"cooldown"|"flood"|"duplicate", retryAfter:N }
```

- `src` = `web` (гость доски), `game` (игрок в PPSSPP), `server` (системное);
- сообщения из игры попадают в ленту доски автоматически, а сообщения с доски —
  в игровой чат PPSSPP (`web.chatToGame=true`), с префиксом `[web] ник`;
- капча: 4 цифры, нарисованные **7-сегментными путями** (в разметке нет `<text>`,
  поэтому код нельзя выдрать парсингом SVG), одноразовая, живёт 240 с;
- лимиты: кулдаун `web.chatCooldownSec` (20 с), не больше `web.chatMaxPer10Min` (8)
  за 10 минут, повторы одного текста в течение 5 минут отклоняются;
- `web.chatEnabled=false` полностью выключает чат (панель скрывается).

`relay` в публичном ответе — только `enabled` и число сессий. В админском добавляются
`port`, `maxSessions`, `pairs`, `pdpFrames`, `pdpDrops`, `bytesIn`, `bytesOut`
(см. [RELAY.md](RELAY.md)).

В админском ответе у каждого игрока дополнительно есть `mac`, `ip`, `idleSec`, `bytesIn/Out`,
`pings`, `hairpin` (источник = шлюз сервера, подозрение на NAT hairpin) и `relaySessions`
(сколько relay-сессий у его MAC; `0` при включённом релее = прямой P2P).

Типы событий в публичной ленте: `join`, `leave`, `game.new`, `group.join`, `group.leave`.

## Админский API

### Шаг 0. Параметры входа

```http
GET /api/auth
→ {"ok":true,"scheme":"hmac-sha256+ctr","salt":"1a3e…","iterations":12000,
   "encrypt":true,"role":"admin","skewSec":180,"ts":1793650228}
```

### Подпись каждого запроса

```
K     = PBKDF2-HMAC-SHA256(adminToken, salt, iterations, 32)
kmac  = HMAC-SHA256(K, "dpt.mac.v1")
canonical = METHOD \n path?query \n unixTs \n nonce \n hex(SHA256(wireBody))
X-Auth-Sig = hex(HMAC-SHA256(kmac, canonical))
```

Заголовки: `X-Auth-Ts`, `X-Auth-Nonce` (hex ≥16 символов), `X-Auth-Sig`.
Окно по времени ±180 с, каждый nonce одноразовый (кэш 10 минут).

`path?query` — **относительный** путь приложения (`/api/status?limit=20`), поэтому схема
работает и когда reverse proxy срезает префикс, и когда пробрасывает его целиком.

### Шифрование тела

Если `adminEncrypt=true`, тело запроса и **любой** JSON-ответ шифруются:

```
envelope = base64( iv16 | HMAC-CTR(HMAC(K,"dpt.enc.v1"), iv) XOR data | HMAC(kmac, iv|data)[0..16] )
```

Запрос: заголовок `X-Auth-Enc: 1`, тело — конверт (base64-строка).
Ответ: заголовок `X-Enc-Resp: 1`, тело — конверт.

### Эндпоинты

| Метод | Путь | Что делает |
|---|---|---|
| GET | `/api/status` | полный снапшот (+ MAC, IP, id, idle, байты) |
| GET | `/api/games` | каталог (+ crosslink) |
| POST | `/api/games` | `{"id":"ULUS10084","name":"…","crosslink":""}` |
| GET | `/api/history?limit=100` | история (+ MAC/IP/трафик) |
| GET | `/api/logs?since=0&limit=200&level=all&cat=PROTO` | хвост лога |
| GET | `/api/bans` | активные баны |
| POST | `/api/bans/clear` | сброс банов |
| POST | `/api/kick` | `{"id":"…"}` / `{"mac":"…"}` / `{"name":"…"}` |
| POST | `/api/chat` | `{"text":"…","sender":"SERVER"}` |
| POST | `/api/reload` | перечитать `data/games.json` |
| GET | `/api/health` | без подписи |
| GET | `/api/auth` | без подписи (только соль/итерации) |
| GET | `/` | админская доска (`admin.html`) |

`GET /api/health` и `GET /api/auth` подписи не требуют — иначе не с чего начать.
Всё остальное без валидной подписи → `401`.

## Примеры

Быстрее всего — готовый клиент `tools/admin-api.ps1` (он использует ту же реализацию
`ApiCrypto`, что и сервер, поэтому не может «разъехаться»):

```powershell
. .\tools\admin-api.ps1
Connect-DptAdmin -Base http://127.0.0.1:44502 -Token (Select-String -Path .\bin\config.ini -Pattern '^adminToken=').Line.Split('=')[1]

Invoke-DptAdmin -Method GET  -Target '/api/status'
Invoke-DptAdmin -Method GET  -Target '/api/logs?limit=50&level=Warn'
Invoke-DptAdmin -Method POST -Target '/api/kick' -Payload @{ name = 'Kai' } -Encrypt
Invoke-DptAdmin -Method POST -Target '/api/chat' -Payload @{ text = 'server restart in 5 min' } -Encrypt
```

Публичная часть — обычный curl:

```bash
curl http://localhost:44501/api/status | python -m json.tool
curl http://localhost:44501/api/games?limit=10
curl http://localhost:44501/status.xml
```

## Коды ответов

| Код | Когда |
|---|---|
| 200 | успех |
| 304 | статика: `If-None-Match` / `If-Modified-Since` |
| 400 | битый JSON/параметры, path traversal |
| 401 | нет/неверная подпись, просроченный `ts`, повторный nonce |
| 403 | админский порт и не-приватный адрес клиента, логи отключены |
| 404 | неизвестный эндпоинт, скрытый на публичном порту, кик никого не нашёл |
| 405 | неверный метод |
| 413 | тело больше лимита |
| 429 | rate limit или блок после серии неудачных подписей |
| 500 | внутренняя ошибка (детали только в логе) |
| 503 | превышен лимит одновременных соединений |

## Совместимость с reverse proxy

- `X-Forwarded-For` / `X-Real-IP` учитываются только от доверенных пиров
  (`web.trustedProxies`, по умолчанию loopback + приватные подсети).
- `X-Forwarded-Prefix` и `web.publicBasePath` срезаются из пути до маршрутизации.
- Ссылки и API-вызовы в UI относительные → работают и на поддомене, и на `/adhoc/`.
- `Connection: keep-alive` поддерживается, но не обязателен: NPM может закрывать соединения.
- `GET /api/health` годится для health-check.