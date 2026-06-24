FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 15959

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/CopilotAutoBYOK/copilot-auto-byok.csproj", "CopilotAutoBYOK/"]
RUN dotnet restore "CopilotAutoBYOK/copilot-auto-byok.csproj"
COPY . .
WORKDIR "/src/CopilotAutoBYOK"
RUN dotnet build "copilot-auto-byok.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "copilot-auto-byok.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "copilot-auto-byok.dll"]
