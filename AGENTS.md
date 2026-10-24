# AnkiDeck — описание проекта для ИИ-агентов

Документ описывает назначение, архитектуру, форматы данных, правила изменения и способы проверки проекта.
Рассчитан на агента, который вносит изменения в код без полного контекста предыдущих обсуждений.

---

## 1. Назначение

Локальное веб-приложение для работы с колодами Anki (`.apkg`):

1. Пользователь загружает `.apkg` на сервер.
2. Сервер читает SQLite-базу коллекции и показывает список **заметок** (не карточек).
3. В таблице можно:
   - фильтровать заметки по Front/Back, наличию HTML-разметки и отсутствию аудио;
   - очищать HTML-разметку в полях Front/Back/Audio (по одной или все сразу);
   - генерировать озвучку поля Front через API Звукограм и прикреплять её к заметке;
   - проигрывать аудиофайлы, уже имеющиеся в колоде;
   - сохранить изменённую колоду обратно в `.apkg` (с медиафайлами).
4. Настройки API Звукограма задаются на отдельной странице и хранятся в cookie браузера.

Интерфейс — на русском языке, код и комментарии — на английском.

---

## 2. Технологии и запуск

| Компонент | Значение |
|---|---|
| Платформа | .NET 10, ASP.NET Core |
| UI | Blazor **статический SSR** (без интерактивного SignalR-контура) |
| База | SQLite через `Microsoft.Data.Sqlite` 10.0.13 |
| Сжатие | `ZstdSharp.Port` 0.8.8 (zstd) |
| Стили | Bootstrap 5 (`wwwroot/lib/bootstrap/dist/css/bootstrap.min.css`) |
| Хранение состояния | серверный `DeckWorkspaceStore` + ASP.NET Core Session; настройки — cookie |

Сборка и запуск:

```powershell
dotnet build
dotnet run --urls http://127.0.0.1:5179
```

Либо запуск собранного приложения:

```powershell
dotnet bin\Debug\net10.0\AnkiDeck.dll --urls http://127.0.0.1:5179
```

Важно: при запуске **внутри песочницы DSH** исходящие HTTPS-запросы могут падать с TLS-ошибкой
(«credentials not found in the security package»). Для проверки Звукограма приложение нужно запускать
обычным процессом вне песочницы. Это ограничение среды, а не кода.

---

## 3. Карта файлов

```
Program.cs                          маршруты, DI, лимит загрузки, сессия, HttpClient
AnkiDeck.csproj                     net10.0, пакеты SQLite и ZstdSharp
Components/
  App.razor                         HTML-оболочка, CSS, JS (прокрутка, живые фильтры)
  Routes.razor                      Router без layout
  _Imports.razor                    общие using
  Pages/
    Anki.razor                      главная страница: загрузка, таблица, действия
    Anki.razor.css                  стили таблицы, кнопок, фильтров
    Settings.razor                  настройки Звукограма и проверка
Models/
  AnkiNote.cs                       AnkiField, AnkiNote
  GeneratedAudio.cs                 сгенерированное аудио (байты, формат, длительность)
  ZvukogramSettings.cs              модель настроек для cookie
Services/
  ApkgReader.cs                     чтение zip и SQLite, ParsedDeck
  DeckWorkspace.cs                  рабочая копия колоды: заметки, правки, медиа, экспорт
  DeckWorkspaceStore.cs             словарь рабочих областей по id сессии
  HtmlMarkup.cs                     детект/очистка HTML, текст для TTS
  AudioReference.cs                 распознавание [sound:...] и имени файла
  SettingsCookie.cs                 чтение/запись cookie настроек
  ZvukogramClient.cs                клиент API /text
wwwroot/                            favicon и bootstrap.min.css
```

---

## 4. Поток данных

```
.apkg (zip)
  └─ collection.anki21b | collection.anki21 | collection.anki2
        └─ SQLite: notes (flds через 0x1F), cards, fields/notetypes | col.models (старая схема)
  └─ media (JSON-манифест ИЛИ zstd+protobuf)
  └─ медиафайлы (сырые ИЛИ zstd-сжатые, имена "0", "1", ...)

Загрузка → DeckWorkspace.CreateAsync
  • копия .apkg во временный каталог
  • ApkgReader.ParseAsync: распаковка БД, чтение заметок
  • ReadMediaManifest: разбор media (JSON или zstd/protobuf)
  • Store.Add + session["ankideck.workspace"] = id

Запросы страницы /anki
  • GET: взять workspace из сессии, отрисовать таблицу
  • POST формы deck-actions: фильтры/действия, перерисовать
  • POST формы anki-upload: новая колода
  • POST формы deck-bulk: «Обработать ВСЕ»

Экспорт /anki/export
  • копирование zip-записей
  • замена collection.* на актуальную БД (для anki21b — zstd)
  • добавление сгенерированных медиа в формате исходной колоды
```

