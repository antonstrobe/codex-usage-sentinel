# Настройка на новом Windows-компьютере

Пользователю достаточно готового EXE, установленного Codex с выполненным входом и своего Telegram-бота. Токен вводится в защищённом поле **Telegram…**, его не нужно вставлять в переписку с агентом. С новым ботом сначала нажмите **Start** в Telegram.

## Для агента Codex

1. Прочитай `AGENTS.md`. Используй `build.ps1` этого проекта без переноса чужих настроек.
2. Проверь Windows, .NET Framework 4.8 и `codex.exe`. Автопоиск: `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe` и PATH. Файл можно выбрать через **Codex CLI…**.
3. Выполни сборку и `test.ps1`. Запусти диагностику и дождись завершения:

```powershell
$probe = Join-Path $env:TEMP 'codex-usage-probe.json'
$app = Start-Process .\dist\CodexUsageSentinel.exe -ArgumentList @('--probe', ('"' + $probe + '"')) -PassThru -Wait
Get-Content -LiteralPath $probe -Encoding UTF8
```

4. При ошибке входа попроси пользователя войти в Codex через ChatGPT. Не читай `auth.json`, cookie и секреты Codex. Отсутствующие лимиты не заменяй выдуманными.
5. Запусти EXE с `--show`. Помоги настроить Telegram, оставив ввод секрета пользователю в окне программы. Реальный тест отправляй только по его просьбе. При невозможности поиска попроси известный личный ID; не отключай чужой webhook.
6. Размести EXE в постоянной папке. По просьбе пользователя включи автозапуск кнопкой или положи ярлык/EXE в **Папку автозагрузки**. По умолчанию окно не появляется, а значок доступен в трее.
7. Проверь Build в заголовке, свежесть лимитов и фактический автозапуск. Заверши временные процессы. Основной монитор оставь работать, если пользователь запросил постоянный контроль.

## Интеграция

Монитор запускает `codex.exe app-server` со стандартным транспортом stdio. Через JSON-RPC выполняет `initialize`, `initialized`, затем `account/rateLimits/read`. Запрос читает лимиты ChatGPT, не запускает задачу модели и не требует сетевого порта. [Официальная документация Codex App Server](https://learn.chatgpt.com/docs/app-server).

Остаток — `100 - usedPercent`, ограниченный 0–100. Основной bucket — `codex` в `rateLimitsByLimitId`, запасной формат — `rateLimits`. Отсутствующие данные означают неизвестный статус. Число сбросов читается из `rateLimitResetCredits.availableCount`, когда доступно. [Схема лимитов](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt).

## Исходники и диагностика

| Файл | Назначение |
| --- | --- |
| `src/Core.cs` | настройки, DPAPI, пороги, Codex |
| `src/Monitor.cs` | Telegram и цикл мониторинга |
| `src/App.cs` | окно, трей, автозагрузка |
| `src/Version.cs` | единая версия Build и метаданные EXE |
| `src/Builder.cs` | Build.exe для сборки из исходников |
| `src/Tests.cs` | тесты с фиктивным Telegram |

Параметры: `--show` — окно, `--tray` — тихий запуск, `--probe <путь.json>` — однократное чтение лимитов без Telegram, `--render <путь.png>` — отрисовка без рассылки. Локальное состояние хранится вне репозитория в LocalAppData.

Для следующего релиза обнови `BuildInfo.Version`, собери и проверь оба EXE и прикрепи их с `SHA256SUMS.txt` к GitHub Release. Никогда не добавляй личные настройки, даже зашифрованные.
