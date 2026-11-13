FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY tools/AdoSim/ tools/AdoSim/
RUN dotnet publish tools/AdoSim/AdoSim.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git builds the seed repositories and serves them over smart HTTP; curl is for the healthcheck.
RUN apt-get update && apt-get install -y --no-install-recommends git curl && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /usr/sbin/nologin sim && mkdir /data && chown sim /data
USER sim
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 ADO_SIM_DATA=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "AdoSim.dll"]