---

## 5. Модель состояния

`DeckWorkspace` (в `Services/DeckWorkspace.cs`) — главный объект на время сессии:

- `Notes` — `IReadOnlyList<AnkiNote>`, обновляется после правок;
- `FileName`, `CollectionEntry`, `CardCount`, `LoadTime`;
- `ExpandedNoteId` — какая заметка показывает строку предпросмотра HTML-очистки;
- `AudioPreviewNoteId` — какая заметка показывает сгенерированное аудио;
- `ShowHtmlOnly`, `ShowNoAudioOnly`, `FrontFilter`, `BackFilter` — состояние фильтров (живёт между POST);
- `_generatedAudio` — словарь `noteId → GeneratedAudio` (до подтверждения);
- `_pendingMedia` — сгенерированные файлы, ещё не попавшие в zip;
- `_mediaEntries` — соответствие `id zip-записи → имя файла`;
- `_mediaProtobufEntries`, `_mediaCompressed` — данные нового формата медиа;
- временный каталог `ankideck-*` во временной папке ОС.

`DeckWorkspaceStore` — singleton-словарь `id → DeckWorkspace`.
Идентификатор хранится в сессии (`DeckWorkspaceStore.SessionKey = "ankideck.workspace"`).
Старые области удаляются при загрузке новой колоды, при shutdown и по TTL 4 часа.
При перезапуске приложения загруженные колоды теряются, cookie настроек остаётся.

`AnkiNote`:

```csharp
record AnkiNote(long NoteId, IReadOnlyList<AnkiField> Fields, bool HasHtmlMarkup);
string GetField(string name); // регистронезависимо, "" если поля нет
```

---

## 6. Форматы `.apkg` и медиа — критично

### 6.1 Файл коллекции

Приоритет: `collection.anki21b` → `collection.anki21` → `collection.anki2`.

- `collection.anki21b` сжат **zstd**, распаковывается `ZstdSharp.Decompressor`.
- `collection.anki21` / `collection.anki2` — обычный SQLite.

Схемы:

- современная (schema 18): есть таблица `fields` (`ntid`, `ord`, `name`), `notetypes`, `decks`;
- старая: имена полей берутся из JSON `col.models`.

Особенность: в schema 18 текстовые колонки объявлены с collation `unicase`.
Перед запросами обязательно:

```csharp
connection.CreateCollation("unicase",
    static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
```

Иначе SQLite падает с `no query solution`.

### 6.2 Медиа

Два формата, определяются по первым 4 байтам `media`:

1. **Старый**: `media` — JSON `{"0":"file.mp3", ...}`, zip-записи `"0"`, `"1"` — сырые файлы.
2. **Новый (Anki 23.10+)**: `media` — **zstd-сжатый protobuf**, а zip-записи — **zstd-сжатые** медиафайлы.

Protobuf-схема манифеста:

```
message MediaEntries { repeated MediaEntry entries = 1; }
message MediaEntry {
  string name = 1;
  uint32 size = 2;   // размер несжатого файла
  bytes sha1  = 3;   // SHA-1 несжатого файла
}
```

Соответствие: индекс записи protobuf = имя zip-записи (`0`, `1`, …).

Правила:

- не парсить `media` только как JSON;
- при отдаче файла проверять zstd-магию `28 B5 2F FD` и распаковывать;
- при экспорте **сохранять формат исходной колоды**:
  - новый формат: сжимать добавленные файлы zstd, дописывать protobuf-записи с size/sha1, сжимать манифест;
  - старый: писать JSON и сырые файлы;
- имена zip-записей новых медиа — следующие числовые id, порядок protobuf-записей должен совпадать.

### 6.3 Проверка медиа

`DeckWorkspace.TryReadMediaAsync(fileName)`:

1. ищет имя среди `_pendingMedia` (сгенерированные);
2. иначе — среди `_mediaEntries`, открывает zip-запись, при необходимости распаковывает zstd;
3. возвращает `(byte[] Content, string ContentType)` или `null`.

Content type определяется по расширению: mp3/wav/ogg/opus/m4a/aac/flac.

---

## 7. HTML-разметка

