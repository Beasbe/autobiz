# Ход работ: AutoParser на Linux (Docker)

Цель: запустить CLI утилиты (изначально Windows-only) на Linux через Docker, заменив
`Windows.Media.Ocr` на Tesseract. Лог проблем и решений по ходу.

## Архитектурное решение

Исходное состояние: `AutoParser.Cli` и `AutoParser.Ocr.Windows` нацелены на
`net8.0-windows10.0.19041.0`, CLI жёстко создаёт `WindowsOcrEngine` (WinRT).

Что сделано:
- `AutoParser.Ocr.Linux` — движок `TesseractOcrEngine : IOcrEngine` (внешний бинарь).
- `AutoParser.Ocr` — выбор движка `LocalOcr.Create(...)` под ОС (`--ocr windows|linux|yandex|none`).
- `AutoParser.Cli` переведён на `net8.0`, OCR выбирается по платформе/ключу.
- Общая логика выбора кадра вынесена в `OcrPageRunner` (была в Windows-движке).
- `AutoParser.Tests` переведён на `net8.0`, OCR-зависимые тесты помечены `OcrDataFact`.
- `DocumentTextService` получил `OcrDpi`/`MaxOcrSide` (рендер и обрезка кадра для OCR).
- `Dockerfile` (Tesseract + rus), `run.sh` (удобный запуск), `serial.md`.

## Проблемы и решения

1. **Нет .NET/tesseract на хосте** → сборка и запуск внутри образа `dotnet/sdk:8.0`.
2. **Вложенный путь AutoParser/AutoParser** → монтировать корень репозитория в `/src`.
3. **NETSDK1100** (Windows-targeting на Linux) → `EnableWindowsTargeting=true`.
4. **NU1201** (net8.0-windows нельзя ссылать из net8.0) → ссылка на Ocr.Windows только на Windows (`IsOSPlatform`), на Linux — `UnavailableOcrEngine`.
5. **MSB4232** (`DefineConstants` вне Target) → перенести в `PropertyGroup Condition`.
6. **CS0234: `using AutoParser.Ocr.Linux` не резолвится** — вложенное пространство имён
   `AutoParser.Ocr.Linux` под текущим `AutoParser.Ocr` даёт неоднозначность. **Решение:
   переименовать namespace в `AutoParser.OcrLinux`** (плоское, без вложенности). Файлы:
   `TesseractOcrEngine.cs`, `AutoParser.Ocr.Linux.csproj` (RootNamespace), `LocalOcr.cs`.
7. **OCR 4×5 с на кадр** — старый порт логики Windows гонял 4 поворота (0/90/270/180),
   каждый полным распознаванием. **Решение: один проход OSD (psm 0) для ориентации + один
   psm 3 для распознавания.** При низкой уверенности OSD (<2.0) сверяем 0° и найденный
   поворот по числу слов.
8. **psm 3 даёт пусто на формах** (СТС/справки) → фолбэк на psm 6 (единый блок), если psm 3
   вернул <5 слов.
9. **Диск 100%** → `docker builder prune -f`, удаление промежуточных образов
   `autoparser-test-linux`/`autoparser-env`; освобождено ~12 ГБ.
10. **Осиротевшие контейнеры** — мои `timeout docker run` оставляли фоновые `dotnet autoparser`
    (до 5 шт., по ~400 МБ). Удалены `docker rm -f`. Впредь не полагаться на `timeout` +
    перенаправление в `/tmp` (песочница терминала не сохраняет файлы вне проекта).
11. **`${PIPESTATUS}` — bash-изм** → шелл `sh` (dash) даёт «Bad substitution»; использовать `$?`.
12. **k3s в проекте не используется** — это системный одноУзловой Kubernetes на хосте,
    к AutoParser отношения не имеет. Пользователь сам остановил его для освобождения ресурсов.

## Итог

Полный прогон заявки 10878 (154 МБ, 138 файлов: 76 jpg, 56 pdf) в контейнере:
- **592 с** (было 775 до оптимизации; Windows-версия по README ~2 мин на тестах).
- Все ключевые поля совпали с заключением эксперта: VIN `WBAHF31020WX16362`,
  ГРЗ `Н222ОУ152`, пробег `42157`, мощность `190/258,3`, собственник, полис, СТС/ПТС.
- Таблица повреждений: «Накладка нижняя зеркала» (Замена), «Крыло переднее» (окраска),
  «Накладка порога левого» (окраска).
- Сформированы docx + xlsx.

Известные ограничения (переключение на Tesseract):
- OCR медленнее Windows.Media.Ocr; качество на низкоконтрастных сканах 150 dpi хуже
  (виден шум, напр. «…левого рса», «Место ДТП» помечено «Проверить»).
- Первый прогон медленный, дальше результаты берутся из кэша OCR на диске.

## Запуск

```bash
docker build -t autoparser .
docker run --rm -v "$PWD/AutoParser/data:/data" -v "$PWD/out:/out" \
  autoparser process "/data/Заявка№10878 26-130531 НТЭ" --out /out
# или ./run.sh process "/data/Заявка№10878 26-130531 НТЭ"
```

## Возможные следующие шаги
- Распараллелить OCR по страницам (сейчас цикл последовательный) — ускорит первый прогон.
- Прогнать golden-тесты с Tesseract и зафиксировать расхождения (тесты переведены на
  `AUTO_PARSER_TEST_OCR=linux`).
