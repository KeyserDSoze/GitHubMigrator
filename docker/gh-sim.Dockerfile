FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY tools/GhSim/ tools/GhSim/
RUN dotnet publish tools/GhSim/GhSim.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git runs the real smart-HTTP upload/receive-pack and the pre-receive rule hook; curl is for the healthcheck.
RUN apt-get update && apt-get install -y --no-install-recommends git curl && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /usr/sbin/nologin sim && mkdir /data && chown sim /data
USER sim
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 GH_SIM_DATA=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "GhSim.dll"]
