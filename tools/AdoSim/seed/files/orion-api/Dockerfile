FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Orion.sln global.json ./
COPY src/Orion.Api/Orion.Api.csproj src/Orion.Api/
RUN dotnet restore src/Orion.Api/Orion.Api.csproj
COPY src/ src/
RUN dotnet publish src/Orion.Api/Orion.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orion.Api.dll"]
