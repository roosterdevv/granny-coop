# Установка Granny Co-op

Мод для **Granny** (DVloper, Steam app 962400) — прототип кооператива на двоих.
Проверенная сборка игры: **v1.3.4**, buildid **21006637**, Unity 6000.0.58f2 (IL2CPP).

> Мод не содержит файлов игры. Всё, что он делает, — загружается в ваш клиент через
> MelonLoader. Ничего из папки игры не патчится на диске.

## Быстрый путь: установщик

Возьмите `GrannyCoop-Setup-1.0.0.exe` из релиза, закройте игру и запустите его.
В окне: папка игры (находится сама через Steam), роль **Хост** или **Гость**, имя,
адрес хоста и порт. Кнопка «Установить» делает всё: MelonLoader 0.7.3, .NET 6 (если его
нет), `GrannyCoop.dll`, настройки. Что заменяется, то сначала сохраняется в `*.bak`.

То же из командной строки:

```
GrannyCoop-Setup-1.0.0.exe --install "<папка игры>" --role host --name Ann --port 47777
GrannyCoop-Setup-1.0.0.exe --install "<папка игры>" --role join --address 192.168.1.10 --name Bob
```

`--silent` убирает вопросы, `--force` ставит на непроверенную сборку игры, `--skip-runtime`
не трогает .NET. Коды выхода: `0` — готово, `2` — не папка Granny, `3` — другая сборка,
`4` — игра запущена, `5` — ошибка записи, `6` — .NET 6 не установился.

Дальше — первый запуск игры: MelonLoader один раз соберёт Il2Cpp-сборки (1–2 минуты,
нужен интернет).

Ниже — ручная установка, если предпочитаете её или экзешник не запускается.

## Что нужно

- Granny из Steam (другая сборка может не совпасть по типам — мод не загрузится).
- **MelonLoader 0.7.3** — https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3
  (файл `MelonLoader.Installer.exe`, официальный релиз).
- Файл `GrannyCoop.dll` из релиза этого репозитория.
- Для игры вдвоём: у хоста — открытый UDP-порт **47777** (или виртуальная локальная сеть,
  например Radmin VPN / ZeroTier / Tailscale, тогда проброс портов не нужен).

## Шаг 1. MelonLoader

1. Закройте Granny.
2. Запустите `MelonLoader.Installer.exe`.
3. Выберите Granny в списке (или укажите путь к `Granny.exe`), версию **0.7.3**, нажмите
   **Install**.
4. Первый запуск игры после установки создаст рядом с `Granny.exe` папки `MelonLoader` и
   `Mods`. Дайте игре дойти до меню и закройте её.

Если установщик не видит игру, его можно запустить с ключом
`--game-path "<папка игры>"` (в кавычках).

## Шаг 2. Мод

Скопируйте `GrannyCoop.dll` в папку `Mods` внутри папки игры:

```
<папка игры>/Mods/GrannyCoop.dll
```

Запустите игру. В `MelonLoader/Logs/Latest.log` должна появиться строка:

```
[Granny Co-op] COOP initialised v0.8.0 role=Off ...
```

Если её нет — проверьте, что DLL лежит именно в `Mods`, а MelonLoader стоит версии 0.7.3.

## Шаг 3. Настройка

Настройки читаются из `UserData/MelonPreferences.cfg` (создаётся после первого запуска с
модом, секция `[GrannyCoop]`):

```ini
[GrannyCoop]
Mode = "Host"          # Off | Host | Join
Address = "127.0.0.1"  # адрес хоста для Join
Port = 47777
PlayerName = "Player"
AutoPlay = false       # только для тестового стенда
```

Либо то же самое флагами командной строки — они главнее файла:

| Флаг | Значение |
|---|---|
| `--coop-host` | быть хостом |
| `--coop-join=<ip>` | подключиться к хосту |
| `--coop-name=<имя>` | имя над головой |
| `--coop-port=<порт>` | порт (по умолчанию 47777) |
| `--coop-off` | выключить мод |