`HtmlMarkup`:

- `IsPresent(fields)` — проверяет **Front, Back, Audio** (если таких полей нет — первые два поля);
- `Contains(value)` — true, если есть:
  - HTML-тег, кроме «осмысленного» `<br>`;
  - HTML-сущность (`&nbsp;`, `&lt;`, `&#160;`, `&#xA0;` и т.п.);
  - «лишний» `<br>`;
- `<br>` считается лишним, если у него нет видимого текста хотя бы с одной стороны:
  `"текст<br>"`, `"<br>текст"`, `"<br>"`, `"текст<br>   "`;
- `<br>` между видимым текстом (`"a<br>b"`) разметкой **не считается** и при очистке сохраняется;
- `Strip(html)` удаляет теги/комментарии/script/style, декодирует сущности,
  `&nbsp;` приводит к обычному пробелу, осмысленные `<br>` оставляет как текст `<br>`;
- `ToSpeechText(front)` — текст для TTS: `Strip`, `<br>` → пробел, удаление `[sound:]`/`[img:]`, схлопывание пробелов.

Поля для очистки и проверки задаются `HtmlMarkup.IsMarkupField`: Front, Back, Audio.

---

## 8. Аудио

### 8.1 Существующие файлы

Anki хранит аудио как `[sound:file.mp3]` в поле.
`AudioReference`:

- `IsPresent(value)` — есть ли ссылка;
- `FirstFileName(value)` — имя первого файла (nullable).

Ячейка Audio:

- если файл есть: компактная кнопка ▶ + имя файла, `<audio src="/anki/media/{name}">`;
- если файла нет: содержимое поля + кнопка «Сгенерировать».

Плейлист-эндпоинт: `GET /anki/media/{fileName}` (только в рамках сессии).

### 8.2 Генерация через Звукограм

Действия формы `deck-actions`:

| Action | Что делает |
|---|---|
| `generate:{noteId}` | запрос в Звукограм по тексту Front, сохранение аудио в памяти workspace, показ строки предпросмотра |
| `confirm-audio:{noteId}` | запись `[sound:имя]` в Audio, регистрация файла в `_pendingMedia`, закрытие предпросмотра |
| `process:{noteId}` | показать очищенные поля (HTML) |
| `confirm:{noteId}` | записать очищенные Front/Back/Audio в БД, пересчитать `HasHtmlMarkup` |

Предпросмотр сгенерированного аудио проигрывается через `GET /anki/audio/{noteId}`
(байты из `_generatedAudio`, доступно только до подтверждения/до перезапуска).

### 8.3 Имена файлов

`ConfirmGeneratedAudioAsync`:

- имя строится из Front: `Strip`, `<br>` → пробел, удаление недопустимых символов Windows
  (`< > : " / \ | ? *`, control), схлопывание пробелов, обрезка до 80 символов, `.`/пробелы в конце;
- пустое имя → `audio`;
- зарезервированные имена (`CON`, `NUL`, `COM1`…) получают `_`;
- расширение — из формата ответа API;
- при совпадении с существующим/добавленным файлом — `-` + 6 случайных `[a-z0-9]`.

### 8.4 Запись в БД

Для изменённой заметки обновляются:

- `notes.flds` (поля через `\u001F`);
- `notes.sfld` — очищенное первое поле;
- `notes.csum` — первые 8 hex-цифр SHA-1 от `sfld` без медиа-ссылок;
- `notes.mod` — текущее unix-время (секунды).

---

## 9. Страницы, формы, маршруты

| Маршрут | Назначение |
|---|---|
| `GET /` | редирект на `/anki` |
| `GET /anki` | страница заметок |
| `POST /anki` | отправка форм `anki-upload`, `deck-actions`, `deck-bulk` |
| `GET /anki/export` | скачать текущий `.apkg` |
| `GET /anki/audio/{noteId}` | сгенерированное (неподтверждённое) аудио |
| `GET /anki/media/{fileName}` | медиафайл колоды |
| `GET/POST /settings` | настройки Звукограма |

Приложение использует **статический SSR**. Формы — обычные `POST` с `@formname`:

- `anki-upload` — загрузка файла, `[SupplyParameterFromForm] IFormFile Upload`;
- `deck-actions` — таблица, фильтры и действия, `Action`, `HtmlOnly`, `NoAudioOnly`,
  `FrontFilter`, `BackFilter`;
- `deck-bulk` — `BulkAction=process-all`;
- `zvukogram-settings` — `Command=save|test` и поля настроек.

Правила для форм:

