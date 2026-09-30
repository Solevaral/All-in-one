<p align="center"><img src="docs/logo.png" width="128" alt="All in One"></p>

# All in One

Каркас для программ под Windows: программы ставятся в него из каталога как модули, обновляются и запускаются вместе с ним.

## Модули

| Модуль | Что делает | Источник |
|---|---|---|
| **zapret** | Обход блокировок Discord и YouTube: стратегии, служба Windows, Game Filter, IPSet, свои списки, диагностика, тесты | [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) |
| **TG WS Proxy** | Локальный MTProto-прокси для Telegram | [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) |
| **TryToCatchMe** | VPN-клиент на sing-box | [Solevaral/TryToCatchMe-client](https://github.com/Solevaral/TryToCatchMe-client) |
| **fDimmer** | Затемнение экрана вместе с системным интерфейсом | [Solevaral/fDimmer](https://github.com/Solevaral/fDimmer) |
| **magniF** | Лупа по удержанию клавиши | [Solevaral/magniF](https://github.com/Solevaral/magniF) |
| **Таймер выключения** | Выключение, перезагрузка или сон по времени или через интервал | встроен |

Каталог модулей на GitHub: [All-in-one-modules](https://github.com/Solevaral/All-in-one-modules). Формат модулей и протокол — [docs/MODULES.md](docs/MODULES.md).

## Установка

[Releases](https://github.com/Solevaral/All-in-one/releases), установщик на выбор:

- `AllInOne-<версия>-setup-net9.exe` — нужен [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0);
- `AllInOne-<версия>-setup-standalone.exe` — .NET внутри.

Папка по умолчанию — `C:\Program Files\AllInOne`. Пути с не-латинскими символами и OneDrive установщик не принимает: zapret из них не работает.

```
C:\Program Files\AllInOne\
  AllInOne.exe
  modules\<Имя>\   программы модулей, запускаются и без All in One
  data\            настройки, каталог, логи, данные модулей
```

All in One работает от администратора: zapret ставит драйвер WinDivert и службы. Автозапуск при входе в Windows (в «Настройках») — задача Планировщика с наивысшими правами, без окна UAC.

Удаление — «Программы и компоненты»: модули останавливаются, установщик спрашивает, удалять ли папки `modules` и `data`.

## Работа модулей

- **Остановка.** Перед обновлением, выходом и выключением по таймеру модуль останавливается командой программы: VPN возвращает системный прокси, zapret отпускает драйвер, fDimmer — яркость, magniF — курсор. Не ответил — вопрос: ждать, завершить или отменить.
- **Обновления.** Версии — из GitHub Releases модулей. Обновление кнопкой или автоматически по галочке модуля; запущенный модуль обновляется при следующем запуске All in One. Новая версия не запустилась — возвращается прежняя.
- **Независимость.** Модули — отдельные процессы. При выходе из All in One их можно оставить работать; при следующем запуске, в том числе после обновления All in One, связь с ними восстанавливается.

## Сборка

.NET 9 SDK:

```bash
dotnet build AllInOne.sln -c Release
```

```bash
dotnet test tests/AllInOne.Tests/AllInOne.Tests.csproj
```

Установщик (Inno Setup 6.3+). Варианты делят папку `obj`, между ними — очистка `src/AllInOne.Host/obj` и `bin`:

```bash
dotnet publish src/AllInOne.Host/AllInOne.Host.csproj -c Release -p:AllInOneFlavor=net9 -o publish/net9
```

```bash
iscc /DAppVersion=0.2.0 /DFlavor=net9 installer/AllInOne.iss
```

## Устройство

```
src/AllInOne.Sdk      контракт модуля, module.json, протокол HostLink
src/AllInOne.Core     каталог, установка и обновление, процессы и службы, IPC-клиент
src/AllInOne.Ui       общие элементы интерфейса и диалоги
src/AllInOne.Host     окно, трей, страницы
modules/              встроенные модули: zapret, TG WS Proxy, таймер выключения
catalog/              встроенный каталог
installer/            установщик (Inno Setup)
docs/                 MODULES.md, reference/HostLink.cs
```

## Лицензия

MIT. zapret и WinDivert — на условиях своих лицензий, скачиваются из собственных релизов.
