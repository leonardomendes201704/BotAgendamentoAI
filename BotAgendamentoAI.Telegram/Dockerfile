FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj", "BotAgendamentoAI.Telegram/"]
RUN dotnet restore "BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj"
COPY . .
WORKDIR /src/BotAgendamentoAI.Telegram
RUN dotnet publish "BotAgendamentoAI.Telegram.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BotAgendamentoAI.Telegram.dll"]
