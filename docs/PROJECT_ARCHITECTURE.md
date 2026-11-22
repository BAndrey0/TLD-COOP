# Карта TLD-COOP

Срез от 22.11.2026 на коммите `193bd41`. Мод 0.16.40 для Survival 2.60, лаунчер 1.1.3. Статус проверок и точка продолжения находятся в [WORK_PROGRESS.md](WORK_PROGRESS.md).

## Границы проекта

TLD-COOP работает с установленной игрой через MelonLoader, Harmony и IL2CPP. [TldCoop.csproj](../TldCoop/TldCoop.csproj) собирает `TLD-Coop.dll`. [CoopProfile.csproj](../CoopProfile/CoopProfile.csproj) собирает отдельный модуль, который перенаправляет штатный путь сохранений в профиль кооператива. [Launcher.csproj](../Launcher/Launcher.csproj) собирает WinForms-лаунчер для .NET Framework 4.8.

```text
Лаунчер → GitHub Releases → архив и скрипты → отдельная копия игры
                                                  │
                            CoopProfile.dll → путь локальных сохранений
                                                  │
                   TLD-Coop.dll → Harmony → штатные объекты и интерфейс
                          │
Клиенты ↔ Steam P2P / UDP ↔ хост → записи мира по сценам
                          │                      │
                 локальное отображение     диск и снимки для гостей
```

## Компоненты и владельцы состояния