Пример ярлыка для хоста:

```
"<папка игры>\Granny.exe" --coop-host --coop-name=Host
```

для гостя:

```
"<папка игры>\Granny.exe" --coop-join=192.168.1.10 --coop-name=Guest
```

## Как играть

1. Хост запускает игру и заходит в меню (или сразу жмёт Play — сцену выбирает хост).
2. Гость запускает игру с `--coop-join=<ip хоста>`. Он загрузит ту же сцену, что и хост,
   и появится рядом.
3. Двери, предметы и Грэнни общие. Грэнни считается на хосте.
4. Если Грэнни поймала одного игрока, его камера переходит на наблюдение за напарником до
   конца сцены.

## Если не соединяется

- Хост и гость должны использовать **один и тот же порт** (`--coop-port`).
- Проверьте, что UDP 47777 доходит до хоста: на роутере хоста нужен проброс, либо оба
  игрока — в одной виртуальной сети.
- Гость должен видеть в логе `COOP net: hello -> ...` и `COOP net: welcome`. Если `hello`
  повторяется, а `welcome` нет — пакеты не доходят.
- Брандмауэр Windows спросит про `Granny.exe` при первом запуске — разрешите доступ для
  частных сетей.

## Ограничения прототипа (честно)

- Проверено на **двух экземплярах на одной машине**; реальная сеть, три игрока и полное
  прохождение не проверялись.
- Фургон и смена дня **не** синхронизированы.
- Ловля передаётся как «мёртв/жив»: напарник видит, что вас поймали, но не проигрывает
  вашу сцену смерти. Возвращение в игру после ловли не проверено.
- Предмет синхронизируется как флаг + копия модели в руке; анимации/звука подбора нет, и
  подбор живым игроком (взгляд + кнопка) ещё не проверялся — только запись флага.
- Нет GUI: только MelonPreferences и флаги командной строки.

Полный список — в [README.ru.md](../README.ru.md), устройство и логи — в
[docs/NOTES.md](NOTES.md).

---

# English

A MelonLoader mod for **Granny** (DVloper, Steam app 962400), verified against v1.3.4
(buildid 21006637, Unity 6000.0.58f2, IL2CPP).

## Install

1. Close the game. Run the official **MelonLoader 0.7.3** installer
   (https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3), pick Granny, install.
2. Launch the game once so MelonLoader creates `MelonLoader/` and `Mods/`, then close it.
3. Copy `GrannyCoop.dll` into `<game dir>/Mods/`.
4. Launch; `MelonLoader/Logs/Latest.log` should show
   `[Granny Co-op] COOP initialised v0.8.0 ...`.

## Configure

Edit `UserData/MelonPreferences.cfg`:

```ini
[GrannyCoop]
Mode = "Host"          # Off | Host | Join
Address = "127.0.0.1"  # host address for Join
Port = 47777
PlayerName = "Player"
```

Command-line flags override the file: `--coop-host`, `--coop-join=<ip>`,
`--coop-name=<name>`, `--coop-port=<port>`, `--coop-off`.

## Play

The host picks the scene and starts; the guest joins with `--coop-join=<host ip>` and loads
the same scene. Doors, items and Granny are shared (Granny is simulated on the host). The
host needs UDP 47777 reachable — port-forward it, or put both players on a virtual LAN
(Radmin VPN, ZeroTier, Tailscale).

## Prototype limits

Two instances on one machine are all that has been tested: no real network, no three
players, no full playthrough. The van and the day cycle are not synced. A catch is carried
as a dead/alive edge only (no replayed death scene). Items sync as a flag plus a hand-model
replica — no pickup animation or sound, and a human pickup is still untested. No GUI.

Full details: [README.md](../README.md) and [docs/NOTES.md](NOTES.md).
