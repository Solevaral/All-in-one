<p align="center"><img src="docs/logo.png" width="128" alt="All-in-one"></p>

# All-in-one

Одна программа для Windows вместо нескольких. Качается только каркас, а нужные программы ставятся в него из каталога как модули. Каркас сам их обновляет, запускает вместе с собой и бережно останавливает.

## Модули

| Модуль | Что делает | Откуда |
|---|---|---|
| **zapret** | Обход блокировок Discord и YouTube: выбор стратегии, служба Windows, Game Filter, IPSet, свои списки, диагностика, тесты | [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) |
| **TG WS Proxy** | Локальный MTProto-прокси для Telegram, подключение одной кнопкой | [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) |
| **TryToCatchMe** | VPN-клиент на sing-box | [Solevaral/TryToCatchMe-client](https://github.com/Solevaral/TryToCatchMe-client) |
| **fDimmer** | Затемнение экрана вместе с системным интерфейсом | [Solevaral/fDimmer](https://github.com/Solevaral/fDimmer) |
| **magniF** | Лупа по удержанию клавиши | [Solevaral/magniF](https://github.com/Solevaral/magniF) |
| **Таймер выключения** | Выключение, перезагрузка или сон в заданное время или через интервал | встроен |

Каталог пополняется без выпуска новой версии каркаса: удалённый список модулей лежит в [All-in-one-modules](https://github.com/Solevaral/All-in-one-modules). Свой модуль можно поставить из zip — см. [docs/MODULES.md](docs/MODULES.md).

## Скачать

Страница [Releases](https://github.com/Solevaral/All-in-one/releases), два варианта:

- `AllInOne-<версия>-win-x64-net9.zip` — лёгкий, нужен [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0);
- `AllInOne-<версия>-win-x64-standalone.zip` — всё внутри, ничего ставить не нужно.

Распакуйте в отдельную папку с латинскими буквами в пути, например `C:\AllInOne`. Не кладите её в OneDrive: zapret не работает из путей с кириллицей, а синхронизация мешает обновлениям.

Каркас запускается от администратора, потому что zapret ставит драйвер WinDivert и службы. В «Настройках» можно включить автозапуск при входе в Windows. Он делается через Планировщик задач с наивысшими правами, поэтому окна UAC при входе не будет.

## Как это работает

- **Бережная остановка.** Перед обновлением, выходом или выключением по таймеру каждый модуль останавливается штатно: VPN возвращает системный прокси, zapret отпускает драйвер, fDimmer — яркость, magniF — курсор. Если модуль не ответил, каркас спрашивает, что делать, и не убивает процесс молча.
- **Обновления.** Версии берутся прямо из GitHub Releases каждого модуля. Обновить можно кнопкой или включить автообновление у модуля. Работающий модуль тогда обновится при следующем запуске каркаса, чтобы не прерывать работу. Если новая версия не запустилась, возвращается прежняя.
- **Независимость от каркаса.** Модули — отдельные процессы. Каркас можно закрыть, оставив их работать, а при следующем запуске он подключится к ним снова. Так же проходит и самообновление каркаса.

## Сборка

Нужен .NET 9 SDK.

```bash
dotnet build AllInOne.sln -c Release
```

```bash
dotnet test
```

Публикация обоих вариантов. Варианты делят папку `obj`, поэтому между ними очищайте `src/AllInOne.Host/obj` и `bin` — иначе standalone соберётся без рантайма:

```bash
dotnet publish src/AllInOne.Host/AllInOne.Host.csproj -c Release -p:AllInOneFlavor=net9 -o publish/net9
```

```bash
dotnet publish src/AllInOne.Host/AllInOne.Host.csproj -c Release -p:AllInOneFlavor=standalone -o publish/standalone
```

## Устройство

```
src/AllInOne.Sdk      контракт модуля, module.json, протокол HostLink
src/AllInOne.Core     каталог, установка и обновление, процессы и службы, IPC-клиент
src/AllInOne.Ui       общие элементы интерфейса и диалоги
src/AllInOne.Host     окно, трей, страницы
modules/              встроенные модули: zapret, TG WS Proxy, таймер выключения
catalog/              встроенный каталог
docs/MODULES.md       как сделать модуль; docs/reference/HostLink.cs — готовая реализация протокола
```

## Лицензия

MIT. zapret и WinDivert распространяются на условиях своих лицензий и скачиваются из их собственных релизов.
