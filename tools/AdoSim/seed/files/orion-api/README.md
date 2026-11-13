# Orion API

.NET 8 minimal API for the Orion Commerce storefront (orders, catalog, loyalty).

```
src/Orion.Api          ASP.NET Core service
tests/Orion.Api.Tests  xUnit tests
Dockerfile             multi-stage image used by the Docker stage
azure-pipelines.yml    build -> docker -> deploy dev -> deploy prod
pipelines/             local templates and the nightly scan
```

The pipeline consumes shared templates from `Orion Platform/orion-templates`.
