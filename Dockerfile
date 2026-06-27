# syntax=docker/dockerfile:1

# --- build -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifest first for better layer caching.
COPY Directory.Build.props Directory.Packages.props ./
COPY ActorBank.Abstractions/ActorBank.Abstractions.csproj ActorBank.Abstractions/
COPY ActorBank.Grains/ActorBank.Grains.csproj ActorBank.Grains/
COPY ActorBank.Api/ActorBank.Api.csproj ActorBank.Api/
RUN dotnet restore ActorBank.Api/ActorBank.Api.csproj

COPY . .
RUN dotnet publish ActorBank.Api/ActorBank.Api.csproj -c Release -o /app --no-restore

# --- runtime ---------------------------------------------------------------
# Alpine 3.23 ships OpenSSL 3.5, which provides ML-DSA for the PQC auth.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23 AS final
WORKDIR /app
COPY --from=build /app ./

# A writable, app-owned dir for the persisted ML-DSA signing key (mount a volume here).
USER root
RUN mkdir -p /keys && chown -R app:app /keys
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    Pqc__KeyFilePath=/keys/pqc-signing-key.pkcs8
EXPOSE 8080
ENTRYPOINT ["dotnet", "ActorBank.Api.dll"]
