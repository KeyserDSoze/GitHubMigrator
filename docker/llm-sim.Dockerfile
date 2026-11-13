FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY global.json ./
COPY tools/LlmSim/ tools/LlmSim/
RUN dotnet publish tools/LlmSim/LlmSim.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is for the healthcheck; the simulator keeps all state in memory, so there is no volume.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /usr/sbin/nologin sim
USER sim
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 LLM_SIM_API_KEYS=llm-sim-key LLM_SIM_REQUIRE_KEY=true LLM_SIM_MODELS=gpt-sim,llama-sim LLM_SIM_RECIPES=default
EXPOSE 8080
ENTRYPOINT ["dotnet", "LlmSim.dll"]