- у каждой формы уникальный `@formname`, имена должны совпадать с `FormName` в атрибутах;
- в HTML попадает скрытое поле `_handler=<имя формы>`; при curl-тестах его нужно отправлять;
- форму **нельзя рендерить условно**, если по ней приходит POST: после обработки форма должна
  оставаться в разметке, иначе Blazor выдаёт
  «Cannot submit the form ... because no form on the page currently has that name».
  Пример: кнопка «Обработать ВСЕ» блокируется, но форма рендерится всегда;
- у каждой формы свой `AntiforgeryToken`.

Фильтры:

- `ShowHtmlOnly`, `ShowNoAudioOnly`, `FrontFilter`, `BackFilter` комбинируются по «И»;
- текстовые фильтры — подстроки, регистронезависимые, максимум 20 символов (сервер обрезает);
- состояние хранится в `DeckWorkspace`, поэтому переживает действия;
- массовая обработка (`deck-bulk`) сбрасывает фильтры, так как её форма их не передаёт.

Живой поиск в заголовках Front/Back:

- `data-live-filter="front|back"`;
- JS в `App.razor` через 450 мс после ввода делает `form.requestSubmit()`;
- перед отправкой запоминает поле и позицию курсора в `sessionStorage`
  (`ankideck.filterFocus`), после перезагрузки восстанавливает фокус и каретку;
- Enter в поле отправляет форму без submitter (фильтр), а не первое действие таблицы.

Скролл:

- перед POST формы `deck-actions` сохраняется `window.scrollY`;
- после загрузки позиция восстанавливается;
- при `process:` дополнительно прокручивается строка предпросмотра.

Ограничения UI:

- в таблице показываются первые **500** заметок, общее количество — в статистике;
- строка предпросмотра HTML и строка аудио — отдельные `<tr class="preview-row">`.

---

## 10. Настройки Звукограма

Cookie `ankideck.zvukogram` (JSON, HttpOnly, SameSite=Lax, 1 год):

```json
{ "email": "...", "token": "...", "voice": "...", "format": "mp3",
  "sampleRate": "", "bitrate": "" }
```

- `voice` — свободная строка (не выпадающий список);
- `sampleRate`: `""`, `24000`, `44100`, `48000`;
- `bitrate`: `""`, `64`, `128`, `192`, `320`;
- формат: `mp3`, `wav`, `ogg`, `opus`, `flac`.

Клиент (`ZvukogramClient`):

```
POST {BaseUrl}index.php?r=api/text
form-urlencoded:
  token, email, voice, text, format,
  channels=1,
  sample_rate (если задан), bitrate (если задан)
```

- `speed` и `pitch` **не отправляются** (используются значения сервиса по умолчанию);
- лимит `/text` — 1000 символов, проверяется в клиенте;
- ответ — JSON со ссылкой `file`, затем файл скачивается;
- парсер устойчив к числам/строкам в `status`, `duration`, BOM и не-JSON ответу;
- при ошибке показывается текст сервиса, HTTP-код, content-type и начало ответа;
- базовый URL настраивается через `Zvukogram:BaseUrl` (по умолчанию `https://zvukogram.com/`).

Проверка настроек:

- текст до 100 символов;
- используется текущая (несохранённая) форма;
- аудио встраивается как `data:` URL и проигрывается, показываются имя, размер, длительность,
  формат, частота, битрейт, «моно».

---

## 11. Экспорт

`GET /anki/export` → `DeckWorkspace.ExportAsync`:

1. читает актуальную БД;
2. для `collection.anki21b` сжимает её zstd;
3. копирует все zip-записи, кроме `collection.*` и (при необходимости) `media`;
4. добавляет `_pendingMedia`:
   - новый формат — zstd-сжатие + protobuf-манифест;
   - старый — сырые байты + JSON-манифест;
5. имя файла скачивания = имя исходной колоды.

Учитывать: изменения применяются к БД сразу, но zip пересобирается только при экспорте.

---

## 12. Правила для агента

**Можно/нужно**

- Перед изменениями: `dotnet build`; после — сборка и проверка сценария.
- Для проверок создавать временные папки `.scratch*` в корне и удалять их после.
- Тестировать API через локальный двойник: задать `Zvukogram__BaseUrl` при запуске.
- Сохранять русский UI и существующие названия кнопок.
- При добавлении поля/фильтра обновлять `DeckWorkspace`, `Anki.razor`, CSS и тесты.
- Для новых действий использовать формат `verb:noteId` и обрабатывать в `HandleActionAsync`.

