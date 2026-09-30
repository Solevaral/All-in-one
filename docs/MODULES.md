# Модули All in One

Устройство модуля, его запись в каталоге и протокол связи программы с All in One.

## Виды модулей

| Вид (`kind`) | Что это | Примеры |
|---|---|---|
| `external` | Отдельная программа: All in One запускает её, следит за процессом и управляет по IPC | TryToCatchMe, fDimmer, magniF |
| `adapter:<имя>` | Встроенный в All in One адаптер над чужой программой; сама программа качается из её релизов | `adapter:zapret`, `adapter:tgwsproxy` |
| `builtin:<имя>` | Встроенный модуль без программы | `builtin:shutdown-timer` |

Новая программа подключается как `external`: запись в каталоге и режим `--hosted` в самой программе.

## Раскладка папки установки

```
C:\Program Files\All in One\
  AllInOne.exe
  modules\
    zapret\            программа целиком, как в её релизе (bin, lists, general*.bat …)
    TG WS Proxy\       TgWsProxy.exe, TgWsProxy_data\
    TryToCatchMe\      TryToCatchMe.exe, binaries\
    fDimmer\           fDimmer.exe
    magniF\            magniF.exe
  data\
    host.json, modules.json, catalog.cache.json, logs\
    modules\<id>\      module.json установленной версии и данные модуля (переживают обновления)
    staging\  backup\  скачанное обновление и прежняя версия для отката
```

Программы в `modules` запускаются и без All in One.

## module.json и запись каталога

Формат общий. В каталоге нет `version`: последнюю версию All in One берёт из GitHub Releases, поэтому каталог меняется только при появлении нового модуля.

```json
{
  "schema": 2,
  "id": "fdimmer",
  "name": "fDimmer",
  "folder": "fDimmer",
  "description": "Затемнение экрана…",
  "category": "Экран",
  "author": "Solevaral",
  "homepage": "https://github.com/Solevaral/fDimmer",
  "kind": "external",
  "minHostVersion": "0.2.0",
  "source": {
    "type": "github",
    "repo": "Solevaral/fDimmer",
    "asset": "fDimmer-*-win-x64-standalone.exe",
    "assetNet9": "fDimmer-*-win-x64-net9.exe",
    "layout": "file",
    "saveAs": "fDimmer.exe"
  },
  "run":    { "exe": "fDimmer.exe", "args": ["--hosted", "--pipe", "{pipe}"] },
  "detect": { "mutex": "Local\\fDimmer.SingleInstance", "process": "fDimmer.exe" },
  "ipc":    { "protocol": 1 },
  "stop":   { "strategy": "ipc", "timeoutSec": 10, "fallback": "ask" },
  "preserve": [],
  "legacyAutostart": { "runKey": "fDimmer" }
}
```

