# HTTP API

Базовый URL: `http://<host>:8080` (порт может быть другим — см. лог/консоль).
Все ответы — `application/json; charset=utf-8` (кроме `/status.xml`).
Gzip включается автоматически при `Accept-Encoding: gzip` и размере ≥ 1 KB.

Мутирующие ручки (`POST`) требуют заголовок `X-Admin-Token`, **если** в `config.ini`
задан `web.adminToken`. Иначе доступны без токена (удобно в локальной сети).

## GET `/api/status`

Главный снапшот доски. Обновляется фронтендом каждые N секунд.

```json
{
  "ok": true,
  "nowUtc": "2026-11-02T19:34:18.193Z",
  "server": {
    "name": "DPT.Agent AdHoc Server", "version": "1.0.0", "build": "2026-11-02 22:32",
    "startedUtc": "2026-11-02T19:32:56.120Z", "uptimeSec": 82,
    "adhocPort": 27312, "webPort": 8080, "webUrl": "http://localhost:8080/",
    "host": "DPTSRV0", "os": "Microsoft Windows NT 6.2.9200.0", "clr": "4.0.30319.42000",
    "cpus": 8, "memoryMB": 26.7, "protocol": "PRO Online / PPSSPP adhocctl", "logLevel": "Info"
  },
  "games": [{
    "id": "ULUS10084", "rawId": "ULES00318", "name": "Monster Hunter Freedom", "known": true,
    "players": 2, "peak": 3, "totalPlayers": 9,
    "createdUtc": "…", "lastActivityUtc": "…",
    "groups": [{
      "name": "ABCD1234", "players": 2, "host": "02:00:00:15:00:01", "createdUtc": "…",
      "members": [{
        "id": "fb19d74e43", "name": "DbgOne", "mac": "02:00:00:15:00:01", "ip": "127.0.0.1",
        "connectUtc": "…", "connectedSec": 61, "idleSec": 1,
        "bytesIn": 153, "bytesOut": 146, "pings": 30, "isHost": true
      }]
    }],
    "idle": []
  }],
  "stats": {
    "online": 2, "peak": 3, "games": 2, "groups": 1,
    "connections": 11, "logins": 11, "chats": 2, "timeouts": 0,
    "protocolErrors": 0, "kicks": 0, "bans": 0, "duplicateMacs": 0,
    "history": 9, "events": 36, "bytesIn": 306, "bytesOut": 292,
    "webRequests": 18, "eventSeq": 36
  },
  "recent": [{
    "name": "HolderThree", "gameId": "ULUS10391", "gameName": "Monster Hunter Freedom Unite",
    "mac": "02:00:00:0D:00:01", "ip": "127.0.0.1", "group": "ABCD1234",
    "connectUtc": "…", "disconnectUtc": "…", "durationSec": 25.4, "reason": "timeout"
  }],
  "events": [{
    "seq": 36, "timeUtc": "…", "type": "join", "gameId": "ULUS10084",
    "gameName": "Monster Hunter Freedom", "player": "Kai", "text": "Kai started playing Monster Hunter Freedom"
  }],
  "unknownIds": ["ULJS00999"],
  "config": {
    "theme": "light", "sound": true, "refreshMs": 2000, "timeoutSec": 25, "maxUsers": 256,
    "exposeLogs": true, "adminRequired": false,
    "historySize": 300, "eventSize": 400, "dbGames": 162, "dbCrosslinks": 115
  }
}
```

`type` событий: `join`, `leave`, `group.join`, `group.leave`, `game.new`, `chat`, `error`, `ban`, `kick`.
`reason` завершения сессии: `peer-closed`, `client-request`, `timeout`, `kicked`, `shutdown`,
`session-lifetime`, `protocol-error`, `send-failed`, `io-error`, `duplicate-mac`, `rx-overflow`.

## GET `/api/games`

```json
{
  "ok": true, "count": 162, "crosslinks": 115,
  "games": [{ "id": "ULUS10084", "name": "Monster Hunter Freedom" },
            { "id": "ULES00318", "name": "Monster Hunter Freedom", "crosslink": "ULUS10084" }],
  "unknownIds": []
}
```

## POST `/api/games`

