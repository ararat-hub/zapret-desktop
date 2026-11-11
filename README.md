# Zapret Desktop

Приложение Windows для подбора и запуска штатных пресетов [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube). Интерфейс на русском, написан на C# и WPF. Движок скачивается из официальных релизов Flowseal.

[Скачать готовую сборку для Windows x64](https://github.com/ararat-hub/zapret-desktop/raw/refs/heads/main/downloads/ZapretDesktop-win-x64.zip)

## Первый запуск

1. Нужны Windows 10/11 x64 и [.NET Desktop Runtime 10 x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. Распакуйте сборку в постоянную папку и откройте **ZapretDesktop.exe**.
3. Нажмите **Установить и подобрать**, согласитесь на загрузку указанной версии и подтвердите запрос Windows (UAC).
4. Для следующих запусков выберите **Discord и YouTube**, **Discord** или **YouTube** и нажмите **Подобрать и включить**. Первый пресет, который дважды проходит HTTPS-проверку выбранных сервисов, остаётся включённым. Приложение запоминает его и проверяет первым в следующий раз.
5. Чтобы выбрать пресет самостоятельно, используйте выпадающий список и кнопку **Включить**. **Остановить** и закрытие приложения завершают запущенный им процесс движка.

HTTPS-проверка обращается к публичному адресу Discord Gateway и к YouTube. Для голосового чата Discord и воспроизведения видео проверьте работу в самих приложениях. Если подбор не проходит, в журнале виден результат для каждого сервиса. Если уже работает другой экземпляр winws, остановите его в исходной программе перед запуском Zapret Desktop.

## Обновления и автозапуск

- Проверка стабильного релиза выполняется при открытии приложения и раз в четыре часа, пока окно открыто. Её можно отключить в настройках или запустить вручную.
- Когда появляется новая версия, в окне отображается кнопка **Установить** с номером версии. Скачивание начинается после подтверждения в диалоге. Приложение сверяет SHA-256 архива с данными GitHub перед распаковкой. Если пресет уже работает, новая версия используется при следующем подключении.
- **Открывать при входе в Windows** запускает окно после входа пользователя. Для включения пресета используется обычная кнопка подключения.

## Сборка из исходников

Скачайте [.NET 10 SDK x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) и выполните в корне проекта.

~~~powershell
dotnet build .\src\ZapretDesktop\ZapretDesktop.csproj -c Release
dotnet run --project .\tests\ZapretDesktop.Checks\ZapretDesktop.Checks.csproj -c Release
dotnet publish .\src\ZapretDesktop\ZapretDesktop.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o .\artifacts\win-x64
~~~

Готовый файл появится в папке `artifacts\win-x64`. Сборка использует компоненты .NET SDK. Портативному EXE нужен Desktop Runtime 10. У этой сборки отсутствует цифровая подпись издателя.

## Хранение и удаление

Настройки текущего пользователя находятся в `%LOCALAPPDATA%\ZapretDesktop\settings.json`. Версии движка хранятся в `%ProgramFiles%\Zapret Desktop\engine`. Чтобы удалить оболочку, выключите автозапуск, закройте приложение и удалите распакованные файлы. Чтобы удалить скачанные версии движка, удалите его папку с правами администратора. Windows управляет загрузкой драйвера WinDivert.

## Код и лицензии

- [Компоненты, сетевые запросы и авторизация](docs/architecture.md)
- [MIT для кода оболочки](LICENSE)
- [Лицензия движка Flowseal](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.4/LICENSE.txt) и [исходники WinDivert с лицензиями LGPLv3/GPLv2](https://github.com/basil00/WinDivert). Движок и драйвер входят в официальный архив Flowseal.
