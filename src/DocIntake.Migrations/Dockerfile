# Database migration runner. Runs as a Container Apps Job inside the VNet because Azure SQL has no
# public network access; the deployment pipeline starts it and waits for completion.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src
COPY . .

RUN dotnet publish src/DocIntake.Migrations/DocIntake.Migrations.csproj \
    --configuration Release \
    --output /app/publish \
    -warnaserror

FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app
COPY --from=build /app/publish .

RUN useradd -u 1001 -m migrator && chown -R migrator:migrator /app
USER migrator

ENTRYPOINT ["dotnet", "DocIntake.Migrations.dll"]
