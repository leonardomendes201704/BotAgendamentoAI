FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj", "BotAgendamentoAI.Admin/"]
RUN dotnet restore "BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj"
COPY . .
WORKDIR /src/BotAgendamentoAI.Admin
RUN dotnet publish "BotAgendamentoAI.Admin.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BotAgendamentoAI.Admin.dll"]
