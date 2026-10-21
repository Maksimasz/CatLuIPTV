# CatLu IPTV 0.0.6

Исходники приложения: C# / .NET 8, WPF, LibVLCSharp. Иконка кота находится в `Assets`.

Установка, первый запуск, EPG, группы, избранное и полный экран описаны в [основном README](../README.md).

## Разработка

Из корня репозитория:

```powershell
dotnet run --project .\IptvPlayer\IptvPlayer.csproj
```

`AppPaths` определяет папку данных: в рабочей копии это корень проекта, в установленной версии — `%LOCALAPPDATA%\CatLu IPTV`. Существующие настройки и источники рабочей копии сохраняются.

## Проверки

```powershell
dotnet run --project .\test\Checks\Checks.csproj
dotnet run --project .\test\WindowChecks\WindowChecks.csproj
```

## Установщик

Из корня репозитория выполните `.\installer\build.ps1`. Нужны .NET 8 SDK и Inno Setup 6/7. Версия берётся из `IptvPlayer.csproj`, установщик создаётся в `release\0.0.6`. .NET Desktop и LibVLC включаются в установщик; личные источники и настройки не включаются.
