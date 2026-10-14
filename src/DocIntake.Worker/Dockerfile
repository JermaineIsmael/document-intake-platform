# Multi-stage Dockerfile for DocIntake.Worker.
# Stage 1: Build with .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src
COPY . .

# Restore and build; warnings are treated as errors for production quality.
RUN dotnet build --configuration Release --warnaserror

# Stage 2: Publish (output ready for execution)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS publish
WORKDIR /src
COPY --from=build /src .
RUN dotnet publish DocIntake.Worker.csproj \
    --configuration Release \
    --no-build \
    --output /app/publish

# Stage 3: Runtime-only container (minimal footprint)
FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app

# Copy published binaries from the publish stage
COPY --from=publish /app/publish .

# Create a non-root user for security
RUN useradd -u 1001 -m workserver && chown -R workserver:workserver /app
USER workserver

# No HTTP ports exposed; this is a background service consuming Service Bus messages.
# Entrypoint executes the worker as a background service
ENTRYPOINT ["dotnet", "DocIntake.Worker.dll"]
