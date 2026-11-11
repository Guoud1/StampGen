# 📖 Инструкция пользователя StampGen

*Версия 2.3.0 · [English below](#english-user-guide)*

---

## 1. Что это

StampGen — программа для проектирования пластин (оснований) с оттисками, которые затем печатаются на 3D-принтере или используются для гравировки. Вы собираете пластину из штампов, задаёте рельеф и фаски, а программа строит **замкнутую (watertight) 3D-модель** и сохраняет её в **STL**. Также экспортируются SVG, PDF, PNG и JPG.

Все расчёты выполняются **локально на вашем компьютере** — интернет не нужен, данные никуда не передаются.

## 2. Запуск

### Готовый exe (Windows, самый простой путь)

1. Скачайте архив с exe из [Releases](https://github.com/Guoud1/StampGen/releases) и распакуйте в любую папку.
2. Запустите `StampGen.exe` двойным кликом — браузер откроется сам.
3. Если браузер не открылся — посмотрите адрес в консоли программы и откройте вручную:

   **http://127.0.0.1:5317**

   Программа поднимает локальный сервер на первом свободном порту из диапазона **5317–5322**; точный адрес всегда печатается в терминале. Порт можно задать вручную: `StampGen.exe --port=5317`.

> Важно: не закрывайте чёрное окно консоли, пока работаете с программой — это и есть сервер. Закрыли окно — программа выключилась.

### Из исходников

```bash
dotnet run --project StampGen -c Release -- --no-browser --port=5317
```

Затем откройте http://127.0.0.1:5317. В Visual Studio 2022/2026 — откройте `StampGen/StampGen.csproj` и нажмите **F5**.

## 3. Интерфейс

- **Менюбар сверху**: меню **Файл** (Новый, Сохранить, Сохранить как…, Открыть…), **Правка** (Отменить/Вернуть, также Ctrl+Z/Ctrl+Y), **Библиотеки** (Добавить библиотеку zip…, Управление библиотеками…, Перечитать папку stamp) и отдельный пункт **«О программе»** — описание, лицензия, email для коммерческих вопросов и гиперссылка **«☕ Поддержать автора проекта — скинуть на кофе»** (YooMoney, открывается в браузере).
- **Левая колонка** — настройки: Проект, Основание (пластина), Глубина рельефа, список оттисков.
- **Правая часть** — визуализация: **2D** — точная схема (перерисовывается сразу), **3D** — реальная модель (строится по кнопке **Обновить**, готовая модель хранится и показывается мгновенно).

## 4. Типовой сценарий (5 минут)

1. Меню **Библиотеки → Добавить библиотеку zip…** — выберите скачанный zip ([готовые наборы здесь](https://disk.yandex.ru/d/D7K6EmBD_wXu3w)).
2. Кнопка **«+ Добавить оттиск»** — в открывшемся окне выберите библиотеку, категорию, кликните нужные штампы, затем **«Добавить выбранные в проект»**.
3. В карточке оттиска задайте размер (мм), положение, угол; при необходимости — копии.
4. Настройте пластину: размеры, поля, фаска сверху, нижняя кромка.
5. Вкладка **3D → Обновить** — посмотрите модель (для быстрого просмотра держите качество «черновик»).
6. Кнопка **«Создать STL»** — выберите качество и сохраните. Если ничего не меняли с последнего построения, файл скачивается **мгновенно из памяти** (без пересчёта).

## 5. Библиотеки штампов

- **Импорт**: меню «Библиотеки → Добавить библиотеку zip…». Внутри может быть один zip или несколько вложенных zip-библиотек (глубина до 3) — каждая добавится отдельно.
- **Где хранятся**: папка `stamp/` рядом с программой. Там лежат и zip-архив (для переноса/бэкапа), и распакованная копия.
- **Управление**: меню «Библиотеки → Управление библиотеками…» — переименование, экспорт zip, удаление.
- **Ручная правка**: распакованную библиотеку в `stamp/<Имя>/` можно править «руками» (добавить/заменить svg, поправить library.json), затем нажмите меню «Библиотеки → **Перечитать папку stamp**».
- **Что делает импорт**: каждый svg переводится в чёрно-белый вид (тёмные цвета → чёрный, светлые → белый), а на стыках элементов рисуется тонкая белая граница шириной 1 % среднего размера штампа — разноцветные детали не сливаются в одно пятно на 2D-схеме, в 3D и в STL.

## 6. Как самому сделать библиотеку штампов

Библиотека — это обычный **zip-архив** простой структуры. Сделать её можно в любом редакторе, «руками», без сборщиков и скриптов.

### 6.1. Структура zip

```
Моя-библиотека.zip
├── library.json          ← необязателен (можно без него)
└── stamps/
    ├── эмблема-1.svg            ← штампы «корневой» категории
    ├── Мои-значки/
    │   ├── звезда.svg           ← категория = имя подпапки
    │   └── шестерёнка.svg
    └── Текстуры/
        ├── штриховка.svg
        └── волны.svg
```

- **Папки внутри `stamps/`** становятся категориями в списке программы (имена любые, включая русские).
- **Имя штампа** — из имени файла (или из `name` в library.json).
- Если `library.json` нет — программа сама просканирует все `*.svg`, разложит их по категориям и создаст `library.json` при импорте.

### 6.2. library.json (если хотите свои имена и порядок)

```json
{
  "name": "Моя библиотека",
  "format": 2,
  "stamps": [
    { "file": "stamps/Мои-значки/звезда.svg",      "name": "Звезда",      "category": "Мои значки" },
    { "file": "stamps/Мои-значки/шестерёнка.svg",  "name": "Шестерёнка",  "category": "Мои значки" },
    { "file": "stamps/Текстуры/штриховка.svg",     "name": "Штриховка",   "category": "Текстуры" }
  ]
}
```

Поля: `name` — название библиотеки в списке; `file` — путь к svg внутри архива; `name` у штампа — отображаемое имя; `category` — категория (можно не совпадать с именем папки).

### 6.3. Требования к SVG

- Подходят обычные svg: `path`, `rect`, `circle`, `polygon`, `ellipse`. Желательно наличие `viewBox` (иначе пропорции принимаются 1:1).
- **Текст лучше переводить в кривые** (Outline / «Convert to paths») — шрифты на чужом компьютере могут отсутствовать.
- Цвета не важны: при импорте всё приводится к ч/б. Осмысленно делайте рисунок одним «тёмным» цветом с прозрачным фоном — он станет чёрным.
- Заливка фигур должна быть сплошной (fill); тонкие линии — тоже заливкой-полосами (stroke-обводки пересчитываются, но заливка надёжнее).
- Размер файла — разумный (десятки–сотни КБ). Чем сложнее контуры, тем дольше считается 3D.

### 6.4. Сборка и импорт

1. Сложите svg по папкам (категориям), при желании добавьте `library.json`.
2. Заархивируйте **содержимое** (чтобы `library.json` и папка `stamps/` были в корне архива).
3. В программе: меню **Библиотеки → Добавить библиотеку zip…** — готово. Библиотека появится в списке, штампы — по категориям.
4. Чтобы поделиться библиотекой — «Библиотеки → Управление библиотеками… → Экспорт zip» или просто передайте исходный архив.

## 7. Оттиски: размеры, копии, рельеф

- **Размер**: поля «Ширина/Высота, мм» (минимум 0.1 мм), слайдеры, сохранение пропорций. Колесо мыши меняет значение, когда слайдер в фокусе.
- **Положение**: слайдеры или перетаскивание мышью по 2D-схеме; оттиск не выходит за рабочую область (с учётом полей).
- **Угол**: 0…360° с шагом 0.5° и прилипанием к 45/90/135…; кнопка «Отзеркалить».
- **Копии**: галочка «Сделать копии» — количество, шаг в мм или %, расстояние и угол ряда. Копии двигаются вместе с оригиналом как единая группа.
- **Мульти-выбор**: галочки в карточках (или «Выделить все») — массовое изменение размеров/угла/рельефа для всех выбранных. Ctrl+клик по оттиску на схеме — то же самое. Delete удаляет выбранные.
- **Рельеф отдельного оттиска**: своя глубина (пусто — общая) и «шершавое дно» (дельта шероховатости в мкм — дно кармана получает детерминированный узор бугорков).
- **Текстовая надпись**: кнопка **«Т +»** — векторные контуры из встроенных шрифтов (работает офлайн, кириллица поддерживается).

## 8. Пластина

- Размеры в мм (по умолчанию 130×80×3; минимум 5×5×1).
- **Фаска сверху** (1×1 мм, 45°) — реально вырезается в геометрии 3D и STL.
- **Нижняя кромка**: высота и угол −80…+80° (плюс — фаска, минус — кайма/цоколь, 0 — прямой угол).
- **Поля** — единое значение для всех четырёх сторон; рабочая область показана на 2D-схеме пунктиром.
- **Глубина рельефа (общая)**: по умолчанию 20 мкм; переключатель мкм/мм.

## 9. 3D и STL

- **Качество**: черновик / среднее / высокое / ультра — влияет на плотность триангуляции.
- **Усиление рельефа** (x1…x200): визуальное exaggeration для просмотра. **В STL всегда записывается точная глубина рельефа** (x1), файл пригоден для печати.
- **Кэш**: построенная 3D-модель хранится до следующего «Обновить»; переключение 2D↔3D — мгновенное. При сохранении STL с теми же параметрами модель берётся из памяти (без пересчёта); изменили параметры — честный пересчёт.
- **Замкнутость**: после генерации в окне показывается строка «Замкнутость граней: все грани закрыты, щелей 0 ✓» — STL готов к слайсеру без «ремонта».

## 10. Проекты и экспорт

- **Проект `.stampgen`** — zip-контейнер (project.json + все svg внутри): Файл → Сохранить / Сохранить как / Открыть.
- **Экспорт**: STL (бинарный), SVG, PDF, PNG (прозрачный фон), JPG (белый фон) — Сохранить как…
- **Автосохранение**: выключено / 30 с / 1 мин / 5 мин; хранится в браузере, после перезагрузки страницы появится баннер «Найдено автосохранение».

## 11. Частые вопросы

- **Браузер не открылся сам** — откройте адрес из консоли: http://127.0.0.1:5317 (порт может быть 5318–5322, если 5317 занят).
- **Порт занят / нужен конкретный порт** — `StampGen.exe --port=N` или переменная окружения `STAMPGEN_PORT=N`.
- **Не хочу, чтобы открывался браузер** — `StampGen.exe --no-browser` (или `STAMPGEN_NO_BROWSER=1`).
- **«Библиотека не импортирована» у старых проектов** — проект ссылается на штамп из библиотеки, которой нет в папке `stamp/`. Импортируйте её zip.
- **STL считается долго** — снизьте качество и число оттисков; для просмотра используйте «черновик».
- **Куда уходят мои данные** — никуда: всё считается и хранится локально.

---

# English user guide

**StampGen** is a designer for stamp plates used in metal printing/engraving. Compose a plate from stamps, set relief depth and chamfers, and export a watertight **STL** (plus SVG / PDF / PNG / JPG). All computation happens locally — no internet needed.

## Running

1. Download `StampGen.exe` from [Releases](https://github.com/Guoud1/StampGen/releases) and run it — the browser opens automatically.
2. If it doesn't, open the address printed in the console: **http://127.0.0.1:5317** (the local server picks the first free port in the 5317–5322 range; override with `--port=N`).
3. Keep the console window open while using the app — that window *is* the local server.

From source: `dotnet run --project StampGen -c Release -- --no-browser --port=5317`, or open `StampGen/StampGen.csproj` in Visual Studio and press **F5**.

## Basic workflow

1. **Libraries → Add library zip…** — import a stamp pack ([ready packs here](https://disk.yandex.ru/d/D7K6EmBD_wXu3w)).
2. **+ Add stamp** — pick library, category, stamps → *Add selected to project*.
3. Adjust size (mm), position, rotation, copies in the stamp card.
4. Configure the plate: dimensions, margins, top chamfer (1×1 mm, 45°), bottom edge.
5. **3D tab → Update** — build the model (keep quality at “draft” for preview).
6. **Create STL** — choose quality and save. If nothing changed since the last build, the file downloads instantly from memory (no re-computation).

## Libraries

Libraries live in the `stamp/` folder next to the exe (zip + unpacked copy). Manage them via **Libraries → Manage libraries…** (rename / export zip / delete). After editing the unpacked folder manually, use **Libraries → Reload stamp folder**. On import every SVG is converted to black-and-white and a thin white border (1 % of the average stamp size) is added between color regions so elements never merge.

## How to make your own stamp library

A library is a plain **zip archive**:

```
My-library.zip
├── library.json          ← optional
└── stamps/
    ├── emblem.svg              ← root category
    ├── My-icons/
    │   ├── star.svg            ← folder name = category
    │   └── gear.svg
    └── Textures/
        ├── hatching.svg
        └── waves.svg
```

- Folders inside `stamps/` become categories (any names, including non-Latin).
- Without `library.json` the app scans all `*.svg` and builds the catalog automatically.
- Optional `library.json`:

```json
{
  "name": "My library",
  "format": 2,
  "stamps": [
    { "file": "stamps/My-icons/star.svg", "name": "Star", "category": "My icons" },
    { "file": "stamps/Textures/hatching.svg", "name": "Hatching", "category": "Textures" }
  ]
}
```

- SVG requirements: regular shapes (`path`, `rect`, `circle`, `polygon`); a `viewBox` is recommended; convert text to outlines; solid fills work best. Colors don't matter — everything is converted to black & white on import.
- Zip the **contents** (so `library.json` and `stamps/` are at the archive root), then **Libraries → Add library zip…**. Nested zips (a zip containing several library zips) are supported up to 3 levels.

## Plate, relief, export

- Plate: mm dimensions (default 130×80×3), uniform margins, top chamfer, bottom edge angle −80…+80°.
- Global relief depth: 20 µm by default (µm/mm switch). Per-stamp depth and “rough bottom” are available in each stamp card.
- 3D tab: quality (draft/medium/high/ultra) and relief exaggeration (x1…x200) are preview-only — **STL always stores the true depth (x1)** and is watertight (0 open edges, verified after every build).
- Projects: native `.stampgen` container (zip with project.json + embedded SVGs). Autosave every 30 s / 1 / 5 min into browser storage.

## FAQ

- Browser didn't open — read the URL from the console (http://127.0.0.1:5317, or 5318–5322).
- Specific port: `StampGen.exe --port=N` or env var `STAMPGEN_PORT=N`.
- “Library not imported” in old projects — import the missing library zip into `stamp/`.
- Where does my data go — nowhere; everything is computed and stored locally.

**License:** free for personal, non-commercial use; commercial use requires written permission from Guoud (iljamakeev@yandex.ru). See [LICENSE.md](../LICENSE.md).
