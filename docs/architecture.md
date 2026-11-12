# Компоненты, запросы и авторизация

## Исходный проект Flowseal

- Пресеты general*.bat подставляют пути к спискам доменов и файлам в bin, затем запускают winws.exe. Сам движок использует WinDivert для обработки сетевых пакетов на Windows. [Код general.bat](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.4/general.bat#L1-L24)
- service.bat предоставляет меню для проверки обновлений, тестов и установки службы. Для административных действий он запрашивает повышение прав Windows. WinDivert работает с трафиком, поэтому для запуска движка нужны административные права. [Код service.bat](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.4/service.bat)
- Запрос обновления Flowseal читает публичный файл версии по HTTPS и открывает страницу релиза. Для этого достаточно анонимного запроса. Логином в Discord управляет сам клиент Discord. Упоминания tokens в конструкциях for /f внутри BAT-файлов относятся к разбору текста. [Проверка обновлений в service.bat](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.4/service.bat#L369-L406)
- Публикация релизов в исходном проекте выполняется GitHub Actions с разрешением contents: write. Action получает временный GITHUB_TOKEN из контекста github.token. Он используется на этапе публикации в CI. [Код release.yml](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.4/.github/workflows/release.yml), [входы action-gh-release](https://github.com/softprops/action-gh-release/blob/v2/action.yml), [документация GITHUB_TOKEN](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token)

## Запуск пресета из Zapret Desktop

1. [MainWindow](../src/ZapretDesktop/MainWindow.xaml.cs) читает настройки и текущую версию движка. По запросу пользователя запускает копию приложения через Windows UAC для установки или запуска движка. Windows показывает системный запрос, когда требуются административные права.
2. [EngineStore и PresetParser](../src/ZapretDesktop/EngineFiles.cs) читают аргументы из строки запуска winws.exe в штатных файлах general*.bat. [EngineSession](../src/ZapretDesktop/EngineSession.cs) передаёт каждый аргумент напрямую дочернему процессу. Используются исходные значения Flowseal для выключенного Game Filter.
3. [Connectivity](../src/ZapretDesktop/Connectivity.cs) проверяет публичный API Discord и получение Hello от Gateway по WebSocket. Для YouTube проверяются доступность сайта и загрузка JPEG с i.ytimg.com. Запросы выполняются анонимно. [PresetSelection](../src/ZapretDesktop/PresetSelection.cs) измеряет исходное соединение, запускает один пресет за раз и оставляет активным первый пресет, при котором выбранные сервисы прошли обе проверки.
4. Дочерний процесс помещается в Windows Job Object. При остановке кнопкой приложение завершает свой winws.exe. При выходе закрывается Job Object.

## Обновление движка

[LocalEngine](../src/ZapretDesktop/LocalEngine.cs) ищет папку Flowseal по пути служб Windows или работающему winws.exe. Можно также указать папку вручную. После подтверждения приложение создаёт локальную копию бинарников, списков и пресетов. При передаче запроса через UAC копия сверяется по SHA-256. [EmbeddedEngine](../src/ZapretDesktop/EmbeddedEngine.cs) позволяет установить официальный ZIP из EXE, сверив SHA-256 с [манифестом сборки](../build/flowseal.json). Дальнейшие обновления проверяются через GitHub.

1. [Releases](../src/ZapretDesktop/Releases.cs) запрашивает публичный REST endpoint releases/latest. Приложение сравнивает номер релиза с установленным и выбирает ZIP, опубликованный Flowseal. GitHub допускает чтение публичных релизов без аутентификации. [GitHub REST API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
2. После согласия пользователя приложение скачивает выбранный ZIP по HTTPS. При перезапуске с правами администратора оно повторно читает метаданные подтверждённого тега и сверяет SHA-256 с метаданными, полученными до подтверждения. Архив проходит проверку размера и SHA-256.
3. Новые файлы распаковываются во временный каталог под Program Files. Маркер текущей версии переключается после проверки структуры архива и чтения пресетов. Уже запущенный процесс продолжает использовать каталог своей версии.
4. Запросы обновлений содержат User-Agent и Accept. [Preferences](../src/ZapretDesktop/Preferences.cs) сохраняет только выбор сервисов, последний пресет и настройку проверки обновлений. Автозапуск окна хранится в HKCU. Для работы приложения достаточно публичных данных. Полей для логина, пароля и токена нет.
