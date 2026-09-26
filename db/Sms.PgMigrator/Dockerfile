FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish db/Sms.PgMigrator/Sms.PgMigrator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sms.PgMigrator.dll"]
