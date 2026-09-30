# Модули All-in-one

Как устроен модуль, как его описать для каталога и как программе «разговаривать» с каркасом.

## Виды модулей

| Вид (`kind`) | Что это | Примеры |
|---|---|---|
| `external` | Отдельная программа. Каркас запускает её, следит за процессом и управляет по IPC | TryToCatchMe, fDimmer, magniF |
| `adapter:<имя>` | Адаптер, встроенный в каркас, над чужой программой. Сама программа (payload) качается из её релизов | `adapter:zapret`, `adapter:tgwsproxy` |
| `builtin:<имя>` | Встроенный модуль без payload | `builtin:shutdown-timer` |

Новая программа почти всегда подключается как `external`: достаточно записи в каталоге и режима `--hosted` в самой программе.

## Раскладка на диске

```
<папка каркаса>\
  AllInOne.exe
  data\                  настройки каркаса, состояние модулей, кэши, логи
  modules\<id>\
    module.json          манифест установленной версии
    payload\             файлы программы — заменяются при обновлении целиком
    data\                данные модуля — переживают обновления
  staging\  backup\      скачанное обновление и прежняя версия для отката
```

## module.json и запись каталога

Формат один и тот же. В каталоге нет `version`: каркас сам спрашивает последнюю версию у GitHub Releases, поэтому каталог меняется только при появлении нового модуля.

```json
{
  "schema": 1,
  "id": "fdimmer",
  "name": "fDimmer",
  "description": "Затемнение экрана…",
  "category": "Экран",
  "author": "Solevaral",
  "homepage": "https://github.com/Solevaral/fDimmer",
  "kind": "external",
  "minHostVersion": "0.1.0",
  "source": {
    "type": "github",
    "repo": "Solevaral/fDimmer",
    "asset": "fDimmer-*-win-x64-standalone.exe",
    "assetNet9": "fDimmer-*-win-x64-net9.exe",
    "layout": "file",
    "saveAs": "fDimmer.exe"
  },
  "run":    { "exe": "payload/fDimmer.exe", "args": ["--hosted", "--pipe", "{pipe}"] },
  "detect": { "mutex": "Local\\fDimmer.SingleInstance", "process": "fDimmer.exe" },
  "ipc":    { "protocol": 1 },
  "stop":   { "strategy": "ipc", "timeoutSec": 10, "fallback": "ask" },
  "preserve": [],
  "legacyAutostart": { "runKey": "fDimmer" }
}
```

| Поле | Смысл |
|---|---|
| `source.asset` | Шаблон имени файла в релизе (`*` — любая подстрока) |
| `source.assetNet9` | Лёгкий вариант. Берётся, если на машине есть .NET 9 Desktop Runtime |
| `source.assetArm64` | Вариант для Windows на ARM |
| `source.layout` | `file` — положить файл в payload под именем `saveAs`; `zip` — распаковать, отрезав общую верхнюю папку |
| `run.args` | `{pipe}` заменяется именем канала (`AllInOne.<id>`) |
| `detect.mutex` | Мьютекс единственного экземпляра. Занят, а нашего процесса нет — значит, запущена отдельная копия программы |
| `detect.port` | Порт, который слушает программа. После остановки каркас ждёт, пока он освободится |
| `stop.strategy` | `ipc` — команда `shutdown` по каналу; `close` — закрыть главное окно; `processTree` — завершить дерево процессов (только для программ, которым нечего освобождать) |
| `stop.fallback` | `ask` — если программа не завершилась за `timeoutSec`, спросить пользователя (подождать, завершить принудительно, отменить); `kill` — завершить без вопроса |
| `preserve` | Пути внутри `payload/`, которые переносятся в новую версию (glob: `*`, `**`). Папка `data/` сохраняется всегда |
| `legacyAutostart.runKey` | Собственный автозапуск программы (`HKCU\…\Run`). Каркас убирает его, чтобы программа не стартовала дважды |
| `minHostVersion` | Если каркас старше, запись видна в каталоге, но установить её нельзя |

