FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY src/DocIntake.Shared/DocIntake.Shared.csproj src/DocIntake.Shared/
COPY src/DocIntake.Api/DocIntake.Api.csproj src/DocIntake.Api/
RUN dotnet restore src/DocIntake.Api/DocIntake.Api.csproj
COPY src/DocIntake.Shared/ src/DocIntake.Shared/
COPY src/DocIntake.Api/ src/DocIntake.Api/
RUN dotnet publish src/DocIntake.Api/DocIntake.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DocIntake.Api.dll"]