**Нельзя**

- Добавлять `@rendermode InteractiveServer`: страница рассчитана на статический SSR и обычные POST-формы.
- Убирать `_handler`/`@formname`/`AntiforgeryToken` и условно скрывать формы, по которым идёт POST.
- Парсить медиа-манифест только как JSON или отдавать медиа без проверки zstd.
- Забывать `unicase` collation при открытии новой SQLite-связи на запись.
- Использовать `IFormFile.OpenReadStream` без учёта того, что форма статическая.
- Хранить состояние страницы в полях компонента между запросами (его нет — только `DeckWorkspace`).
- Возвращать `[sound:...]` с абсолютными путями или недопустимыми символами.
- Коммитить `bin`, `obj`, временные `.scratch*`.

---

## 13. Как проверять

### 13.1 Сборка

```powershell
dotnet build -v q
```

Ожидается 0 ошибок и 0 предупреждений.

### 13.2 Синтетическая колода

Собрать `.apkg` скриптом на Python: SQLite + zip.

- Старая схема: таблицы `col`, `notes`, `cards`; JSON в `col.models` / `col.decks`;
- Новая схема: `fields`/`notetypes`/`decks`;
- Для нового формата медиа: `media` — zstd+protobuf, записи — zstd-сжатые.

### 13.3 HTTP-проверка

```powershell
$jar = "cookies.txt"
curl.exe -s -c $jar -o page.html http://127.0.0.1:5179/anki
$token = [regex]::Match((Get-Content page.html -Raw),
    'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
curl.exe -s -b $jar -c $jar -o result.html `
  -F '_handler=anki-upload' -F "Upload=@deck.apkg" `
  -F "__RequestVerificationToken=$token" http://127.0.0.1:5179/anki
```

Для действий:

```powershell
-F '_handler=deck-actions' -F 'Action=process:1001'
-F '_handler=deck-bulk'    -F 'BulkAction=process-all'
```

Проверять: количество `<tr>` в `<tbody>`, наличие `class="html-mark"`, `preview-row`,
`/anki/media/...`, значения `[sound:...]` в БД после экспорта.

### 13.4 Двойник Звукограма

Запустить локальный HTTP-сервер на `127.0.0.1:8099`, отдающий:

- `POST /index.php?r=api/text` → JSON `{status:1,file:"http://127.0.0.1:8099/a.wav",format:"wav",duration:1}`;
- `GET /a.wav` → аудио.

Запустить приложение:

```powershell
$env:Zvukogram__BaseUrl = 'http://127.0.0.1:8099/'
dotnet run --urls http://127.0.0.1:5180
```

Сохранить настройки с фиктивными token/email/voice и проверить «Проверить» и генерацию.

### 13.5 Браузер

Для UI-проверок использовался headless Chrome + CDP (порт 9223+), cookie сессии внедрялась через
`Network.setCookie`. Проверялись: порядок кнопок, высота строк, фокус/каретка при живом поиске,
прокрутка после POST.

---

## 14. Известные ограничения и технический долг

- только `/text` (до 1000 символов), без `/longtext` и очереди;
- нет подтверждения перед «Обработать ВСЕ» и нет undo;
- показываются первые 500 заметок (без пагинации);
- нет аутентификации (рассчитано на локальный запуск);
- токен Звукограма хранится в cookie (по требованию), а не в защищённом хранилище;
- при массовой обработке сбрасываются фильтры;
- сессии и рабочие области живут в памяти: перезапуск приложения теряет загруженные колоды;
- `bin`/`obj` не игнорируются `.gitignore` (файла нет).

---

## 15. Частые проблемы

| Симптом | Причина / решение |
|---|---|
| `Cannot submit the form ...` | форма, по которой пришёл POST, отсутствует в разметке после обработки; рендерить всегда |
| `no query solution` | не зарегистрирована collation `unicase` |
| Ошибка SSL при проверке Звукограма | приложение запущено в песочнице DSH; запустить вне её |
| «Не удалось разобрать ответ» | сервис вернул не-JSON или изменил типы; смотреть текст в сообщении |
| Не играют исходные аудио | не поддержан zstd/protobuf формат медиа |
| После экспорта пропало медиа | манифест записан в чужом формате (JSON вместо protobuf или наоборот) |
| Строка с кнопкой выше остальных | кнопка должна быть компактной (`.generate-audio`, `.play-audio`) |
| Фильтр сбрасывает прокрутку | проверять `App.razor` (scroll/focus restore) |
