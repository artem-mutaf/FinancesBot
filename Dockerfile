# =====================================================================
#  Сборка Docker-образа приложения. Двухэтапная (multi-stage):
#   1) "build" — образ с полным .NET SDK (~800 МБ): компилирует проект;
#   2) "final" — образ только со средой выполнения ASP.NET (~220 МБ):
#      в него копируется лишь готовый результат. SDK на сервере не нужен.
#  Образы Microsoft мультиархитектурные: на ARM-сервере (Oracle Ampere)
#  Docker сам скачает ARM-версию.
# =====================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только .csproj и restore — Docker кэширует каждый шаг. Пока список пакетов
# не меняется, при пересборке скачивание NuGet-пакетов пропускается (быстрее в разы).
COPY FinancesTelegramBot/FinancesTelegramBot.csproj FinancesTelegramBot/
RUN dotnet restore FinancesTelegramBot/FinancesTelegramBot.csproj

# Теперь весь код и публикация в папку /app
COPY FinancesTelegramBot/ FinancesTelegramBot/
RUN dotnet publish FinancesTelegramBot/FinancesTelegramBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Запуск не от root: если в приложении найдут уязвимость, у взломщика будет меньше прав.
# Пользователь "app" (его id в переменной APP_UID) уже есть в образах Microsoft.
USER $APP_UID

# Приложение слушает порт 8080 (так настроены образы .NET 8+). Наружу его не публикуем —
# снаружи приходят только в Caddy, а он передаёт запросы сюда по внутренней сети Docker.
EXPOSE 8080
ENTRYPOINT ["dotnet", "FinancesTelegramBot.dll"]
