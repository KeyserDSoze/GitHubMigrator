FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY src/backend/ src/backend/
COPY tests/backend/ tests/backend/
RUN dotnet restore src/backend/GitHubMigrator.slnx
RUN dotnet publish src/backend/applications/GitHubMigrator.Api/GitHubMigrator.Api.csproj -c Release --no-restore -o /app/publish

FROM build AS test
ENTRYPOINT ["dotnet", "test", "src/backend/GitHubMigrator.slnx", "--no-restore", "--verbosity", "normal"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN adduser --disabled-password --home /app --gecos "" appuser
USER appuser
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "GitHubMigrator.Api.dll"]