## Каталог

- **Встроенный** — `catalog/builtin-catalog.json`, зашит в exe. Работает без сети.
- **Удалённый** — `catalog.json` в репозитории [Solevaral/All-in-one-modules](https://github.com/Solevaral/All-in-one-modules). Его запись с тем же `id` перекрывает встроенную. Последний загруженный кэшируется в `data\catalog.cache.json`.
- **Из файла** — «Каталог → Установить из файла…»: zip с `module.json` в корне и папкой `payload`.

Чтобы добавить программу, допишите запись в удалённый `catalog.json`. Выпускать новую версию каркаса для этого не нужно.

## Обновления

1. Скачать ассет в `staging`, сверить размер и sha256 (`digest` из GitHub API).
2. **Бережно остановить** модуль (`shutdown` по IPC). Если он не ответил за `timeoutSec`, спросить пользователя.
3. Убедиться, что файлы свободны: в папке модуля не осталось процессов.
4. Перенести прежний `payload` в `backup`, положить новый, вернуть пути из `preserve`.
5. Запустить модуль, если он работал до обновления, и дождаться статуса «работает».
6. Если что-то не так — откат из `backup`.

Автообновление (галочка у модуля): остановленный модуль обновляется сразу, работающий — при следующем запуске каркаса, до запуска модулей.

## Протокол HostLink (версия 1)

Именованный канал `\\.\pipe\AllInOne.<id>`. **Сервер — программа**, клиент — каркас. Поэтому программа не зависит от каркаса: если каркас перезапустился (например, после самообновления), он просто подключается снова. Каркас проверяет, что канал открыт процессом из папки этого модуля.

Сообщения — JSON, по одному на строку (UTF-8, `\n`):

```
→ {"id":1,"method":"hello","params":{"protocol":1,"host":"AllInOne"}}
← {"id":1,"result":{"protocol":1,"appVersion":"1.3.0","processId":1234,
                    "capabilities":{"actions":[{"id":"settings","title":"Настройки…"}]}}}
→ {"id":2,"method":"getStatus"}
← {"id":2,"result":{"state":"running","summary":"Яркость экрана 60%","detail":"общий"}}
← {"event":"statusChanged","data":{"state":"running","summary":"…"}}
← {"event":"notify","data":{"title":"fDimmer","text":"…"}}
→ {"id":3,"method":"shutdown","params":{"reason":"update"}}
← {"id":3,"result":{"accepted":true}}
```

| Метод | Что делает программа |
|---|---|
| `hello` | Сообщает версию протокола и программы, PID и действия для кнопок каркаса |
| `getStatus` | `state` (`running` / `error` / `busy`), `summary` — короткая строка для плитки, `detail` — подробности |
| `showWindow` | Показывает главное окно |
| `invoke` | `{"action":"<id>"}` — выполняет действие из `hello` |
| `shutdown` | Отвечает `{"accepted":true}`, **освобождает всё, что держит** (системный прокси, гамму экрана, курсор, хуки ввода) и завершает процесс |

Ошибка метода: `{"id":N,"error":{"code":"unknownMethod","message":"…"}}`.

Правила режима `--hosted`:
- нет своей иконки в трее (уведомления уходят каркасу событием `notify`);
- нет своего переключателя автозапуска, свой Run-ключ не ставится;
- мьютекс единственного экземпляра прежний — отдельная копия и копия из каркаса не запустятся одновременно;
- если каркас отключился, программа продолжает работать и ждёт нового подключения.

Готовые реализации:
- .NET — один файл [`docs/reference/HostLink.cs`](reference/HostLink.cs), без зависимостей (используется в fDimmer и magniF);
- Rust/Tauri — `src-tauri/src/hostlink/mod.rs` в [TryToCatchMe-client](https://github.com/Solevaral/TryToCatchMe-client).

Отладка без каркаса:

```powershell
.\tools\hostlink-probe.ps1 -Pipe AllInOne.fdimmer -Methods hello,getStatus
```
