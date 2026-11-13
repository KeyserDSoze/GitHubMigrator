FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY src/backend/ src/backend/
COPY tests/backend/ tests/backend/
COPY tools/ tools/
RUN dotnet restore src/backend/GitHubMigrator.slnx
RUN dotnet publish src/backend/applications/GitHubMigrator.Api/GitHubMigrator.Api.csproj -c Release --no-restore -o /app/publish

FROM build AS test
ENTRYPOINT ["dotnet", "test", "src/backend/GitHubMigrator.slnx", "--no-restore", "--verbosity", "normal"]

# PostgreSQL + Azure DevOps simulator suite; the simulator serves real git repositories.
FROM build AS integration
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
COPY tools/ tools/
COPY tests/integration/ tests/integration/
RUN dotnet restore tests/integration/GitHubMigrator.Integration.Tests.csproj
ENTRYPOINT ["dotnet", "test", "tests/integration/GitHubMigrator.Integration.Tests.csproj", "--no-restore", "--verbosity", "normal"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Required only for verified F-035/036 local Git object acquisition.
# Install in the root build phase, never run Git as root at runtime.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# aspnet:10.0 is Ubuntu 24.04 and ships useradd, not adduser.
RUN useradd --system --home-dir /app --shell /usr/sbin/nologin appuser
USER appuser
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "GitHubMigrator.Api.dll"]
