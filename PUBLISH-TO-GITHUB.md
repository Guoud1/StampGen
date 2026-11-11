# 🚀 Публикация StampGen на GitHub через Visual Studio

Пошаговая инструкция, как выложить этот проект в новый репозиторий
**https://github.com/Guoud1/StampGen**.

Всё для публикации уже подготовлено и лежит в корне проекта:
`README.md`, `LICENSE.md`, `CHANGELOG.md`, `BUILD-EXE.md`, `PUBLISH-TO-GITHUB.md` (этот файл),
`AGENTS.md` (политика для ИИ-агентов), `tools/` (хеш ключа владельца),
`docs/` (инструкция, аттестация владельца, донат-страница, логотип, скриншоты), `.gitignore`, `.gitattributes`.

---

## Шаг 0. Что нужно

1. **Visual Studio 2022 (17.10+) или 2026** — она понимает файл решения `StampGen.slnx`.
2. **Git** — ставится вместе с VS (галочка «Git for Windows» в установщике); отдельно: https://git-scm.com
3. Аккаунт GitHub и сам пустой репозиторий: https://github.com/Guoud1/StampGen

> `.gitignore` уже настроен: из репозитория автоматически исключаются `bin/`, `obj/`,
> `publish/`, `.vs/`, временные файлы и папка `stamp/` (личные библиотеки пользователя).
> Ничего дополнительно придумывать не нужно.

---

## Шаг 1. Создать Git-репозиторий в Visual Studio

1. Откройте решение (`StampGen.slnx`) в Visual Studio.
2. Меню **Git → Create Git Repository…** («Добавить в систему управления версиями»).
3. Слева выберите **GitHub**, вход под учётной записью **Guoud1** (VS предложит войти в браузере при первом запуске).
4. В поле репозитория:
   - VS умеет подключаться к **существующему** репозиторию — укажите
     `https://github.com/Guoud1/StampGen.git`;
   - если такой вариант не предложить в вашей версии VS — создайте «new», а удалённый
     адрес добавите на шаге 3.
5. Убедитесь, что VS **не** перезаписал ваш `.gitignore` (шаблон «Visual Studio» можно
   просто оставить — файл в проекте уже есть и имеет приоритет) и **не** добавил
   лицензию MIT — у проекта своя, `LICENSE.md`.
6. Нажмите **Create and Push**. Первый коммит улетит на GitHub.

> Если VS не предложил удалённый репозиторий: **Git → Push** → в диалоге
> «Specify the URL of an empty Git repository on GitHub» вставьте
> `https://github.com/Guoud1/StampGen.git` → **Push**. Ветка должна называться `main`.

### Запасной путь — командная строка

Из корня проекта (рядом с `StampGen.slnx`):

```bash
git init -b main
git add .
git commit -m "StampGen v2.3.0 — первый публичный релиз"
git remote add origin https://github.com/Guoud1/StampGen.git
git push -u origin main
```

Перед коммитом взгляните на `git status`: в списке **не должно** быть
`publish/`, `bin/`, `obj/`, `.vs/`, `stamp/`, `*.exe` — они исключены `.gitignore`.

---

## Шаг 2. Проверить репозиторий на GitHub

Откройте https://github.com/Guoud1/StampGen и убедитесь:

- ✅ README отображается с картинками: логотип и скриншоты из `docs/screenshots/` видны;
- ✅ ссылка «скинуть денег на кофе» стоит **закреплённой сразу под заголовком** (первая строка после бейджей);
- ✅ таблица «Библиотеки проекта | О программе» на месте, обе ссылки работают;
- ✅ «О программе» формальное, с email для коммерческих вопросов (iljamakeev@yandex.ru);
- ✅ ссылка на первоисточник, Яндекс.Диск со штампами и донат-гиперссылка (YooMoney) на месте;
- ✅ английский блок в конце README читается.

---

## Шаг 3. Релиз с готовым exe

Пользователи «без студии» качают exe из **Releases** — README уже ведёт их туда.

1. Соберите exe: `.\build-exe.ps1` → папка `publish\win-x64` (подробности — [BUILD-EXE.md](BUILD-EXE.md)).
2. Упакуйте **содержимое** `publish\win-x64` в zip: `StampGen-v2.3.0-win-x64.zip`
   (внутри `StampGen.exe` + `wwwroot\`).
3. На GitHub: **Releases → Draft a new release** → тег `v2.3.0` (create from main),
   название «StampGen v2.3.0», в описание — краткую выжимку из CHANGELOG.
4. Перетащите zip в поле вложений → **Publish release**.

Ссылка, которую можно давать людям: `https://github.com/Guoud1/StampGen/releases/latest`

---

## Шаг 4. Закрепить ссылку на донат (по желанию, 1 минута)

GitHub позволяет закреплять issue на странице репозитория — сделайте заметный «стикер»:

1. **Issues → New issue** → заголовок: `☕ Скинуть денег на кофе для автора проекта`.
2. В текст — гиперссылку на донат:
   ```
   ☕ Донат (YooMoney): https://yoomoney.ru/fundraise/1LFSQ49MGAC.261111
   ```
3. Создайте issue и нажмите справа **Pin issue** («Закрепить»).

Строка доната в README и так стоит первой под заголовком, а закреплённый issue
будет виден на главной странице репозитория ещё и отдельным стикером.

> GitHub Sponsors в РФ не работает — донат идёт только через гиперссылку
> YooMoney: https://yoomoney.ru/fundraise/1LFSQ49MGAC.261111

---

## Шаг 5. Порядок действий для будущих обновлений

1. Правки кода → поднять версию: `StampGen.csproj` (`Version`/`FileVersion`/`InformationalVersion`),
   строка в меню (`Index.razor`), окно «О программе», `docs/INSTRUCTION.md`, бейдж в `README.md`.
2. Новая запись в `CHANGELOG.md`.
3. Коммит → `git push` (в VS: **Git → Push**).
4. Тег + Release с новым exe (шаг 3).
5. Если релиз обфусцированный (`build-exe.ps1 -Obfuscate`) — сначала прогнать
   дымовой тест (запуск, библиотеки, STL, «О программе»).

---

*StampGen © Guoud — iljamakeev@yandex.ru. Коммерческое использование без разрешения правообладателя запрещено.*