| Поле | Смысл |
|---|---|
| `schema` | 2 — текущая схема. Записи другой схемы пропускаются |
| `folder` | Папка программы в `modules\`. По умолчанию — `name` |
| `source.asset` | Шаблон имени файла в релизе (`*` — любая подстрока) |
| `source.assetNet9` | Вариант для машины с .NET 9 Desktop Runtime |
| `source.assetArm64` | Вариант для Windows на ARM |
| `source.layout` | `file` — положить файл под именем `saveAs`; `zip` — распаковать, общая верхняя папка архива отрезается |
| `run.exe`, `run.args` | Путь к exe внутри папки программы; `{pipe}` заменяется именем канала `AllInOne.<id>` |
| `detect.mutex` | Мьютекс единственного экземпляра. Занят, а процесса из папки модуля нет — запущена отдельная копия |
| `detect.process` | Имя exe. Процесс с этим именем не из папки модуля — отдельная копия |
| `detect.port` | Порт программы. После остановки All in One ждёт его освобождения |
| `stop.strategy` | `ipc` — команда `shutdown`; `close` — закрыть главное окно; `processTree` — завершить дерево процессов (для программ, которым нечего освобождать) |
| `stop.fallback` | `ask` — программа не завершилась за `timeoutSec`: вопрос пользователю (ждать, завершить, отменить); `kill` — завершить без вопроса |
| `preserve` | Пути внутри папки программы (glob `*`, `**`), переносимые в новую версию. `data\modules\<id>` сохраняется всегда |
| `legacyAutostart.runKey` | Собственный автозапуск программы (`HKCU\…\Run`), который All in One убирает |
| `embeddable` | Окно программы можно встроить в окно All in One (экспериментальные функции) |
| `minHostVersion` | Более старый All in One показывает запись, но не устанавливает |

## Каталог

- **Встроенный** — `catalog/builtin-catalog.json`, зашит в exe, работает без сети.
- **На GitHub** — `catalog.json` в [Solevaral/All-in-one-modules](https://github.com/Solevaral/All-in-one-modules). Записи с тем же `id` перекрывают встроенные. Последний загруженный кэшируется в `data\catalog.cache.json`.
- **Из файла** — «Каталог → Установить из файла…»: zip с `module.json` в корне и папкой `program`.

Новая программа — новая запись в `catalog.json` на GitHub, без выпуска All in One.

## Обновления модуля

1. Ассет скачивается в `data\staging`, проверяются размер и sha256 (`digest` из GitHub API).
2. Модуль останавливается командой `shutdown`. Не ответил за `timeoutSec` — вопрос пользователю.
3. Проверка, что в папке программы не осталось процессов.
4. Папка программы переносится в `data\backup`, на её место — новая, пути из `preserve` возвращаются.
5. Модуль запускается, если работал, и должен перейти в «Работает».
6. Иначе — откат из `data\backup`.

Автообновление: остановленный модуль обновляется сразу, запущенный — при следующем запуске All in One, до запуска модулей.

Ошибки источника (нет сети, GitHub недоступен, репозиторий удалён, файла нет в релизе, лимит запросов) показываются на странице «Обновления» и уведомлением в трее.

## Обновление All in One

Скачивается установщик того же варианта (`AllInOne-<версия>-setup-net9.exe` или `-standalone.exe`) и запускается с `/VERYSILENT /UPDATE`. All in One закрывается, модули продолжают работать. Установщик заменяет `AllInOne.exe` и запускает его с `--post-update`, новая версия подключается к работающим модулям. Папки `modules` и `data` установщик не трогает.

Команды для установщика:
- `AllInOne.exe --exit` — закрыть All in One, модули продолжают работать;
- `AllInOne.exe --stop-all` — остановить все модули и закрыть All in One (перед удалением).

## Протокол HostLink (версия 1)

Именованный канал `\\.\pipe\AllInOne.<id>`. **Сервер — программа**, клиент — All in One: программа не зависит от All in One, после его перезапуска связь восстанавливается. All in One принимает канал, только если его сервер — процесс из папки модуля.

Сообщения — JSON, по одному на строку (UTF-8, `\n`):

```
→ {"id":1,"method":"hello","params":{"protocol":1,"host":"AllInOne"}}
← {"id":1,"result":{"protocol":1,"appVersion":"1.3.1","processId":1234,
                    "capabilities":{"actions":[{"id":"settings","title":"Настройки…"}]}}}
→ {"id":2,"method":"getStatus"}
← {"id":2,"result":{"state":"running","summary":"Яркость экрана 60%","detail":"общий"}}
← {"event":"statusChanged","data":{"state":"running","summary":"…"}}
← {"event":"notify","data":{"title":"…","text":"…"}}
→ {"id":3,"method":"shutdown","params":{"reason":"update"}}
← {"id":3,"result":{"accepted":true}}
```

| Метод | Что делает программа |
|---|---|
| `hello` | Версия протокола и программы, PID, действия для кнопок All in One |
| `getStatus` | `state` (`running` / `error` / `busy`), `summary` — строка для плитки, `detail` — подробности |
| `showWindow` | Показывает главное окно |
| `invoke` | `{"action":"<id>"}` — действие из `hello` |
| `shutdown` | Отвечает `{"accepted":true}`, освобождает системные ресурсы (системный прокси, гамму, курсор, хуки ввода) и завершает процесс |

Ошибка метода: `{"id":N,"error":{"code":"unknownMethod","message":"…"}}`.

Режим `--hosted`:
- иконка в трее остаётся;
- переключателя автозапуска нет, свой Run-ключ не ставится — автозапуском управляет All in One;
- мьютекс единственного экземпляра прежний;
- при отключении All in One программа продолжает работать и ждёт нового подключения.

Реализации:
- .NET — один файл [`docs/reference/HostLink.cs`](reference/HostLink.cs), без зависимостей (fDimmer, magniF);
- Rust/Tauri — `src-tauri/src/hostlink/mod.rs` в [TryToCatchMe-client](https://github.com/Solevaral/TryToCatchMe-client).

Отладка без All in One:

```powershell
.\tools\hostlink-probe.ps1 -Pipe AllInOne.fdimmer -Methods hello,getStatus
```
