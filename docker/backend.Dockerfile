FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY src/backend/ src/backend/
COPY tests/backend/ tests/backend/
COPY tests/simulators/ tests/simulators/
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

# Static analysers the AI conversion gates run on model-written workflows. Pinned and verified against the sha256 published
# with each release; without them no proposal can be approvable (the API reports it).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS workflow-tools
ARG TARGETARCH
ARG ACTIONLINT_VERSION=1.7.12
ARG ZIZMOR_VERSION=1.32.0
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /tools
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
      amd64) AL=amd64; AL_SHA=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8; \
             ZZ=x86_64-unknown-linux-gnu; ZZ_SHA=bb110cc765eca9cd7efb2e284a956be5974c60027b0970f16397d86208a9957b ;; \
      arm64) AL=arm64; AL_SHA=325e971b6ba9bfa504672e29be93c24981eeb1c07576d730e9f7c8805afff0c6; \
             ZZ=aarch64-unknown-linux-gnu; ZZ_SHA=cb152b207f815116d7fa1c1ee4614f61578803351395052908095e79adfd9fd0 ;; \
      *) echo "unsupported architecture ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o actionlint.tgz "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_${AL}.tar.gz"; \
    echo "${AL_SHA}  actionlint.tgz" | sha256sum -c -; \
    tar xzf actionlint.tgz actionlint; \
    curl -fsSL -o zizmor.tgz "https://github.com/zizmorcore/zizmor/releases/download/v${ZIZMOR_VERSION}/zizmor-${ZZ}.tar.gz"; \
    echo "${ZZ_SHA}  zizmor.tgz" | sha256sum -c -; \
    tar xzf zizmor.tgz zizmor; \
    chmod 0755 actionlint zizmor

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Required only for verified F-035/036 local Git object acquisition.
# Install in the root build phase, never run Git as root at runtime.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# aspnet:10.0 is Ubuntu 24.04 and ships useradd, not adduser.
RUN useradd --system --home-dir /app --shell /usr/sbin/nologin appuser
USER appuser
COPY --from=workflow-tools /tools/actionlint /tools/zizmor /usr/local/bin/
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "GitHubMigrator.Api.dll"]
