# Публичный статус для списка серверов PPSSPP

PPSSPP умеет показывать список ad hoc серверов (`Network → Ad Hoc server`) с живым
статусом: игры, комнаты, ники. Список серверов — это JSON в репозитории PPSSPP
(`assets/adhoc-servers.json`), а статус сервер отдаёт сам по ссылке из поля
`status_data_json` (или `status_xml`).

Наш сервер отдаёт оба формата из публичной доски:

| Что | URL | Формат |
|---|---|---|
| живой статус | `https://<host>/data.json` | JSON (основной, его и просим в список) |
| живой статус | `https://<host>/status.xml` | XML (`<prometheus>`), для старых клиентов и ботов |
| human | `https://<host>/` | обычная публичная доска |

Алиас: `/status.json` = `/data.json`.

## Как это устроено

`GET /data.json` (публичный порт, `web.publicPort`, по умолчанию `44501`):

```json
{
  "name": "DPT.PSP AdHoc Server",
  "relay": true,
  "updatedUtc": "2026-11-03T00:00:00.000Z",
  "usercount": 3,
  "games": [
    {
      "name": "Ace Combat X2 - Joint Assault",
      "usercount": 3,
      "game_ids": ["ULUS10511", "NPJH50263", "ULES01408"],
      "groups": [
        {
          "name": "DJ01",
          "usercount": 2,
          "users": [
            { "name": "DataA", "pdp_ports": [5911, 10001] },
            { "name": "DataB", "pdp_ports": [] }
          ]
        },
        { "name": "Groupless", "usercount": 1, "users": [{ "name": "DataC" }] }
      ]
    }
  ]
}
```

- `game_ids` — канонический product ID **плюс все crosslink-алиасы** (регионы).
  PPSSPP по ним подтягивает своё название игры, поэтому US/EU/JP копии выглядят
  одинаково.
- `Groupless` — игроки, которые уже в игре, но ещё не в комнате. PPSSPP показывает
  их как «Players waiting».
- `pdp_ports` — порты PDP-сессий игрока в релее (то, что реально видит сервер).
  Если игрок в прямом P2P — массив пустой.
- Счётчики — числа (PPSSPP принимает и числа, и строки, но числа чище).

**Чего здесь нет и не будет:** MAC, IP, session id, hairpin-флагов, байтов, логов.
Ровно то же, что на публичной доске.

Заголовки: `Cache-Control: public, max-age=10`, `Access-Control-Allow-Origin: *`,
`Content-Type: application/json`. CORS нужен сторонним статус-страницам
(например `lobby-status.html` у ArenaAnywhere), которые читают `data.json` из браузера.

## Настройки (`config.ini`, секция `[web]`)

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `statusEndpoints` | `true` | отдавать `/data.json` и `/status.xml` (иначе 403) |
| `statusPorts` | `true` | публиковать `pdp_ports` |
| `serverName` | `DPT.PSP AdHoc Server` | имя в ответе |
| `serverLocation` | *(пусто)* | регион, для списка серверов |
| `serverDescription` | *(пусто)* | описание, для списка серверов |

## Заявка в список серверов PPSSPP

Список живёт в `assets/adhoc-servers.json` в репозитории PPSSPP
([hrydgard/ppsspp](https://github.com/hrydgard/ppsspp)). Нужен pull request,
добавляющий объект в массив `servers`:

```json
{
  "name": "DPT.PSP AdHoc",
  "host": "play.dartpower.xyz",
  "discord": "",
  "web": "https://play.dartpower.xyz",
  "location": "Lithuania",
  "location-emoji": "🇱🇹",
  "description": "PSP ad hoc server with packet relay, public lobby board and 3900+ game titles",
  "data_mode": "AemuPostoffice",
  "status_data_json": "https://play.dartpower.xyz/data.json",
  "icon": "/assets/img/favicon.svg"
}
```

Поля, которые читает клиент (`Core/HLE/sceNetAdhoc.cpp`, `ParseServerListEntriesJSON`):

| Поле | Обязательно | Что делает |
|---|---|---|
| `name` | да | подпись в списке |
| `host` | да | адрес control-сервера (`27312`) и релея (`27313`) |
| `data_mode` | да | `AemuPostoffice` — наш случай (есть релей); иначе `P2P` |
| `status_data_json` | нет | URL `/data.json` |
| `status_data_json_2` | нет | второй URL (дубль, клиент берёт последний непустой) |
| `status_xml` | нет | URL `/status.xml`, используется если нет `data_json` |
| `status_web` | нет | ссылка «открыть в браузере» |
| `web`, `discord`, `location`, `location-emoji`, `description`, `ip`, `icon` | нет | оформление карточки |

`data_mode: "AemuPostoffice"` — важно: по нему PPSSPP предлагает включить
«Try to use server-provided packet relay», а без него клиент считает сервер
P2P-шным и релей не использует.

## Подводные камни

1. **PPSSPP скачивает статус по HTTP.** В `AdhocServerScreen.cpp` вызывается
   `http::RemoveHttpsIfNeeded(...)` — `https://` превращается в `http://`.
   Значит `https://host/data.json` должен быть доступен и по `http://host/data.json`
   (80-й порт), **без редиректа на https**: редирект клиент не отработает.
   Если 80-й порт закрыт — укажите в списке прямой порт:
   `http://host:44501/data.json` (публичный порт доски должен быть открыт).
2. **Реверс-прокси.** Работает, если прокси отдаёт `/data.json` как есть
   (sub-path тоже поддержан: см. `web.publicBasePath`). CORS-заголовок при этом
   не должен теряться — наш сервер ставит его сам.
3. **Пустой сервер — это нормально.** Если online 0, `games` будет `[]`,
   PPSSPP покажет «no players». Не отключайте `statusEndpoints`, иначе карточка
   сервера будет пустой с ошибкой.
4. **Проверка перед PR:**

   ```powershell
   (Invoke-RestMethod http://127.0.0.1:44501/data.json).games | Select-Object name, usercount
   ```