| Подсистема | Входы и зависимости | Где принимается решение и что связывает компоненты |
| --- | --- | --- |
| Лаунчер | [Launcher/Program.cs](../Launcher/Program.cs), [LauncherView.cs](../Launcher/LauncherView.cs) | Хранит пути и канал обновлений в `launcher.json`. Выбирает выпуск, проверяет доступность установки, вызывает установку, обновление, синхронизацию DLC и запуск. Видимость кнопок определяется состоянием установки и результатом проверки релиза. |
| Установка, запуск и выпуск | [kit](../kit), [package.ps1](../tools/package.ps1), [check-fast.ps1](../tools/check-fast.ps1) | Установщик копирует совместимую Steam-игру, updater применяет файлы из комплекта, стартовый скрипт запускает установленную копию. Манифест связывает версию, исходный коммит и контрольные суммы. Риск возникает на границе выбора пакета, применения файлов и обновления самого лаунчера. |
| Старт мода и интерфейс | [MyMod.cs](../TldCoop/TldCoop/MyMod.cs), [Pathes.cs](../TldCoop/TldCoop/Pathes.cs), [PatchLoader.cs](../TldCoop/TldCoop/PatchLoader.cs), [MenuChange.cs](../TldCoop/TldCoop/MenuChange.cs), [GameCompat.cs](../TldCoop/TldCoop/GameCompat.cs) | `MyMod` запускает Harmony и координирует кадр, секундные проверки и смену сцены. `Pathes` и отдельные классы перехватывают штатные действия. `PatchLoader` сообщает о неприменённых патчах, поэтому частично работающая сборка может загрузиться. |
| Сессия и транспорт | [GameServer](../TldCoop/GameServer), [SteamConnect.cs](../TldCoop/TldCoop/SteamConnect.cs), [SteamGameJoin.cs](../TldCoop/TldCoop/SteamGameJoin.cs), [ClientHandle.cs](../TldCoop/TldCoop/ClientHandle.cs), [ConnectionWatchdog.cs](../TldCoop/TldCoop/ConnectionWatchdog.cs) | Хост ведёт слоты и обрабатывает команды, клиенты получают состояние. Steam отправляет движение ненадёжно на канале 1, прочие пакеты через надёжный канал 0. Прямой UDP проверяется отдельно. `ThreadManager` доставляет отложенные действия в главный поток. |
| Игроки, экипировка, модели | [MotionSync.cs](../TldCoop/TldCoop/MotionSync.cs), [Comps.cs](../TldCoop/TldCoop/Comps.cs), [ClothingSync.cs](../TldCoop/TldCoop/ClothingSync.cs), [Shared.cs](../TldCoop/TldCoop/Shared.cs), [AvatarLighting.cs](../TldCoop/TldCoop/AvatarLighting.cs), [NativeAppearance.cs](../TldCoop/TldCoop/NativeAppearance.cs) | Клиент сообщает собственное движение и экипировку. Удалённая поза, анимация и освещение применяются локально. Штатные модели, экспериментальный рендер и диагностические пробы имеют разные пути включения. |
| Переходы и общая расстановка | [SpawnSafety.cs](../TldCoop/TldCoop/SpawnSafety.cs), [ScenePlacementSync.cs](../TldCoop/TldCoop/ScenePlacementSync.cs), [FurnitureReplicationPolicy.cs](../TldCoop/TldCoop/FurnitureReplicationPolicy.cs), [SceneRepairSync.cs](../TldCoop/TldCoop/SceneRepairSync.cs) | Хост принимает и записывает изменения по ID сцены, даже находясь в другой локации. Снимок мебели сверяется по ревизиям. Клиент сопоставляет запись с локальным объектом после штатной загрузки. Старые ID, неоднозначные совпадения и порядок загрузки остаются отдельными рисками. Здесь же сходятся кровати, мебель, ремонт декора и часть маршрутизации других подсистем. |
| Предметы, инвентарь и контейнеры | [PickupSync.cs](../TldCoop/TldCoop/PickupSync.cs), [GearPlacementSync.cs](../TldCoop/TldCoop/GearPlacementSync.cs), [ContainerLootSync.cs](../TldCoop/TldCoop/ContainerLootSync.cs), [CoopContainerFlow.cs](../TldCoop/TldCoop/CoopContainerFlow.cs), [CuringStorageSync.cs](../TldCoop/TldCoop/CuringStorageSync.cs) | Штатная игра хранит личный инвентарь. Хост хранит общие предметы и содержимое контейнеров. Мод дополняет штатные представления сетевыми. Контейнеры, просолка, перенос и подбор с земли используют разные модели резервирования и подтверждения. Проверять нужно и количество объектов в мире, и состояние инвентарей. |
| Костры, готовка и вода | [FireHeatSync.cs](../TldCoop/TldCoop/FireHeatSync.cs), [ScenePlacementSync.cs](../TldCoop/TldCoop/ScenePlacementSync.cs), [SceneWaterSync.cs](../TldCoop/TldCoop/SceneWaterSync.cs) | Хост ведёт запас горения и подтверждает выдачу воды из источника. Готовка выполняется у владельца посуды с публикацией состояния конфорки. Осмотр чужой посуды локальный, операция выдачи проходит через владельца. Личный прогресс работы и общее время влияют на разные таймеры. |
| Время, погода и личная работа | [Shared.cs](../TldCoop/TldCoop/Shared.cs), [ClientHandle.cs](../TldCoop/TldCoop/ClientHandle.cs), [SleepRendezvousFix.cs](../TldCoop/TldCoop/SleepRendezvousFix.cs), [BedRest.cs](../TldCoop/TldCoop/BedRest.cs), [CoopFatigue.cs](../TldCoop/TldCoop/CoopFatigue.cs), [NativeWorkProgress.cs](../TldCoop/TldCoop/NativeWorkProgress.cs), [SanityManager.cs](../TldCoop/TldCoop/SanityManager.cs) | Хост публикует часы и погоду по регионам. Локальная игра выполняет чтение, крафт, ремонт, лечение, разделку и другие действия, а патчи корректируют время, прогресс, силы и контроль. Общий сон использует готовность игроков. Правила баланса заданы отдельно в [BALANCE-0.16-RU.md](BALANCE-0.16-RU.md). |
| Животные, ресурсы и окружение | [WildlifeSceneHandshake.cs](../TldCoop/TldCoop/WildlifeSceneHandshake.cs), [WildlifeOwnershipPolicy.cs](../TldCoop/TldCoop/WildlifeOwnershipPolicy.cs), [RadialSpawnSync.cs](../TldCoop/TldCoop/RadialSpawnSync.cs), [ThrownGearSync.cs](../TldCoop/TldCoop/ThrownGearSync.cs), [FarTerritory.cs](../TldCoop/TldCoop/FarTerritory.cs), [DeathRespawn.cs](../TldCoop/TldCoop/DeathRespawn.cs) | Право симуляции животных привязано к готовому игроку сцены. Сбор и удаление ресурсов, тросы, укрытия, передатчики, броски и спасение используют отдельные обработчики. Точки риска — смена владельца симуляции, повторная загрузка и параллельный сбор. |
| Сохранения и завершение сессии | [Profile.cs](../CoopProfile/CoopProfile/Profile.cs), [SaveSlotCompat.cs](../TldCoop/TldCoop/SaveSlotCompat.cs), [LegacySaveNames.cs](../TldCoop/TldCoop/LegacySaveNames.cs), [WorldSaveIdentity.cs](../TldCoop/TldCoop/WorldSaveIdentity.cs), [WorldSaveMatch.cs](../TldCoop/TldCoop/WorldSaveMatch.cs), [MPSaveManager.cs](../TldCoop/TldCoop/MPSaveManager.cs), [CoopReturnToMenu.cs](../TldCoop/TldCoop/CoopReturnToMenu.cs) | Штатный слот и записи мода имеют разные пути записи. Возобновление связано с ID мира и сидом. При выходе важен порядок подтверждения сетевых изменений, фиксации локального сохранения и отключения. Чтение старых ключей и имён типов входит в контракт совместимости. |
| Проверки и диагностические инструменты | [tests](../tests), [SelfTest.cs](../TldCoop/TldCoop/SelfTest.cs), [BundleAudit](../tools/BundleAudit), [render-models.ps1](../tools/render-models.ps1), [AGENTS.md](../AGENTS.md) | `SessionPolicies` проверяет правила и форматы в управляемом коде, `NativeAssets` — изоляцию идентификаторов ресурсов. Unity-пробы используют отдельные копии сохранений. Интеграционное покрытие проверяется по [матрице](TEST_MATRIX.md). |

## Связи для последующих проверок

- Подбор и передача предмета пересекают три слоя. Сетевой результат меняет запись хоста, модель предмета в сцене и личный инвентарь.
- Переход через дверь пересекает загрузку сцены, готовность слота, восстановление объектов, освещение и права симуляции животных.
- Сон и личная работа пересекают штатные таймеры, общее время, погоду, расход топлива, готовку и прогресс персонажа.
- Выход из меню пересекает незавершённые взаимодействия, сохранение на диск, подтверждения от гостей и очистку Steam-сессии.
