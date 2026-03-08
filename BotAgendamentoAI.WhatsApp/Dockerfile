FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BotAgendamentoAI.WhatsApp/BotAgendamentoAI.WhatsApp.csproj", "BotAgendamentoAI.WhatsApp/"]
RUN dotnet restore "BotAgendamentoAI.WhatsApp/BotAgendamentoAI.WhatsApp.csproj"
COPY . .
WORKDIR /src/BotAgendamentoAI.WhatsApp
RUN dotnet publish "BotAgendamentoAI.WhatsApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BotAgendamentoAI.WhatsApp.dll"]