Добавить/переименовать игру или связать регионы.

```bash
curl -X POST http://localhost:8080/api/games \
  -H "Content-Type: application/json" \
  -d '{"id":"ULUS10084","name":"Monster Hunter Freedom","crosslink":""}'
```

Ответ: `{"ok":true,"id":"ULUS10084","name":"…","crosslink":"","count":163}`.
`id` — ровно 9 символов `[A-Z0-9]`. Пустой `crosslink` ничего не меняет; чтобы убрать связь,
передайте `crosslink` основного ID (или правьте `data/games.json` вручную).

## GET `/api/history?limit=100`

Последние завершённые сессии (новые первыми), поля как в `recent`, плюс `bytesIn`/`bytesOut`.

## GET `/api/logs?since=0&limit=200&level=all&cat=PROTO`

Хвост лога из памяти (ring buffer, без чтения файла).

```json
{
  "ok": true, "seq": 128,
  "lines": [{ "seq": 127, "t": "22:36:18.756", "utc": "…", "lvl": "D", "cat": "PROTO",
              "msg": "LOGIN Kai mac=…" }]
}
```

`level`: `all|Error|Warn|Info|Debug|Verbose`. `since` — вернуть только записи с `seq > since`.
Инкрементальный опрос: фронтенд присылает последний известный `seq`.
Отключается `web.exposeLogs=false` (тогда 403). Если задан `web.adminToken`, лог тоже
требует `X-Admin-Token` (в UI токен вводится в настройках).

## GET `/api/bans`

`{"ok":true,"count":1,"bans":[{"ip":"1.2.3.4","secondsLeft":"87"}]}`

## POST `/api/bans/clear`

Очистить список временных банов: `{"ok":true,"cleared":3}`.

## POST `/api/kick`

```json
{ "id": "fb19d74e43" }
```

Селектор — одно из полей: `id` (session id), `mac`, `name` (ник), либо `?q=` в query.

```json
{ "ok": true, "kicked": 1, "detail": "Kai / 02:00:00:15:00:01 / 127.0.0.1" }
```

Нет совпадений → `404` и `"ok":false`.

## POST `/api/chat`

Отправить сообщение в игровой чат (PPSSPP покажет его в группе).

```json
{ "text": "Сервер перезапустится через 5 минут", "sender": "SERVER" }
```

Ответ: `{"ok":true,"sent":3,"text":"…"}`. Текст обрезается до 63 байт (лимит протокола).

## POST `/api/reload`

Перечитать `data/games.json` с диска (после ручной правки).
Ответ: `{"ok":true,"games":162}`.

## GET `/api/health`

`{"ok":true,"name":"…","version":"1.0.0","uptimeSec":82}` — для мониторинга, без токена.

## GET `/status.xml`

Легаси-формат PRO Online:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<prometheus usercount="2">
	<game name="Monster Hunter Freedom" usercount="2">
		<group name="ABCD1234" usercount="2">
			<user>Kai</user>
			<user>Ryo</user>
		</group>
	</game>
</prometheus>
```

## Коды ответов

| Код | Когда |
|---|---|
| 200 | успех |
| 304 | статика: `If-None-Match` / `If-Modified-Since` совпали |
| 400 | битый JSON или параметры |
| 401 | нужен/неверный `X-Admin-Token` |
| 403 | логи отключены |
| 404 | неизвестный endpoint/файл, кик никого не нашёл |
| 405 | неверный метод |
| 413 | тело запроса больше лимита |
| 429 | превышен `web.requestsPerMinute` |
| 500 | внутренняя ошибка (пишется в лог) |
| 503 | превышен `web.maxConcurrentRequests` |

## Примеры

```bash
curl http://localhost:8080/api/status | python -m json.tool
curl -X POST http://localhost:8080/api/kick -d '{"name":"Kai"}'
curl -X POST http://localhost:8080/api/chat -d '{"text":"hello"}'
curl "http://localhost:8080/api/logs?since=0&limit=50&level=Warn"
```

С токеном:

```bash
curl -X POST http://localhost:8080/api/kick \
  -H "X-Admin-Token: hunter2" -d '{"mac":"02:00:00:15:00:01"}'
```